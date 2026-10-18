using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Asm.As.Oib.SiplaceSetupCenter.Contracts.Data;
using Asm.As.Oib.SiplaceSetupCenter.Contracts.Message;
using Interlock.Core.Errors;
using Interlock.Core.Model;

namespace Interlock.Service.Oib
{
    /// <summary>
    /// Converts between the Setup Center contract objects and the service's own model. Pure: no I/O, no state, so every rule here
    /// is unit-tested with real OIB objects and the clients only call it.
    /// </summary>
    public static class SetupCenterMapper
    {
        /// <summary>The <c>ResultState</c> Setup Center returns for a call that worked.</summary>
        private const int ResultOk = 1;

        public static PuInfo ToPuInfo(PackagingUnit pu, string linePath)
        {
            if (pu == null)
                throw new ArgumentNullException(nameof(pu));

            return new PuInfo
            {
                Uid = pu.UID,
                ComponentName = pu.ComponentName,
                Mpn = pu.ManufacturePartNumber,
                Supplier = pu.Supplier,
                LinePath = linePath
            };
        }

        /// <summary>
        /// Every machine of every line, once. Setup Center lists them in <c>Machines</c> (obsolete) and in
        /// <c>MachineConfigurations</c>; both are read. A machine without a line path of its own takes the one of its line.
        /// </summary>
        public static IList<MachineRef> ToMachines(Configuration c)
        {
            var machines = new List<MachineRef>();
            if (c == null || c.LineConfigurations == null)
                return machines;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in c.LineConfigurations)
            {
                if (line == null)
                    continue;
                foreach (var machine in MachineLocatorsOf(line))
                {
                    if (machine == null || string.IsNullOrWhiteSpace(machine.MachineId) || !seen.Add(machine.MachineId))
                        continue;
                    machines.Add(new MachineRef
                    {
                        MachineId = machine.MachineId,
                        LinePath = FirstNonBlank(machine.LinePath, line.LinePath)
                    });
                }
            }
            return machines;
        }

        /// <summary>The distinct, non-blank line paths of the configuration (compared ignoring case), in the order Setup Center lists them.</summary>
        public static IList<string> ToLinePaths(Configuration c)
        {
            var paths = new List<string>();
            if (c == null || c.LineConfigurations == null)
                return paths;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in c.LineConfigurations)
            {
                if (line != null && !string.IsNullOrWhiteSpace(line.LinePath) && seen.Add(line.LinePath))
                    paths.Add(line.LinePath);
            }
            return paths;
        }

        /// <summary>
        /// Every packaging unit mounted on the machines of <paramref name="setups"/>. Locations without a packaging unit, or with a
        /// blank UID, are skipped. The line path comes from the location state; the machine comes from the state, or from the
        /// dictionary key when the state names none.
        /// </summary>
        public static IList<MountedPu> ToMountedPus(Dictionary<string, MachineSetup> setups)
        {
            var mounted = new List<MountedPu>();
            if (setups == null)
                return mounted;

            foreach (var entry in setups)
            {
                var setup = entry.Value;
                if (setup == null || setup.Tables == null)
                    continue;
                foreach (var table in setup.Tables)
                {
                    if (table == null || table.ComponentLocationStates == null)
                        continue;
                    foreach (var state in table.ComponentLocationStates)
                    {
                        if (state == null || state.PackagingUnit == null || string.IsNullOrWhiteSpace(state.PackagingUnit.UID))
                            continue;
                        mounted.Add(new MountedPu
                        {
                            Pu = ToPuInfo(state.PackagingUnit, LinePathOf(state)),
                            MachineId = FirstNonBlank(MachineIdOf(state), entry.Key)
                        });
                    }
                }
            }
            return mounted;
        }

        /// <summary>
        /// The mount of the feeder, or null when Setup Center did not report it. The answer is keyed by the id that was asked for;
        /// when the key differs only in case or padding it is still found. <see cref="FeederMount.Native"/> is the
        /// <see cref="PackagingUnitLocation"/> Setup Center returned. Setup Center may list several locations for one feeder id
        /// (the old single-location answer is obsolete for that reason): the first one with a component location stands for the
        /// feeder.
        /// </summary>
        public static FeederMount ToFeederMount(string feederId, GetPackagingUnitLocationsOnFeedersResponse r)
        {
            if (string.IsNullOrWhiteSpace(feederId) || r == null)
                return null;

            var native = FindLocation(r, feederId);
            if (native == null)
                return null;

            return new FeederMount
            {
                FeederId = feederId.Trim(),
                MachineId = MachineIdOf(native),
                LinePath = LinePathOf(native),
                Native = native
            };
        }

        /// <summary>
        /// The request item that locks the mounted feeder: every property Setup Center's <see cref="ComponentLocation"/> and
        /// <see cref="LockFeederLocationInfo"/> share (Machine, Location, Device, Track, FeederId, FeederType and the rest) copied
        /// over, plus <paramref name="reason"/>.
        /// </summary>
        public static LockFeederLocationInfo ToLockInfo(FeederMount m, string reason)
        {
            var location = RequireLocation(m);
            var info = new LockFeederLocationInfo();
            CopySharedProperties(location, info);
            if (string.IsNullOrWhiteSpace(info.FeederId))
                info.FeederId = m.FeederId;
            info.Reason = reason;
            return info;
        }

        /// <summary>The request item that unlocks the mounted feeder: the shared properties of its location in a plain <see cref="FeederLocation"/>.</summary>
        public static FeederLocation ToFeederLocation(FeederMount m)
        {
            var location = RequireLocation(m);
            var feeder = new FeederLocation();
            CopySharedProperties(location, feeder);
            if (string.IsNullOrWhiteSpace(feeder.FeederId))
                feeder.FeederId = m.FeederId;
            return feeder;
        }

        /// <summary>
        /// The lock entries of every packaging unit in a <c>LockStateChanged</c> notice. Null requests, resources and units are
        /// ignored, and so are units without a UID. Dates are UTC: a UTC date stays, a local one is converted, an unspecified
        /// one is taken to be UTC already.
        /// </summary>
        public static IList<PuLockReport> ToReports(LockStateChangedReportRequest r)
        {
            var reports = new List<PuLockReport>();
            if (r == null || r.Resource == null || r.Resource.PackagingUnits == null)
                return reports;

            foreach (var pu in r.Resource.PackagingUnits)
            {
                if (pu == null || string.IsNullOrWhiteSpace(pu.UID))
                    continue;

                var infos = new List<LockInfoRef>();
                if (pu.LockInfos != null)
                {
                    foreach (var info in pu.LockInfos)
                    {
                        if (info == null)
                            continue;
                        infos.Add(new LockInfoRef
                        {
                            Source = info.Source,
                            Reason = info.Reason,
                            DateUtc = ToUtc(info.Date),
                            Message = info.Message
                        });
                    }
                }
                reports.Add(new PuLockReport { Uid = pu.UID, LockInfos = infos });
            }
            return reports;
        }

        /// <summary>
        /// Throws <see cref="OibCallException"/> when Setup Center did not answer every item with the OK state (1), or answered
        /// nothing at all: no answer is no confirmation. The message names the action and joins Setup Center's messages with "; ".
        /// </summary>
        public static void ThrowIfFailed(string action, IEnumerable<int> resultStates, IEnumerable<string> messages)
        {
            var states = resultStates == null ? new List<int>() : resultStates.ToList();
            if (states.Count == 0)
                throw new OibCallException(action + " failed: no result returned");

            var failed = states.Where(s => s != ResultOk).Distinct().ToList();
            if (failed.Count == 0)
                return;

            var text = messages == null
                ? string.Empty
                : string.Join("; ", messages.Where(m => !string.IsNullOrWhiteSpace(m)));
            if (text.Length == 0)
                text = "result state " + string.Join(", ", failed);
            throw new OibCallException(action + " failed: " + text);
        }

        public static void ThrowIfFailed(string action, PackagingUnitResponse r)
        {
            var results = r == null ? null : r.Results;
            ThrowIfFailed(
                action,
                results == null ? null : results.Select(x => x == null ? 0 : x.ResultState),
                results == null
                    ? null
                    : results
                        .Where(x => x != null && x.Messages != null)
                        .SelectMany(x => x.Messages)
                        .Select(m => m == null ? null : m.Message));
        }

        public static void ThrowIfFailed(string action, LockOnlineFeedersResponse r)
        {
            var results = r == null ? null : r.LockOnlineFeedersResults;
            ThrowIfFailed(
                action,
                results == null ? null : results.Select(x => x == null ? 0 : x.ResultState),
                results == null ? null : results.Where(x => x != null && x.Messages != null).SelectMany(x => x.Messages));
        }

        public static void ThrowIfFailed(string action, UnlockOnlineFeedersResponse r)
        {
            var results = r == null ? null : r.UnlockOnlineFeedersResults;
            ThrowIfFailed(
                action,
                results == null ? null : results.Select(x => x == null ? 0 : x.ResultState),
                results == null ? null : results.Where(x => x != null && x.Messages != null).SelectMany(x => x.Messages));
        }

        private static ComponentLocation RequireLocation(FeederMount m)
        {
            if (m == null)
                throw new ArgumentNullException(nameof(m));

            var native = m.Native as PackagingUnitLocation;
            if (native == null)
                throw new ArgumentException("The feeder mount does not come from Setup Center.", nameof(m));
            if (native.ComponentLocation == null)
                throw new OibCallException("Setup Center reported no location for feeder " + m.FeederId);
            return native.ComponentLocation;
        }

        /// <summary>
        /// Copies each public readable property of <paramref name="source"/> to the public writable property of the same name on
        /// <paramref name="target"/> when the target's type accepts the source's. A <see cref="MachineLocator"/> is copied the
        /// same way into a new object, so request and answer do not share it.
        /// </summary>
        private static void CopySharedProperties(object source, object target)
        {
            var sourceProperties = source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var targetProperty in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!targetProperty.CanWrite || targetProperty.GetIndexParameters().Length > 0 || !targetProperty.SetMethod.IsPublic)
                    continue;

                var sourceProperty = sourceProperties.FirstOrDefault(p => p.Name == targetProperty.Name && p.CanRead && p.GetMethod.IsPublic && p.GetIndexParameters().Length == 0);
                if (sourceProperty == null || !targetProperty.PropertyType.IsAssignableFrom(sourceProperty.PropertyType))
                    continue;

                var value = sourceProperty.GetValue(source, null);
                var machine = value as MachineLocator;
                if (machine != null)
                {
                    var copy = new MachineLocator();
                    CopySharedProperties(machine, copy);
                    value = copy;
                }
                targetProperty.SetValue(target, value, null);
            }
        }

        private static PackagingUnitLocation FindLocation(GetPackagingUnitLocationsOnFeedersResponse r, string feederId)
        {
            List<PackagingUnitLocation> locations;
            if (TryGetIgnoringCaseAndPadding(r.PackagingUnitLocationListOnFeeders, feederId, out locations) && locations != null)
            {
                var first = locations.FirstOrDefault(l => l != null && l.ComponentLocation != null) ?? locations.FirstOrDefault(l => l != null);
                if (first != null)
                    return first;
            }

            PackagingUnitLocation single;
            TryGetIgnoringCaseAndPadding(LegacyFeederLocations(r), feederId, out single);
            return single;
        }

        private static bool TryGetIgnoringCaseAndPadding<T>(Dictionary<string, T> map, string key, out T value)
        {
            value = default(T);
            if (map == null)
                return false;
            if (map.TryGetValue(key, out value))
                return true;

            var wanted = key.Trim();
            foreach (var entry in map)
            {
                if (entry.Key != null && string.Equals(entry.Key.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }
            value = default(T);
            return false;
        }

        // The OIB marks some members obsolete. Older Setup Center versions fill only those, newer ones the replacements, so the
        // mapper reads the member the contract gave first and then the replacement. The warnings are silenced here, nowhere else.
#pragma warning disable CS0618
        private static IEnumerable<MachineLocator> MachineLocatorsOf(LineConfiguration line)
        {
            var machines = line.Machines ?? new List<MachineLocator>();
            var configured = (line.MachineConfigurations ?? new List<MachineConfiguration>()).Select(c => c == null ? null : c.MachineLocator);
            return machines.Concat(configured);
        }

        private static string MachineIdOf(PackagingUnitLocation location)
        {
            var machine = location.ComponentLocation == null ? null : location.ComponentLocation.Machine;
            return FirstNonBlank(location.MachineId, machine == null ? null : machine.MachineId);
        }

        private static string LinePathOf(PackagingUnitLocation location)
        {
            var machine = location.ComponentLocation == null ? null : location.ComponentLocation.Machine;
            return FirstNonBlank(location.LinePath, machine == null ? null : machine.LinePath);
        }

        private static Dictionary<string, PackagingUnitLocation> LegacyFeederLocations(GetPackagingUnitLocationsOnFeedersResponse r)
        {
            return r.PackagingUnitLocationsOnFeeders;
        }
#pragma warning restore CS0618

        private static string FirstNonBlank(string first, string second)
        {
            return string.IsNullOrWhiteSpace(first) ? second : first;
        }

        private static DateTime ToUtc(DateTime date)
        {
            switch (date.Kind)
            {
                case DateTimeKind.Utc:
                    return date;
                case DateTimeKind.Local:
                    return date.ToUniversalTime();
                default:
                    return DateTime.SpecifyKind(date, DateTimeKind.Utc);
            }
        }
    }
}
