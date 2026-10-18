# Interlock service

Interlock stops wrong parts and problem feeders from reaching SIPLACE lines. It holds locks on feeders, PU UIDs, part
numbers (PN), MPNs, suppliers and conveyors, answers Setup Center's External Control scan checks from those locks, enforces
them in OIB (lock PUs and online feeders, block conveyors), and turns Setup Center component mismatches (Reason = 1) into PU
locks. The UI and third parties use its REST API. Design: `docs/superpowers/specs/2026-10-18-interlock-design.md`.

## Architecture

- `Interlock.Core`: models, `DecisionEngine` (pure scan-check rules), lock service, auth, the enforcement worker, cache and retry logic. No OIB, DB or WCF references.
- `Interlock.Service`: Windows service (or `--console`). It hosts External Control (WCF, from `App.config`) and REST (WCF WebHttp, JSON), and holds the OIB smart-proxy adapters, the Npgsql repositories, the JSONL lock spool and the Serilog file log.
- Locks live in PostgreSQL schema `interlock`. An in-memory cache (reloaded every 30 s) answers every scan check, so Setup Center never waits on the database.
- Each change goes to the DB first, then to the cache, then to the `ActionQueue`. One worker thread makes the OIB calls and retries 5 s / 30 s / 2 min, then marks the lock FAILED.
- The Setup Center `LockStateChanged` subscription feeds a bounded queue. One background thread turns those notices into PU locks, so the work never runs on the OIB thread.
- `Composition` wires all of it. Start-up fails when External Control or REST cannot listen.

## Build

```
dotnet build Interlock/Interlock.sln -c Release
dotnet test Interlock/tests/Interlock.Tests --filter "TestCategory!=Integration"
```

The output is in `Interlock/src/Interlock.Service/bin/Release/net48/`. Copy that whole folder to the server. It includes the OIB
smart-proxy DLLs and `Interlock.Service.exe.config`.

## Database

Run these as a PostgreSQL superuser or as the database owner. Replace the names and the password.

```
psql -h <dbhost> -U postgres -c "CREATE DATABASE \"Drop_Production\""      # skip if it exists
psql -h <dbhost> -U postgres -d Drop_Production -v ON_ERROR_STOP=1 -f Interlock/db/001_interlock_schema.sql
psql -h <dbhost> -U postgres -d Drop_Production -v ON_ERROR_STOP=1 <<'SQL'
CREATE ROLE interlock_svc LOGIN PASSWORD '<strong password>';
GRANT CONNECT ON DATABASE "Drop_Production" TO interlock_svc;
GRANT USAGE ON SCHEMA interlock TO interlock_svc;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA interlock TO interlock_svc;   -- DML only; rows are never deleted
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA interlock TO interlock_svc;          -- bigserial ids
SQL
```

The service role gets no DDL rights and does not own the schema. Rerun the two `GRANT ... ALL ...` lines after any script adds
tables.

NOTE: `001_interlock_schema.sql` uses `IF NOT EXISTS` everywhere. Running it again creates missing objects, but it never
changes existing ones. To change an index definition (for example `lock_active_uq`) or a column on a deployed database, write
a manual migration script.

## Configuration (`Interlock.Service.exe.config`)

`appSettings`:

| Key | Default | Meaning |
|---|---|---|
| `Asm.As.Oib.SetupCenter.Proxy.Endpoint` | required | Setup Center smart-proxy endpoint, e.g. `net.tcp://adapterhost:1406/Asm.As.Oib.SetupCenter/SiplaceSetupCenter/Reliable` |
| `Asm.As.Oib.SiplacePro.LineControl.Proxy.Endpoint` | required | Line Control endpoint, e.g. `net.tcp://adapterhost:1406/Asm.As.Oib.SiplacePro.LineControl/Secure` |
| `Asm.As.Oib.Monitoring.Proxy.MonitoringEndpoint` | required | Monitoring endpoint (station list) |
| `Interlock.CoreHost` | required | Computer that runs the OIB core (event subscription) |
| `Interlock.EventCallbackName` | `Interlock.SetupCenterEvents` | Name part of the callback address the core sends `LockStateChanged` to |
| `Interlock.EventCallbackPort` | `8092` | Port the proxy listens on for those events (1..65535) |
| `Interlock.RestBaseAddress` | `http://+:8090/interlock/v1` | REST base address |
| `Interlock.DbSchema` | `interlock` | Database schema (`^[a-z_][a-z0-9_]*$`) |
| `Interlock.SpoolPath` | `spool\locks.jsonl` | Lock spool file for Setup Center locks made while the DB is down |
| `Interlock.LogDir` | `logs` | Log folder (`interlock-yyyyMMdd.log`, daily, 30 files kept) |
| `Interlock.OibSendTimeoutSeconds` | `30` | Longest time one OIB call may take (5..120). Raise it if `GetActualSetups` on a big line times out |

Relative paths resolve against the folder of `Interlock.Service.exe`. If a required key is missing or a value is malformed,
start-up fails with an error that names the key.

`connectionStrings`: the entry named **`Interlock`** (it is looked up by name), for example
`Host=dbhost;Port=5432;Database=Drop_Production;Username=interlock_svc;Password=...`. Unless you set them, the connect timeout
is 5 s and the command timeout 10 s. The file holds the password, so limit read access to the service account and
administrators. The log shows only the host, port and database.

`system.serviceModel`: the two External Control services. The default is `basicHttpBinding` on port 8091 with MEX. A
ready-made net.tcp block (`NETTCP-ALTERNATIVE`, ports 8093/8094) sits in the same file. The comment at the top of `App.config`
gives the exact steps to switch bindings.

## Install the Windows service

Run as administrator. Use a dedicated service account that can read the install folder and write `logs\` and `spool\`.

```
sc create Interlock binPath= "C:\Interlock\Interlock.Service.exe" start= auto DisplayName= "Interlock"
sc description Interlock "Locks wrong parts and feeders on SIPLACE lines"
sc failure Interlock reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc failureflag Interlock 1
powershell -Command "New-EventLog -LogName Application -Source Interlock"
netsh http add urlacl url=http://+:8090/interlock/v1/ user=DOMAIN\svc-interlock
netsh http add urlacl url=http://+:8091/Interlock/ user=DOMAIN\svc-interlock
netsh advfirewall firewall add rule name="Interlock REST" dir=in action=allow protocol=TCP localport=8090 remoteip=<UI/third-party hosts>
netsh advfirewall firewall add rule name="Interlock External Control" dir=in action=allow protocol=TCP localport=8091 remoteip=<Setup Center host>
netsh advfirewall firewall add rule name="Interlock SC events" dir=in action=allow protocol=TCP localport=8092 remoteip=<OIB core host>
```

Then set the service account in services.msc (Interlock > Properties > Log On). Do not pass the password on the `sc`
command line, because it ends up in the shell history and in process listings. A group managed service account
(`sc config Interlock obj= "DOMAIN\gmsa-interlock$"`) needs no password. Start it with `sc start Interlock`.

Restarts: `sc failure` restarts the service after a crash. `sc failureflag Interlock 1` also restarts it when it stops with
an error. A failed start-up (bad configuration, a port in use, a missing URL ACL) stops with exit code 1064, so it is retried
too, every 60 s. A configuration error stays until the file is fixed. Stopping the service with `sc stop` is never retried.

The 8091 URL ACL is needed only while External Control uses http. net.tcp needs no URL ACL, but the firewall rule must name
the ports you configured.

**Security warning:** the default REST address `http://+:8090/interlock/v1` is plain HTTP. Anyone on the LAN can sniff PINs,
session tokens and API keys. Put REST behind HTTPS (a reverse proxy, or an `https://` base address with a bound certificate)
or a VPN, and limit `remoteip` in the firewall to the UI PCs and the third-party systems.

If start-up fails (missing configuration, a port in use, a missing URL ACL), the service stops with exit code 1064 and the SCM
shows the failure. The details go to:
- `startup-error.txt` next to the exe, or in `%ProgramData%\Interlock\` if the exe folder is not writable (exception type,
  message and stack). This file is the only trace of a configuration error, because that error happens before the log file
  is opened.
- The log file `logs\interlock-yyyyMMdd.log`, for failures after the configuration was read.
- The Application event log, source `Interlock`. This needs the `New-EventLog` line above, run once as administrator.

The REST host accepts `+` (strong wildcard: every host name on that port) and `*` (weak wildcard) as the host of
`Interlock.RestBaseAddress`, matching the `netsh http add urlacl` string. `localhost` listens for loopback names only.

## First administrator and API keys

```
C:\Interlock\Interlock.Service.exe --add-user admin ADMIN
PIN: ****
PIN again: ****
User 'admin' created with role ADMIN.
```

The PIN needs at least 4 characters. It is read twice and never shown or logged. The command uses only the database (no OIB)
and exits with 0 on success and 1 on failure. Roles are `OPERATOR`, `QA`, `ENGINEER` and `ADMIN`. Other commands are
`--console` (run in the foreground until Enter or Ctrl+C) and `--help`.

To create an API key for a third party, log in as an admin and post the key:

```
curl -s -X POST http://<host>:8090/interlock/v1/session -d "{\"username\":\"admin\",\"pin\":\"<pin>\"}"
curl -s -X POST http://<host>:8090/interlock/v1/admin/apikeys -H "Authorization: Bearer <token>" -d "{\"name\":\"mes\",\"canUnlock\":false}"
```

The answer shows the key once (`key`); only its SHA-256 hash is stored. Clients send it as `X-Api-Key: <key>`. Disable a key
with `PUT /admin/apikeys/{id}?active=false`.

## Register External Control in Factory Explorer

1. Start the service. The log shows lines like
   `External Control listening: IMetadataExchange at http://<machine>:8091/Interlock/SetupCenterExternalControl/mex`.
2. In Factory Explorer > Service Configuration, register (per line or plant-wide):
   - `SIPLACE.SetupCenter.ExternalControl` -> `http://<machine>:8091/Interlock/SetupCenterExternalControl/mex`
   - `SIPLACE.Feeder.ExternalControl` -> `http://<machine>:8091/Interlock/FeederExternalControl/mex`
3. If Setup Center needs another binding, follow the steps in the `App.config` header comment (for net.tcp the MEX addresses
   become `net.tcp://<machine>:8093/...SetupCenterExternalControl/mex` and `net.tcp://<machine>:8094/...FeederExternalControl/mex`).
   Then register again.

## Operations

- **Health:** `GET http://<host>:8090/interlock/v1/health` needs no login. It returns `dbOk`, `setupCenterConnected`, the
  licenses (null = unknown), `monitoringOk`, `queueLength`, `lockCount`, `cacheLoadedAt`, `failedLocks`,
  `cacheRefreshFailures` / `cacheRefreshError` (exception type only) and the recent `alarms`. OIB values are refreshed every 30 s.
- **Logs:** `logs\interlock-yyyyMMdd.log`. Errors carry the full exception. PINs, tokens, API keys and the connection string
  are never written.
- **Spool:** `spool\locks.jsonl` holds Setup Center mismatch locks made while the DB was down, one JSON line each. The first
  successful reload writes them to the DB and deletes the file. At start-up the spool is loaded into the cache and queue, so
  those locks are enforced even if the DB is still down. A record the DB keeps refusing (a poison record) blocks every reload:
  `cacheRefreshFailures` keeps rising and `cacheRefreshError` names the exception. Stop the service, move the bad line out of
  the file, and start it again.
- **Alarms:**
  - `Lock #<id> FAILED` means the OIB calls for that lock failed 4 times. Check OIB, then call `POST /locks/{id}/retry` (or Retry in the UI).
  - `Conveyor re-blocked: <station> (#<id>)` means the watchdog found a locked conveyor unblocked and blocked it again. Ask who unblocked it.
  - `dbOk: false` means the DB is unreachable (see below). A rising `cacheRefreshFailures` with `dbOk: true` means a poison spool record or another reload error. Read the log.
  - `setupCenterConnected: false` means OIB Setup Center is unreachable. Enforcement jobs wait and retry, and scan checks still answer.
- **Database down (spec section 9):** scan checks keep answering from the cache. Decision-log rows wait in a 10 000-row
  memory buffer (the oldest are dropped with a warning) and are written later. `POST /session`, `POST /locks`, release and
  retry return 503. Setup Center mismatch locks are still enforced in OIB and are spooled. Health shows `dbOk: false`. The
  startup reconcile, which re-queues PENDING/FAILED locks, runs after the first successful reload.
- **Stop:** `sc stop Interlock`. The service stops its timers, unsubscribes, closes REST and External Control, and writes the
  buffered decisions (about 25 s at most). Queued enforcement is not lost. It is re-queued from the DB state at the next start.

## Site checklist (verify on a real line before go-live)

From the spec (section 11):
- [ ] Which binding Setup Center uses to call External Control (MEX / Service Locator).
- [ ] `HasAdapterLicense()` is true for SetupCenter and LineControl.
- [ ] Stations run in forced setup verification (needed for `LockPackagingUnits` and Feeder External Control).
- [ ] What Setup Center does when our External Control service is down.
- [ ] `IsStationConveyorBlockSupported` is true for the target stations.
- [ ] `LockStateChanged` delivers Reason=1 on a real wrong-part scan.

OIB adapter checks (Task 13):
- [ ] 1. `LockPackagingUnits` / `UnlockPackagingUnits` / `LockOnlineFeeders` / `UnlockOnlineFeeders` return one result per item with `ResultState == 1` on success. An empty result list counts as a failure.
- [ ] 2. `LockOnlineFeeders` accepts the location copied from the mounted feeder (`Machine.LineName` is obsolete; `LinePath` is used).
- [ ] 3. Feeder ids with several PU locations: only the first location with a component location is locked. Check that this is enough.
- [ ] 4. `HasAdapterLicense()` is obsolete and returned true with a dead endpoint. Health does not treat it as connectivity. Confirm the real license value.
- [ ] 5. `IsConnected()` returns false (about 7 s) on a dead endpoint instead of throwing. Health handles both cases.
- [ ] 6. `SubscribeToCoreEvents(core, name, port)`: events arrive on the callback port under the service account (firewall, URL ACL), and `SubscriptionStateInvalid` followed by the 30 s tick recovers.
- [ ] 7. `X509RevocationMode` inside the proxies and the `ASM.SW.Service` DNS identity work against the real certificates.
- [ ] 8. `ComponentLocationState.LinePath` is filled in `GetActualSetups` answers. A blank line makes every line-scoped lock apply.
- [ ] 9. Line Control: `IsStationConveyorBlockSupported` and `BlockStationConveyor` work on a real station path from Monitoring `FullPath`.
- [ ] 10. Send timeout: a hung OIB call holds the single worker for up to `Interlock.OibSendTimeoutSeconds` (30 s by default). Confirm that is acceptable.
- [ ] 11. Re-lock and re-unlock idempotency: find which `ResultState` Setup Center returns for a PU or feeder that is already locked or unlocked. A non-1 state counts as a failure.
- [ ] 12. Forced setup verification prerequisite: see how a refused `LockPackagingUnits` looks and confirm that its message reaches the lock error.
- [ ] 13. `LockOnlineFeeders` accepts a request with only `LinePath` (no `LineName`), because the smart-proxy path may leave `LineName` null.
- [ ] 14. Secure core eventing: confirm whether the subscription works against a secure core with the 3-argument `SubscribeToCoreEvents`, or whether the secure variant is needed.
- [ ] 15. The 30 s send timeout is enough for the slowest real call (`GetActualSetups` for many machines). If not, raise `Interlock.OibSendTimeoutSeconds` (max 120).

Open items from tasks 10, 11, 14 and 15:
- [ ] Setup Center binding: the shipped http binding was never run end to end on this PC; net.tcp was. Decide the binding with Setup Center, then register the MEX URLs.
- [ ] `Messages` is null on OK External Control results (as in the OIB tutorial). Confirm Setup Center accepts that.
- [ ] Poison spool record: rehearse the manual fix (stop, edit `spool\locks.jsonl`, start) and see `cacheRefreshFailures` rise.
- [ ] Stale mismatch re-spooled while the DB is down: the last release time cannot be read then, so an old Reason=1 entry for a recently released PU may lock it again. Check the effect on a real line during a DB outage.
- [ ] Conveyor watchdog: after a manual unblock, the re-block happens within 60 s and the alarm appears in the UI. FAILED conveyor locks are not watched.
- [ ] Setup Center behaviour when the service is down (spec section 9). Note the result in the operating instructions.
- [ ] License: with one license missing, health shows it and only that function fails.
- [ ] Forced setup verification and secure-core eventing (see checks 12 and 14).
- [ ] Run the integration tests against the site test database before go-live: `INTERLOCK_TEST_DB=<connection string> dotnet test Interlock/tests/Interlock.Tests --filter "TestCategory=Integration"`.

## Known limitations

- REST is plain HTTP by default (see the security warning). HTTPS needs a configuration change and a certificate.
- Sessions live in memory: a service restart logs every UI user out.
- Login throttling: after 5 wrong PINs in 60 s, the username or the source address is blocked for 60 s. Users behind a shared NAT or kiosk can block each other.
- If the service stops while the DB is down, decision-log rows still in the buffer are lost (a warning gives the count).
- A Setup Center notice that arrives while the service stops, or while the 1000-entry escalation queue is full, is dropped with a warning.
- Locks are never time-limited or auto-expiring; notifications (email, LINE) are not part of this release.
- Line paths match exactly (no parent/child hierarchy). Compare them with the site's real line names.
- A poison spool record blocks the cache reload until someone edits the file by hand.
- The schema script does not migrate changed definitions (see Database).
- Only one OIB call runs at a time (one worker). A slow or hung call delays the other locks by up to the send timeout.
- Startup reconcile re-queues PENDING/FAILED work from the DB. Jobs queued in memory before a crash are rebuilt only from that state.
- The `/health` endpoint needs no login. It shows lock ids, station paths and exception type names, but never hosts or credentials.
