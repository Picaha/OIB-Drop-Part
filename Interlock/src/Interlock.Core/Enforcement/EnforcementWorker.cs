using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Interlock.Core.Errors;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Core.Enforcement
{
    /// <summary>
    /// Takes the due jobs from the <see cref="ActionQueue"/> and makes the OIB calls for PU, FEEDER and CONVEYOR locks.
    /// A failed call is retried by <see cref="RetryPolicy"/> and ends as FAILED with an alarm. Every call is written to
    /// the action log. A database that cannot be reached never stops enforcement. Run it from one thread.
    /// </summary>
    public sealed class EnforcementWorker
    {
        private const string NotSupportedError = "Conveyor block not supported";
        private const string NoLanesError = "Conveyor lock has no lanes";

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
                    if (job.Kind != JobKind.Reblock)
                        return true;
                    _log.Warn("Lock #" + l.Id + ": only conveyor locks are re-blocked; the job is dropped.");
                    return false;
                case LockType.Conveyor:
                    return true;
                default:
                    _log.Warn("Lock #" + l.Id + ": " + DbEnum.ToDb(l.Type) + " jobs are not handled yet; the job is dropped.");
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

        /// <summary>Logs the failed call, then queues a retry, or ends the job as FAILED when it must not or can no longer be retried.</summary>
        private void OnFailure(EnforcementJob job, string action, string error, bool canRetry)
        {
            var l = job.Lock;
            WriteAction(job, action, false, error);

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

        private void WriteAction(EnforcementJob job, string action, bool success, string error)
        {
            var l = job.Lock;
            if (l.Id == 0)
                return;

            var entry = new ActionLogEntry
            {
                LockId = l.Id,
                Action = action,
                RequestJson = RequestJson(l),
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
            if (l.Status == LockStatus.Active && IsInCache(l))
                _cache.Upsert(l);
        }

        /// <summary>
        /// True while the cache still holds the lock: by id, or for a spooled lock (id 0) by identity. A lock released
        /// while its job ran is gone from the cache, and the worker must not put it back.
        /// </summary>
        private bool IsInCache(LockRecord l)
        {
            if (l.Id != 0)
                return _cache.Snapshot.Any(e => e.Id == l.Id);
            return _cache.FindActive(l.Type, l.TargetNorm, l.LinePath, l.Lanes) != null;
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

            /// <summary>True when an OIB write call was made, so the action log gets a row.</summary>
            public bool CalledOib { get; private set; }

            /// <summary>Set when the job failed in a way a retry cannot fix.</summary>
            public string PermanentError { get; private set; }

            public static Outcome Done(EnforceState state, bool calledOib)
            {
                return new Outcome { State = state, CalledOib = calledOib };
            }

            public static Outcome Failed(string error)
            {
                return new Outcome { PermanentError = error };
            }
        }
    }
}
