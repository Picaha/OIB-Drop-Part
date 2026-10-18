using System;
using System.Collections.Generic;

namespace Interlock.Core.Model
{
    /// <summary>Maps the model enums to and from their upper-case database text.</summary>
    public static class DbEnum
    {
        private static readonly Dictionary<string, LockType> LockTypes = Build<LockType>(ToDb);
        private static readonly Dictionary<string, LockStatus> Statuses = Build<LockStatus>(ToDb);
        private static readonly Dictionary<string, LockSource> Sources = Build<LockSource>(ToDb);
        private static readonly Dictionary<string, EnforceState> EnforceStates = Build<EnforceState>(ToDb);
        private static readonly Dictionary<string, Lanes> LanesValues = Build<Lanes>(ToDb);
        private static readonly Dictionary<string, Role> Roles = Build<Role>(ToDb);

        public static string ToDb(LockType value)
        {
            switch (value)
            {
                case LockType.Feeder: return "FEEDER";
                case LockType.Pu: return "PU";
                case LockType.Pn: return "PN";
                case LockType.Mpn: return "MPN";
                case LockType.Supplier: return "SUPPLIER";
                case LockType.Conveyor: return "CONVEYOR";
                default: throw Undefined(value);
            }
        }

        public static string ToDb(LockStatus value)
        {
            switch (value)
            {
                case LockStatus.Active: return "ACTIVE";
                case LockStatus.Released: return "RELEASED";
                default: throw Undefined(value);
            }
        }

        public static string ToDb(LockSource value)
        {
            switch (value)
            {
                case LockSource.Manual: return "MANUAL";
                case LockSource.Api: return "API";
                case LockSource.SetupCenter: return "SETUPCENTER";
                default: throw Undefined(value);
            }
        }

        public static string ToDb(EnforceState value)
        {
            switch (value)
            {
                case EnforceState.Pending: return "PENDING";
                case EnforceState.Applied: return "APPLIED";
                case EnforceState.Failed: return "FAILED";
                case EnforceState.NotNeeded: return "NOT_NEEDED";
                default: throw Undefined(value);
            }
        }

        public static string ToDb(Lanes value)
        {
            switch (value)
            {
                case Lanes.Left: return "LEFT";
                case Lanes.Right: return "RIGHT";
                case Lanes.Both: return "BOTH";
                default: throw Undefined(value);
            }
        }

        public static string ToDb(Role value)
        {
            switch (value)
            {
                case Role.Operator: return "OPERATOR";
                case Role.Qa: return "QA";
                case Role.Engineer: return "ENGINEER";
                case Role.Admin: return "ADMIN";
                default: throw Undefined(value);
            }
        }

        public static LockType ParseLockType(string text) { return Parse(LockTypes, text, "lock type"); }

        public static LockStatus ParseStatus(string text) { return Parse(Statuses, text, "lock status"); }

        public static LockSource ParseSource(string text) { return Parse(Sources, text, "lock source"); }

        public static EnforceState ParseEnforceState(string text) { return Parse(EnforceStates, text, "enforce state"); }

        public static Lanes ParseLanes(string text) { return Parse(LanesValues, text, "lanes"); }

        public static Role ParseRole(string text) { return Parse(Roles, text, "role"); }

        private static Dictionary<string, T> Build<T>(Func<T, string> toDb) where T : struct
        {
            var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (T value in Enum.GetValues(typeof(T)))
                map[toDb(value)] = value;
            return map;
        }

        private static T Parse<T>(Dictionary<string, T> map, string text, string kind)
        {
            T value;
            if (text != null && map.TryGetValue(text, out value))
                return value;
            throw new FormatException("Unknown " + kind + ": '" + text + "'.");
        }

        private static ArgumentOutOfRangeException Undefined<T>(T value) where T : struct
        {
            return new ArgumentOutOfRangeException("value", value, "Undefined " + typeof(T).Name + " value.");
        }
    }
}
