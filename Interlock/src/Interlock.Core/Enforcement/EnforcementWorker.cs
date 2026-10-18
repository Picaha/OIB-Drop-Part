using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Interlock.Core.Decisions;
using Interlock.Core.Errors;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Text;

namespace Interlock.Core.Enforcement
{
    /// <summary>
    /// Takes the due jobs from the <see cref="ActionQueue"/> and makes the OIB calls for PU, FEEDER, CONVEYOR, PN, MPN and
    /// SUPPLIER locks. A PN, MPN or SUPPLIER lock sweeps the mounted reels and locks the matching ones; its release unlocks
    /// only the reels no other lock still covers. A failed call is retried by <see cref="RetryPolicy"/> and ends as FAILED
    /// with an alarm. Every call is written to the action log. A database that cannot be reached never stops enforcement.
    /// Run it from one thread.
    /// </summary>
    public sealed class EnforcementWorker
    {
        private const string NotSupportedError = "Conveyor block not supported";
        private const string NoLanesError = "Conveyor lock has no lanes";
        private const string UnstoredSweepError = "Sweep needs a stored lock";

        private readonly ActionQueue _queue;
        private readonly ILockRepository _locks;
        private readonly IAuditRepository _audit;
        private readonly ISetupCenterClient _sc;
        private readonly ILineControlClient _lc;
        private readonly LockCache _cache;
        private readonly AlarmBoard _alarms;
        private readonly IClock _clock;
        private readonly ILog _log;

        public EnforcementWorker(ActionQueue queue, ILockRepository locks, IAuditRepository audit, ISetupCenterClient sc,
            ILineControlClient lc, LockCache cache, AlarmBoard alarms, IClock clock, ILog log)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (locks == null) throw new ArgumentNullException(nameof(locks));
            if (audit == null) throw new ArgumentNullException(nameof(audit));
            if (sc == null) throw new ArgumentNullException(nameof(sc));
            if (lc == null) throw new ArgumentNullException(nameof(lc));
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (alarms == null) throw new ArgumentNullException(nameof(alarms));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (log == null) throw new ArgumentNullException(nameof(log));

            _queue = queue;
            _locks = locks;
            _audit = audit;
            _sc = sc;
            _lc = lc;
            _cache = cache;
            _alarms = alarms;
            _clock = clock;
            _log = log;
        }

        /// <summary>Runs every job that is due now, one after the other. A job that fails is queued again or ends as FAILED; it never stops the others.</summary>
        public void RunDue()
        {
            foreach (var job in _queue.TakeDue(_clock.UtcNow))
            {
                try
                {
                    Run(job);
                }
                catch (Exception ex)
                {
                    _log.Error("Enforcement of lock #" + job.Lock.Id + " stopped unexpectedly.", ex);
                }
            }
        }

        private void Run(EnforcementJob job)
        {
            var l = job.Lock;
            if (!CanHandle(job))
                return;

            if (job.Kind != JobKind.Release && !IsInCache(l))
            {
                _log.Info("Lock #" + l.Id + ": " + job.Kind + " job skipped, the lock is no longer cached.");
                return;
            }

            if (l.Id == 0)
                _log.Warn("Lock " + DbEnum.ToDb(l.Type) + " " + l.Target + " has no id yet (not stored): the action log and enforce state are not written to the database.");

            var action = ActionName(job);
            Outcome outcome;
            try
            {
                outcome = Execute(job);
            }
            catch (Exception ex)
            {
                OnFailure(job, action, ex.Message, true);
                return;
            }

            if (outcome.PermanentError != null)
            {
                OnFailure(job, action, outcome.PermanentError, false);
                return;
            }

            if (outcome.LoggedError != null)
            {
                OnFailure(job, action, outcome.LoggedError, true, false);
                return;
            }

            if (outcome.CalledOib)
                WriteAction(job, action, true, null);
            SetState(job, outcome.State, null);

            if (job.Kind == JobKind.Reblock)
                _alarms.Raise("Conveyor re-blocked: " + l.Target + " (#" + l.Id + ")", _clock.UtcNow);
        }

        private bool CanHandle(EnforcementJob job)
        {
            var l = job.Lock;
            switch (l.Type)
            {
                case LockType.Pu:
                case LockType.Feeder:
                case LockType.Pn:
                case LockType.Mpn:
                case LockType.Supplier:
                    if (job.Kind != JobKind.Reblock)
                        return true;
                    _log.Warn("Lock #" + l.Id + ": only conveyor locks are re-blocked; the job is dropped.");
                    return false;
                case LockType.Conveyor:
                    return true;
                default:
                    _log.Warn("Lock #" + l.Id + ": lock type " + l.Type + " is not known; the job is dropped.");
                    return false;
            }
        }

        private static string ActionName(EnforcementJob job)
        {
            var release = job.Kind == JobKind.Release;
            switch (job.Lock.Type)
            {
                case LockType.Pu:
                    return release ? Actions.UNLOCK_PU : Actions.LOCK_PU;
                case LockType.Pn:
                case LockType.Mpn:
                case LockType.Supplier:
                    return release ? Actions.UNLOCK_PU : Actions.SWEEP;
                case LockType.Feeder:
                    return release ? Actions.UNLOCK_FEEDER_ONLINE : Actions.LOCK_FEEDER_ONLINE;
                default:
                    if (job.Kind == JobKind.Reblock)
                        return Actions.REBLOCK_CONVEYOR;
                    return release ? Actions.UNBLOCK_CONVEYOR : Actions.BLOCK_CONVEYOR;
            }
        }

        private Outcome Execute(EnforcementJob job)
        {
            switch (job.Lock.Type)
            {
                case LockType.Pu:
                    return RunPu(job);
                case LockType.Feeder:
                    return RunFeeder(job);
                case LockType.Pn:
                case LockType.Mpn:
                case LockType.Supplier:
                    return job.Kind == JobKind.Release ? ReleaseSweep(job) : ApplySweep(job);
                default:
                    return RunConveyor(job);
            }
        }

        private Outcome RunPu(EnforcementJob job)
        {
            var l = job.Lock;
            var uids = new[] { l.Target };

            if (job.Kind == JobKind.Release)
            {
                if (_locks.IsUidCoveredByOtherActiveLock(l.TargetNorm, l.Id))
                {
                    _log.Info("Lock #" + l.Id + ": PU " + l.Target + " stays locked, another active lock still covers it.");
                    return Outcome.Done(EnforceState.Applied, false);
                }
                _sc.UnlockPackagingUnits(uids);
            }
            else
            {
                _sc.LockPackagingUnits(uids, l.Reason);
            }
            return Outcome.Done(EnforceState.Applied, true);
        }

        private Outcome RunFeeder(EnforcementJob job)
        {
            var l = job.Lock;
            var mount = _sc.FindMountedFeeder(l.Target);

            if (job.Kind == JobKind.Release)
            {
                if (mount == null)
                    return Outcome.Done(EnforceState.Applied, false);
                _sc.UnlockOnlineFeeder(mount);
            }
            else
            {
                if (mount == null)
                    return Outcome.Done(EnforceState.NotNeeded, false);
                _sc.LockOnlineFeeder(mount, l.Reason);
            }
            return Outcome.Done(EnforceState.Applied, true);
        }

        private Outcome RunConveyor(EnforcementJob job)
        {
            var l = job.Lock;
            if (l.Lanes == null)
                return Outcome.Failed(NoLanesError);
            var lanes = l.Lanes.Value;

            switch (job.Kind)
            {
                case JobKind.Release:
                    _lc.Unblock(l.Target, lanes, l.Reason);
                    break;
                case JobKind.Reblock:
                    _lc.Block(l.Target, lanes, l.Reason);
                    break;
                default:
                    if (!_lc.IsBlockSupported(l.Target))
                        return Outcome.Failed(NotSupportedError);
                    _lc.Block(l.Target, lanes, l.Reason);
                    break;
            }
            return Outcome.Done(EnforceState.Applied, true);
        }

        /// <summary>
        /// Sweeps the mounted reels of the machines in the lock's line scope and locks those the lock matches, in one
        /// call. Each locked reel is recorded for the lock, only after the call succeeded, so the release unlocks exactly
        /// those; a record that cannot be written fails the attempt like a failed OIB call, because a reel locked without a
        /// record can never be unlocked by the release. A sweep that finds nothing is APPLIED too. The sweep writes its own
        /// action rows: one LOCK_PU row for the lock call and one SWEEP row per attempt, which also records a failed attempt.
        /// A lock without an id has nowhere to record the reels, so it fails at once, before any OIB call.
        /// </summary>
        private Outcome ApplySweep(EnforcementJob job)
        {
            var l = job.Lock;
            if (l.Id == 0)
                return Outcome.Failed(UnstoredSweepError);

            var machines = 0;
            var matched = new List<string>();
            try
            {
                var machineIds = MachineIdsInScope(l);
                machines = machineIds.Count;
                if (machines > 0)
                    matched = MatchingUids(l, _sc.GetMountedPus(machineIds));
                if (matched.Count > 0)
                    LockAndRecord(job, matched);
            }
            catch (Exception ex)
            {
                WriteAction(job, Actions.SWEEP, false, ex.Message, SweepJson(machines, matched));
                return Outcome.FailedAfterLogging(ex.Message);
            }

            WriteAction(job, Actions.SWEEP, true, null, SweepJson(machines, matched));
            return Outcome.Done(EnforceState.Applied, false);
        }

        /// <summary>
        /// Unlocks the reels the sweep locked, except those another active lock still covers, in one call, then closes the
        /// record of every one of them. The records stay open when the unlock fails, so the retry finds them again.
        /// </summary>
        private Outcome ReleaseSweep(EnforcementJob job)
        {
            var l = job.Lock;
            var open = new List<string>();
            var unlock = new List<string>();
            try
            {
                if (l.Id != 0)
                    open.AddRange(_locks.GetOpenAppliedPus(l.Id));
                unlock = UidsNoOtherLockCovers(l, open);
                if (unlock.Count > 0)
                {
                    _sc.UnlockPackagingUnits(unlock);
                    WriteAction(job, Actions.UNLOCK_PU, true, null, UidsJson(unlock));
                }
            }
            catch (Exception ex)
            {
                WriteAction(job, Actions.UNLOCK_PU, false, ex.Message, UidsJson(unlock));
                return Outcome.FailedAfterLogging(ex.Message);
            }

            var now = _clock.UtcNow;
            foreach (var stored in open)
            {
                var uid = stored;
                TryDb("close the applied PU", () => _locks.CloseAppliedPu(l.Id, uid, now));
            }
            return Outcome.Done(EnforceState.Applied, false);
        }

        /// <summary>The ids of the machines whose line is in the lock's scope, each once, in the order Setup Center lists them.</summary>
        private List<string> MachineIdsInScope(LockRecord l)
        {
            var ids = new List<string>();
            var seen = new HashSet<string>();
            foreach (var machine in _sc.GetMachines())
            {
                if (machine == null || string.IsNullOrWhiteSpace(machine.MachineId))
                    continue;
                if (DecisionEngine.LineInScope(l.LinePath, machine.LinePath) && seen.Add(machine.MachineId))
                    ids.Add(machine.MachineId);
            }
            return ids;
        }

        /// <summary>The UIDs of the mounted reels the lock matches, each once (compared with <see cref="Normalizer.Norm"/>), as Setup Center wrote them.</summary>
        private static List<string> MatchingUids(LockRecord l, IEnumerable<MountedPu> mounted)
        {
            var uids = new List<string>();
            var seen = new HashSet<string>();
            foreach (var m in mounted)
            {
                if (m == null || m.Pu == null || !DecisionEngine.Matches(l, m.Pu))
                    continue;
                var norm = Normalizer.Norm(m.Pu.Uid);
                if (norm != null && seen.Add(norm))
                    uids.Add(m.Pu.Uid);
            }
            return uids;
        }

        /// <summary>The open UIDs, each once, that no other active lock covers (overlap guard), as the repository stored them.</summary>
        private List<string> UidsNoOtherLockCovers(LockRecord l, IEnumerable<string> open)
        {
            var uids = new List<string>();
            var seen = new HashSet<string>();
            foreach (var uid in open)
            {
                var norm = Normalizer.Norm(uid);
                if (norm == null || !seen.Add(norm))
                    continue;
                if (_locks.IsUidCoveredByOtherActiveLock(norm, l.Id))
                    _log.Info("Lock #" + l.Id + ": PU " + uid + " stays locked, another active lock still covers it.");
                else
                    uids.Add(uid);
            }
            return uids;
        }

        /// <summary>
        /// Locks the reels, writes the LOCK_PU row of that call, then records each reel for the lock. A failed call records
        /// nothing. A call or a record that fails throws, and the sweep attempt fails.
        /// </summary>
        private void LockAndRecord(EnforcementJob job, IList<string> uids)
        {
            var l = job.Lock;
            var request = UidsJson(uids);
            try
            {
                _sc.LockPackagingUnits(uids, l.Reason);
            }
            catch (Exception ex)
            {
                WriteAction(job, Actions.LOCK_PU, false, ex.Message, request);
                throw;
            }
            WriteAction(job, Actions.LOCK_PU, true, null, request);

            var now = _clock.UtcNow;
            foreach (var uid in uids)
                _locks.AddAppliedPu(l.Id, uid, now);
        }

        /// <summary>
        /// Logs the failed call, then queues a retry, or ends the job as FAILED when it must not or can no longer be retried.
        /// An Apply or Reblock job whose lock has left the cache (released meanwhile) is dropped instead: no retry, no FAILED, no alarm.
        /// </summary>
        private void OnFailure(EnforcementJob job, string action, string error, bool canRetry, bool writeRow = true)
        {
            var l = job.Lock;
            if (writeRow)
                WriteAction(job, action, false, error);

            if (job.Kind != JobKind.Release && !IsInCache(l))
            {
                _log.Info("Lock #" + l.Id + " " + action + " failed and the lock is no longer cached, so there is no retry and no FAILED state: " + error);
                return;
            }

            if (canRetry)
            {
                job.FailedAttempts++;
                var delay = RetryPolicy.DelayAfterFailure(job.FailedAttempts);
                if (delay.HasValue)
                {
                    job.NotBeforeUtc = _clock.UtcNow + delay.Value;
                    _queue.Enqueue(job);
                    _log.Warn("Lock #" + l.Id + " " + action + " failed (attempt " + job.FailedAttempts + "), retry in "
                        + delay.Value.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s: " + error);
                    return;
                }
            }

            SetState(job, EnforceState.Failed, error);
            _alarms.Raise("Lock #" + l.Id + " FAILED: " + error, _clock.UtcNow);
        }

        /// <summary>Writes one action row. <paramref name="requestJson"/> defaults to the lock's target and lanes.</summary>
        private void WriteAction(EnforcementJob job, string action, bool success, string error, string requestJson = null)
        {
            var l = job.Lock;
            if (l.Id == 0)
                return;

            var entry = new ActionLogEntry
            {
                LockId = l.Id,
                Action = action,
                RequestJson = requestJson ?? RequestJson(l),
                Success = success,
                Error = error,
                Attempt = job.FailedAttempts + 1,
                CreatedAtUtc = _clock.UtcNow
            };
            TryDb("write the action log", () => _audit.AddAction(entry));
        }

        /// <summary>Sets the state on the job's lock copy and in the database, and refreshes the cached copy of an ACTIVE lock that is still cached.</summary>
        private void SetState(EnforcementJob job, EnforceState state, string error)
        {
            var l = job.Lock;
            l.EnforceState = state;
            l.EnforceError = error;

            if (l.Id != 0)
                TryDb("save the enforce state", () => _locks.SetEnforceState(l.Id, state, error));
            if (l.Status == LockStatus.Active)
                _cache.UpsertIfPresent(l);
        }

        /// <summary>
        /// True while the cache still holds the lock: by id, or for a spooled lock (id 0) as a spooled entry (also id 0) with the
        /// same identity. Apply and Reblock jobs act only on such a lock. A lock released while its job waited or ran is gone
        /// from the cache, and the worker neither runs, retries nor re-caches it. Once the spool is flushed the cache holds the
        /// stored lock instead of the spooled one, so a stale job of the spooled lock is dropped here.
        /// </summary>
        private bool IsInCache(LockRecord l)
        {
            if (l.Id != 0)
                return _cache.Snapshot.Any(e => e.Id == l.Id);
            return _cache.Snapshot.Any(e => e.Id == 0 && e.Status == LockStatus.Active && LockCache.SameIdentity(e, l));
        }

        /// <summary>Runs a database write. A failure is logged and the OIB result stands.</summary>
        private void TryDb(string what, Action write)
        {
            try
            {
                write();
            }
            catch (DbUnavailableException ex)
            {
                _log.Warn("Database unavailable, could not " + what + ": " + ex.Message);
            }
            catch (Exception ex)
            {
                _log.Error("Could not " + what + ".", ex);
            }
        }

        private static string RequestJson(LockRecord l)
        {
            var json = new StringBuilder("{\"target\":");
            AppendJsonString(json, l.Target);
            json.Append(",\"lanes\":");
            AppendJsonString(json, l.Lanes.HasValue ? DbEnum.ToDb(l.Lanes.Value) : null);
            return json.Append('}').ToString();
        }

        private static string SweepJson(int machines, IEnumerable<string> matched)
        {
            var json = new StringBuilder("{\"machines\":").Append(machines.ToString(CultureInfo.InvariantCulture)).Append(",\"matched\":");
            AppendJsonArray(json, matched);
            return json.Append('}').ToString();
        }

        private static string UidsJson(IEnumerable<string> uids)
        {
            var json = new StringBuilder("{\"uids\":");
            AppendJsonArray(json, uids);
            return json.Append('}').ToString();
        }

        private static void AppendJsonArray(StringBuilder json, IEnumerable<string> values)
        {
            json.Append('[');
            var first = true;
            foreach (var value in values)
            {
                if (!first)
                    json.Append(',');
                first = false;
                AppendJsonString(json, value);
            }
            json.Append(']');
        }

        private static void AppendJsonString(StringBuilder json, string value)
        {
            if (value == null)
            {
                json.Append("null");
                return;
            }

            json.Append('"');
            foreach (var c in value)
            {
                if (c == '"' || c == '\\')
                    json.Append('\\').Append(c);
                else if (c < ' ')
                    json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else
                    json.Append(c);
            }
            json.Append('"');
        }

        private sealed class Outcome
        {
            private Outcome() { }

            public EnforceState State { get; private set; }

            /// <summary>True when an OIB write call was made, so the action log gets a row. A sweep writes its own rows and leaves this false.</summary>
            public bool CalledOib { get; private set; }

            /// <summary>Set when the job failed in a way a retry cannot fix.</summary>
            public string PermanentError { get; private set; }

            /// <summary>Set when the job failed, a retry may fix it, and the failed attempt is already in the action log.</summary>
            public string LoggedError { get; private set; }

            public static Outcome Done(EnforceState state, bool calledOib)
            {
                return new Outcome { State = state, CalledOib = calledOib };
            }

            public static Outcome Failed(string error)
            {
                return new Outcome { PermanentError = error };
            }

            public static Outcome FailedAfterLogging(string error)
            {
                return new Outcome { LoggedError = error };
            }
        }
    }
}
