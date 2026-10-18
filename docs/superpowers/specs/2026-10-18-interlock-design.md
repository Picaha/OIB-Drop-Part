# Interlock Service — Design Spec

- Date: 2026-10-18
- Status: approved in brainstorming, waiting for written-spec review
- Scope: sub-project **A** = requirements 1 and 2 in `docs/interlock/research-notes.md`
- Out of scope: **B** collector hardening (req 3) and **C** the ~20 monitoring/interlock ideas (req 4). Each gets its own spec later.
- OIB API details and doc topic IDs: `docs/interlock/research-notes.md` §3

## 1. Goal

Stop wrong parts and problem feeders from reaching SIPLACE lines.

- Lock **feeder**, **PU UID**, **PN**, **MPN**, **Supplier**, from manual input and from Setup Center (wrong part found, or a banned MPN scanned).
- Lock **conveyor** from manual input.
- Every lock and unlock works from the UI **and** from a third-party API.

Success means:
- A locked item cannot be set up or used on the line.
- Only an authorized role can release a lock.
- Every lock, release and OIB call can be traced afterwards.

## 2. Decisions

| # | Topic | Decision |
|---|---|---|
| D1 | Order | Build A first. B and C come later. |
| D2 | Setup Center input | Both: (a) External Control scan check returns NotOk; (b) the `LockStateChanged` event with Reason=1 is escalated. |
| D3 | Wrong part (Reason=1) | Lock that PU UID through `LockPackagingUnits`. Source = SETUPCENTER. |
| D4 | DB down, service up | Answer Setup Center from the in-memory cache of the last known locks, and raise an alarm. |
| D5 | Roles | Any user may lock. Only QA / ENGINEER / ADMIN may release, and a release reason is required. |
| D6 | UI login | User table in PostgreSQL (username + PIN hash + role). Third parties use an API key. |
| D7 | PN / MPN / Supplier scope | Plant-wide by default, or limited to one line per lock row. |
| D8 | Hosting | Separate Windows Service plus a separate WinForms UI. The UI calls the same REST API that third parties use. |
| D9 | UI technology | WinForms desktop app, not a web UI. |
| D10 | Supplier match | Match only the PU `Supplier` field, not `Manufacturer`. |
| D11 | Machine IDs | Read from Setup Center `GetConfiguration()`. |

## 3. Architecture

```
Setup Center ──External Control (WCF)──►┐
                                        │   Interlock.Service (Windows Service)
UI / 3rd party ──REST (WCF WebHttp)────►│   ├─ LockRegistry  (PostgreSQL + memory cache)
                                        │   ├─ DecisionEngine (pure, in Core)
SetupCenter LockStateChanged event ────►│   ├─ ActionQueue → EnforcementWorker
                                        │   └─ Health / Reconnect
              ◄── SetupCenter smart proxy: LockPackagingUnits, Lock/UnlockOnlineFeeders,
                  GetPackagingUnitLocationsOnFeeders, GetActualSetups, GetConfiguration
              ◄── LineControl smart proxy: BlockStationConveyor, IsStationConveyorBlocked
              ◄── Monitoring smart proxy: GetConfiguration (station FullPath list)
```

### 3.1 Projects (all .NET Framework 4.8)

| Project | Type | Holds |
|---|---|---|
| `Interlock.Core` | class library | Models, `DecisionEngine`, lock rules, role checks, sweep diff, retry policy, and interfaces (`ILockRepository`, `ISetupCenterClient`, `ILineControlClient`, `IMonitoringClient`, `IClock`). No OIB or DB references. |
| `Interlock.Service` | Windows Service (also runs as a console app with `--console` for debugging) | WCF hosts, OIB client adapters, Npgsql repository, worker, REST API, file logging |
| `Interlock.Ui` | WinForms | Screens that call the REST API |
| `Interlock.Tests` | test project | Unit tests for Core and integration tests for the repository |

The existing collector (`Monitoring Tutorial.csproj`, `Form1.cs`) is not changed by this sub-project.

### 3.2 Components inside the service

- **LockRegistry**: loads every ACTIVE lock into memory at start. Each change updates the DB first, then the cache. The cache reloads from the DB every 30 s, so it also picks up direct DB edits.
- **DecisionEngine** (in Core): `Decide(PuInfo pu, string linePath, IReadOnlyCollection<Lock> active)` → `Decision {Ok | NotOk, message, matchedLockIds}`. It is pure and does no I/O.
- **ExternalControlHost**: a WCF `ServiceHost` with a MEX endpoint. It implements `ISiplaceSetupCenterExternalControl` and `ISiplaceFeederExternalControl` from `proxySetupCenter.cs`, not from the contracts DLL. It reads only the cache and **never calls `ISiplaceSetupCenter`** while a callback is running (that deadlocks). Any follow-up work goes to the ActionQueue.
- **ActionQueue + EnforcementWorker**: an in-process queue drained by one worker thread. It makes the OIB calls, retries, and writes `action_log`.
- **SetupCenterEventListener**: subscribes to `LockStateChanged`. On Reason=1 (component mismatch) it creates a PU lock with source SETUPCENTER.
- **RestApi**: WCF WebHttp with JSON. It is the only way to create, release or retry a lock.
- **Health**: checks `IsConnected()` and `HasAdapterLicense()`, recreates proxies with exponential backoff (5 s → max 5 min), and reports DB status and queue length.

## 4. Data model

All tables live in schema `interlock` in database `Drop_Production`.

### `lock`
| Column | Type | Notes |
|---|---|---|
| id | bigserial PK | |
| lock_type | text | FEEDER, PU, PN, MPN, SUPPLIER, CONVEYOR |
| target | text | feeder ID / PU UID / PN / MPN / supplier name / station full path |
| target_norm | text | `upper(trim(target))`, used for matching |
| lanes | text null | CONVEYOR only: LEFT, RIGHT, BOTH |
| line_path | text null | null = plant-wide. Used only for PN/MPN/SUPPLIER. |
| reason | text not null | |
| source | text | MANUAL, API, SETUPCENTER |
| created_by | text | username, API key name, or `SetupCenter` |
| created_at | timestamptz | |
| status | text | ACTIVE, RELEASED |
| released_by / released_at / release_reason | null until released | |
| enforce_state | text | PENDING, APPLIED, FAILED, NOT_NEEDED |
| enforce_error | text null | last error |

Unique partial index on `(lock_type, target_norm, coalesce(line_path,''), coalesce(lanes,''))` WHERE status = 'ACTIVE'.
Rows are never deleted. A release only changes the status.

### `lock_applied_pu`
Records the PUs that a PN/MPN/SUPPLIER sweep locked, so a release unlocks exactly those.
`id, lock_id FK, uid, applied_at, released_at null`.

### `action_log`
One row per OIB call: `id, lock_id null, action, request_json, success, error, attempt, created_at`.
Actions: LOCK_PU, UNLOCK_PU, LOCK_FEEDER_ONLINE, UNLOCK_FEEDER_ONLINE, BLOCK_CONVEYOR, UNBLOCK_CONVEYOR, SWEEP, REBLOCK_CONVEYOR.

### `decision_log`
One row per External Control answer: `id, method, reason_code, uid, pn, mpn, supplier, line_path, machine_id, feeder_id, result, message, matched_lock_ids, created_at`.

### `app_user`
`id, username unique, pin_hash (PBKDF2, salt + iterations stored), role (OPERATOR, QA, ENGINEER, ADMIN), active, created_at`.

### `api_key`
`id, name unique, key_hash (SHA-256), can_unlock bool, active, created_at`.

## 5. Enforcement per lock type

| Type | On lock | On release |
|---|---|---|
| PU | Queue `LockPackagingUnits([uid], reason)`. The scan check also returns NotOk for this UID. | Queue `UnlockPackagingUnits([uid])`. |
| FEEDER | `GetFeederMaintenanceStatus` reports the feeder as blocked. Queue `GetPackagingUnitLocationsOnFeeders([feederId])`; if the feeder is mounted, `LockOnlineFeeders` with the location fields. | If it was locked online, `UnlockOnlineFeeders`. A feeder blocked at placement clears when it is placed again (Setup Center asks again then). |
| PN / MPN / SUPPLIER | (1) Scan check returns NotOk for matching PUs in scope. (2) Queue a SWEEP: `GetConfiguration` → machines in scope → `GetActualSetups` → matching mounted PUs → `LockPackagingUnits`, and record each UID in `lock_applied_pu`. | For each UID in `lock_applied_pu`: if no other ACTIVE lock still covers that UID, `UnlockPackagingUnits`. NotOk locks inside Setup Center clear on the next control-status call, which then returns OK. |
| CONVEYOR | `IsStationConveyorBlockSupported(path)`, then `BlockStationConveyor(path, lanes, reason)`. If not supported: FAILED with that error. | `UnblockStationConveyor(path, lanes, ...)`. |

Shared rules:
- **Overlap guard:** never call `UnlockPackagingUnits` for a UID that another ACTIVE lock still covers, whether through a PU lock on that UID or through an open `lock_applied_pu` row of another ACTIVE lock. This applies to PU releases and sweep releases.
- **enforce_state:** APPLIED once every OIB call for the lock has succeeded. A sweep that finds no mounted PU still counts as APPLIED. NOT_NEEDED is used when there is nothing to do online now, for example a FEEDER lock whose feeder is not mounted; the scan and placement checks still enforce it.
- **Setup Center escalation (D3):** react only to `LockStateChanged` with Reason=1. Our own locks show up as Reason=0/Source=4 and NotOk locks as Source=9/Reason=28, so they do not loop. If an ACTIVE PU lock already exists, do nothing.
- **Conveyor watchdog:** every 60 s, call `IsStationConveyorBlocked` for each ACTIVE conveyor lock. If the conveyor is no longer blocked, block it again, write REBLOCK_CONVEYOR, and show an alarm.
- **Retry:** 3 attempts at 5 s, 30 s and 2 min. After that the lock becomes FAILED and shows an alarm in the UI; the `/retry` endpoint re-queues it.
- **Startup reconcile:** queue every ACTIVE lock whose `enforce_state` is not APPLIED or NOT_NEEDED.
- **Station list:** Monitoring `GetConfiguration()` → `Station.FullPath`.

## 6. Scan check rules (DecisionEngine)

Input: PU (`UID`, `ComponentName`, `ManufacturePartNumber`, `Supplier`) and `PackagingUnitLocation.LinePath`.

1. Normalize every value with `upper(trim(x))`. A null or empty value never matches.
2. PU lock: match on UID.
3. PN / MPN / SUPPLIER lock: match on the field, and the lock is in scope when `line_path` is null **or** equals the PU's `LinePath`.
4. If the PU's `LinePath` is empty (offline preparation area), every line-scoped lock also applies. This errs on the safe side.
5. Any match → `ResultState = 2 (NotOk)`. Message: `Interlock: <TYPE> <target> locked – <reason> (#<id>)`, with all matches joined by `; `. No match → `ResultState = 1 (OK)`.

External Control methods:
- `GetPackagingUnitControlStatus` and `...Extended`: the rules above, one `ExternalControlResult` per location.
- `GetFeederMaintenanceStatus`: return entries only for feeders with an ACTIVE FEEDER lock, with `ResultState=1` and the lock message as `Reason`, copying the `FeederLocation` fields.
- `Ping`: answer normally.
- `GetNewPackagingUnitData`: we hold no PU master data, so for each location return `PackagingUnit = null` and `ResultState = 0 (Unknown)`, as the OIB tutorial does for "not found".
- `GetComponentsStatus`, `GetTrayCarrierMappingData`, `UpdateTrayCarrierMappingData`: return an empty response object. These are used only when their Setup Center options are enabled, and we do not enable them.
- Every call writes `decision_log` (buffered, see §9).
- Target response time: under 1 s.

## 7. REST API

Base URL: `http://<host>:<port>/interlock/v1`, with JSON bodies. The port comes from config. HTTPS can be enabled later through config.

| Method | Path | Body / query | Notes |
|---|---|---|---|
| POST | `/session` | `{username, pin}` | → `{token, role, expiresAt}`. Tokens last 8 h and are kept in memory. |
| POST | `/locks` | `{type, target, lanes?, linePath?, reason}` | → 201 `{lock}`. If an identical ACTIVE lock exists → 200 with the existing lock. |
| GET | `/locks` | `?status=&type=&linePath=&q=` | list |
| GET | `/locks/{id}` | | the lock plus its `action_log` rows |
| POST | `/locks/{id}/release` | `{reason}` | Needs role QA/ENGINEER/ADMIN or an API key with `can_unlock`. |
| POST | `/locks/{id}/retry` | | Only for FAILED locks. |
| GET | `/decisions` | `?uid=&pn=&from=&to=` | decision history |
| GET | `/health` | | OIB SC/LineControl/Monitoring, licenses, DB, queue length, cache age |
| GET | `/lines`, `/stations` | | for UI drop-downs |
| GET/POST/PUT | `/admin/users`, `/admin/apikeys` | | ADMIN only. A new key is shown once. |

- Authentication: `X-Api-Key: <key>` for third parties, or `Authorization: Bearer <token>` for UI users. Every endpoint except `/session` and `/health` needs one of them.
- Errors: 400 for validation, 401 when not authenticated, 403 for the wrong role, 404 when not found, 409 for an invalid state change, 503 when the DB is down. The body is `{error, message}`.
- Validation: `target` and `reason` are required. `lanes` is required for CONVEYOR and not allowed for other types. `linePath` is allowed only for PN/MPN/SUPPLIER. A CONVEYOR target must be a known station.

## 8. WinForms UI (`Interlock.Ui`)

- **Login:** username and PIN.
- **Active locks:** a grid filtered by type, line and text search. Row color: APPLIED green, PENDING yellow, FAILED red. Auto-refresh every 5 s.
- **New lock dialog:** type → target. UID and feeder fields accept barcode-scanner input. CONVEYOR uses a station drop-down plus a lane choice. PN/MPN/SUPPLIER offer a line drop-down or "whole plant". Reason is required.
- **Release:** enabled only for QA/ENGINEER/ADMIN, and asks for a reason.
- **Retry:** shown on FAILED rows.
- **History tab:** released locks, the action log, and the decision log, searchable by UID/PN.
- **Status bar:** health for SetupCenter, LineControl and DB, plus a FAILED-lock alarm count.
- **Admin tab** (ADMIN only): users and API keys.
- Settings: the service base URL in `App.config`.

## 9. Error handling and degraded modes

| Situation | Behaviour |
|---|---|
| Exception inside an External Control callback | Log it and throw `FaultException<SiplaceSetupCenterFault>` (ErrorCode 20000). |
| DB down | Scan checks keep answering from the cache (D4). `decision_log` rows go to a bounded memory buffer (10 000 rows, oldest dropped, with a warning) and are flushed later. UI/API create/release/retry → 503. A SETUPCENTER Reason=1 lock is still enforced in OIB and its record goes to a local spool file (`spool/locks.jsonl`), which is written to the DB when it is back. Health shows red. |
| OIB unreachable | Queue items wait and retry (§5). Proxies are recreated with backoff. Health shows red. |
| License missing | Log it, show health red, and disable only the affected function (SetupCenter or LineControl). |
| Whole service down | Setup Center's behaviour is not documented for PU/feeder External Control; see the site checklist (§11). |
| Logging | Rolling file log: daily files, kept 30 days. |

## 10. Testing

- **Unit tests (TDD), Core:** DecisionEngine (each type, normalization, line scope, empty LinePath, several matches), lock validation and duplicate rules, the role/permission matrix, sweep diff (which UIDs to lock, and which to unlock when other locks still overlap), the retry schedule, and the Reason=1 escalation filter.
- **Service tests with fakes:** OIB access goes through `ISetupCenterClient` / `ILineControlClient` / `IMonitoringClient`. Fakes check the worker flows, the watchdog re-block, startup reconcile, and that External Control callbacks never call `ISetupCenterClient`.
- **Integration tests:** the Npgsql repository against a test schema, `interlock_test`.
- **Manual on a real line:** the site checklist in §11.

## 11. Deployment and site checklist

Deployment:
1. Run the SQL migration script that creates schema `interlock` and its tables, plus a least-privilege DB user for the service.
2. Install the service with `sc create` or an installer, set it to start automatically, and run it under a service account.
3. Register URLs: `netsh http add urlacl` for the REST port and the External Control HTTP endpoint, if one is used.
4. In Factory Explorer → Service Configuration, register `SIPLACE.SetupCenter.ExternalControl` and `SIPLACE.Feeder.ExternalControl` (per line or plant-wide).
5. Config keys: `Asm.As.Oib.SetupCenter.Proxy.Endpoint`, `Asm.As.Oib.SiplacePro.LineControl.Proxy.Endpoint`, the Monitoring endpoint, the connection string named `Interlock`, the REST port, and the External Control base address.
6. Use the existing secure-endpoint pattern (DNS identity `ASM.SW.Service`, `BindingParameters`, `RevocationMode = NoCheck`).

Verify on site before go-live:
- [ ] Which binding Setup Center uses to call External Control (MEX / Service Locator).
- [ ] `HasAdapterLicense()` is true for SetupCenter and LineControl.
- [ ] Stations run in forced setup verification (needed for `LockPackagingUnits` and Feeder External Control).
- [ ] What Setup Center does when our External Control service is down.
- [ ] `IsStationConveyorBlockSupported` is true for the target stations.
- [ ] `LockStateChanged` delivers Reason=1 on a real wrong-part scan.

## 12. Not in this sub-project

Timed or auto-expiring locks, notifications by email or LINE, the web UI, the collector changes (B), and the extra ideas (C).
