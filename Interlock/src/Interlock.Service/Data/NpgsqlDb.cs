using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Interlock.Core.Errors;
using Npgsql;
using NpgsqlTypes;

namespace Interlock.Service.Data
{
    /// <summary>
    /// Opens connections to the Interlock database. Every failure to connect, and every connection failure or transient
    /// server condition while a call runs, becomes <see cref="DbUnavailableException"/>; any other failure of a running call
    /// (constraint violations, syntax, permissions) goes through unchanged. Each call uses one short-lived connection from
    /// the Npgsql pool. Unless the connection string sets them, the connect timeout is 5 s and the command timeout 10 s:
    /// the lock write gate is held across database calls.
    /// The schema name is the only text put into SQL; it is checked against <c>^[a-z_][a-z0-9_]*$</c>.
    /// </summary>
    public sealed class NpgsqlDb
    {
        internal const int DefaultConnectTimeoutSeconds = 5;
        internal const int DefaultCommandTimeoutSeconds = 10;
        internal const int MaxRows = 5000;

        /// <summary>
        /// The btrim characters for the SQL side of a normalised comparison (space, tab, CR, LF) as an SQL literal: plain
        /// <c>btrim</c> strips spaces only, and scanners add CR/LF.
        /// </summary>
        internal const string TrimChars = @"E' \t\r\n'";

        private const string SchemaToken = "{schema}";
        private static readonly Regex SchemaName = new Regex(@"\A[a-z_][a-z0-9_]*\z");

        private readonly string _connectionString;

        public NpgsqlDb(string connectionString, string schema)
        {
            if (connectionString == null)
                throw new ArgumentNullException(nameof(connectionString));
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("A connection string is required.", nameof(connectionString));
            if (schema == null)
                throw new ArgumentNullException(nameof(schema));
            if (!SchemaName.IsMatch(schema))
                throw new ArgumentException("The schema name must match ^[a-z_][a-z0-9_]*$.", nameof(schema));

            _connectionString = WithDefaultTimeouts(connectionString);
            Schema = schema;
        }

        public string Schema { get; }

        /// <summary>
        /// An open connection; the caller disposes it. Every failure to connect throws <see cref="DbUnavailableException"/>
        /// with the cause as inner exception, server errors included (a rotated password, a pg_hba change, a missing
        /// database): no record can be at fault before a connection exists, so each of them means the database is unavailable.
        /// </summary>
        public NpgsqlConnection Open()
        {
            var connection = new NpgsqlConnection(_connectionString);
            try
            {
                connection.Open();
                return connection;
            }
            catch (Exception e)
            {
                connection.Dispose();
                throw new DbUnavailableException("Database unavailable: " + e.Message, e);
            }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on a fresh connection from <see cref="Open"/>. Failures while it runs are mapped with
        /// <see cref="Map"/>: connection failures and transient server conditions become <see cref="DbUnavailableException"/>.
        /// </summary>
        internal T Run<T>(Func<NpgsqlConnection, T> work)
        {
            try
            {
                using (var connection = Open())
                    return work(connection);
            }
            catch (Exception e)
            {
                var mapped = Map(e);
                if (ReferenceEquals(mapped, e))
                    throw;
                throw mapped;
            }
        }

        internal void Run(Action<NpgsqlConnection> work)
        {
            Run<object>(connection =>
            {
                work(connection);
                return null;
            });
        }

        /// <summary>A command for <paramref name="sql"/> with every <c>{schema}</c> replaced by the validated schema name.</summary>
        internal NpgsqlCommand Command(NpgsqlConnection connection, string sql, NpgsqlTransaction transaction = null)
        {
            return new NpgsqlCommand(sql.Replace(SchemaToken, Schema), connection, transaction);
        }

        // ---- error mapping ----

        /// <summary>
        /// True for server conditions that mean "try again later": connection exceptions (class 08), insufficient resources
        /// (class 53), server shutdown or start-up (57P01, 57P02, 57P03), serialization failure (40001) and deadlock (40P01).
        /// </summary>
        internal static bool IsTransientSqlState(string sqlState)
        {
            if (string.IsNullOrEmpty(sqlState))
                return false;
            if (sqlState.StartsWith("08", StringComparison.Ordinal) || sqlState.StartsWith("53", StringComparison.Ordinal))
                return true;
            switch (sqlState)
            {
                case "57P01":
                case "57P02":
                case "57P03":
                case "40001":
                case "40P01":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A <see cref="DbUnavailableException"/> wrapping <paramref name="e"/> when it is a connection failure or a transient
        /// server condition; otherwise <paramref name="e"/> itself.
        /// </summary>
        internal static Exception Map(Exception e)
        {
            if (e == null)
                throw new ArgumentNullException(nameof(e));

            var postgres = e as PostgresException;
            bool unavailable;
            if (postgres != null)
                unavailable = IsTransientSqlState(postgres.SqlState);
            else
                unavailable = e is NpgsqlException || e is SocketException || e is TimeoutException || e is IOException;

            return unavailable ? new DbUnavailableException("Database unavailable: " + e.Message, e) : e;
        }

        // ---- connection string ----

        /// <summary>The connection string with <c>Timeout=5</c> and <c>Command Timeout=10</c> added where it sets neither itself.</summary>
        internal static string WithDefaultTimeouts(string connectionString)
        {
            // NpgsqlConnectionStringBuilder.ContainsKey answers "is this keyword supported", so read the keys as written.
            var written = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var keys = new HashSet<string>(written.Keys.Cast<string>().Select(k => k.Replace(" ", string.Empty).ToUpperInvariant()));

            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!keys.Contains("TIMEOUT"))
                builder.Timeout = DefaultConnectTimeoutSeconds;
            if (!keys.Contains("COMMANDTIMEOUT"))
                builder.CommandTimeout = DefaultCommandTimeoutSeconds;
            return builder.ConnectionString;
        }

        // ---- values ----

        /// <summary>A row limit within 1..<see cref="MaxRows"/>.</summary>
        internal static int ClampLimit(int limit)
        {
            return Math.Max(1, Math.Min(MaxRows, limit));
        }

        /// <summary>A typed parameter; null becomes <see cref="DBNull"/>.</summary>
        internal static NpgsqlParameter Param(string name, NpgsqlDbType type, object value)
        {
            return new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value };
        }

        internal static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value)
        {
            command.Parameters.Add(Param(name, type, value));
        }

        /// <summary>The value as UTC: a local time is converted, an unspecified kind is taken as UTC already.</summary>
        internal static DateTime ToUtc(DateTime value)
        {
            switch (value.Kind)
            {
                case DateTimeKind.Utc: return value;
                case DateTimeKind.Local: return value.ToUniversalTime();
                default: return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }
        }

        internal static object ToUtcOrNull(DateTime? value)
        {
            return value.HasValue ? (object)ToUtc(value.Value) : null;
        }

        /// <summary>
        /// The text without NUL characters, which PostgreSQL rejects in text (22021). Used for the action and decision logs,
        /// so one stray NUL from a scanner or an OIB reply cannot make a log batch fail on every retry.
        /// </summary>
        internal static string WithoutNul(string text)
        {
            if (text == null || text.IndexOf('\0') < 0)
                return text;
            return text.Replace("\0", string.Empty);
        }

        internal static string GetText(DbDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        /// <summary>
        /// A timestamptz column as UTC (<c>Kind = Utc</c>). Read as <see cref="DateTimeOffset"/>: Npgsql 4 returns a
        /// timestamptz <see cref="DateTime"/> converted to local time.
        /// </summary>
        internal static DateTime GetUtc(DbDataReader reader, int ordinal)
        {
            return reader.GetFieldValue<DateTimeOffset>(ordinal).UtcDateTime;
        }

        internal static DateTime? GetNullableUtc(DbDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? (DateTime?)null : GetUtc(reader, ordinal);
        }
    }
}
