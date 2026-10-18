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
    /// The PU scan check Setup Center calls (<c>SIPLACE.SetupCenter.ExternalControl</c>), implemented from the generated
    /// <c>proxySetupCenter.cs</c> contract. It answers from the lock cache only: it takes no Setup Center client, because a call
    /// back into Setup Center from inside a callback deadlocks. Every location is answered, in request order, and writes one
    /// decision log row. An unexpected failure is logged and sent to Setup Center as a <see cref="SiplaceSetupCenterFault"/>.
    /// </summary>
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Multiple, UseSynchronizationContext = false)]
    public sealed class SetupCenterExternalControlService : ISiplaceSetupCenterExternalControl
    {
        private readonly LockCache _cache;
        private readonly DecisionLogBuffer _decisions;
        private readonly IClock _clock;
        private readonly ILog _log;

        public SetupCenterExternalControlService(LockCache cache, DecisionLogBuffer decisions, IClock clock, ILog log)
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

        public bool Ping()
        {
            return true;
        }

        public ExternalControlResult[] GetPackagingUnitControlStatus(PackagingUnitLocation[] packagingUnitLocations)
        {
            return CheckAll(nameof(GetPackagingUnitControlStatus), packagingUnitLocations, null);
        }

        public GetPackagingUnitControlStatusExtendedResponse GetPackagingUnitControlStatusExtended(GetPackagingUnitControlStatusExtendedRequest request)
        {
            var results = CheckAll(nameof(GetPackagingUnitControlStatusExtended), request?.PackagingUnitLocations, request?.Reason);
            return new GetPackagingUnitControlStatusExtendedResponse { ExternalControlResults = results };
        }

        /// <summary>
        /// We hold no PU master data: every location is answered "unknown" with no PU, as the OIB tutorial does for a PU it does
        /// not know.
        /// </summary>
        public ExternalControlResult[] GetNewPackagingUnitData(PackagingUnitLocation[] packagingUnitLocations)
        {
            const string method = nameof(GetNewPackagingUnitData);
            try
            {
                var now = _clock.UtcNow;
                if (packagingUnitLocations == null)
                    return new ExternalControlResult[0];

                var results = new ExternalControlResult[packagingUnitLocations.Length];
                for (var i = 0; i < results.Length; i++)
                {
                    Record(ExternalControlMapper.ToEntry(method, null, packagingUnitLocations[i], ExternalControlMapper.LogUnknown, null, now));
                    results[i] = new ExternalControlResult { PackagingUnit = null, ResultState = ExternalControlMapper.ResultUnknown };
                }
                return results;
            }
            catch (Exception ex)
            {
                throw Fail(method, ex);
            }
        }

        // The next three are used only when their Setup Center options are enabled, and we do not enable them.
        public ComponentStatusResponse GetComponentsStatus(ComponentStatusRequest request)
        {
            return new ComponentStatusResponse();
        }

        public TrayCarrierMappingDataResponse GetTrayCarrierMappingData(TrayCarrierMappingDataRequest request)
        {
            return new TrayCarrierMappingDataResponse();
        }

        public TrayCarrierMappingDataResponse UpdateTrayCarrierMappingData(UpdateTrayCarrierMappingDataRequest request)
        {
            return new TrayCarrierMappingDataResponse();
        }

        private ExternalControlResult[] CheckAll(string method, PackagingUnitLocation[] locations, int? reasonCode)
        {
            try
            {
                var now = _clock.UtcNow;
                if (locations == null)
                    return new ExternalControlResult[0];

                // One snapshot for the whole batch, so the locations of one call are checked against the same locks.
                var active = _cache.Snapshot;
                var results = new ExternalControlResult[locations.Length];
                for (var i = 0; i < results.Length; i++)
                    results[i] = CheckOne(method, reasonCode, locations[i], active, now);
                return results;
            }
            catch (Exception ex)
            {
                throw Fail(method, ex);
            }
        }

        /// <summary>
        /// A location without a PU (or a null location) has nothing to check: it is answered OK with no message. A PU with a blank
        /// UID is checked like any other: it never matches a PU lock, but PN, MPN and SUPPLIER locks still apply.
        /// </summary>
        private ExternalControlResult CheckOne(string method, int? reasonCode, PackagingUnitLocation loc, IReadOnlyCollection<LockRecord> active, DateTime now)
        {
            var pu = loc?.PackagingUnit;
            if (pu == null)
            {
                Record(ExternalControlMapper.ToEntry(method, reasonCode, loc, ExternalControlMapper.LogOk, null, now));
                return new ExternalControlResult { PackagingUnit = pu, ResultState = ExternalControlMapper.ResultOk };
            }

            var decision = DecisionEngine.Decide(ExternalControlMapper.ToPuInfo(loc), active);
            Record(ExternalControlMapper.ToEntry(method, reasonCode, loc, decision.Ok ? ExternalControlMapper.LogOk : ExternalControlMapper.LogNotOk, decision, now));

            var result = new ExternalControlResult { PackagingUnit = pu, ResultState = decision.Ok ? ExternalControlMapper.ResultOk : ExternalControlMapper.ResultNotOk };
            if (!decision.Ok)
                result.Messages = new[] { new ExternalControlResultMessage { Message = decision.Message } };
            return result;
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

        private FaultException<SiplaceSetupCenterFault> Fail(string method, Exception ex)
        {
            _log.Error(method + " failed.", ex);
            return ExternalControlMapper.ToFault(method, ex);
        }
    }
}
