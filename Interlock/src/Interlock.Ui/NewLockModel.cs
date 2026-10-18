using Interlock.Core.Api;

namespace Interlock.Ui
{
    /// <summary>
    /// What the "New lock" dialog collects and which parts of it apply to the chosen type. Free of controls so it can be tested.
    /// The checks here are a convenience; the service stays the authority.
    /// </summary>
    public sealed class NewLockModel
    {
        public static readonly string[] Types = { "FEEDER", "PU", "PN", "MPN", "SUPPLIER", "CONVEYOR" };
        public static readonly string[] LaneValues = { "LEFT", "RIGHT", "BOTH" };

        public string Type { get; set; }
        public string Target { get; set; }
        public string StationPath { get; set; }
        public string Lanes { get; set; }

        /// <summary>Null means the whole plant.</summary>
        public string LinePath { get; set; }

        public string Reason { get; set; }

        public bool ShowStationPicker { get { return Type == "CONVEYOR"; } }
        public bool ShowLanes { get { return Type == "CONVEYOR"; } }
        public bool ShowLinePicker { get { return Type == "PN" || Type == "MPN" || Type == "SUPPLIER"; } }

        /// <summary>The free-text (scanner) target is used for every type except CONVEYOR.</summary>
        public bool ShowTarget { get { return !ShowStationPicker; } }

        /// <summary>Null when the input is complete, otherwise the text to show.</summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(Type)) return "Choose a lock type";
            if (ShowStationPicker && string.IsNullOrWhiteSpace(StationPath)) return "Choose a station";
            if (ShowTarget && string.IsNullOrWhiteSpace(Target)) return "Enter or scan the target";
            if (string.IsNullOrWhiteSpace(Reason)) return "Enter a reason";
            return null;
        }

        public CreateLockDto ToDto()
        {
            return new CreateLockDto
            {
                type = Type,
                target = ShowStationPicker ? Trim(StationPath) : Trim(Target),
                lanes = ShowLanes ? (string.IsNullOrWhiteSpace(Lanes) ? "BOTH" : Lanes.Trim()) : null,
                linePath = ShowLinePicker ? Trim(LinePath) : null,
                reason = Trim(Reason)
            };
        }

        private static string Trim(string s)
        {
            return s == null ? null : s.Trim();
        }
    }
}
