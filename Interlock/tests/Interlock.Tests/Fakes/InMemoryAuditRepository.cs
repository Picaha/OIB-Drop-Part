using System;
using System.Collections.Generic;
using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Text;

namespace Interlock.Tests.Fakes
{
    /// <summary>
    /// An audit repository that lives in memory and orders like the real one: <see cref="GetActions"/> by id, and
    /// <see cref="QueryDecisions"/> the newest rows first (a time tie goes to the later insert). Entries are copied on write
    /// and on read, so callers cannot change stored rows by accident. Tests read the stored copies from <see cref="Actions"/>
    /// and <see cref="Decisions"/>, in insertion order.
    /// </summary>
    public sealed class InMemoryAuditRepository : IAuditRepository
    {
        private readonly object _gate = new object();
        private long _lastActionId;

        /// <summary>While true, every <see cref="IAuditRepository"/> member throws <see cref="DbUnavailableException"/>.</summary>
        public bool Down { get; set; }

        public List<ActionLogEntry> Actions { get; } = new List<ActionLogEntry>();

        public List<DecisionLogEntry> Decisions { get; } = new List<DecisionLogEntry>();

        /// <summary>Stores a copy of the entry; like the database, the copy gets the next id when the entry has none.</summary>
        public void AddAction(ActionLogEntry e)
        {
            if (e == null)
                throw new ArgumentNullException(nameof(e));
            CheckUp();
            lock (_gate)
            {
                var stored = Copy(e);
                if (stored.Id == 0)
                    stored.Id = ++_lastActionId;
                else if (stored.Id > _lastActionId)
                    _lastActionId = stored.Id;
                Actions.Add(stored);
            }
        }

        public IList<ActionLogEntry> GetActions(long lockId)
        {
            CheckUp();
            lock (_gate)
                return Actions.Where(a => a.LockId == lockId).OrderBy(a => a.Id).Select(Copy).ToList();
        }

        /// <summary>Stores copies of the whole batch, or nothing when an entry is null.</summary>
        public void AddDecisions(IList<DecisionLogEntry> e)
        {
            if (e == null)
                throw new ArgumentNullException(nameof(e));
            CheckUp();
            var copies = e.Select(Copy).ToList();
            lock (_gate)
                Decisions.AddRange(copies);
        }

        /// <summary>
        /// The newest <c>Limit</c> matching rows, newest first. Uid and Pn are compared after <c>Normalizer.Norm</c>; a blank
        /// filter is no filter. Both time limits are inclusive.
        /// </summary>
        public IList<DecisionLogEntry> QueryDecisions(DecisionQuery q)
        {
            CheckUp();
            q = q ?? new DecisionQuery();
            var uid = Normalizer.Norm(q.Uid);
            var pn = Normalizer.Norm(q.Pn);

            lock (_gate)
            {
                return Decisions
                    .Select((d, index) => new { Entry = d, Index = index })
                    .Where(x => (uid == null || Normalizer.Norm(x.Entry.Uid) == uid)
                        && (pn == null || Normalizer.Norm(x.Entry.Pn) == pn)
                        && (q.FromUtc == null || x.Entry.CreatedAtUtc >= q.FromUtc)
                        && (q.ToUtc == null || x.Entry.CreatedAtUtc <= q.ToUtc))
                    .OrderByDescending(x => x.Entry.CreatedAtUtc)
                    .ThenByDescending(x => x.Index)
                    .Take(q.Limit)
                    .Select(x => Copy(x.Entry))
                    .ToList();
            }
        }

        private void CheckUp()
        {
            if (Down)
                throw new DbUnavailableException("The in-memory audit repository is down.");
        }

        private static ActionLogEntry Copy(ActionLogEntry a)
        {
            return new ActionLogEntry
            {
                Id = a.Id,
                LockId = a.LockId,
                Action = a.Action,
                RequestJson = a.RequestJson,
                Success = a.Success,
                Error = a.Error,
                Attempt = a.Attempt,
                CreatedAtUtc = a.CreatedAtUtc
            };
        }

        private static DecisionLogEntry Copy(DecisionLogEntry d)
        {
            if (d == null)
                throw new ArgumentException("The batch contains a null entry.", "e");
            return new DecisionLogEntry
            {
                Method = d.Method,
                ReasonCode = d.ReasonCode,
                Uid = d.Uid,
                Pn = d.Pn,
                Mpn = d.Mpn,
                Supplier = d.Supplier,
                LinePath = d.LinePath,
                MachineId = d.MachineId,
                FeederId = d.FeederId,
                Result = d.Result,
                Message = d.Message,
                MatchedLockIds = d.MatchedLockIds,
                CreatedAtUtc = d.CreatedAtUtc
            };
        }
    }
}
