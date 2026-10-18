# Interlock Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows Service plus a WinForms UI that lock feeder / PU UID / PN / MPN / Supplier / conveyor on SIPLACE lines, enforce the locks through OIB, and offer one REST API for the UI and third parties.

**Architecture:** `Interlock.Core` holds the pure rules (decisions, validation, permissions, enforcement worker) behind port interfaces. `Interlock.Service` hosts WCF External Control (called by Setup Center), WCF WebHttp REST, the OIB smart-proxy adapters and the Npgsql repositories. `Interlock.Ui` is a WinForms client of the REST API. The existing collector (`Form1.cs`) is not touched.

**Tech Stack:** .NET Framework 4.8, C# 7.3, SDK-style csproj built with `dotnet` 9 SDK, WCF (`System.ServiceModel`, `System.ServiceModel.Web`), Npgsql 4.1.13, Serilog 2.12.0 + Serilog.Sinks.File 5.0.0, NUnit 3.14.0 + NUnit3TestAdapter 4.5.0 + Microsoft.NET.Test.Sdk 17.8.0, OIB smart proxies from `Interfaces/Smart Proxies/`.

**Spec:** `docs/superpowers/specs/2026-10-18-interlock-design.md` (decisions D1–D11). OIB API notes: `docs/interlock/research-notes.md`.

## Global Constraints

- Target `net48` everywhere. `LangVersion` 7.3 (no records, no switch expressions, no nullable reference types).
- All new code lives under `Interlock/` at the repo root. Do not modify `Form1.cs`, `Monitoring Tutorial.csproj` or `Tutorial.sln`.
- `Interlock.Core` must not reference OIB DLLs, Npgsql, WCF or WinForms.
- External Control callbacks must never call `ISiplaceSetupCenter` / `ISetupCenterClient`. Follow-up work goes to `ActionQueue`.
- Normalization: `upper(trim(x))`. A null or blank value never matches.
- DB values for enums are upper-case text: lock_type `FEEDER, PU, PN, MPN, SUPPLIER, CONVEYOR`; source `MANUAL, API, SETUPCENTER`; status `ACTIVE, RELEASED`; enforce_state `PENDING, APPLIED, FAILED, NOT_NEEDED`; lanes `LEFT, RIGHT, BOTH`; role `OPERATOR, QA, ENGINEER, ADMIN`.
- Scan-check message, exact: `Interlock: <TYPE> <target> locked – <reason> (#<id>)` (en dash), several matches joined by `; `.
- Retry delays after failures 1/2/3: 5 s, 30 s, 2 min; after the 4th failed call → FAILED.
- Cache reload every 30 s; conveyor watchdog every 60 s; UI refresh every 5 s; session lifetime 8 h; decision-log buffer 10 000 rows.
- PIN hash format: `pbkdf2-sha256$100000$<saltBase64>$<hashBase64>` (16-byte salt, 32-byte hash). API key hash: lower-case hex SHA-256.
- All timestamps are stored and sent as UTC; DTO dates are ISO 8601 strings from `DateTime.ToString("o")`.
- Commit after each task with a `feat:`/`test:`/`chore:` message and the `Co-Authored-By` trailer the session uses.

## Review Focus

1. Barcode scanners add `\r`, `\n` or `\t` around the scanned value. A target typed as `" abc123\r\n"` must match UID `ABC123`. → Task 1 (`Normalizer`) and Task 2 (DecisionEngine) tests.
2. Setup Center sends a batch where one location has `PackagingUnit == null` or an empty UID. The call must not throw; that element gets `ResultState = 1` and the rest are still checked. → Task 14 test.
3. After QA releases an escalated PU lock, Setup Center still lists the old Reason=1 `LockInfo` on later `LockStateChanged` events. That stale entry must not re-lock the PU, but a new mismatch after the release must. → Task 10 tests (`EscalationFilter`).
4. UI and API create the same lock at the same moment. The unique index fires; the loser must get the existing lock (`Created == false`), not an error. → Task 8 test.
5. The service restarts while a release is still queued, leaving a RELEASED lock with `enforce_state = PENDING`, so the PU would stay locked in OIB for ever. Startup reconcile must queue a Release job for it. → Task 11 test.

---

## File Structure

```
Interlock/
  Interlock.sln
  Directory.Build.props                 net48, LangVersion 7.3, shared OIB path property
  README.md                             deployment + site checklist (Task 16)
  db/001_interlock_schema.sql           schema DDL (Task 12)
  src/Interlock.Core/
    Model/        Enums.cs, DbEnum.cs, LockRecord.cs, PuInfo.cs, Decision.cs, LogEntries.cs, Users.cs, Refs.cs
    Text/Normalizer.cs
    Decisions/DecisionEngine.cs
    Locks/CreateLockRequest.cs, LockValidator.cs, LockCache.cs, LockService.cs
    Errors/InterlockException.cs, RepositoryExceptions.cs
    Security/Principal.cs, Permissions.cs, PinHasher.cs, ApiKeyHasher.cs, SessionStore.cs, AuthService.cs
    Enforcement/EnforcementJob.cs, ActionQueue.cs, RetryPolicy.cs, EnforcementWorker.cs, EscalationFilter.cs,
                ConveyorWatchdog.cs, CacheRefresher.cs, Reconciler.cs, DecisionLogBuffer.cs, AlarmBoard.cs
    Ports/        ILockRepository.cs, IAuditRepository.cs, IUserRepository.cs, ISetupCenterClient.cs,
                  ILineControlClient.cs, IMonitoringClient.cs, IStationDirectory.cs, ILockSpool.cs, IClock.cs, ILog.cs
    Api/Dtos.cs, DtoMapper.cs
  src/Interlock.Service/
    Program.cs, InterlockWindowsService.cs, Composition.cs, ServiceSettings.cs, SerilogLog.cs, App.config
    Data/NpgsqlDb.cs, NpgsqlLockRepository.cs, NpgsqlAuditRepository.cs, NpgsqlUserRepository.cs, JsonlLockSpool.cs
    Oib/OibEndpoints.cs, SetupCenterClient.cs, SetupCenterMapper.cs, SetupCenterEventSubscriber.cs,
        LineControlClient.cs, MonitoringClient.cs, StationDirectory.cs
    ExternalControl/SetupCenterExternalControlService.cs, FeederExternalControlService.cs,
                    ExternalControlMapper.cs, ExternalControlHost.cs
    Rest/IInterlockRestApi.cs, InterlockRestApi.cs, RestErrors.cs, RestHost.cs
    Health/HealthMonitor.cs
    (linked) ../../../Interfaces/WCF Proxies (C#)/proxySetupCenter.cs
  src/Interlock.Ui/
    Program.cs, App.config, ApiClient.cs, Session.cs, RowStyle.cs,
    LoginForm.cs, MainForm.cs, NewLockForm.cs, ReasonForm.cs, HistoryForm.cs, AdminForm.cs
  tests/Interlock.Tests/
    Core/*.cs, Service/*.cs, Integration/*.cs, Fakes/*.cs
```

Commands (run from the repo root):
- Build: `dotnet build Interlock/Interlock.sln`
- Unit tests: `dotnet test Interlock/tests/Interlock.Tests --filter "TestCategory!=Integration"`
- One fixture: `dotnet test Interlock/tests/Interlock.Tests --filter "FullyQualifiedName~<FixtureName>"`

---

### Task 1: Solution scaffold, core model, normalizer

**Files:**
- Create: `Interlock/Interlock.sln`, `Interlock/Directory.Build.props`, `Interlock/.gitignore` (`bin/`, `obj/`, `*.user`)
- Create: `Interlock/src/Interlock.Core/Interlock.Core.csproj`, `Model/Enums.cs`, `Model/DbEnum.cs`, `Model/LockRecord.cs`, `Text/Normalizer.cs`
- Create: `Interlock/tests/Interlock.Tests/Interlock.Tests.csproj`, `Core/NormalizerTests.cs`, `Core/DbEnumTests.cs`

**Interfaces:**
- Produces:
  - `enum LockType { Feeder, Pu, Pn, Mpn, Supplier, Conveyor }`, `enum LockStatus { Active, Released }`, `enum LockSource { Manual, Api, SetupCenter }`, `enum EnforceState { Pending, Applied, Failed, NotNeeded }`, `enum Lanes { Left, Right, Both }`, `enum Role { Operator, Qa, Engineer, Admin }` (namespace `Interlock.Core.Model`)
  - `static class DbEnum`: `string ToDb(<each enum>)`, `LockType ParseLockType(string)`, `LockStatus ParseStatus(string)`, `LockSource ParseSource(string)`, `EnforceState ParseEnforceState(string)`, `Lanes ParseLanes(string)`, `Role ParseRole(string)`. `NotNeeded` ↔ `NOT_NEEDED`, `SetupCenter` ↔ `SETUPCENTER`. Parse is case-insensitive and throws `FormatException` on unknown text.
  - `sealed class LockRecord` with public get/set properties: `long Id, LockType Type, string Target, string TargetNorm, Lanes? Lanes, string LinePath, string Reason, LockSource Source, string CreatedBy, DateTime CreatedAtUtc, LockStatus Status, string ReleasedBy, DateTime? ReleasedAtUtc, string ReleaseReason, EnforceState EnforceState, string EnforceError`, plus `LockRecord Clone()` (shallow copy). `Id == 0` means "not stored yet" (spooled).
  - `static class Normalizer { string Norm(string value) }` → `null` for null/blank, else `value.Trim().ToUpperInvariant()`.

- [ ] **Step 1: Create the solution and projects**

`Directory.Build.props` sets `TargetFramework=net48`, `LangVersion=7.3`, `OibDir=$(MSBuildThisFileDirectory)..\Interfaces\Smart Proxies\`. Test csproj references NUnit 3.14.0, NUnit3TestAdapter 4.5.0, Microsoft.NET.Test.Sdk 17.8.0 and `Interlock.Core`. Add both projects to `Interlock.sln` (`dotnet new sln`, `dotnet sln add`).

- [ ] **Step 2: Write the failing tests**

```csharp
[TestFixture] public class NormalizerTests {
  [TestCase(" abc123\r\n", "ABC123")] [TestCase("\tMpn-1 ", "MPN-1")] [TestCase("x", "X")]
  public void Norm_trims_and_uppercases(string input, string expected) => Assert.That(Normalizer.Norm(input), Is.EqualTo(expected));
  [TestCase(null)] [TestCase("")] [TestCase("  \r\n")]
  public void Norm_blank_is_null(string input) => Assert.That(Normalizer.Norm(input), Is.Null);
}
[TestFixture] public class DbEnumTests {
  [Test] public void Round_trips_every_value() { foreach (LockType t in Enum.GetValues(typeof(LockType))) Assert.That(DbEnum.ParseLockType(DbEnum.ToDb(t)), Is.EqualTo(t)); /* same loop for each enum */ }
  [Test] public void Special_names() { Assert.That(DbEnum.ToDb(EnforceState.NotNeeded), Is.EqualTo("NOT_NEEDED")); Assert.That(DbEnum.ToDb(LockSource.SetupCenter), Is.EqualTo("SETUPCENTER")); Assert.That(DbEnum.ToDb(LockType.Pu), Is.EqualTo("PU")); }
  [Test] public void Parse_is_case_insensitive() => Assert.That(DbEnum.ParseLanes("both"), Is.EqualTo(Lanes.Both));
  [Test] public void Parse_unknown_throws() => Assert.Throws<FormatException>(() => DbEnum.ParseLockType("REEL"));
}
```

- [ ] **Step 3: Run tests, expect compile failure** — `dotnet test Interlock/tests/Interlock.Tests` → FAIL (types missing).
- [ ] **Step 4: Implement the enums, `DbEnum`, `LockRecord`, `Normalizer`** as listed in Interfaces.
- [ ] **Step 5: Run tests** — same command → PASS.
- [ ] **Step 6: Commit** — `git add Interlock && git commit -m "chore: scaffold interlock solution and core model"`

---

### Task 2: DecisionEngine (scan-check rules)

**Files:**
- Create: `Interlock/src/Interlock.Core/Model/PuInfo.cs`, `Model/Decision.cs`, `Decisions/DecisionEngine.cs`
- Test: `Interlock/tests/Interlock.Tests/Core/DecisionEngineTests.cs`

**Interfaces:**
- Consumes: `LockRecord`, `LockType`, `Normalizer`, `DbEnum` (Task 1)
- Produces:
  - `sealed class PuInfo { string Uid, ComponentName, Mpn, Supplier, LinePath }`
  - `sealed class Decision { bool Ok; string Message; IList<long> MatchedLockIds }`
  - `static class DecisionEngine`:
    - `bool Matches(LockRecord l, PuInfo pu)`: PU → UID; PN → ComponentName; MPN → Mpn; SUPPLIER → Supplier; FEEDER/CONVEYOR → false. Values are compared with `Normalizer.Norm`, and a null side never matches. PN/MPN/SUPPLIER are in scope when `l.LinePath` is null, or when the PU's `LinePath` is blank (spec §6 rule 4), or when both line paths are equal ignoring case.
    - `Decision Decide(PuInfo pu, IReadOnlyCollection<LockRecord> active)`: only `Status == Active` locks; `Ok = no match`; `Message` = matches in `Id` order formatted with `FormatMessage` and joined by `"; "`; `Message = null` when OK.
    - `string FormatMessage(LockRecord l)` → `$"Interlock: {DbEnum.ToDb(l.Type)} {l.Target} locked – {l.Reason} (#{l.Id})"`
    - `LockRecord FindFeederLock(string feederId, IReadOnlyCollection<LockRecord> active)` → the lowest-Id ACTIVE FEEDER lock whose `TargetNorm` equals `Norm(feederId)`, else null.

- [ ] **Step 1: Write the failing tests**

Helper `L(id, type, target, line=null)` builds an ACTIVE `LockRecord` with `TargetNorm = Normalizer.Norm(target)` and `Reason = "r" + id`.

```csharp
[Test] public void Pu_lock_matches_uid_after_scanner_whitespace() {
  var d = DecisionEngine.Decide(new PuInfo { Uid = " uid-1\r\n" }, new[] { L(7, LockType.Pu, "UID-1") });
  Assert.That(d.Ok, Is.False); Assert.That(d.MatchedLockIds, Is.EqualTo(new long[] { 7 }));
  Assert.That(d.Message, Is.EqualTo("Interlock: PU UID-1 locked – r7 (#7)")); }
[Test] public void Pn_mpn_supplier_match_their_own_fields() { /* PN on ComponentName, MPN on Mpn, SUPPLIER on Supplier; a PN lock with value equal to Mpn does NOT match */ }
[Test] public void Line_scoped_lock_ignores_other_line() =>
  Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1", LinePath = "Lines\\L2" }, new[] { L(1, LockType.Pn, "C1", "Lines\\L1") }).Ok, Is.True);
[Test] public void Line_scoped_lock_applies_when_pu_line_is_blank() =>
  Assert.That(DecisionEngine.Decide(new PuInfo { ComponentName = "C1", LinePath = "" }, new[] { L(1, LockType.Pn, "C1", "Lines\\L1") }).Ok, Is.False);
[Test] public void Plant_wide_lock_applies_everywhere() { /* LinePath null on lock, PU on any line → NotOk */ }
[Test] public void Null_fields_never_match() => Assert.That(DecisionEngine.Decide(new PuInfo(), new[] { L(1, LockType.Mpn, "X") }).Ok, Is.True);
[Test] public void Several_matches_joined_in_id_order() =>
  Assert.That(DecisionEngine.Decide(new PuInfo { Uid = "U", ComponentName = "C" }, new[] { L(9, LockType.Pn, "C"), L(3, LockType.Pu, "U") }).Message,
    Is.EqualTo("Interlock: PU U locked – r3 (#3); Interlock: PN C locked – r9 (#9)"));
[Test] public void Released_locks_are_ignored() { /* Status = Released → Ok */ }
[Test] public void Feeder_lock_found_by_id() { Assert.That(DecisionEngine.FindFeederLock(" f-01 ", new[] { L(4, LockType.Feeder, "F-01") }).Id, Is.EqualTo(4)); Assert.That(DecisionEngine.FindFeederLock("F-02", new[] { L(4, LockType.Feeder, "F-01") }), Is.Null); }
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~DecisionEngineTests` → FAIL (missing types).
- [ ] **Step 3: Implement `PuInfo`, `Decision`, `DecisionEngine`** per Interfaces.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `feat: add interlock decision engine`

---

### Task 3: Errors, lock validation, lock cache

**Files:**
- Create: `Interlock/src/Interlock.Core/Errors/InterlockException.cs`, `Locks/CreateLockRequest.cs`, `Locks/LockValidator.cs`, `Locks/LockCache.cs`, `Model/Refs.cs`
- Test: `Interlock/tests/Interlock.Tests/Core/LockValidatorTests.cs`, `Core/LockCacheTests.cs`

**Interfaces:**
- Produces:
  - `enum ErrorKind { Validation, Unauthenticated, Forbidden, NotFound, InvalidState, Unavailable }`; `sealed class InterlockException : Exception { ErrorKind Kind; ctor(ErrorKind kind, string message) }`
  - `sealed class CreateLockRequest { LockType? Type; string Target; Lanes? Lanes; string LinePath; string Reason }`
  - `sealed class StationRef { string LinePath; string StationPath }` (in `Model/Refs.cs`)
  - `static class LockValidator { void Validate(CreateLockRequest r, IList<StationRef> knownStations) }` — throws `InterlockException(Validation, …)` when: `Type` is null; `Target` is blank; `Reason` is blank (whitespace only counts as blank); `Lanes` is null for CONVEYOR; `Lanes` is set for a non-CONVEYOR type; `LinePath` is set for a type other than PN/MPN/SUPPLIER; or a CONVEYOR target is not a known `StationPath` (case-insensitive). When `knownStations` is empty and the type is CONVEYOR, it throws `InterlockException(Unavailable, "Station list not available")`.
  - `sealed class LockCache`: `IReadOnlyCollection<LockRecord> Snapshot { get; }` (an immutable array swapped atomically); `DateTime? LoadedAtUtc { get; }`; `void Replace(IEnumerable<LockRecord> active, DateTime nowUtc)`; `void Upsert(LockRecord l)` (replaces the entry with the same `Id`; when `Id == 0`, replaces the entry with the same type + TargetNorm + LinePath + Lanes); `void Remove(long id)`; `LockRecord FindActive(LockType t, string targetNorm, string linePath, Lanes? lanes)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// LockValidatorTests — one [TestCase] per rule above, asserting Kind == ErrorKind.Validation:
[Test] public void Blank_reason_rejected() {
  var ex = Assert.Throws<InterlockException>(() => LockValidator.Validate(new CreateLockRequest { Type = LockType.Pu, Target = "U1", Reason = "   " }, Stations()));
  Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Validation)); }
[Test] public void Conveyor_requires_known_station() { /* Target "Lines\\L1\\S9" not in list → Validation */ }
[Test] public void Conveyor_with_empty_station_list_is_unavailable() { /* knownStations empty → Unavailable */ }
[Test] public void Valid_requests_pass() { /* PU, PN with LinePath, CONVEYOR with lanes and known station (different case) → no throw */ }
// LockCacheTests
[Test] public void Replace_then_snapshot_contains_only_new_set() { cache.Replace(new[] { L(1), L(2) }, t0); cache.Replace(new[] { L(3) }, t1); Assert.That(cache.Snapshot.Select(l => l.Id), Is.EqualTo(new long[] { 3 })); Assert.That(cache.LoadedAtUtc, Is.EqualTo(t1)); }
[Test] public void Upsert_spooled_entry_replaced_by_identity_not_id() { /* Upsert Id 0 PU "U1", Upsert Id 0 PU "u1 " (same norm) → Snapshot.Count == 1 */ }
[Test] public void Remove_by_id() { cache.Replace(new[] { L(1), L(2) }, t0); cache.Remove(1); Assert.That(cache.Snapshot.Single().Id, Is.EqualTo(2)); }
[Test] public void Snapshot_is_stable_while_writer_changes_cache() { var s = cache.Snapshot; cache.Upsert(L(2)); Assert.That(s.Count, Is.EqualTo(1)); }
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~LockValidatorTests|FullyQualifiedName~LockCacheTests"` → FAIL.
- [ ] **Step 3: Implement** per Interfaces. `LockCache` uses a `lock` around writes and `Volatile.Write` of a new `LockRecord[]`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `feat: add lock validation and cache`

---

### Task 4: Security primitives

**Files:**
- Create: `Interlock/src/Interlock.Core/Security/Principal.cs`, `Permissions.cs`, `PinHasher.cs`, `ApiKeyHasher.cs`, `SessionStore.cs`, `Ports/IClock.cs`
- Test: `Interlock/tests/Interlock.Tests/Core/SecurityTests.cs`, `Fakes/FakeClock.cs`

**Interfaces:**
- Produces:
  - `interface IClock { DateTime UtcNow { get; } }`; `sealed class SystemClock : IClock`
  - `sealed class Principal { string Name; Role? Role; bool IsApiKey; bool CanUnlock }` — static factories `ForUser(string name, Role role)` and `ForApiKey(string name, bool canUnlock)`.
  - `static class Permissions { bool CanLock(Principal p); bool CanRelease(Principal p); bool CanAdmin(Principal p) }` — lock: any principal; release: `Role ∈ {Qa, Engineer, Admin}` or `IsApiKey && CanUnlock`; admin: `Role == Admin` only (API keys never).
  - `static class PinHasher { string Hash(string pin); bool Verify(string pin, string stored) }` — `Rfc2898DeriveBytes(pin, salt, 100000, HashAlgorithmName.SHA256)`, with the format from Global Constraints. `Verify` returns false for a malformed `stored`.
  - `static class ApiKeyHasher { string NewKey(); string Hash(string key) }` — `NewKey` returns 64 lower-case hex chars from `RandomNumberGenerator`.
  - `sealed class SessionStore { ctor(IClock clock, TimeSpan lifetime); string Create(Principal p); Principal Resolve(string token); DateTime ExpiresAtUtc(string token) }` — the token is 64 hex chars; `Resolve` returns null for unknown or expired tokens and removes expired ones.
  - `Fakes/FakeClock : IClock` with a settable `UtcNow` and `Advance(TimeSpan)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Pin_hash_verifies_and_has_format() { var h = PinHasher.Hash("1234"); StringAssert.StartsWith("pbkdf2-sha256$100000$", h); Assert.That(PinHasher.Verify("1234", h), Is.True); Assert.That(PinHasher.Verify("1235", h), Is.False); }
[Test] public void Pin_hash_is_salted() => Assert.That(PinHasher.Hash("1234"), Is.Not.EqualTo(PinHasher.Hash("1234")));
[Test] public void Malformed_hash_does_not_verify() => Assert.That(PinHasher.Verify("1234", "garbage"), Is.False);
[Test] public void Api_key_hash_is_sha256_hex() => Assert.That(ApiKeyHasher.Hash("abc"), Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
[TestCase(Role.Operator, false)] [TestCase(Role.Qa, true)] [TestCase(Role.Engineer, true)] [TestCase(Role.Admin, true)]
public void Release_permission_by_role(Role r, bool expected) => Assert.That(Permissions.CanRelease(Principal.ForUser("u", r)), Is.EqualTo(expected));
[Test] public void Api_key_release_needs_flag_and_never_admin() { Assert.That(Permissions.CanRelease(Principal.ForApiKey("mes", false)), Is.False); Assert.That(Permissions.CanRelease(Principal.ForApiKey("mes", true)), Is.True); Assert.That(Permissions.CanAdmin(Principal.ForApiKey("mes", true)), Is.False); }
[Test] public void Session_expires_after_8_hours() { var clock = new FakeClock(); var s = new SessionStore(clock, TimeSpan.FromHours(8)); var t = s.Create(Principal.ForUser("a", Role.Qa)); clock.Advance(TimeSpan.FromHours(7.9)); Assert.That(s.Resolve(t).Name, Is.EqualTo("a")); clock.Advance(TimeSpan.FromHours(0.2)); Assert.That(s.Resolve(t), Is.Null); }
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~SecurityTests` → FAIL.
- [ ] **Step 3: Implement** per Interfaces.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `feat: add security primitives`

---

### Task 5: Ports, fakes, action queue, retry policy

**Files:**
- Create: `Interlock/src/Interlock.Core/Ports/*.cs` (all port files in File Structure except `IClock.cs`), `Model/LogEntries.cs`, `Model/Users.cs`, `Errors/RepositoryExceptions.cs`, `Enforcement/EnforcementJob.cs`, `Enforcement/ActionQueue.cs`, `Enforcement/RetryPolicy.cs`, `Enforcement/AlarmBoard.cs`
- Create: `Interlock/tests/Interlock.Tests/Fakes/InMemoryLockRepository.cs`, `InMemoryAuditRepository.cs`, `InMemoryUserRepository.cs`, `FakeSetupCenterClient.cs`, `FakeLineControlClient.cs`, `FakeStationDirectory.cs`, `MemorySpool.cs`, `ListLog.cs`
- Test: `Interlock/tests/Interlock.Tests/Core/ActionQueueTests.cs`, `Core/RetryPolicyTests.cs`

**Interfaces:**
- Produces (namespace `Interlock.Core.Ports` unless noted):
  - `ILog { void Info(string m); void Warn(string m); void Error(string m, Exception ex) }`
  - `ILockRepository`: `IList<LockRecord> LoadActive(); IList<LockRecord> LoadPendingReleases(); LockRecord Insert(LockRecord l); LockRecord FindActive(LockType t, string targetNorm, string linePath, Lanes? lanes); LockRecord Get(long id); IList<LockRecord> Query(LockQuery q); void MarkReleased(long id, string by, DateTime atUtc, string reason); void SetEnforceState(long id, EnforceState s, string error); void AddAppliedPu(long lockId, string uid, DateTime atUtc); IList<string> GetOpenAppliedPus(long lockId); void CloseAppliedPu(long lockId, string uid, DateTime atUtc); bool IsUidCoveredByOtherActiveLock(string uidNorm, long exceptLockId); DateTime? GetLatestReleaseUtc(LockType t, string targetNorm)`. `Insert` returns the stored row with its `Id` and throws `DuplicateActiveLockException` on the unique index. `MarkReleased` also sets `enforce_state = PENDING`. `LoadPendingReleases` = RELEASED rows with `enforce_state = PENDING`.
  - `sealed class LockQuery { LockStatus? Status; LockType? Type; string LinePath; string Text; EnforceState? EnforceState; int Limit = 500 }` (Text = case-insensitive substring of target or reason)
  - `IAuditRepository`: `void AddAction(ActionLogEntry e); IList<ActionLogEntry> GetActions(long lockId); void AddDecisions(IList<DecisionLogEntry> e); IList<DecisionLogEntry> QueryDecisions(DecisionQuery q)`
  - `IUserRepository`: `AppUser FindUser(string username); IList<AppUser> ListUsers(); void SaveUser(AppUser u); ApiKeyRecord FindApiKeyByHash(string keyHash); IList<ApiKeyRecord> ListApiKeys(); ApiKeyRecord InsertApiKey(ApiKeyRecord k); void SetApiKeyActive(long id, bool active)`
  - `ISetupCenterClient`: `bool IsConnected(); bool HasLicense(); void LockPackagingUnits(IList<string> uids, string reason); void UnlockPackagingUnits(IList<string> uids); FeederMount FindMountedFeeder(string feederId); void LockOnlineFeeder(FeederMount m, string reason); void UnlockOnlineFeeder(FeederMount m); IList<MachineRef> GetMachines(); IList<MountedPu> GetMountedPus(IList<string> machineIds); IList<string> GetLinePaths()` — failures throw `OibCallException`.
  - `ILineControlClient`: `bool HasLicense(); bool IsBlockSupported(string stationPath); void Block(string stationPath, Lanes lanes, string details); void Unblock(string stationPath, Lanes lanes, string details); ConveyorState GetState(string stationPath)`
  - `IMonitoringClient { IList<StationRef> GetStations() }`; `IStationDirectory { IList<StationRef> Stations { get; } IList<string> LinePaths { get; } }`
  - `ILockSpool { void Append(LockRecord l); IList<LockRecord> ReadAll(); void Clear() }`
  - Model (`Interlock.Core.Model`): `ActionLogEntry { long Id; long? LockId; string Action; string RequestJson; bool Success; string Error; int Attempt; DateTime CreatedAtUtc }`; `DecisionLogEntry { string Method; int? ReasonCode; string Uid, Pn, Mpn, Supplier, LinePath, MachineId, FeederId; string Result; string Message; string MatchedLockIds; DateTime CreatedAtUtc }`; `DecisionQuery { string Uid; string Pn; DateTime? FromUtc; DateTime? ToUtc; int Limit = 500 }`; `AppUser { long Id; string Username; string PinHash; Role Role; bool Active; DateTime CreatedAtUtc }`; `ApiKeyRecord { long Id; string Name; string KeyHash; bool CanUnlock; bool Active; DateTime CreatedAtUtc }`; `MachineRef { string MachineId; string LinePath }`; `MountedPu { PuInfo Pu; string MachineId }`; `FeederMount { string FeederId; string MachineId; string LinePath; object Native }`; `ConveyorState { bool RightBlocked; bool LeftBlocked; bool IsBlocked(Lanes l) }` (Both → both blocked)
  - Errors: `DbUnavailableException : Exception`, `DuplicateActiveLockException : Exception`, `OibCallException : Exception`
  - Action names (`static class Actions`): `LOCK_PU, UNLOCK_PU, LOCK_FEEDER_ONLINE, UNLOCK_FEEDER_ONLINE, BLOCK_CONVEYOR, UNBLOCK_CONVEYOR, SWEEP, REBLOCK_CONVEYOR`
  - `enum JobKind { Apply, Release, Reblock }`; `sealed class EnforcementJob { LockRecord Lock; JobKind Kind; int FailedAttempts; DateTime NotBeforeUtc }`
  - `sealed class ActionQueue { void Enqueue(EnforcementJob j); IList<EnforcementJob> TakeDue(DateTime nowUtc); void Cancel(long lockId); int Count { get; } }` — thread-safe; `TakeDue` removes and returns due jobs ordered by `NotBeforeUtc`, then by insertion order; `Cancel` drops every queued job for that lock id (0 is never cancelled).
  - `static class RetryPolicy { TimeSpan? DelayAfterFailure(int failedAttempts) }` → 1: 5 s, 2: 30 s, 3: 2 min, ≥4: null.
  - `sealed class AlarmBoard { void Raise(string text, DateTime atUtc); IList<string> Recent(int max) }` (keeps the last 50, newest first, formatted as `"<ISO time> <text>"`)
  - Fakes:
    - In-memory repositories implement every member above. `Insert` enforces the uniqueness rule. Each has `bool Down`, which makes every member throw `DbUnavailableException`. Test helpers `InsertActive(LockRecord)` and `InsertReleased(LockRecord)` store a row as given, bypassing the rules. `InMemoryAuditRepository` exposes `List<ActionLogEntry> Actions` and `List<DecisionLogEntry> Decisions`.
    - `FakeSetupCenterClient` records **every** call in `List<string> Calls` as `"<Method>"` or `"<Method>:<comma-joined ids>"` (uids, feeder id, or machine ids), for example `"GetMachines"`, `"GetMountedPus:m1"`, `"FindMountedFeeder:F1"`, `"LockOnlineFeeder:F1"`, `"LockPackagingUnits:U1,U2"`. It also has `bool ThrowAlways` (every write call records itself and then throws `OibCallException`), `Dictionary<string, FeederMount> Mounted`, `List<MachineRef> Machines`, and `List<MountedPu> MountedPus`.
    - `FakeLineControlClient` has `Dictionary<string, ConveyorState> States`, `bool Supported = true`, `List<string> Calls` (`"Block:<path>:<LANES>"` etc.), and `bool ThrowAlways`.
    - `ListLog : ILog` has `List<string> Infos`, `Warnings`, `Errors`.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestCase(1, 5)] [TestCase(2, 30)] [TestCase(3, 120)]
public void Delay_after_failure(int n, int seconds) => Assert.That(RetryPolicy.DelayAfterFailure(n), Is.EqualTo(TimeSpan.FromSeconds(seconds)));
[Test] public void No_retry_after_fourth_failure() => Assert.That(RetryPolicy.DelayAfterFailure(4), Is.Null);
[Test] public void TakeDue_returns_only_due_jobs_in_order() { /* enqueue at t+10s, t+0, t+0 → TakeDue(t) returns the two t+0 jobs in insertion order; Count == 1 */ }
[Test] public void Cancel_removes_jobs_for_lock() { /* two jobs lock 5, one lock 6 → Cancel(5) → Count == 1 */ }
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ActionQueueTests|FullyQualifiedName~RetryPolicyTests"` → FAIL.
- [ ] **Step 3: Implement** the ports, model, errors, queue, retry policy, alarm board and fakes.
- [ ] **Step 4: Run** → PASS, and `dotnet build Interlock/Interlock.sln` → 0 errors.
- [ ] **Step 5: Commit** — `feat: add ports, action queue and retry policy`

---

### Task 6: EnforcementWorker — PU, FEEDER, CONVEYOR

**Files:**
- Create: `Interlock/src/Interlock.Core/Enforcement/EnforcementWorker.cs`
- Test: `Interlock/tests/Interlock.Tests/Core/EnforcementWorkerTests.cs`

**Interfaces:**
- Consumes: Task 5 ports, `ActionQueue`, `RetryPolicy`, `AlarmBoard`, `LockCache`
- Produces: `sealed class EnforcementWorker { ctor(ActionQueue queue, ILockRepository locks, IAuditRepository audit, ISetupCenterClient sc, ILineControlClient lc, LockCache cache, AlarmBoard alarms, IClock clock, ILog log); void RunDue() }`

Behaviour of each job (`RunDue` takes `queue.TakeDue(clock.UtcNow)` and runs them one by one):

| Lock type | Apply | Release | Reblock |
|---|---|---|---|
| PU | `LockPackagingUnits([Target], Reason)` → APPLIED | if `!locks.IsUidCoveredByOtherActiveLock(TargetNorm, Id)` → `UnlockPackagingUnits([Target])`; → APPLIED | — |
| FEEDER | `FindMountedFeeder(Target)`; null → NOT_NEEDED; else `LockOnlineFeeder(m, Reason)` → APPLIED | `FindMountedFeeder`; if mounted → `UnlockOnlineFeeder(m)`; → APPLIED | — |
| CONVEYOR | `IsBlockSupported(Target)` false → FAILED with error `"Conveyor block not supported"` (no retry); else `Block(Target, Lanes, Reason)` → APPLIED | `Unblock(Target, Lanes, Reason)` → APPLIED | `Block(...)`, action `REBLOCK_CONVEYOR`, `alarms.Raise("Conveyor re-blocked: <Target> (#<Id>)")` |

- Every OIB call writes one `ActionLogEntry` (`Action` from `Actions`, `RequestJson` = `{"target":…,"lanes":…}`, `Success`, `Error`, `Attempt = FailedAttempts + 1`).
- On an `OibCallException` or other exception: `FailedAttempts++`; if `RetryPolicy.DelayAfterFailure(FailedAttempts)` has a value, re-enqueue with `NotBeforeUtc = now + delay`; else set FAILED with the error and `alarms.Raise("Lock #<Id> FAILED: <error>")`.
- State changes go to both `locks.SetEnforceState` and the cached copy (`cache.Upsert` for ACTIVE locks). When `Lock.Id == 0`, skip repository writes and log a warning.
- If `audit`/`locks` throw `DbUnavailableException`, log it and carry on; the OIB result stands.

- [ ] **Step 1: Write the failing tests** (fixture builds the worker with the fakes and a `FakeClock`)

```csharp
[Test] public void Apply_pu_locks_and_marks_applied() { Enqueue(Pu(1, "U1"), JobKind.Apply); worker.RunDue();
  Assert.That(sc.Calls, Is.EqualTo(new[] { "LockPackagingUnits:U1" })); Assert.That(repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied)); Assert.That(audit.Actions.Single().Action, Is.EqualTo("LOCK_PU")); }
[Test] public void Failure_retries_at_5s_30s_2min_then_failed() { sc.ThrowAlways = true; // Calls counts the 4 LockPackagingUnits attempts Enqueue(Pu(1,"U1"), JobKind.Apply);
  worker.RunDue(); clock.Advance(TimeSpan.FromSeconds(4)); worker.RunDue(); Assert.That(sc.Calls.Count, Is.EqualTo(1));
  clock.Advance(TimeSpan.FromSeconds(1)); worker.RunDue(); clock.Advance(TimeSpan.FromSeconds(30)); worker.RunDue(); clock.Advance(TimeSpan.FromMinutes(2)); worker.RunDue();
  Assert.That(sc.Calls.Count, Is.EqualTo(4)); Assert.That(repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Failed)); Assert.That(queue.Count, Is.EqualTo(0)); Assert.That(alarms.Recent(1)[0], Does.Contain("Lock #1 FAILED")); }
[Test] public void Release_pu_skipped_when_other_lock_covers_uid() { /* repo has ACTIVE PU lock #2 "U1"; release job for #1 "U1" → no Unlock call; #1 APPLIED */ }
[Test] public void Feeder_not_mounted_is_not_needed() { Enqueue(Feeder(1, "F1"), JobKind.Apply); worker.RunDue(); Assert.That(sc.Calls, Is.EqualTo(new[] { "FindMountedFeeder:F1" })); Assert.That(repo.Get(1).EnforceState, Is.EqualTo(EnforceState.NotNeeded)); }
[Test] public void Feeder_mounted_is_locked_online() { sc.Mounted["F1"] = new FeederMount { FeederId = "F1" }; Enqueue(Feeder(1, "F1"), JobKind.Apply); worker.RunDue(); Assert.That(sc.Calls, Is.EqualTo(new[] { "FindMountedFeeder:F1", "LockOnlineFeeder:F1" })); Assert.That(repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied)); }
[Test] public void Conveyor_not_supported_fails_without_retry() { lc.Supported = false; … Assert.That(repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Failed)); Assert.That(queue.Count, Is.EqualTo(0)); }
[Test] public void Reblock_blocks_logs_and_raises_alarm() { Enqueue(Conv(1, "S1", Lanes.Both), JobKind.Reblock); worker.RunDue();
  Assert.That(lc.Calls, Is.EqualTo(new[] { "Block:S1:BOTH" })); Assert.That(audit.Actions.Single().Action, Is.EqualTo("REBLOCK_CONVEYOR")); Assert.That(alarms.Recent(1)[0], Does.Contain("Conveyor re-blocked: S1 (#1)")); }
[Test] public void Db_down_does_not_stop_enforcement() { repo.Down = true; audit.Down = true; Enqueue(Pu(1,"U1"), JobKind.Apply); Assert.DoesNotThrow(() => worker.RunDue()); Assert.That(sc.Calls.Count, Is.EqualTo(1)); }
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~EnforcementWorkerTests` → FAIL.
- [ ] **Step 3: Implement `EnforcementWorker`** per the table and rules.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `feat: enforce PU, feeder and conveyor locks`

---

### Task 7: EnforcementWorker — PN / MPN / SUPPLIER sweep and overlap guard

**Files:**
- Modify: `Interlock/src/Interlock.Core/Enforcement/EnforcementWorker.cs`
- Test: `Interlock/tests/Interlock.Tests/Core/SweepTests.cs`

**Interfaces:**
- Consumes: `DecisionEngine.Matches` (Task 2), `ISetupCenterClient.GetMachines/GetMountedPus` and `ILockRepository.AddAppliedPu/GetOpenAppliedPus/CloseAppliedPu/IsUidCoveredByOtherActiveLock` (Task 5)
- Produces: no new public API. PN/MPN/SUPPLIER jobs are handled inside `EnforcementWorker`.

Rules:
- **Apply:** machines = `GetMachines()` filtered to `LinePath` equal to the lock's `LinePath` (ignoring case), or all machines when the lock's `LinePath` is null. pus = `GetMountedPus(machineIds)` where `DecisionEngine.Matches(lock, pu.Pu)`. Distinct UIDs (by `Normalizer.Norm`) → one `LockPackagingUnits(uids, Reason)` call when there is any, then `AddAppliedPu(lock.Id, uid, now)` for each. Write one `SWEEP` action with `RequestJson` = `{"machines":n,"matched":[uids]}` → APPLIED (also when nothing matched).
- **Release:** for each `GetOpenAppliedPus(lock.Id)`: unlock only the UIDs where `!IsUidCoveredByOtherActiveLock(Norm(uid), lock.Id)` (one `UnlockPackagingUnits` call for all of them), then `CloseAppliedPu` for **every** open UID → APPLIED.
- A failure retries the whole job, following Task 6's retry rule.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Sweep_locks_matching_mounted_pus_in_scope() {
  sc.Machines.AddRange(new[] { M("m1", "Lines\\L1"), M("m2", "Lines\\L2") });
  sc.MountedPus.AddRange(new[] { Mounted("m1", uid: "A", pn: "C1"), Mounted("m1", uid: "B", pn: "C2"), Mounted("m2", uid: "C", pn: "C1") });
  Enqueue(Pn(1, "C1", line: "Lines\\L1"), JobKind.Apply); worker.RunDue();
  Assert.That(sc.Calls, Is.EqualTo(new[] { "GetMachines", "GetMountedPus:m1", "LockPackagingUnits:A" })); Assert.That(repo.GetOpenAppliedPus(1), Is.EqualTo(new[] { "A" })); }
[Test] public void Sweep_with_no_match_is_applied_without_lock_call() { sc.Machines.Add(M("m1", "Lines\\L1")); sc.MountedPus.Add(Mounted("m1", uid: "B", pn: "C2")); Enqueue(Pn(1, "C1"), JobKind.Apply); worker.RunDue();
  Assert.That(sc.Calls.Any(c => c.StartsWith("LockPackagingUnits")), Is.False); Assert.That(repo.Get(1).EnforceState, Is.EqualTo(EnforceState.Applied)); Assert.That(audit.Actions.Single().Action, Is.EqualTo("SWEEP")); }
[Test] public void Release_unlocks_only_uncovered_uids_and_closes_all() {
  repo.AddAppliedPu(1, "A", t); repo.AddAppliedPu(1, "B", t); repo.InsertActive(Pu(2, "B"));   // B still covered by PU lock #2
  Enqueue(Released(Pn(1, "C1")), JobKind.Release); worker.RunDue();
  Assert.That(sc.Calls, Is.EqualTo(new[] { "UnlockPackagingUnits:A" })); Assert.That(repo.GetOpenAppliedPus(1), Is.Empty); }
[Test] public void Mpn_and_supplier_sweep_use_their_fields() { /* mounted A(mpn "M1"), B(supplier "S1"), C(pn "M1"); MPN lock "M1" locks only A; SUPPLIER lock "S1" locks only B */ }
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~SweepTests` → FAIL.
- [ ] **Step 3: Implement** the PN/MPN/SUPPLIER branches.
- [ ] **Step 4: Run** `--filter "FullyQualifiedName~SweepTests|FullyQualifiedName~EnforcementWorkerTests"` → PASS.
- [ ] **Step 5: Commit** — `feat: sweep mounted PUs for PN/MPN/supplier locks`

---

### Task 8: LockService — create, release, retry, query

**Files:**
- Create: `Interlock/src/Interlock.Core/Locks/LockService.cs`
- Test: `Interlock/tests/Interlock.Tests/Core/LockServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 3–5
- Produces:
  - `sealed class CreateLockResult { LockRecord Lock; bool Created }`; `sealed class LockDetail { LockRecord Lock; IList<ActionLogEntry> Actions }`
  - `sealed class LockService { ctor(ILockRepository locks, IAuditRepository audit, LockCache cache, ActionQueue queue, IStationDirectory stations, ILockSpool spool, IClock clock, ILog log)`
    - `CreateLockResult CreateLock(Principal p, CreateLockRequest r)`: validate (Task 3). Set `Source = p.IsApiKey ? Api : Manual`, `CreatedBy = p.Name`, `TargetNorm = Norm(Target)`, `Target = Target.Trim()`, `LinePath` trimmed or null, `Status = Active`, `EnforceState = Pending`. If the cache or `locks.FindActive` has an identical ACTIVE lock → return it with `Created = false`. Otherwise `Insert`; on `DuplicateActiveLockException`, re-read with `FindActive` and return it with `Created = false`. After a new insert: `cache.Upsert`, then enqueue an Apply job. On `DbUnavailableException` → `InterlockException(Unavailable, "Database unavailable")`.
    - `LockRecord Release(Principal p, long id, string reason)`: no `CanRelease` → Forbidden; blank reason → Validation; not found → NotFound; already RELEASED → InvalidState. Then `MarkReleased`, `cache.Remove(id)`, `queue.Cancel(id)`, enqueue a Release job, and return the updated row. DB down → Unavailable.
    - `LockRecord Retry(Principal p, long id)`: not FAILED → InvalidState. ACTIVE → needs `CanLock` and enqueues Apply; RELEASED → needs `CanRelease` and enqueues Release. Sets PENDING first.
    - `IList<LockRecord> Query(Principal p, LockQuery q)` and `LockDetail Get(Principal p, long id)` (NotFound when missing). DB down → Unavailable.
    - `internal LockRecord CreateFromSetupCenter(string uid, string reason)` — used in Task 10.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Create_inserts_caches_and_queues_apply() { var r = svc.CreateLock(Qa, PuReq("U1")); Assert.That(r.Created, Is.True); Assert.That(cache.Snapshot.Single().Id, Is.EqualTo(r.Lock.Id)); Assert.That(queue.TakeDue(clock.UtcNow).Single().Kind, Is.EqualTo(JobKind.Apply)); Assert.That(r.Lock.Source, Is.EqualTo(LockSource.Manual)); }
[Test] public void Api_key_create_has_api_source() { var r = svc.CreateLock(Principal.ForApiKey("mes", false), PuReq("U1")); Assert.That(r.Lock.Source, Is.EqualTo(LockSource.Api)); Assert.That(r.Lock.CreatedBy, Is.EqualTo("mes")); }
[Test] public void Identical_active_lock_returns_existing() { var a = svc.CreateLock(Qa, PuReq("U1")); var b = svc.CreateLock(Qa, PuReq(" u1 ")); Assert.That(b.Created, Is.False); Assert.That(b.Lock.Id, Is.EqualTo(a.Lock.Id)); }
[Test] public void Concurrent_duplicate_insert_returns_existing() { repo.FailNextInsertWithDuplicate(existing: Pu(9, "U1")); var r = svc.CreateLock(Qa, PuReq("U1")); Assert.That(r.Created, Is.False); Assert.That(r.Lock.Id, Is.EqualTo(9)); }
[Test] public void Operator_cannot_release() => Assert.That(Assert.Throws<InterlockException>(() => svc.Release(Op, id, "ok")).Kind, Is.EqualTo(ErrorKind.Forbidden));
[Test] public void Release_marks_removes_cancels_and_queues_release() { var id = svc.CreateLock(Qa, PuReq("U1")).Lock.Id; var r = svc.Release(Qa, id, "ok");
  Assert.That(r.Status, Is.EqualTo(LockStatus.Released)); Assert.That(r.ReleasedBy, Is.EqualTo("qa1")); Assert.That(r.EnforceState, Is.EqualTo(EnforceState.Pending)); Assert.That(cache.Snapshot, Is.Empty);
  Assert.That(queue.TakeDue(clock.UtcNow).Select(j => j.Kind), Is.EqualTo(new[] { JobKind.Release })); } // the Apply job was cancelled
[Test] public void Release_twice_is_invalid_state() { svc.Release(Qa, id, "ok"); Assert.That(Assert.Throws<InterlockException>(() => svc.Release(Qa, id, "ok")).Kind, Is.EqualTo(ErrorKind.InvalidState)); }
[Test] public void Retry_only_failed() { Assert.That(Assert.Throws<InterlockException>(() => svc.Retry(Qa, id)).Kind, Is.EqualTo(ErrorKind.InvalidState)); repo.SetEnforceState(id, EnforceState.Failed, "x"); Assert.That(svc.Retry(Op, id).EnforceState, Is.EqualTo(EnforceState.Pending)); }
[Test] public void Retry_of_released_failed_needs_release_permission() { /* released + FAILED → Retry(Op) Forbidden; Retry(Qa) queues Release */ }
[Test] public void Db_down_create_is_unavailable() { repo.Down = true; Assert.That(Assert.Throws<InterlockException>(() => svc.CreateLock(Qa, PuReq("U1"))).Kind, Is.EqualTo(ErrorKind.Unavailable)); }
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~LockServiceTests` → FAIL.
- [ ] **Step 3: Implement `LockService`** per Interfaces. Add `FailNextInsertWithDuplicate` to `InMemoryLockRepository`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `feat: add lock service`

---

### Task 9: AuthService — login, authenticate, admin

**Files:**
- Create: `Interlock/src/Interlock.Core/Security/AuthService.cs`
- Test: `Interlock/tests/Interlock.Tests/Core/AuthServiceTests.cs`

**Interfaces:**
- Consumes: `IUserRepository`, `SessionStore`, `PinHasher`, `ApiKeyHasher`, `Permissions` (Tasks 4–5)
- Produces: `sealed class LoginResult { string Token; Role Role; DateTime ExpiresAtUtc }`, `sealed class ApiKeyCreated { ApiKeyRecord Record; string PlainKey }`, and `sealed class AuthService { ctor(IUserRepository users, SessionStore sessions, IClock clock)`:
  - `LoginResult Login(string username, string pin)`: unknown user, inactive user, or a wrong PIN → `InterlockException(Unauthenticated, "Invalid username or PIN")` (the same text in all three cases). The username lookup ignores case.
  - `Principal Authenticate(string bearerToken, string apiKey)`: if a bearer token is present, it must resolve; otherwise an API key hash must match an active key; otherwise Unauthenticated.
  - `IList<AppUser> ListUsers(Principal p)`; `void SaveUser(Principal p, string username, Role role, bool active, string pinOrNull)` — a new user needs a PIN of at least 4 characters (Validation); a null PIN keeps the existing hash.
  - `ApiKeyCreated CreateApiKey(Principal p, string name, bool canUnlock)`; `IList<ApiKeyRecord> ListApiKeys(Principal p)`; `void SetApiKeyActive(Principal p, long id, bool active)` — every admin call needs `CanAdmin`, otherwise Forbidden.
  - `void CreateInitialUser(string username, Role role, string pin)` — no principal check. Only the service's `--add-user` console command calls it (Task 16).

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Login_ok_returns_token_and_role() { users.SaveUser(User("qa1", Role.Qa, PinHasher.Hash("1234"))); var r = auth.Login("QA1", "1234"); Assert.That(r.Role, Is.EqualTo(Role.Qa)); Assert.That(auth.Authenticate(r.Token, null).Name, Is.EqualTo("qa1")); }
[TestCase("qa1", "9999")] [TestCase("nobody", "1234")]
public void Login_failures_share_message(string u, string pin) { var ex = Assert.Throws<InterlockException>(() => auth.Login(u, pin)); Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Unauthenticated)); Assert.That(ex.Message, Is.EqualTo("Invalid username or PIN")); }
[Test] public void Inactive_user_cannot_login() { users.SaveUser(User("old", Role.Qa, PinHasher.Hash("1234"), active: false)); Assert.That(Assert.Throws<InterlockException>(() => auth.Login("old", "1234")).Message, Is.EqualTo("Invalid username or PIN")); }
[Test] public void Initial_user_can_log_in() { auth.CreateInitialUser("admin", Role.Admin, "4321"); Assert.That(auth.Login("admin", "4321").Role, Is.EqualTo(Role.Admin)); }
[Test] public void Api_key_authenticates_until_deactivated() { var k = auth.CreateApiKey(Admin, "mes", true); Assert.That(auth.Authenticate(null, k.PlainKey).CanUnlock, Is.True); auth.SetApiKeyActive(Admin, k.Record.Id, false); Assert.Throws<InterlockException>(() => auth.Authenticate(null, k.PlainKey)); }
[Test] public void Non_admin_cannot_manage_users() => Assert.That(Assert.Throws<InterlockException>(() => auth.ListUsers(Qa)).Kind, Is.EqualTo(ErrorKind.Forbidden));
[Test] public void No_credentials_is_unauthenticated() => Assert.That(Assert.Throws<InterlockException>(() => auth.Authenticate(null, null)).Kind, Is.EqualTo(ErrorKind.Unauthenticated));
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~AuthServiceTests` → FAIL.
- [ ] **Step 3: Implement `AuthService`.**
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `feat: add auth service`

---

### Task 10: Setup Center escalation, lock spool, cache refresher

**Files:**
- Create: `Interlock/src/Interlock.Core/Enforcement/EscalationFilter.cs`, `Enforcement/CacheRefresher.cs`
- Modify: `Interlock/src/Interlock.Core/Locks/LockService.cs` (add `OnLockStateChanged`)
- Create: `Interlock/src/Interlock.Service/Interlock.Service.csproj` (Exe, references Core; Npgsql 4.1.13, Serilog 2.12.0, Serilog.Sinks.File 5.0.0; framework refs `System.ServiceModel`, `System.ServiceModel.Web`, `System.ServiceProcess`, `System.Runtime.Serialization`, `System.Configuration`), `Data/JsonlLockSpool.cs`
- Test: `Interlock/tests/Interlock.Tests/Core/EscalationTests.cs`, `Core/CacheRefresherTests.cs`, `Service/JsonlLockSpoolTests.cs` (the test project now also references Interlock.Service)

**Interfaces:**
- Produces:
  - `sealed class LockInfoRef { int Source; int Reason; DateTime DateUtc; string Message }`; `sealed class PuLockReport { string Uid; IList<LockInfoRef> LockInfos }`
  - `static class EscalationFilter { LockInfoRef FindNewMismatch(PuLockReport r, DateTime? lastReleaseUtc) }` → the newest `LockInfo` with `Reason == 1` whose `DateUtc > lastReleaseUtc` (or any `Reason == 1` when `lastReleaseUtc` is null); null otherwise.
  - `LockService.OnLockStateChanged(IList<PuLockReport> reports)`: for each report with a non-blank UID, skip it when the cache already has an ACTIVE PU lock for that UID. Otherwise `lastRelease = locks.GetLatestReleaseUtc(Pu, Norm(uid))` (null on `DbUnavailableException`), then `FindNewMismatch`. On a hit, create a PU lock with `Source = SetupCenter`, `CreatedBy = "SetupCenter"`, and `Reason = "Setup Center component mismatch"` plus `": " + Message` when the message is not blank. If the DB is down: `spool.Append(lock with Id 0)`, `cache.Upsert`, enqueue Apply, and log a warning. It never throws to the caller.
  - `sealed class CacheRefresher { ctor(ILockRepository locks, ILockSpool spool, LockCache cache, IClock clock, ILog log); bool DbHealthy { get; } void Tick() }` — `Tick`: insert each spooled lock (skip it on `DuplicateActiveLockException`), `spool.Clear()`, then `cache.Replace(locks.LoadActive(), now)`, `DbHealthy = true`. On `DbUnavailableException`, set `DbHealthy = false` and keep the cache and the spool.
  - `sealed class JsonlLockSpool : ILockSpool { ctor(string path) }` — one `DataContractJsonSerializer` JSON object per line (UTF-8). `ReadAll` skips blank lines; `Clear` deletes the file.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Mismatch_escalates_once() { svc.OnLockStateChanged(new[] { Report("U1", Info(1, t0)) }); svc.OnLockStateChanged(new[] { Report("U1", Info(1, t0)) });
  Assert.That(repo.LoadActive().Count(l => l.Type == LockType.Pu && l.Source == LockSource.SetupCenter), Is.EqualTo(1)); }
// helper: Info(int reason, DateTime date, int source = 0)
[Test] public void Own_locks_do_not_escalate() => Assert.That(EscalationFilter.FindNewMismatch(Report("U1", Info(0, t0, source: 4), Info(28, t0, source: 9)), null), Is.Null);
[Test] public void Stale_mismatch_after_release_is_ignored() => Assert.That(EscalationFilter.FindNewMismatch(Report("U1", Info(1, t0)), lastReleaseUtc: t0.AddMinutes(5)), Is.Null);
[Test] public void New_mismatch_after_release_escalates() => Assert.That(EscalationFilter.FindNewMismatch(Report("U1", Info(1, t0.AddMinutes(10))), t0.AddMinutes(5)), Is.Not.Null);
[Test] public void Db_down_spools_caches_and_queues() { repo.Down = true; svc.OnLockStateChanged(new[] { Report("U1", Info(1, t0)) });
  Assert.That(spool.ReadAll().Single().TargetNorm, Is.EqualTo("U1")); Assert.That(cache.Snapshot.Single().Id, Is.EqualTo(0)); Assert.That(queue.Count, Is.EqualTo(1)); }
[Test] public void Refresher_flushes_spool_then_reloads() { spool.Append(Pu(0, "U1")); refresher.Tick(); Assert.That(spool.ReadAll(), Is.Empty); Assert.That(cache.Snapshot.Single().Id, Is.GreaterThan(0)); Assert.That(refresher.DbHealthy, Is.True); }
[Test] public void Refresher_keeps_cache_when_db_down() { cache.Replace(new[] { Pu(5, "A") }, t0); spool.Append(Pu(0, "U1")); repo.Down = true; refresher.Tick();
  Assert.That(refresher.DbHealthy, Is.False); Assert.That(cache.Snapshot.Single().Id, Is.EqualTo(5)); Assert.That(spool.ReadAll().Count, Is.EqualTo(1)); }
[Test] public void Jsonl_spool_round_trip() { var s = new JsonlLockSpool(Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".jsonl")); s.Append(Pu(0, "U1")); s.Append(Pu(0, "U2")); Assert.That(s.ReadAll().Select(l => l.Target), Is.EqualTo(new[] { "U1", "U2" })); s.Clear(); Assert.That(s.ReadAll(), Is.Empty); }
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~EscalationTests|FullyQualifiedName~CacheRefresherTests|FullyQualifiedName~JsonlLockSpoolTests"` → FAIL.
- [ ] **Step 3: Implement** `EscalationFilter`, `LockService.OnLockStateChanged`, `CacheRefresher`, the Service csproj (empty `Program.Main` for now) and `JsonlLockSpool`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `feat: escalate Setup Center mismatches and spool locks when DB is down`

---

### Task 11: Startup reconcile, conveyor watchdog, decision-log buffer

**Files:**
- Create: `Interlock/src/Interlock.Core/Enforcement/Reconciler.cs`, `Enforcement/ConveyorWatchdog.cs`, `Enforcement/DecisionLogBuffer.cs`
- Test: `Interlock/tests/Interlock.Tests/Core/BackgroundTests.cs`

**Interfaces:**
- Produces:
  - `static class Reconciler { int QueueOnStartup(ILockRepository locks, ActionQueue queue, IClock clock) }` — Apply jobs for ACTIVE locks with `EnforceState ∈ {Pending, Failed}`, Release jobs for `LoadPendingReleases()`. Returns the number of jobs queued.
  - `sealed class ConveyorWatchdog { ctor(LockCache cache, ILineControlClient lc, ActionQueue queue, IClock clock, ILog log); void Tick() }` — for each ACTIVE CONVEYOR lock in `cache.Snapshot` with `EnforceState == Applied`: `GetState(Target)`; if `!IsBlocked(Lanes.Value)` → enqueue a Reblock job. A `GetState` exception → log a warning and continue with the next lock.
  - `sealed class DecisionLogBuffer { ctor(int capacity, ILog log); void Add(DecisionLogEntry e); int Flush(IAuditRepository audit); int Count { get; } }` — when full, drops the oldest entry and logs one warning per overflow episode (reset after a successful flush). `Flush` writes batches of at most 500 and returns the number written. On `DbUnavailableException` it keeps the batch and returns 0.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Reconcile_queues_pending_failed_and_pending_releases() { repo.InsertActive(Pu(1,"A", EnforceState.Pending)); repo.InsertActive(Pu(2,"B", EnforceState.Failed)); repo.InsertActive(Pu(3,"C", EnforceState.Applied)); repo.InsertReleased(Pu(4,"D", EnforceState.Pending));
  Assert.That(Reconciler.QueueOnStartup(repo, queue, clock), Is.EqualTo(3)); var jobs = queue.TakeDue(clock.UtcNow);
  Assert.That(jobs.Where(j => j.Kind == JobKind.Apply).Select(j => j.Lock.Id), Is.EquivalentTo(new long[] { 1, 2 })); Assert.That(jobs.Single(j => j.Kind == JobKind.Release).Lock.Id, Is.EqualTo(4)); }
[Test] public void Watchdog_queues_reblock_when_conveyor_unblocked() { cache.Replace(new[] { Conv(1, "S1", Lanes.Both, EnforceState.Applied) }, t0); lc.States["S1"] = new ConveyorState { RightBlocked = true, LeftBlocked = false }; watchdog.Tick(); Assert.That(queue.TakeDue(clock.UtcNow).Single().Kind, Is.EqualTo(JobKind.Reblock)); }
[Test] public void Watchdog_ignores_blocked_and_unapplied() { cache.Replace(new[] { Conv(1, "S1", Lanes.Left, EnforceState.Applied), Conv(2, "S2", Lanes.Both, EnforceState.Pending) }, t0);
  lc.States["S1"] = new ConveyorState { LeftBlocked = true }; lc.States["S2"] = new ConveyorState(); watchdog.Tick(); Assert.That(queue.Count, Is.EqualTo(0)); }
[Test] public void Watchdog_survives_get_state_error() { lc.ThrowAlways = true; cache.Replace(new[] { Conv(1, "S1", Lanes.Both, EnforceState.Applied) }, t0); Assert.DoesNotThrow(() => watchdog.Tick()); Assert.That(log.Warnings.Count, Is.EqualTo(1)); }
[Test] public void Buffer_drops_oldest_when_full() { var b = new DecisionLogBuffer(3, log); for (int i = 0; i < 5; i++) b.Add(Entry(i)); Assert.That(b.Count, Is.EqualTo(3)); b.Flush(audit); Assert.That(audit.Decisions.Select(d => d.Uid), Is.EqualTo(new[] { "2", "3", "4" })); Assert.That(log.Warnings.Count, Is.EqualTo(1)); }
[Test] public void Buffer_keeps_rows_when_db_down() { audit.Down = true; b.Add(Entry(1)); Assert.That(b.Flush(audit), Is.EqualTo(0)); Assert.That(b.Count, Is.EqualTo(1)); }
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~BackgroundTests` → FAIL.
- [ ] **Step 3: Implement** the three classes.
- [ ] **Step 4: Run** the full unit suite → PASS.
- [ ] **Step 5: Commit** — `feat: add startup reconcile, conveyor watchdog and decision buffer`

---

### Task 12: Database schema and Npgsql repositories

**Files:**
- Create: `Interlock/db/001_interlock_schema.sql`
- Create: `Interlock/src/Interlock.Service/Data/NpgsqlDb.cs`, `NpgsqlLockRepository.cs`, `NpgsqlAuditRepository.cs`, `NpgsqlUserRepository.cs`
- Test: `Interlock/tests/Interlock.Tests/Integration/RepositoryTests.cs` (`[Category("Integration")]`)

**Interfaces:**
- Consumes: the port interfaces from Task 5
- Produces: `sealed class NpgsqlDb { ctor(string connectionString, string schema); NpgsqlConnection Open() }` (it maps `NpgsqlException`/`SocketException`/`TimeoutException` on open or execute to `DbUnavailableException`), and `NpgsqlLockRepository(NpgsqlDb db)`, `NpgsqlAuditRepository(NpgsqlDb db)`, `NpgsqlUserRepository(NpgsqlDb db)` implementing the ports.

Schema rules (the columns are those in spec §4):
- The script starts with `CREATE SCHEMA IF NOT EXISTS interlock;` and qualifies every object with `interlock.`. The tests run it with `interlock.` replaced by the test schema name.
- `CHECK` constraints for every enum column, using the values in Global Constraints.
- `CREATE UNIQUE INDEX lock_active_uq ON interlock.lock (lock_type, target_norm, coalesce(line_path,''), coalesce(lanes,'')) WHERE status = 'ACTIVE';`
- Indexes: `lock(status)`, `lock_applied_pu(lock_id) WHERE released_at IS NULL`, `lock_applied_pu(upper(uid)) WHERE released_at IS NULL`, `action_log(lock_id)`, `decision_log(created_at)`, `decision_log(upper(uid))`.
- All timestamps are `timestamptz`. Repositories write `DateTime` with `Kind = Utc` and read back as UTC.
- A unique-violation `PostgresException` (SqlState `23505`) on `lock_active_uq` → `DuplicateActiveLockException`.
- The schema name is validated against `^[a-z_][a-z0-9_]*$` before it is put into SQL. Every value is a parameter.

- [ ] **Step 1: Write the failing integration tests**

The fixture reads env `INTERLOCK_TEST_DB` (a connection string to a **non-production** database) and calls `Assert.Ignore` when it is missing. In `[OneTimeSetUp]` it drops and recreates schema `interlock_test` from the script. `[SetUp]` truncates all tables.

```csharp
[Test] public void Insert_and_load_active_round_trip() { var l = repo.Insert(Pu(0, "U1")); Assert.That(l.Id, Is.GreaterThan(0)); var back = repo.LoadActive().Single(); Assert.That(back.TargetNorm, Is.EqualTo("U1")); Assert.That(back.CreatedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc)); }
[Test] public void Duplicate_active_throws_duplicate_exception() { repo.Insert(Pu(0, "U1")); Assert.Throws<DuplicateActiveLockException>(() => repo.Insert(Pu(0, "U1"))); }
[Test] public void Released_allows_new_active() { var a = repo.Insert(Pu(0, "U1")); repo.MarkReleased(a.Id, "qa1", t, "ok"); Assert.DoesNotThrow(() => repo.Insert(Pu(0, "U1"))); Assert.That(repo.GetLatestReleaseUtc(LockType.Pu, "U1"), Is.EqualTo(t).Within(TimeSpan.FromMilliseconds(1))); }
[Test] public void Mark_released_sets_pending_and_load_pending_releases() { var a = repo.Insert(Pu(0, "U1")); repo.SetEnforceState(a.Id, EnforceState.Applied, null); repo.MarkReleased(a.Id, "qa1", t, "ok");
  Assert.That(repo.LoadPendingReleases().Single().Id, Is.EqualTo(a.Id)); repo.SetEnforceState(a.Id, EnforceState.Applied, null); Assert.That(repo.LoadPendingReleases(), Is.Empty); }
[Test] public void Applied_pu_coverage() { var pn = repo.Insert(Pn(0, "C1")); repo.AddAppliedPu(pn.Id, "a1", t); Assert.That(repo.IsUidCoveredByOtherActiveLock("A1", exceptLockId: 999), Is.True); Assert.That(repo.IsUidCoveredByOtherActiveLock("A1", pn.Id), Is.False); repo.CloseAppliedPu(pn.Id, "a1", t); Assert.That(repo.IsUidCoveredByOtherActiveLock("A1", 999), Is.False); }
[Test] public void Query_filters_by_status_type_line_text() { /* rows: PU U1 active, PN C1 on L1 active, PN C2 released; Query(Status=Active,Type=Pn) → [C1]; Query(LinePath="Lines\\L1") → [C1]; Query(Text="u1") → [U1] */ }
[Test] public void Audit_and_users_round_trip() { /* AddAction → GetActions(lockId) equal fields; AddDecisions 2 rows → QueryDecisions(Uid="u1") returns the U1 row only; SaveUser/FindUser("QA1") ignores case; InsertApiKey → FindApiKeyByHash; SetApiKeyActive(false) → Active false */ }
// separate fixture NpgsqlDbTests without the Integration category (no DB needed):
[Test] public void Unreachable_server_maps_to_db_unavailable() { var bad = new NpgsqlLockRepository(new NpgsqlDb("Host=127.0.0.1;Port=1;Timeout=2;Username=x;Password=x;Database=x", "interlock_test")); Assert.Throws<DbUnavailableException>(() => bad.LoadActive()); }
```

- [ ] **Step 2: Run** `set INTERLOCK_TEST_DB=...` then `dotnet test Interlock/tests/Interlock.Tests --filter TestCategory=Integration` → FAIL (missing classes). Without the env var → tests reported Ignored.
- [ ] **Step 3: Write the SQL script and implement the repositories.**
- [ ] **Step 4: Run** the integration tests → PASS, and `--filter FullyQualifiedName~NpgsqlDbTests` → PASS without the env var.
- [ ] **Step 5: Commit** — `feat: add interlock schema and Npgsql repositories`

---

### Task 13: OIB adapters and mappers

**Files:**
- Create: `Interlock/src/Interlock.Service/Oib/OibEndpoints.cs`, `SetupCenterMapper.cs`, `SetupCenterClient.cs`, `SetupCenterEventSubscriber.cs`, `LineControlClient.cs`, `MonitoringClient.cs`, `StationDirectory.cs`
- Modify: `Interlock/src/Interlock.Service/Interlock.Service.csproj` — `<Reference>` with `HintPath` `$(OibDir)SetupCenter\` for `Asm.As.Oib.Common`, `Asm.As.Oib.Common.Proxy`, `Asm.As.Oib.WS.Common`, `Asm.As.Oib.WS.Eventing.Contracts`, `Asm.As.Oib.SIPLACESetupCenter.Contracts`, `Asm.As.Oib.SiplaceSetupCenter.Proxy`; `$(OibDir)SiplacePro.LineControl\` for `Asm.As.Oib.SIPLACEPro.LineControl.Contracts`, `Asm.As.Oib.SIPLACEPro.LineControl.Proxy`; `$(OibDir)Monitoring\` for `Asm.As.Oib.Monitoring.Contracts`, `Asm.As.Oib.Monitoring.Proxy`.
- Test: `Interlock/tests/Interlock.Tests/Service/SetupCenterMapperTests.cs`

**Interfaces:**
- Consumes: the `ISetupCenterClient`, `ILineControlClient`, `IMonitoringClient`, `IStationDirectory` ports and `PuLockReport` (Task 10)
- Produces:
  - `static class OibEndpoints { EndpointAddress SecureAddress(string uri); Binding SecureBinding(string uri) }` — the `Form1.GetMonitoringAdapters` pattern: `EndpointIdentity.CreateDnsIdentity("ASM.SW.Service")`, `new BindingParameters(uri, true)`, `EndpointHelper.CreateBindingFromParameters`. Each client sets `RevocationMode = X509RevocationMode.NoCheck` on its channel credentials where the proxy exposes them.
  - `static class SetupCenterMapper` (pure, works on `Asm.As.Oib.SiplaceSetupCenter.Contracts.Data` types):
    - `PuInfo ToPuInfo(PackagingUnit pu, string linePath)` (UID, ComponentName, ManufacturePartNumber → Mpn, Supplier)
    - `IList<MachineRef> ToMachines(Configuration c)` (`LineConfigurations[].Machines[]` → MachineId + LinePath, falling back to the line's `LinePath` when the machine's is empty)
    - `IList<MountedPu> ToMountedPus(Dictionary<string, MachineSetup> setups)` (every `Tables[].ComponentLocationStates[]` with a non-null `PackagingUnit` and a non-blank UID; the LinePath comes from the state)
    - `FeederMount ToFeederMount(string feederId, GetPackagingUnitLocationsOnFeedersResponse r)` (null when the id is not in `PackagingUnitLocationsOnFeeders`; `Native` = that `PackagingUnitLocation`)
    - `LockFeederLocationInfo ToLockInfo(FeederMount m, string reason)` (copy `Machine, Location, Device, Track, FeederId, FeederType` plus every other settable property shared with `ComponentLocation`, using reflection over matching property names)
    - `FeederLocation ToFeederLocation(FeederMount m)` (the same copy rule)
    - `IList<PuLockReport> ToReports(LockStateChangedReportRequest r)` (`Resource.PackagingUnits[]` → UID + `LockInfos` (Source, Reason, `Date.ToUniversalTime()`, Message))
    - `void ThrowIfFailed(string action, IEnumerable<int> resultStates, IEnumerable<string> messages)` → `OibCallException` when any state != 1, with the messages joined by `"; "`
  - `sealed class ReconnectBackoff { ctor(IClock clock); bool CanTry { get; } void Failed(); void Succeeded() }` — after failures 1, 2, 3, … it waits 5 s, 10 s, 20 s, … capped at 5 min (spec §3.2). `Succeeded` resets it.
  - `SetupCenterClient : ISetupCenterClient, IDisposable { ctor(string endpointUri, IClock clock) }` — lazily creates `new SiplaceSetupCenter(address, binding)`. After a communication exception it drops the proxy and calls `backoff.Failed()`. While `!backoff.CanTry`, calls throw `OibCallException("Setup Center not connected")` without touching the network. Every exception is wrapped in `OibCallException`. `LineControlClient` and `MonitoringClient` use the same backoff. Calls: `LockPackagingUnits(List<PackagingUnit>{UID}, reason)` / `UnlockPackagingUnits`, then `ThrowIfFailed` on `Results[].ResultState`; `GetPackagingUnitLocationsOnFeeders(new … { FeederIds = [id] })`; `LockOnlineFeeders(new LockOnlineFeedersRequest { LockFeederLocationInfos = [..] })` with `ThrowIfFailed` on `LockOnlineFeedersResults`; `UnlockOnlineFeeders(new UnlockOnlineFeedersRequest { FeederLocations = [..] })`; `GetConfiguration()`; `GetActualSetups(machineIds)`; `GetLinePaths()` = distinct `LineConfigurations[].LinePath`.
  - `SetupCenterEventSubscriber : IDisposable { ctor(string endpointUri, string coreHost, string callbackName, int callbackPort, Action<IList<PuLockReport>> onReports, ILog log); void Start(); void Stop() }` — `SubscribeToCoreEvents(coreHost, callbackName, callbackPort)`, attaches `LockStateChanged += r => onReports(SetupCenterMapper.ToReports(r))` (exceptions logged, never thrown back), and re-subscribes on `SubscriptionStateInvalid`.
  - `LineControlClient : ILineControlClient { ctor(string endpointUri, IClock clock) }` — maps `Lanes` → `ConveyorLanes` (Right=0, Left=1, Both=2). `Block`/`Unblock` throw `OibCallException` when the proxy returns false. `GetState` uses `IsStationConveyorBlocked(path, out right, out left)`.
  - `MonitoringClient : IMonitoringClient { ctor(string endpointUri, IClock clock) }` — `MonitoringManager.GetOrCreateInstance(address, binding).GetConfiguration().Lines[].Stations[]` → `StationRef { LinePath = line.FullPath, StationPath = station.FullPath }`.
  - `StationDirectory : IStationDirectory { ctor(IMonitoringClient m, ISetupCenterClient sc, ILog log); void Refresh() }` — keeps the last good lists when a refresh fails.

- [ ] **Step 1: Write the failing mapper tests** (they build OIB contract objects directly)

```csharp
[Test] public void Mounted_pus_skip_empty_locations() { var setups = new Dictionary<string, MachineSetup> { ["m1"] = Setup("m1", State("A", "C1", "Lines\\L1"), State(null), StateWithUid(" ")) };
  var r = SetupCenterMapper.ToMountedPus(setups); Assert.That(r.Single().Pu.Uid, Is.EqualTo("A")); Assert.That(r.Single().Pu.LinePath, Is.EqualTo("Lines\\L1")); Assert.That(r.Single().MachineId, Is.EqualTo("m1")); }
[Test] public void Lock_info_copies_location_fields() { var loc = new PackagingUnitLocation { ComponentLocation = new ComponentLocation { FeederId = "F1", FeederType = 7, Track = 3, Location = 2, Device = 1, Machine = new MachineLocator { MachineId = "m1", LineName = "L1" } } };
  var info = SetupCenterMapper.ToLockInfo(new FeederMount { FeederId = "F1", Native = loc }, "bad feeder");
  Assert.That(new object[] { info.FeederId, info.FeederType, info.Track, info.Location, info.Device, info.Machine.MachineId, info.Reason }, Is.EqualTo(new object[] { "F1", 7, 3, 2, 1, "m1", "bad feeder" })); }
[Test] public void Reports_map_lock_infos() { /* request with PackagingUnits [UID "A", LockInfos [{Source 9, Reason 1, Date local 10:00, Message "mismatch"}]] → Uid "A", LockInfos[0] = (9, 1, Date.ToUniversalTime(), "mismatch") */ }
[Test] public void Backoff_doubles_to_five_minutes() { var b = new ReconnectBackoff(clock); for (int i = 0; i < 8; i++) b.Failed(); Assert.That(b.CanTry, Is.False); clock.Advance(TimeSpan.FromMinutes(5)); Assert.That(b.CanTry, Is.True); b.Succeeded(); b.Failed(); clock.Advance(TimeSpan.FromSeconds(5)); Assert.That(b.CanTry, Is.True); }
[Test] public void Throw_if_failed_joins_messages() { var ex = Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", new[] { 1, 2 }, new[] { "not in verification" })); StringAssert.Contains("not in verification", ex.Message); }
[Test] public void Machines_fall_back_to_line_path() { /* LineConfiguration { LinePath "Lines\\L1", Machines [{MachineId "m1", LinePath ""}] } → MachineRef("m1", "Lines\\L1") */ }
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~SetupCenterMapperTests` → FAIL.
- [ ] **Step 3: Implement** the mapper and the clients.
- [ ] **Step 4: Run** → PASS; `dotnet build Interlock/Interlock.sln` → 0 errors.
- [ ] **Step 5: Commit** — `feat: add OIB SetupCenter, LineControl and Monitoring adapters`

---

### Task 14: External Control WCF services and host

**Files:**
- Modify: `Interlock/src/Interlock.Service/Interlock.Service.csproj` — `<Compile Include="..\..\..\Interfaces\WCF Proxies (C#)\proxySetupCenter.cs" Link="ExternalControl\proxySetupCenter.cs" />`
- Create: `Interlock/src/Interlock.Service/ExternalControl/ExternalControlMapper.cs`, `SetupCenterExternalControlService.cs`, `FeederExternalControlService.cs`, `ExternalControlHost.cs`
- Test: `Interlock/tests/Interlock.Tests/Service/ExternalControlTests.cs`

**Interfaces:**
- Consumes: `LockCache`, `DecisionEngine`, `DecisionLogBuffer`, `ILog`, `IClock`; and the types in `www.siplace.com.OIB._2008._05.SetupCenter.Contracts.Service` / `.Data` (from `proxySetupCenter.cs`)
- Produces:
  - `[ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Multiple, UseSynchronizationContext = false)] sealed class SetupCenterExternalControlService : ISiplaceSetupCenterExternalControl { ctor(LockCache cache, DecisionLogBuffer decisions, IClock clock, ILog log) }`:
    - `Ping()` → true.
    - `GetPackagingUnitControlStatus(locs)` and `GetPackagingUnitControlStatusExtended(req)` → one result per location: a location with a null `PackagingUnit` or a blank UID gets `ResultState = 1` and no message. Otherwise `DecisionEngine.Decide(ExternalControlMapper.ToPuInfo(loc), cache.Snapshot)`: `Ok` → 1; else 2 with `Messages = [ new ExternalControlResultMessage { Message = d.Message } ]`. `PackagingUnit = loc.PackagingUnit` in both cases. Each location adds one `DecisionLogEntry` (`Method` = the method name, `ReasonCode` = `req.Reason` for Extended).
    - `GetNewPackagingUnitData(locs)` → per location `new ExternalControlResult { PackagingUnit = null, ResultState = 0 }`.
    - `GetComponentsStatus` → `new ComponentStatusResponse()`; `GetTrayCarrierMappingData` and `UpdateTrayCarrierMappingData` → `new TrayCarrierMappingDataResponse()`.
    - Any unexpected exception → log it, then `throw new FaultException<SiplaceSetupCenterFault>(new SiplaceSetupCenterFault { ErrorCode = 20000, ExtendedMessage = <method> + " failed: " + ex.Message }, new FaultReason(ex.Message))`.
  - `FeederExternalControlService : ISiplaceFeederExternalControl` (same ctor and attributes): `FeederExternalControlPing` → `new PingResponse()`. `GetFeederMaintenanceStatus(req)` → for each `FeederLocations[]` with `DecisionEngine.FindFeederLock(FeederId, cache.Snapshot) != null`: `ExternalControlMapper.ToBlocked(location, FormatMessage(lock))` (ResultState = 1). Feeders that are not locked get no entry. Each call logs one decision per feeder.
  - `static class ExternalControlMapper { PuInfo ToPuInfo(PackagingUnitLocation loc); FeederMaintenanceStatusInfo ToBlocked(FeederLocation src, string reason) }` — `ToBlocked` copies every readable+writable property declared on `FeederLocation` and its base types, then sets `Reason` and `ResultState = 1`.
  - `sealed class ExternalControlHost : IDisposable { ctor(SetupCenterExternalControlService sc, FeederExternalControlService feeder, ILog log); void Open(); void Close() }` — two `ServiceHost`s configured from `App.config` `<system.serviceModel>` (service names = the implementation type full names), addresses passed through `EndpointHelper.ReplaceLocalhost`, opened with `EndpointHelper.OpenServiceHost`, with the listen URIs logged. Default config: `basicHttpBinding` at `http://localhost:8091/Interlock/SetupCenterExternalControl` and `http://localhost:8091/Interlock/FeederExternalControl`, each with a `mexHttpBinding` endpoint `mex` and `serviceMetadata httpGetEnabled="true"`. The binding can be changed on site without recompiling (spec §11).

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Locked_uid_is_not_ok_with_message() { cache.Replace(new[] { L(5, LockType.Pu, "U1") }, t0);
  var r = sc.GetPackagingUnitControlStatus(new[] { Loc("U1", "C1") }).Single(); Assert.That(r.ResultState, Is.EqualTo(2)); Assert.That(r.Messages.Single().Message, Is.EqualTo("Interlock: PU U1 locked – r5 (#5)")); }
[Test] public void Batch_with_null_pu_and_blank_uid_does_not_throw() { var r = sc.GetPackagingUnitControlStatus(new[] { new PackagingUnitLocation(), Loc(" ", "C1"), Loc("U2", "C1") });
  Assert.That(r.Select(x => x.ResultState), Is.EqualTo(new[] { 1, 1, 1 })); }
[Test] public void Extended_logs_reason_code() { sc.GetPackagingUnitControlStatusExtended(new GetPackagingUnitControlStatusExtendedRequest { Reason = 2, PackagingUnitLocations = new[] { Loc("U1", "C1") } }); Assert.That(TakeDecisions().Single().ReasonCode, Is.EqualTo(2)); }
[Test] public void New_pu_data_is_unknown() => Assert.That(sc.GetNewPackagingUnitData(new[] { Loc("U9", "C") }).Single().ResultState, Is.EqualTo(0));
[Test] public void Feeder_status_lists_only_locked_feeders_with_copied_fields() { cache.Replace(new[] { L(3, LockType.Feeder, "F1") }, t0);
  var resp = feeder.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest { FeederLocations = new[] { Fl("F1", track: 4), Fl("F2", track: 5) } });
  var info = resp.FeederMaintenanceStatusInfos.Single(); Assert.That(new object[] { info.FeederId, info.Track, info.ResultState }, Is.EqualTo(new object[] { "F1", 4, 1 })); StringAssert.Contains("(#3)", info.Reason); }
[Test] public void Services_have_no_setup_center_dependency() => Assert.That(typeof(SetupCenterExternalControlService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType), Has.None.EqualTo(typeof(ISetupCenterClient)));
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~ExternalControlTests` → FAIL.
- [ ] **Step 3: Implement** the mapper, both services and the host. If `proxySetupCenter.cs` conflicts with another compiled file, keep it as the only generated proxy in this project.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** — `feat: add Setup Center and feeder External Control services`

---

### Task 15: REST API

**Files:**
- Create: `Interlock/src/Interlock.Core/Api/Dtos.cs`, `Api/DtoMapper.cs`
- Create: `Interlock/src/Interlock.Service/Rest/IInterlockRestApi.cs`, `InterlockRestApi.cs`, `RestErrors.cs`, `RestHost.cs`, `Health/HealthMonitor.cs`
- Test: `Interlock/tests/Interlock.Tests/Core/DtoMapperTests.cs`, `Service/RestApiTests.cs`

**Interfaces:**
- Consumes: `LockService`, `AuthService`, `IAuditRepository`, `IStationDirectory`, `AlarmBoard`, `ActionQueue`, `LockCache`, `CacheRefresher.DbHealthy`, `ISetupCenterClient`, `ILineControlClient`
- Produces:
  - DTOs (`[DataContract]`, camelCase `[DataMember(Name=…)]`, dates as ISO strings): `LockDto { id, type, target, lanes, linePath, reason, source, createdBy, createdAt, status, releasedBy, releasedAt, releaseReason, enforceState, enforceError }`, `CreateLockDto { type, target, lanes, linePath, reason }`, `ReasonDto { reason }`, `SessionRequestDto { username, pin }`, `SessionDto { token, role, expiresAt }`, `ActionDto { action, request, success, error, attempt, createdAt }`, `LockDetailDto { lock, actions }`, `DecisionDto { method, reasonCode, uid, pn, mpn, supplier, linePath, machineId, feederId, result, message, matchedLockIds, createdAt }`, `StationDto { linePath, stationPath }`, `HealthDto { setupCenterConnected, setupCenterLicense, lineControlLicense, monitoringOk, dbOk, queueLength, cacheLoadedAt, failedLocks, alarms[] }`, `UserDto { username, role, active, pin }`, `ApiKeyDto { id, name, canUnlock, active, key }`, `ErrorDto { error, message }`.
  - `static class DtoMapper { LockDto ToDto(LockRecord l); CreateLockRequest ToRequest(CreateLockDto d); ActionDto ToDto(ActionLogEntry e); DecisionDto ToDto(DecisionLogEntry e) }` — `ToRequest` parses `type`/`lanes` with `DbEnum` and turns a `FormatException` into `InterlockException(Validation, "Unknown type '<x>'")` (or `"Unknown lanes '<x>'"`); enum values go out as DB text.
  - `[ServiceContract] interface IInterlockRestApi` with `[WebInvoke]`/`[WebGet]` (`RequestFormat = ResponseFormat = WebMessageFormat.Json`, `BodyStyle = Bare`), UriTemplates relative to the base `/interlock/v1`: `POST session`, `POST locks`, `GET locks?status={status}&type={type}&linePath={linePath}&q={q}`, `GET locks/{id}`, `POST locks/{id}/release`, `POST locks/{id}/retry`, `GET decisions?uid={uid}&pn={pn}&from={from}&to={to}`, `GET health`, `GET lines`, `GET stations`, `GET admin/users`, `POST admin/users`, `GET admin/apikeys`, `POST admin/apikeys`, `PUT admin/apikeys/{id}?active={active}`.
  - `InterlockRestApi : IInterlockRestApi` — reads `Authorization: Bearer <t>` and `X-Api-Key` from `WebOperationContext.Current.IncomingRequest.Headers`, calls `AuthService.Authenticate` (except `session` and `health`), and delegates. `POST locks` sets status 201 when `Created`, else 200.
  - `static class RestErrors { HttpStatusCode StatusFor(ErrorKind k); WebFaultException<ErrorDto> ToFault(Exception ex, ILog log) }` — Validation 400, Unauthenticated 401, Forbidden 403, NotFound 404, InvalidState 409, Unavailable 503; any other exception → 500 with message `"Internal error"` (details only in the log). `error` = the `ErrorKind` name in lower case.
  - `RestHost : IDisposable { ctor(IInterlockRestApi api, string baseAddress, ILog log); void Open(); void Close() }` — a `WebServiceHost` with `WebHttpBinding` (HTTP; `security mode="Transport"` can be switched on through config later).
  - `HealthMonitor { ctor(…ports…, IClock clock); HealthDto Snapshot(); void Tick() }` — `Tick` (every 30 s) checks `IsConnected`/`HasLicense` and `IStationDirectory` freshness and caches the results; `Snapshot` never calls OIB.

- [ ] **Step 1: Write the failing tests**

```csharp
// DtoMapperTests
[Test] public void Lock_dto_uses_db_text_and_iso_utc() { var d = DtoMapper.ToDto(Pu(1, "U1", created: new DateTime(2026,10,18,1,2,3,DateTimeKind.Utc))); Assert.That(d.type, Is.EqualTo("PU")); Assert.That(d.enforceState, Is.EqualTo("PENDING")); Assert.That(d.createdAt, Is.EqualTo("2026-10-18T01:02:03.0000000Z")); }
[Test] public void Unknown_type_is_validation_error() => Assert.That(Assert.Throws<InterlockException>(() => DtoMapper.ToRequest(new CreateLockDto { type = "REEL" })).Kind, Is.EqualTo(ErrorKind.Validation));
[TestCase(ErrorKind.Validation, 400)] [TestCase(ErrorKind.Unauthenticated, 401)] [TestCase(ErrorKind.Forbidden, 403)] [TestCase(ErrorKind.NotFound, 404)] [TestCase(ErrorKind.InvalidState, 409)] [TestCase(ErrorKind.Unavailable, 503)]
public void Status_mapping(ErrorKind k, int code) => Assert.That((int)RestErrors.StatusFor(k), Is.EqualTo(code));
// RestApiTests — real WebServiceHost on http://localhost:80/Temporary_Listen_Addresses/interlock-<guid>/interlock/v1 (no urlacl needed), wired with in-memory fakes
[Test] public void Login_then_create_then_get() { var s = Post<SessionDto>("session", new { username = "qa1", pin = "1234" }); var created = Post("locks", new { type = "PU", target = "U1", reason = "bad" }, bearer: s.token);
  Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created)); var again = Post("locks", new { type = "PU", target = "u1", reason = "bad" }, bearer: s.token); Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK)); }
[Test] public void Missing_credentials_is_401_json() { var r = Get("locks"); Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized)); StringAssert.Contains("\"error\":\"unauthenticated\"", r.Body); }
[Test] public void Operator_release_is_403() { /* login as operator, create PU lock, POST locks/{id}/release {reason:"x"} → 403, body error "forbidden" */ }
[Test] public void Api_key_header_works() { /* key created via AuthService.CreateApiKey; POST locks with header X-Api-Key → 201 and lock source "API", createdBy = key name */ }
[Test] public void Health_needs_no_auth() { Assert.That(Get("health").StatusCode, Is.EqualTo(HttpStatusCode.OK)); }
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~DtoMapperTests|FullyQualifiedName~RestApiTests"` → FAIL.
- [ ] **Step 3: Implement** the DTOs, mapper, contract, service, error mapping, host and health monitor.
- [ ] **Step 4: Run** → PASS. If `RestApiTests` cannot bind port 80 on the dev PC, it must `Assert.Ignore` with the reason, not fail.
- [ ] **Step 5: Commit** — `feat: add REST API`

---

### Task 16: Service host, composition, deployment docs

**Files:**
- Create: `Interlock/src/Interlock.Service/Program.cs`, `InterlockWindowsService.cs`, `Composition.cs`, `ServiceSettings.cs`, `SerilogLog.cs`, `App.config`
- Create: `Interlock/README.md`
- Test: `Interlock/tests/Interlock.Tests/Service/ServiceSettingsTests.cs`

**Interfaces:**
- Consumes: everything above
- Produces:
  - `sealed class ServiceSettings { static ServiceSettings Load(NameValueCollection appSettings, ConnectionStringSettingsCollection cs) }` — keys: `Asm.As.Oib.SetupCenter.Proxy.Endpoint`, `Asm.As.Oib.SiplacePro.LineControl.Proxy.Endpoint`, `Asm.As.Oib.Monitoring.Proxy.MonitoringEndpoint`, `Interlock.CoreHost`, `Interlock.EventCallbackName` (default `Interlock.SetupCenterEvents`), `Interlock.EventCallbackPort` (default 8092), `Interlock.RestBaseAddress` (default `http://+:8090/interlock/v1`), `Interlock.DbSchema` (default `interlock`), `Interlock.SpoolPath` (default `spool\locks.jsonl` next to the exe), `Interlock.LogDir` (default `logs`); connection string **named** `Interlock`. A missing required key → `ConfigurationErrorsException` that names the key.
  - `Composition : IDisposable { ctor(ServiceSettings s); void Start(); void Stop() }` — builds every object. Start order: Serilog → repositories → `CacheRefresher.Tick()` (on failure, log and continue with an empty cache, DbHealthy false) → `Reconciler.QueueOnStartup` (skipped if the DB is down) → External Control host → REST host → event subscriber → timers: worker every 500 ms, cache 30 s, watchdog 60 s, decision flush 5 s, health 30 s, station directory 5 min. Each timer callback catches and logs everything and never overlaps itself (skip when the previous run is still active).
  - `SerilogLog : ILog` — `WriteTo.File(Path.Combine(logDir, "interlock-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)`.
  - `Program.Main(string[] args)`: `--console` → run `Composition` until Enter; `--add-user <username> <ROLE>` → reads the PIN twice from the console without echo, saves through `AuthService` as a bootstrap (no principal check, console only), and exits; no args → `ServiceBase.Run(new InterlockWindowsService())`.
  - `README.md`: build, DB migration (create a least-privilege role with only DML on schema `interlock`), `sc create Interlock binPath= "...\Interlock.Service.exe" start= auto`, `netsh http add urlacl url=http://+:8090/interlock/v1/ user=<service account>` and the same for port 8091, Factory Explorer registration of `SIPLACE.SetupCenter.ExternalControl` / `SIPLACE.Feeder.ExternalControl` with the MEX URLs, first admin via `--add-user`, and the site checklist from spec §11 copied as checkboxes.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Defaults_applied() { var s = ServiceSettings.Load(Required(), Cs("Interlock", "Host=x")); Assert.That(s.RestBaseAddress, Is.EqualTo("http://+:8090/interlock/v1")); Assert.That(s.DbSchema, Is.EqualTo("interlock")); Assert.That(s.EventCallbackPort, Is.EqualTo(8092)); }
[Test] public void Missing_endpoint_names_key() { var ex = Assert.Throws<ConfigurationErrorsException>(() => ServiceSettings.Load(new NameValueCollection(), Cs("Interlock", "Host=x"))); StringAssert.Contains("Asm.As.Oib.SetupCenter.Proxy.Endpoint", ex.Message); }
[Test] public void Connection_string_read_by_name_not_position() { var s = ServiceSettings.Load(Required(), Cs(("DropProduction", "Host=a"), ("Interlock", "Host=b"))); Assert.That(s.ConnectionString, Is.EqualTo("Host=b")); }
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~ServiceSettingsTests` → FAIL.
- [ ] **Step 3: Implement** settings, composition, Serilog adapter, Program, Windows service, App.config (with the External Control `<system.serviceModel>` section from Task 14) and README.
- [ ] **Step 4: Verify**
  - `dotnet test Interlock/tests/Interlock.Tests --filter "TestCategory!=Integration"` → all PASS.
  - Manual smoke: with `INTERLOCK_TEST_DB` config and fake/unreachable OIB endpoints, `Interlock.Service.exe --console` starts, logs "REST listening", `GET /interlock/v1/health` returns JSON with `dbOk: true` and `setupCenterConnected: false`, and Enter stops it cleanly.
- [ ] **Step 5: Commit** — `feat: add interlock Windows service host and deployment guide`

---

### Task 17: WinForms UI — client, login, main grid

**Files:**
- Create: `Interlock/src/Interlock.Ui/Interlock.Ui.csproj` (WinExe, `UseWindowsForms`, references Core), `Program.cs`, `App.config` (`Interlock.ApiBaseUrl`), `ApiClient.cs`, `Session.cs`, `RowStyle.cs`, `LoginForm.cs`, `MainForm.cs`
- Test: `Interlock/tests/Interlock.Tests/Ui/RowStyleTests.cs`, `Ui/ApiClientTests.cs` (the test project references Interlock.Ui)

**Interfaces:**
- Consumes: the DTOs from Task 15 (`Interlock.Core.Api`)
- Produces:
  - `sealed class ApiClient { ctor(string baseUrl, HttpMessageHandler handler = null); SessionDto Login(string u, string pin); IList<LockDto> GetLocks(string status, string type, string linePath, string q); LockDetailDto GetLock(long id); LockDto Create(CreateLockDto d); LockDto Release(long id, string reason); LockDto Retry(long id); IList<DecisionDto> GetDecisions(string uid, string pn, DateTime? from, DateTime? to); HealthDto GetHealth(); IList<string> GetLines(); IList<StationDto> GetStations(); /* admin: GetUsers, SaveUser, GetApiKeys, CreateApiKey, SetApiKeyActive */ string Token { get; set; } }` — `HttpClient` + `DataContractJsonSerializer`; a non-2xx response throws `ApiException { int Status; string Error; string Message }` built from `ErrorDto`; a 401 also raises the `Unauthorized` event.
  - `static class RowStyle { Color BackColorFor(string enforceState) }` → APPLIED `Color.FromArgb(220,245,220)`, PENDING `Color.FromArgb(255,245,200)`, FAILED `Color.FromArgb(255,215,215)`, NOT_NEEDED `Color.FromArgb(235,235,235)`.
  - `Session { string Username; Role Role; bool CanRelease; bool IsAdmin }` (derived from `Permissions` with `Principal.ForUser`).
  - `LoginForm` (username, PIN masked, Enter submits; shows `ApiException.Message`). `MainForm`: an Active-locks `DataGridView` (columns `#, Type, Target, Lanes, Line, Reason, Source, By, Created (local), State, Error`), filters for type / line / text, a 5 s `Timer` refresh that keeps the selection, buttons `New lock`, `Release` (enabled only when `Session.CanRelease`), `Retry` (visible for FAILED rows), `History`, `Admin` (only when `IsAdmin`), and a `StatusStrip` showing SC / LineControl / DB health dots, the FAILED count and the latest alarm. A 401 reopens `LoginForm`.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestCase("APPLIED", 220, 245, 220)] [TestCase("PENDING", 255, 245, 200)] [TestCase("FAILED", 255, 215, 215)]
public void Row_colors(string s, int r, int g, int b) => Assert.That(RowStyle.BackColorFor(s), Is.EqualTo(Color.FromArgb(r, g, b)));
[Test] public void Error_body_becomes_api_exception() { var c = new ApiClient("http://x/interlock/v1", new StubHandler(HttpStatusCode.Forbidden, "{\"error\":\"forbidden\",\"message\":\"Release needs QA\"}"));
  var ex = Assert.Throws<ApiException>(() => c.Release(1, "r")); Assert.That(ex.Status, Is.EqualTo(403)); Assert.That(ex.Message, Is.EqualTo("Release needs QA")); }
[Test] public void Bearer_token_sent() { var h = new StubHandler(HttpStatusCode.OK, "[]"); var c = new ApiClient("http://x/interlock/v1", h) { Token = "t1" }; c.GetLocks("ACTIVE", null, null, null); Assert.That(h.LastRequest.Headers.Authorization.ToString(), Is.EqualTo("Bearer t1")); StringAssert.EndsWith("/locks?status=ACTIVE", h.LastRequest.RequestUri.ToString()); }
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RowStyleTests|FullyQualifiedName~ApiClientTests"` → FAIL.
- [ ] **Step 3: Implement** `ApiClient`, `RowStyle`, `Session`, `LoginForm`, `MainForm`, `Program`.
- [ ] **Step 4: Verify** — tests PASS. Manual: with the service in `--console`, log in as the bootstrap admin, create a PU lock through the API (curl), and see it within 5 s with the PENDING colour.
- [ ] **Step 5: Commit** — `feat: add interlock WinForms client and main screen`

---

### Task 18: WinForms UI — dialogs (new lock, release, retry, history, admin)

**Files:**
- Create: `Interlock/src/Interlock.Ui/NewLockForm.cs`, `ReasonForm.cs`, `HistoryForm.cs`, `AdminForm.cs`, `NewLockModel.cs`
- Modify: `Interlock/src/Interlock.Ui/MainForm.cs` (wire the buttons)
- Test: `Interlock/tests/Interlock.Tests/Ui/NewLockModelTests.cs`

**Interfaces:**
- Consumes: `ApiClient`, `Session` (Task 17)
- Produces:
  - `sealed class NewLockModel { string Type; string Target; string StationPath; string Lanes; string LinePath /* null = whole plant */; string Reason; bool ShowStationPicker; bool ShowLanes; bool ShowLinePicker; CreateLockDto ToDto() }` — `ShowStationPicker`/`ShowLanes` only for CONVEYOR (target = `StationPath`); `ShowLinePicker` only for PN/MPN/SUPPLIER; `ToDto` trims the target, sends `lanes`/`linePath` only when visible, and leaves validation to the server.
  - `NewLockForm`: a type combo; a target textbox that accepts scanner input (Enter moves focus to Reason, not submit); a station combo from `GetStations()` and a lanes radio (Left / Right / Both, default Both); a line combo from `GetLines()` with the first item "Whole plant"; a multi-line Reason; OK → `Create`. A `Created == false` (200) response shows "Already locked (#id)".
  - `ReasonForm(string title)`: a required reason, used by Release. Retry asks only for a Yes/No confirm.
  - `HistoryForm`: tabs — Released locks (`status=RELEASED`), Actions of the selected lock (`GetLock(id).actions`), Decisions (filters UID/PN/from/to, max 500 rows).
  - `AdminForm`: a users grid (add/edit role/active/reset PIN) and an API keys grid (create shows the plain key once in a copyable read-only textbox with a warning; toggle active).

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Conveyor_shows_station_and_lanes_only() { var m = new NewLockModel { Type = "CONVEYOR" }; Assert.That(new[] { m.ShowStationPicker, m.ShowLanes, m.ShowLinePicker }, Is.EqualTo(new[] { true, true, false })); }
[Test] public void Pn_whole_plant_sends_null_line() { var d = new NewLockModel { Type = "PN", Target = " C1\r\n", LinePath = null, Reason = "r" }.ToDto(); Assert.That(d.target, Is.EqualTo("C1")); Assert.That(d.linePath, Is.Null); Assert.That(d.lanes, Is.Null); }
[Test] public void Conveyor_target_is_station_path() { var d = new NewLockModel { Type = "CONVEYOR", StationPath = "Lines\\L1\\S1", Lanes = "BOTH", LinePath = "ignored", Reason = "r" }.ToDto(); Assert.That(new[] { d.target, d.lanes, d.linePath }, Is.EqualTo(new[] { "Lines\\L1\\S1", "BOTH", null })); }
[Test] public void Pu_hides_line_picker() { var m = new NewLockModel { Type = "PU", Target = "U1", LinePath = "Lines\\L1", Reason = "r" }; Assert.That(m.ShowLinePicker, Is.False); Assert.That(m.ToDto().linePath, Is.Null); }
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~NewLockModelTests` → FAIL.
- [ ] **Step 3: Implement** the model and forms, and wire them into `MainForm`.
- [ ] **Step 4: Verify** — tests PASS. Manual against the `--console` service: create each lock type, release as QA (button disabled as OPERATOR), retry a FAILED conveyor lock, view history, create an API key and use it with curl.
- [ ] **Step 5: Commit** — `feat: add interlock UI dialogs`

---

## Done when

- `dotnet test Interlock/tests/Interlock.Tests --filter "TestCategory!=Integration"` passes; integration tests pass against a test database.
- The service runs with `--console`, and the UI can create, release and retry every lock type against it.
- `Interlock/README.md` holds the deployment steps and the spec §11 site checklist. Going live still needs that checklist done on a real line.
