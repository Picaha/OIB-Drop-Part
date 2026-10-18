using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Service.Oib
{
    /// <summary>
    /// The stations and lines last learned from OIB. Reading never calls OIB and never blocks on a refresh: readers get immutable
    /// snapshots, and <see cref="Refresh"/> replaces them whole. The stations come from Monitoring; the line paths are those of
    /// the stations plus the ones Setup Center knows, sorted ignoring case. Each source is refreshed on its own: when one fails
    /// its last good list stays (a warning is logged) and the other still updates.
    /// </summary>
    public sealed class StationDirectory : IRefreshableStationDirectory
    {
        private static readonly IList<StationRef> NoStations = new ReadOnlyCollection<StationRef>(new StationRef[0]);
        private static readonly IList<string> NoLines = new ReadOnlyCollection<string>(new string[0]);

        private readonly IMonitoringClient _monitoring;
        private readonly ISetupCenterClient _setupCenter;
        private readonly ILog _log;
        private readonly object _refreshGate = new object();
        private volatile IList<StationRef> _stations = NoStations;
        private volatile IList<string> _setupCenterLines = NoLines;
        private volatile IList<string> _linePaths = NoLines;

        public StationDirectory(IMonitoringClient monitoring, ISetupCenterClient setupCenter, ILog log)
        {
            if (monitoring == null)
                throw new ArgumentNullException(nameof(monitoring));
            if (setupCenter == null)
                throw new ArgumentNullException(nameof(setupCenter));
            if (log == null)
                throw new ArgumentNullException(nameof(log));

            _monitoring = monitoring;
            _setupCenter = setupCenter;
            _log = log;
        }

        public IList<StationRef> Stations
        {
            get { return _stations; }
        }

        public IList<string> LinePaths
        {
            get { return _linePaths; }
        }

        /// <summary>Asks Monitoring and Setup Center again. Never throws; a source that fails keeps its last good list.</summary>
        public void Refresh()
        {
            lock (_refreshGate)
            {
                var changed = false;

                try
                {
                    var stations = _monitoring.GetStations();
                    _stations = new ReadOnlyCollection<StationRef>(
                        (stations ?? new StationRef[0])
                            .Where(s => s != null)
                            .Select(s => new StationRef { LinePath = s.LinePath, StationPath = s.StationPath })
                            .ToList());
                    changed = true;
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not refresh the stations from Monitoring, keeping the last list: " + ex.Message);
                }

                try
                {
                    var lines = _setupCenter.GetLinePaths();
                    _setupCenterLines = new ReadOnlyCollection<string>((lines ?? new string[0]).Where(l => !string.IsNullOrWhiteSpace(l)).ToList());
                    changed = true;
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not refresh the line paths from Setup Center, keeping the last list: " + ex.Message);
                }

                if (changed)
                    _linePaths = BuildLinePaths(_stations, _setupCenterLines);
            }
        }

        private static IList<string> BuildLinePaths(IEnumerable<StationRef> stations, IEnumerable<string> setupCenterLines)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            foreach (var line in stations.Select(s => s.LinePath).Concat(setupCenterLines))
            {
                if (!string.IsNullOrWhiteSpace(line) && seen.Add(line))
                    lines.Add(line);
            }
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            return new ReadOnlyCollection<string>(lines);
        }
    }
}
