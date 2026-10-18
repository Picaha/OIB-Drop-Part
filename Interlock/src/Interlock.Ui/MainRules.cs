using System;
using System.Globalization;
using Interlock.Core.Api;

namespace Interlock.Ui
{
    /// <summary>The small decisions of the main screen, kept free of controls so they can be tested.</summary>
    public static class MainRules
    {
        public static bool CanRelease(Session session, LockDto selected)
        {
            return session != null && session.CanRelease && selected != null && selected.status == "ACTIVE";
        }

        public static bool CanRetry(LockDto selected)
        {
            return selected != null && selected.enforceState == "FAILED";
        }

        /// <summary>A combo value to a query value: "All" and blanks mean no filter.</summary>
        public static string FilterValue(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "All", StringComparison.OrdinalIgnoreCase))
                return null;
            return text.Trim();
        }

        /// <summary>An ISO 8601 UTC text as local time; the raw text when it cannot be parsed.</summary>
        public static string LocalTime(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "";
            DateTime t;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) return iso;
            if (t.Kind == DateTimeKind.Unspecified) t = DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    public enum Dot { Unknown, Ok, Bad }

    /// <summary>What the status strip shows. Every value comes from the (already sanitised) health DTO.</summary>
    public sealed class HealthSummary
    {
        public Dot SetupCenter { get; private set; }
        public Dot LineControl { get; private set; }
        public Dot Db { get; private set; }
        public int Failed { get; private set; }
        public string LatestAlarm { get; private set; }

        public static HealthSummary From(HealthDto h)
        {
            if (h == null)
                return new HealthSummary { LatestAlarm = "" };

            Dot sc;
            if (!h.setupCenterConnected || h.setupCenterLicense == false) sc = Dot.Bad;
            else if (h.setupCenterLicense == null) sc = Dot.Unknown;
            else sc = Dot.Ok;

            Dot lc = h.lineControlLicense == null ? Dot.Unknown : (h.lineControlLicense.Value ? Dot.Ok : Dot.Bad);
            Dot db = (!h.dbOk || h.cacheRefreshFailures > 0) ? Dot.Bad : Dot.Ok;

            // The service lists alarms newest first.
            var alarm = h.alarms != null && h.alarms.Length > 0 ? h.alarms[0] : "";
            return new HealthSummary { SetupCenter = sc, LineControl = lc, Db = db, Failed = h.failedLocks, LatestAlarm = alarm ?? "" };
        }
    }
}
