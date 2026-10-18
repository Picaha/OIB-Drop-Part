using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.ServiceModel;
using Interlock.Core.Decisions;
using Interlock.Core.Model;
using www.siplace.com.OIB._2008._05.SetupCenter.Contracts.Data;
using www.siplace.com.OIB._2008._05.SetupCenter.Contracts.Service;

namespace Interlock.Service.ExternalControl
{
    /// <summary>
    /// Converts between the generated Setup Center External Control contract objects (<c>proxySetupCenter.cs</c>) and the service's
    /// own model. Pure: no I/O and no state. The generated types are used on their own here, so nothing in this file imports
    /// the OIB contract assemblies, whose types carry the same names in other namespaces.
    /// </summary>
    public static class ExternalControlMapper
    {
        /// <summary><c>ExternalControlResult.ResultState</c>: Setup Center takes the PU data as it is.</summary>
        internal const int ResultUnknown = 0;

        /// <summary><c>ExternalControlResult.ResultState</c>: the PU may be used.</summary>
        internal const int ResultOk = 1;

        /// <summary><c>ExternalControlResult.ResultState</c>: the PU is blocked; the messages say why.</summary>
        internal const int ResultNotOk = 2;

        /// <summary><c>FeederMaintenanceStatusInfo.ResultState</c>: the feeder is blocked; <c>Reason</c> says why.</summary>
        private const int FeederBlocked = 1;

        /// <summary>The ErrorCode the OIB tutorials give to a fault for an unknown problem.</summary>
        internal const int UnknownProblemErrorCode = 20000;

        /// <summary><c>decision_log.result</c> texts.</summary>
        internal const string LogOk = "OK";

        internal const string LogNotOk = "NOT_OK";

        internal const string LogUnknown = "UNKNOWN";

        /// <summary>Every property of <see cref="FeederLocation"/> and its base types that can be read and written.</summary>
        private static readonly PropertyInfo[] FeederLocationProperties = ReadWriteProperties(typeof(FeederLocation));

        /// <summary>
        /// The scanned PU and its line, for <c>DecisionEngine</c>. A location without a PU gives a <see cref="PuInfo"/> with no PU
        /// fields; a blank <c>LinePath</c> stays blank, which makes every line-scoped lock apply (spec section 6 rule 4).
        /// </summary>
        public static PuInfo ToPuInfo(PackagingUnitLocation loc)
        {
            if (loc == null)
                throw new ArgumentNullException(nameof(loc));

            var pu = loc.PackagingUnit;
            return new PuInfo
            {
                Uid = pu?.UID,
                ComponentName = pu?.ComponentName,
                Mpn = pu?.ManufacturePartNumber,
                Supplier = pu?.Supplier,
                LinePath = loc.LinePath
            };
        }

        /// <summary>
        /// The blocked-feeder answer for <paramref name="src"/>: a copy of every readable and writable property declared on
        /// <see cref="FeederLocation"/> and its base types (a shallow copy, as in the OIB tutorial), then the reason and
        /// <c>ResultState</c> 1.
        /// </summary>
        public static FeederMaintenanceStatusInfo ToBlocked(FeederLocation src, string reason)
        {
            if (src == null)
                throw new ArgumentNullException(nameof(src));

            var info = new FeederMaintenanceStatusInfo();
            foreach (var property in FeederLocationProperties)
                property.SetValue(info, property.GetValue(src, null), null);
            info.Reason = reason;
            info.ResultState = FeederBlocked;
            return info;
        }

        /// <summary>The fault Setup Center gets for an unexpected failure (spec section 9): error code 20000, the method and the cause.</summary>
        internal static FaultException<SiplaceSetupCenterFault> ToFault(string method, Exception ex)
        {
            var fault = new SiplaceSetupCenterFault
            {
                ErrorCode = UnknownProblemErrorCode,
                ExtendedMessage = method + " failed: " + ex.Message
            };
            return new FaultException<SiplaceSetupCenterFault>(fault, new FaultReason(ex.Message));
        }

        /// <summary>
        /// The decision log row for one PU location. A null location or PU leaves the fields it would have filled empty.
        /// <paramref name="decision"/> is null when the location was not checked against the locks.
        /// </summary>
        internal static DecisionLogEntry ToEntry(string method, int? reasonCode, PackagingUnitLocation loc, string result, Decision decision, DateTime nowUtc)
        {
            var pu = loc?.PackagingUnit;
            return new DecisionLogEntry
            {
                Method = method,
                ReasonCode = reasonCode,
                Uid = pu?.UID,
                Pn = pu?.ComponentName,
                Mpn = pu?.ManufacturePartNumber,
                Supplier = pu?.Supplier,
                LinePath = loc?.LinePath,
                MachineId = loc?.MachineId,
                Result = result,
                Message = decision?.Message,
                MatchedLockIds = decision == null || decision.MatchedLockIds.Count == 0
                    ? null
                    : string.Join(",", decision.MatchedLockIds.Select(id => id.ToString(CultureInfo.InvariantCulture))),
                CreatedAtUtc = nowUtc
            };
        }

        /// <summary>The decision log row for one feeder; <paramref name="blockedBy"/> is the feeder lock, or null when the feeder is free.</summary>
        internal static DecisionLogEntry ToEntry(string method, FeederLocation loc, LockRecord blockedBy, DateTime nowUtc)
        {
            var machine = loc.Machine;
            return new DecisionLogEntry
            {
                Method = method,
                FeederId = loc.FeederId,
                MachineId = machine?.MachineId,
                LinePath = machine?.LinePath,
                Result = blockedBy == null ? LogOk : LogNotOk,
                Message = blockedBy == null ? null : DecisionEngine.FormatMessage(blockedBy),
                MatchedLockIds = blockedBy == null ? null : blockedBy.Id.ToString(CultureInfo.InvariantCulture),
                CreatedAtUtc = nowUtc
            };
        }

        private static PropertyInfo[] ReadWriteProperties(Type from)
        {
            var found = new List<PropertyInfo>();
            for (var t = from; t != null && t != typeof(object); t = t.BaseType)
            {
                found.AddRange(t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0));
            }
            return found.ToArray();
        }
    }
}
