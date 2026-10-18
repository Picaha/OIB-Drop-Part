# Interlock — Requirements & OIB Research Notes

Handoff notes for starting the interlock implementation. Nothing in the codebase has been changed yet.

## 1. Requirements (from user, 2026-10-18)

1. Interlock triggered by **manual input** and by **input from Setup Center** when a wrong part or a banned MPN is found:
   1. Lock feeder
   2. Lock PackagingUnit ID (PU UID)
   3. Lock PN (component name)
   4. Lock MPN or Supplier
2. Lock conveyor by manual input.
   - Every lock/unlock must be callable by a user (UI) **and** by a third-party system (API).
3. Improve the existing collector (Form1.cs) — reliability and performance.
4. Propose ~20 further ideas for monitoring/interlock.

User preferences: answer in Thai, keep answers short; use the superpowers workflow (brainstorm → plan → TDD → review).

## 2. Build environment

- MSBuild: `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`
- `Tutorial.sln` builds (Debug) with only warnings: MSB3327 (ClickOnce signing cert missing), CS0472 (int compared to null at Form1.cs 706/711/875/880).
- Target .NET Framework 4.8. Stay on 4.8: OIB proxies are WCF, and External Control requires hosting WCF services.
- Shared OIB DLLs (`Asm.As.Oib.Common*`, `WS.Common`, `WS.Eventing.Contracts`, `UserManagement.Proxy`) are byte-identical across all `Interfaces/Smart Proxies/*` folders, so Monitoring + SetupCenter + SiplacePro.LineControl smart proxies can be referenced together.
- OIB help: decompile with `hh.exe -decompile <outdir> <copy-of-chm-on-simple-path>`. Useful topics (`html/<id>.htm`):
  - Setup Center MES API (External Control) tutorial: `3F069F7B-7745-4FCC-91A7-03EDF39279C9`
  - Feeder External Control tutorial: `b86709c3-9d67-4b20-800a-17a283e98525`
  - `LockPackagingUnits`: `8fcf9117-9b4a-a72c-2631-5c51c049c113`; `LockOnlineFeeders`: `ff84c1ca-4e71-796b-b2f5-f8bbca4ac2c5`
  - `ExternalControlResult` properties: `6cd892f9-3364-8c81-19ef-9ffdde12f5e2`; `LockInfo` properties: `4056b935-05a8-3906-dda8-9740bdde770c`
  - `BlockStationConveyor`: `477806be-ddbf-d6df-18fe-b0fac0151521`; `BlockStationInputConveyor`: `217ca4c9-8995-a5ef-e99b-11133dcddc37`

## 3. OIB APIs mapped to each requirement

### 3.1 Setup Center smart proxy (we call it)

Class `Asm.As.Oib.SiplaceSetupCenter.Proxy.Business.Objects.SiplaceSetupCenter`, ctor `(EndpointAddress, Binding)`.
Config key `Asm.As.Oib.SetupCenter.Proxy.Endpoint` (sample: `net.tcp://adapterhost:1406/Asm.As.Oib.SetupCenter/SiplaceSetupCenter/Reliable`).

| Method | Use |
|---|---|
| `LockPackagingUnits(List<PackagingUnit> puIDs, string reason)` → `PackagingUnitResponse` | Lock PU (req 1.2) |
| `UnlockPackagingUnits(List<PackagingUnit>)` | Release PU |
| `LockOnlineFeeders(LockOnlineFeedersRequest)` / `UnlockOnlineFeeders(UnlockOnlineFeedersRequest)` | Lock feeder online (req 1.1) |
| `GetPackagingUnitLocationsOnFeeders(req{FeederIds})` → `Dictionary<string, PackagingUnitLocation>` | Resolve a feeder ID to its location (needed by LockOnlineFeeders) |
| `GetActualSetups(List<string> machineIds)` → `Dictionary<string, MachineSetup>` (`Tables[].ComponentLocationStates[]` : `PackagingUnitLocation`) | Find currently mounted PUs by PN/MPN/Supplier (req 1.3/1.4) |
| `HasAdapterLicense()`, `IsConnected()`, event `LockStateChanged` | License check, health, Setup Center lock events |

Facts from the docs:
- `LockPackagingUnits`: the lock is **permanent**. Neither the station operator nor the Setup Center GUI can remove it; only an external system can unlock. It requires stations in forced setup verification (also works in the offline area). For splice chains only the first PU counts. The lock appears as `LockInfo` Reason=0, Source=4.
- `LockOnlineFeeders` mandatory fields in `LockFeederLocationInfo`: `Machine.LineName`, `Machine.MachineId`, `Location`, `Device`, `Track`, `FeederId`, `FeederType`, plus `Reason` text.
- `PackagingUnit` fields: `UID`, `ComponentName` (= PN), `ManufacturePartNumber` (= MPN), `Manufacturer`, `Supplier`, `BatchId`, `ExpiryDate`, `MsdLevel`, `MsdOpenDate`, `LockInfos`.
- `LockInfo.Reason` 1 = component mismatch (Setup Center detected a wrong part), 28 = locked by external MES. Full table is in the LockInfo topic.

### 3.2 External Control services (Setup Center calls us)

The docs say: implement the interfaces from `Interfaces/WCF Proxies (C#)/proxySetupCenter.cs`, **not** from the contracts DLL. Host with WCF `ServiceHost` plus a MEX endpoint. If the app does not run as admin, it needs `netsh http add urlacl`.

**`ISiplaceSetupCenterExternalControl`**: register in Service Locator as `SIPLACE.SetupCenter.ExternalControl` (Factory Explorer → Service Configuration; can be scoped per line).
- `GetPackagingUnitControlStatus(Extended)` is called on set, refill, changeover-table register and Setup Center reconnect. This is the hook for prevention of PN/MPN/Supplier/UID locks at scan time.
- Extended `Reason`: 0 Unknown, 1 Set, 2 Refill, 3 FeederPlaced, 4 AnnounceTable, 5 OfflineInsertTable, 6 SetupControlDataRequest (Setup Center ≥ 9.4).
- `ResultState`: 0 Unknown, 1 OK, **2 NotOk**. NotOk makes Setup Center add an internal lock (Source=9, Reason=28), which clears once we later return 1. Messages go in `ExternalControlResultMessage`.
- **Must not call `ISiplaceSetupCenter` inside these callbacks**, or it deadlocks. Any follow-up lock action has to be queued and run asynchronously.
- Report errors with `FaultException<SiplaceSetupCenterFault>`.

**`ISiplaceFeederExternalControl`**: register as `SIPLACE.Feeder.ExternalControl`.
- `GetFeederMaintenanceStatus` is called when a station starts, when Setup Center starts, and when a feeder or table is placed. Return entries only for blocked feeders, with `ResultState=1` and a `Reason` (shown on the Setup Center GUI and on the station). Copy the `FeederLocation` fields into `FeederMaintenanceStatusInfo`.
- Requires forced verification mode. This is the prevention layer for "lock feeder".

### 3.3 Line Control smart proxy (conveyor, req 2)

Class `Asm.As.Oib.SiplacePro.LineControl.Proxy.Business.Objects.LineControl`, ctor `(EndpointAddress, Binding)`.
Config key `Asm.As.Oib.SiplacePro.LineControl.Proxy.Endpoint` (sample: `net.tcp://adapterhost:1406/Asm.As.Oib.SiplacePro.LineControl/Secure`).

| Method | Notes |
|---|---|
| `BlockStationConveyor(stationPath, ConveyorLanes, details)` / `UnblockStationConveyor(...)` | Lanes Left/Right/Both, takes a reason text |
| `IsStationConveyorBlockSupported(stationPath)`, `IsStationConveyorBlocked(path, out bool, out bool)` | Check support and current state |
| `BlockStationInputConveyor(station, bLock, appName)` / `UnBlockStationInputConveyor(station, appName)` | SIPLACE Pro ≥ 10. The lock is held only by Line Control Server, so **an operator at the station can unlock it**. With `bLock=true` the lock survives a new download. |
| `StopLine` / `ContinueLine`, `HasAdapterLicense()` | |

Station path = SIPLACE Pro full path. It is available from Monitoring `Station.FullPath` / `GetConfiguration()`.

### 3.4 Security pattern to reuse

The existing code connects to secure endpoints with `EndpointIdentity.CreateDnsIdentity("ASM.SW.Service")`, `new BindingParameters(address, true)`, `EndpointHelper.CreateBindingFromParameters(...)` and `RevocationMode = NoCheck` (see `GetMonitoringAdapters` in Form1.cs). Use the same pattern for the SetupCenter and LineControl proxies.

## 4. Draft architecture (not yet approved — confirm during brainstorming)

- **Lock registry**: a PostgreSQL table plus an in-memory cache. Lock types FEEDER / PU / PN / MPN / SUPPLIER / CONVEYOR, optional line/machine scope, reason, source (MANUAL / API / SETUPCENTER), who, status, release info.
- **Enforcement per lock type:**
  - PU → `LockPackagingUnits`, plus NotOk from External Control.
  - FEEDER → Feeder External Control (`ResultState=1`), plus `LockOnlineFeeders` if the feeder is currently mounted.
  - PN / MPN / SUPPLIER → NotOk at scan time, plus a sweep of mounted PUs (`GetActualSetups`) followed by `LockPackagingUnits`.
  - CONVEYOR → `BlockStationConveyor`.
- **Interfaces:** a WinForms Interlock screen for manual input, and a REST API for third parties (WCF WebHttp, API key), both going through the same service. Every OIB call result goes to an action log.
- **Setup Center input:** External Control decisions, plus optional escalation of `LockStateChanged` Reason=1 (component mismatch) into a hard lock.

## 5. Open questions

1. Where do machine IDs come from for `GetActualSetups` (config list, Setup Center configuration, or Monitoring)?
2. Which binding Setup Center uses to call External Control (check the MEX / Service Locator registration on the real system).
3. Licenses: `HasAdapterLicense()` for both SetupCenter and LineControl.
4. Meaning of "input from Setup Center": the External Control scan check, the `LockStateChanged` event, or both.
5. Fail-open vs fail-closed when our service is down.
6. Who may release locks, and how users are authenticated.

## 6. Existing collector issues to fix (from review)

- Reconnect after an OIB core restart is broken. `MessageBox` blocks auto mode (Form1.cs:1378). Success is checked through `_buttonInitialize.Enabled` (Form1.cs:242). A stale `_currentSubscription` is never reset. → Add a HeartBeat watchdog and a full re-init.
- Events are lost on DB failure (BoardProcessed, FeederError and StateTransition have no retry). → Queue + background writer + local spool.
- The ListView grows without limit (one row per board) and there is no file log.
- Heavy network calls run per PickupError on the callback thread. → Cache recipes per station.
- No dedup: add `ON CONFLICT DO NOTHING` and a unique `message_oid`.
- Startup relies on Task Scheduler ONLOGON. → Move to a Windows Service later.
- The connection string is picked by "first entry". → Read it by the name `DropProduction`.
- The DB password is in App.config and in the GitHub history. → Rotate it, use a least-privilege user, and add `.gitignore` (bin/obj/packages).
