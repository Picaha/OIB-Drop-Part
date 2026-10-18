using System;
using System.Globalization;
using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Core.Api
{
    /// <summary>
    /// Maps the model to the REST DTOs and the request DTOs and query strings back. Every text that comes from a caller goes
    /// through <see cref="InputGuard"/>; a value that cannot be parsed is <see cref="ErrorKind.Validation"/>.
    /// </summary>
    public static class DtoMapper
    {
        public static LockDto ToDto(LockRecord l)
        {
            if (l == null) throw new ArgumentNullException(nameof(l));

            return new LockDto
            {
                id = l.Id,
                type = DbEnum.ToDb(l.Type),
                target = l.Target,
                lanes = l.Lanes.HasValue ? DbEnum.ToDb(l.Lanes.Value) : null,
                linePath = l.LinePath,
                reason = l.Reason,
                source = DbEnum.ToDb(l.Source),
                createdBy = l.CreatedBy,
                createdAt = Iso(l.CreatedAtUtc),
                status = DbEnum.ToDb(l.Status),
                releasedBy = l.ReleasedBy,
                releasedAt = Iso(l.ReleasedAtUtc),
                releaseReason = l.ReleaseReason,
                enforceState = DbEnum.ToDb(l.EnforceState),
                enforceError = l.EnforceError
            };
        }

        public static ActionDto ToDto(ActionLogEntry e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));

            return new ActionDto
            {
                action = e.Action,
                request = e.RequestJson,
                success = e.Success,
                error = e.Error,
                attempt = e.Attempt,
                createdAt = Iso(e.CreatedAtUtc)
            };
        }

        public static DecisionDto ToDto(DecisionLogEntry e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));

            return new DecisionDto
            {
                method = e.Method,
                reasonCode = e.ReasonCode,
                uid = e.Uid,
                pn = e.Pn,
                mpn = e.Mpn,
                supplier = e.Supplier,
                linePath = e.LinePath,
                machineId = e.MachineId,
                feederId = e.FeederId,
                result = e.Result,
                message = e.Message,
                matchedLockIds = e.MatchedLockIds,
                createdAt = Iso(e.CreatedAtUtc)
            };
        }

        public static LockDetailDto ToDto(LockDetail d)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));

            return new LockDetailDto
            {
                @lock = ToDto(d.Lock),
                actions = d.Actions.Select(a => ToDto(a)).ToArray()
            };
        }

        public static StationDto ToDto(StationRef s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));

            return new StationDto { linePath = s.LinePath, stationPath = s.StationPath };
        }

        /// <summary>The user as a response: the PIN is never part of it.</summary>
        public static UserDto ToDto(AppUser u)
        {
            if (u == null) throw new ArgumentNullException(nameof(u));

            return new UserDto { username = u.Username, role = DbEnum.ToDb(u.Role), active = u.Active };
        }

        /// <summary>The key as a response: never the hash, and the plain <c>key</c> only when the caller passes it (on creation).</summary>
        public static ApiKeyDto ToDto(ApiKeyRecord k, string plainKeyOrNull)
        {
            if (k == null) throw new ArgumentNullException(nameof(k));

            return new ApiKeyDto { id = k.Id, name = k.Name, canUnlock = k.CanUnlock, active = k.Active, key = plainKeyOrNull };
        }

        public static CreateLockRequest ToRequest(CreateLockDto d)
        {
            if (d == null)
                throw new InterlockException(ErrorKind.Validation, "A request body is required");

            var request = new CreateLockRequest
            {
                Target = InputGuard.Clean(d.target, "target"),
                LinePath = InputGuard.Clean(d.linePath, "linePath"),
                Reason = InputGuard.Clean(d.reason, "reason")
            };

            var type = InputGuard.Clean(d.type, "type");
            if (!string.IsNullOrEmpty(type))
                request.Type = Parse(DbEnum.ParseLockType, type, "type");

            var lanes = InputGuard.Clean(d.lanes, "lanes");
            if (!string.IsNullOrEmpty(lanes))
                request.Lanes = Parse(DbEnum.ParseLanes, lanes, "lanes");

            return request;
        }

        /// <summary>The cleaned reason of a release request.</summary>
        public static string ToReason(ReasonDto d)
        {
            if (d == null)
                throw new InterlockException(ErrorKind.Validation, "A request body is required");

            return InputGuard.Clean(d.reason, "reason");
        }

        public static LockQuery ToLockQuery(string status, string type, string linePath, string text)
        {
            var q = new LockQuery
            {
                LinePath = InputGuard.Clean(linePath, "linePath"),
                Text = InputGuard.Clean(text, "q")
            };

            status = InputGuard.Clean(status, "status");
            if (!string.IsNullOrEmpty(status))
                q.Status = Parse(DbEnum.ParseStatus, status, "status");

            type = InputGuard.Clean(type, "type");
            if (!string.IsNullOrEmpty(type))
                q.Type = Parse(DbEnum.ParseLockType, type, "type");

            return q;
        }

        public static DecisionQuery ToDecisionQuery(string uid, string pn, string from, string to)
        {
            return new DecisionQuery
            {
                Uid = InputGuard.Clean(uid, "uid"),
                Pn = InputGuard.Clean(pn, "pn"),
                FromUtc = ParseTime(from, "from"),
                ToUtc = ParseTime(to, "to")
            };
        }

        public static long ParseId(string text, string what)
        {
            long id;
            if (!long.TryParse(text == null ? null : text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id))
                throw new InterlockException(ErrorKind.Validation, "Invalid " + what);
            return id;
        }

        public static bool ParseFlag(string text, string name)
        {
            var t = text == null ? null : text.Trim();
            if (string.Equals(t, "true", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(t, "false", StringComparison.OrdinalIgnoreCase))
                return false;
            throw new InterlockException(ErrorKind.Validation, "'" + name + "' must be true or false");
        }

        public static Role ParseRole(string text)
        {
            var role = InputGuard.Clean(text, "role");
            return Parse(DbEnum.ParseRole, role, "role");
        }

        /// <summary>ISO 8601 with a trailing Z (<c>"o"</c> format); a time of unspecified kind is taken as UTC.</summary>
        public static string Iso(DateTime utc)
        {
            DateTime value;
            switch (utc.Kind)
            {
                case DateTimeKind.Utc: value = utc; break;
                case DateTimeKind.Local: value = utc.ToUniversalTime(); break;
                default: value = DateTime.SpecifyKind(utc, DateTimeKind.Utc); break;
            }
            return value.ToString("o", CultureInfo.InvariantCulture);
        }

        public static string Iso(DateTime? utc)
        {
            return utc.HasValue ? Iso(utc.Value) : null;
        }

        private static DateTime? ParseTime(string text, string name)
        {
            var t = InputGuard.Clean(text, name);
            if (string.IsNullOrEmpty(t))
                return null;

            DateTime value;
            if (!DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value))
                throw new InterlockException(ErrorKind.Validation, "Invalid " + name + " time");
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static T Parse<T>(Func<string, T> parse, string text, string what)
        {
            try
            {
                return parse(text);
            }
            catch (FormatException)
            {
                throw new InterlockException(ErrorKind.Validation, "Unknown " + what + " '" + text + "'");
            }
        }
    }
}
