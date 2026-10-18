using System;
using System.Collections.Generic;
using System.ServiceModel;
using Interlock.Core.Decisions;
using Interlock.Core.Enforcement;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using www.siplace.com.OIB._2008._05.SetupCenter.Contracts.Data;
using www.siplace.com.OIB._2008._05.SetupCenter.Contracts.Service;

namespace Interlock.Service.ExternalControl
{
    /// <summary>
    /// The feeder maintenance status Setup Center calls (<c>SIPLACE.Feeder.ExternalControl</c>), implemented from the generated
    /// <c>proxySetupCenter.cs</c> contract. Like the PU service it answers from the lock cache only. Only feeders with an active
    /// FEEDER lock get an entry; every feeder asked about writes one decision log row.
    /// </summary>
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Multiple, UseSynchronizationContext = false)]
    public sealed class FeederExternalControlService : ISiplaceFeederExternalControl
    {
        private readonly LockCache _cache;
        private readonly DecisionLogBuffer _decisions;
        private readonly IClock _clock;
        private readonly ILog _log;

        public FeederExternalControlService(LockCache cache, DecisionLogBuffer decisions, IClock clock, ILog log)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (decisions == null) throw new ArgumentNullException(nameof(decisions));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (log == null) throw new ArgumentNullException(nameof(log));

            _cache = cache;
            _decisions = decisions;
            _clock = clock;
            _log = log;
        }

        public PingResponse FeederExternalControlPing(PingRequest request)
        {
            return new PingResponse();
        }

        public GetFeederMaintenanceStatusResponse GetFeederMaintenanceStatus(GetFeederMaintenanceStatusRequest request)
        {
            const string method = nameof(GetFeederMaintenanceStatus);
            try
            {
                var now = _clock.UtcNow;
                var blocked = new List<FeederMaintenanceStatusInfo>();
                var locations = request?.FeederLocations;
                if (locations != null)
                {
                    // One snapshot for the whole batch, so the feeders of one call are checked against the same locks.
                    var active = _cache.Snapshot;
                    foreach (var location in locations)
                    {
                        if (location == null)
                            continue; // there is no feeder in this slot: nothing to answer and nothing to log

                        var feederLock = DecisionEngine.FindFeederLock(location.FeederId, active);
                        Record(ExternalControlMapper.ToEntry(method, location, feederLock, now));
                        if (feederLock != null)
                            blocked.Add(ExternalControlMapper.ToBlocked(location, DecisionEngine.FormatMessage(feederLock)));
                    }
                }
                return new GetFeederMaintenanceStatusResponse { FeederMaintenanceStatusInfos = blocked.ToArray() };
            }
            catch (Exception ex)
            {
                _log.Error(method + " failed.", ex);
                throw ExternalControlMapper.ToFault(method, ex);
            }
        }

        /// <summary>The buffer only queues the row, so this never waits for the database; a failure here must not reach Setup Center.</summary>
        private void Record(DecisionLogEntry entry)
        {
            try
            {
                _decisions.Add(entry);
            }
            catch (Exception ex)
            {
                _log.Error("Could not buffer a decision log row.", ex);
            }
        }
    }
}
