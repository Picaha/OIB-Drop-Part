using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Interlock.Core.Api;

namespace Interlock.Ui
{
    /// <summary>The filters of the decision history: text boxes and local dates become query values (UTC).</summary>
    public sealed class DecisionFilter
    {
        public const int MaxRows = 500;

        public string Uid { get; set; }
        public string Pn { get; set; }

        /// <summary>Local date (the time part is ignored); null = no lower limit.</summary>
        public DateTime? From { get; set; }

        /// <summary>Local date (the time part is ignored); the whole day is included; null = no upper limit.</summary>
        public DateTime? To { get; set; }

        public string UidValue { get { return Blank(Uid); } }
        public string PnValue { get { return Blank(Pn); } }

        /// <summary>Start of the "from" day in local time, as UTC.</summary>
        public DateTime? FromUtc()
        {
            return From.HasValue ? DateTime.SpecifyKind(From.Value.Date, DateTimeKind.Local).ToUniversalTime() : (DateTime?)null;
        }

        /// <summary>Last tick of the "to" day in local time, as UTC.</summary>
        public DateTime? ToUtc()
        {
            return To.HasValue ? DateTime.SpecifyKind(To.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local).ToUniversalTime() : (DateTime?)null;
        }

        public string Validate()
        {
            if (From.HasValue && To.HasValue && From.Value.Date > To.Value.Date) return "'From' is after 'To'";
            return null;
        }

        /// <summary>Newest first, at most <see cref="MaxRows"/> rows. A row with an unreadable time sorts last.</summary>
        public static IList<DecisionDto> NewestFirst(IEnumerable<DecisionDto> rows)
        {
            if (rows == null) return new DecisionDto[0];
            return rows.OrderByDescending(Time).Take(MaxRows).ToList();
        }

        private static DateTime Time(DecisionDto d)
        {
            DateTime t;
            if (d != null && DateTime.TryParse(d.createdAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t))
                return t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t;
            return DateTime.MinValue;
        }

        private static string Blank(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
    }
}
