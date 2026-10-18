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
    /// An audit repository that lives in memory. Tests read the stored entries from <see cref="Actions"/> and
    /// <see cref="Decisions"/>; both keep insertion order, and so do the query methods.
    /// </summary>
    public sealed class InMemoryAuditRepository : IAuditRepository
    {
        private readonly object _gate = new object();
        private long _lastActionId;

        /// <summary>While true, every <see cref="IAuditRepository"/> member throws <see cref="DbUnavailableException"/>.</summary>
        public bool Down { get; set; }

        public List<ActionLogEntry> Actions { get; } = new List<ActionLogEntry>();

        public List<DecisionLogEntry> Decisions { get; } = new List<DecisionLogEntry>();

        /// <summary>Stores the entry itself and, like the database, gives it the next id when it has none.</summary>
        public void AddAction(ActionLogEntry e)
        {
            if (e == null)
                throw new ArgumentNullException(nameof(e));
            CheckUp();
            lock (_gate)
            {
                if (e.Id == 0)
                    e.Id = ++_lastActionId;
                else if (e.Id > _lastActionId)
                    _lastActionId = e.Id;
                Actions.Add(e);
            }
        }

        public IList<ActionLogEntry> GetActions(long lockId)
        {
            CheckUp();
            lock (_gate)
                return Actions.Where(a => a.LockId == lockId).ToList();
        }

        public void AddDecisions(IList<DecisionLogEntry> e)
        {
            if (e == null)
                throw new ArgumentNullException(nameof(e));
            CheckUp();
            lock (_gate)
                Decisions.AddRange(e);
        }

        /// <summary>Uid and Pn are compared after <c>Normalizer.Norm</c>; a blank filter is no filter. Both time limits are inclusive.</summary>
        public IList<DecisionLogEntry> QueryDecisions(DecisionQuery q)
        {
            CheckUp();
            q = q ?? new DecisionQuery();
            var uid = Normalizer.Norm(q.Uid);
            var pn = Normalizer.Norm(q.Pn);

            lock (_gate)
            {
                return Decisions
                    .Where(d => (uid == null || Normalizer.Norm(d.Uid) == uid)
                        && (pn == null || Normalizer.Norm(d.Pn) == pn)
                        && (q.FromUtc == null || d.CreatedAtUtc >= q.FromUtc)
                        && (q.ToUtc == null || d.CreatedAtUtc <= q.ToUtc))
                    .Take(q.Limit)
                    .ToList();
            }
        }

        private void CheckUp()
        {
            if (Down)
                throw new DbUnavailableException("The in-memory audit repository is down.");
        }
    }
}
