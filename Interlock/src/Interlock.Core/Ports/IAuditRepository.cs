using System.Collections.Generic;
using Interlock.Core.Model;

namespace Interlock.Core.Ports
{
    /// <summary>Stores the action log and the decision log. Every member throws <c>DbUnavailableException</c> when the database cannot be reached.</summary>
    public interface IAuditRepository
    {
        void AddAction(ActionLogEntry e);
        IList<ActionLogEntry> GetActions(long lockId);
        void AddDecisions(IList<DecisionLogEntry> e);
        IList<DecisionLogEntry> QueryDecisions(DecisionQuery q);
    }
}
