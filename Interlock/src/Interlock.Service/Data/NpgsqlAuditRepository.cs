using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Npgsql;
using NpgsqlTypes;

namespace Interlock.Service.Data
{
    /// <summary>
    /// The action log and the decision log in PostgreSQL. Actions are listed by id; the decision search returns the newest
    /// rows first. A batch of decisions is written in one transaction: all of it or nothing. NUL characters are removed from
    /// every text written, because PostgreSQL rejects them and a rejected batch would fail on every retry.
    /// </summary>
    public sealed class NpgsqlAuditRepository : IAuditRepository
    {
        private const string DecisionColumns =
            "method, reason_code, uid, pn, mpn, supplier, line_path, machine_id, feeder_id, result, message, matched_lock_ids, created_at";
        private const string TrimChars = NpgsqlDb.TrimChars;

        private readonly NpgsqlDb _db;

        public NpgsqlAuditRepository(NpgsqlDb db)
        {
            if (db == null)
                throw new ArgumentNullException(nameof(db));
            _db = db;
        }

        /// <summary>Stores the entry with NUL characters removed from its texts; the database gives it its id. The caller's entry is not changed.</summary>
        public void AddAction(ActionLogEntry e)
        {
            if (e == null)
                throw new ArgumentNullException(nameof(e));

            _db.Run(c =>
            {
                using (var command = _db.Command(c,
                    "INSERT INTO {schema}.action_log (lock_id, action, request_json, success, error, attempt, created_at) "
                    + "VALUES (@lock_id, @action, @request_json, @success, @error, @attempt, @created_at)"))
                {
                    NpgsqlDb.Add(command, "lock_id", NpgsqlDbType.Bigint, e.LockId);
                    NpgsqlDb.Add(command, "action", NpgsqlDbType.Text, NpgsqlDb.WithoutNul(e.Action));
                    NpgsqlDb.Add(command, "request_json", NpgsqlDbType.Text, NpgsqlDb.WithoutNul(e.RequestJson));
                    NpgsqlDb.Add(command, "success", NpgsqlDbType.Boolean, e.Success);
                    NpgsqlDb.Add(command, "error", NpgsqlDbType.Text, NpgsqlDb.WithoutNul(e.Error));
                    NpgsqlDb.Add(command, "attempt", NpgsqlDbType.Integer, e.Attempt);
                    NpgsqlDb.Add(command, "created_at", NpgsqlDbType.TimestampTz, NpgsqlDb.ToUtc(e.CreatedAtUtc));
                    command.ExecuteNonQuery();
                }
            });
        }

        public IList<ActionLogEntry> GetActions(long lockId)
        {
            return _db.Run(c =>
            {
                using (var command = _db.Command(c,
                    "SELECT id, lock_id, action, request_json, success, error, attempt, created_at "
                    + "FROM {schema}.action_log WHERE lock_id = @lock_id ORDER BY id"))
                {
                    NpgsqlDb.Add(command, "lock_id", NpgsqlDbType.Bigint, lockId);
                    var actions = new List<ActionLogEntry>();
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            actions.Add(ReadAction(reader));
                    }
                    return actions;
                }
            });
        }

        /// <summary>
        /// Writes the whole batch in one transaction, with NUL characters removed from the texts; when one row fails, none is
        /// stored and the failure is thrown.
        /// </summary>
        public void AddDecisions(IList<DecisionLogEntry> e)
        {
            if (e == null)
                throw new ArgumentNullException(nameof(e));
            foreach (var entry in e)
            {
                if (entry == null)
                    throw new ArgumentException("The batch contains a null entry.", nameof(e));
            }
            if (e.Count == 0)
                return;

            _db.Run(c =>
            {
                using (var transaction = c.BeginTransaction())
                using (var command = _db.Command(c,
                    "INSERT INTO {schema}.decision_log (" + DecisionColumns + ") VALUES (@method, @reason_code, @uid, @pn, @mpn, "
                    + "@supplier, @line_path, @machine_id, @feeder_id, @result, @message, @matched_lock_ids, @created_at)",
                    transaction))
                {
                    var method = Text(command, "method");
                    var reasonCode = command.Parameters.Add(new NpgsqlParameter("reason_code", NpgsqlDbType.Integer));
                    var uid = Text(command, "uid");
                    var pn = Text(command, "pn");
                    var mpn = Text(command, "mpn");
                    var supplier = Text(command, "supplier");
                    var linePath = Text(command, "line_path");
                    var machineId = Text(command, "machine_id");
                    var feederId = Text(command, "feeder_id");
                    var result = Text(command, "result");
                    var message = Text(command, "message");
                    var matchedLockIds = Text(command, "matched_lock_ids");
                    var createdAt = command.Parameters.Add(new NpgsqlParameter("created_at", NpgsqlDbType.TimestampTz));

                    foreach (var d in e)
                    {
                        method.Value = LogText(d.Method);
                        reasonCode.Value = d.ReasonCode.HasValue ? (object)d.ReasonCode.Value : DBNull.Value;
                        uid.Value = LogText(d.Uid);
                        pn.Value = LogText(d.Pn);
                        mpn.Value = LogText(d.Mpn);
                        supplier.Value = LogText(d.Supplier);
                        linePath.Value = LogText(d.LinePath);
                        machineId.Value = LogText(d.MachineId);
                        feederId.Value = LogText(d.FeederId);
                        result.Value = LogText(d.Result);
                        message.Value = LogText(d.Message);
                        matchedLockIds.Value = LogText(d.MatchedLockIds);
                        createdAt.Value = NpgsqlDb.ToUtc(d.CreatedAtUtc);
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
            });
        }

        /// <summary>
        /// The newest <c>Limit</c> matching rows (kept within 1..5000), newest first; a time tie goes to the later insert. Uid
        /// and Pn match ignoring case and surrounding whitespace (blank = no filter); both time limits are inclusive.
        /// </summary>
        public IList<DecisionLogEntry> QueryDecisions(DecisionQuery q)
        {
            q = q ?? new DecisionQuery();

            var sql = new StringBuilder("SELECT " + DecisionColumns + " FROM {schema}.decision_log WHERE true");
            var parameters = new List<NpgsqlParameter>();
            if (!string.IsNullOrWhiteSpace(q.Uid))
            {
                sql.Append(" AND upper(btrim(uid, " + TrimChars + ")) = upper(@uid)");
                parameters.Add(NpgsqlDb.Param("uid", NpgsqlDbType.Text, q.Uid.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(q.Pn))
            {
                sql.Append(" AND upper(btrim(pn, " + TrimChars + ")) = upper(@pn)");
                parameters.Add(NpgsqlDb.Param("pn", NpgsqlDbType.Text, q.Pn.Trim()));
            }
            if (q.FromUtc.HasValue)
            {
                sql.Append(" AND created_at >= @from_utc");
                parameters.Add(NpgsqlDb.Param("from_utc", NpgsqlDbType.TimestampTz, NpgsqlDb.ToUtc(q.FromUtc.Value)));
            }
            if (q.ToUtc.HasValue)
            {
                sql.Append(" AND created_at <= @to_utc");
                parameters.Add(NpgsqlDb.Param("to_utc", NpgsqlDbType.TimestampTz, NpgsqlDb.ToUtc(q.ToUtc.Value)));
            }
            sql.Append(" ORDER BY created_at DESC, id DESC LIMIT @limit");
            parameters.Add(NpgsqlDb.Param("limit", NpgsqlDbType.Integer, NpgsqlDb.ClampLimit(q.Limit)));

            return _db.Run(c =>
            {
                using (var command = _db.Command(c, sql.ToString()))
                {
                    command.Parameters.AddRange(parameters.ToArray());
                    var decisions = new List<DecisionLogEntry>();
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            decisions.Add(ReadDecision(reader));
                    }
                    return decisions;
                }
            });
        }

        // ---- internals ----

        private static NpgsqlParameter Text(NpgsqlCommand command, string name)
        {
            return command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text));
        }

        /// <summary>A log text as a parameter value: NUL characters removed, null as <see cref="DBNull"/>.</summary>
        private static object LogText(string text)
        {
            return (object)NpgsqlDb.WithoutNul(text) ?? DBNull.Value;
        }

        private static ActionLogEntry ReadAction(DbDataReader r)
        {
            return new ActionLogEntry
            {
                Id = r.GetInt64(0),
                LockId = r.IsDBNull(1) ? (long?)null : r.GetInt64(1),
                Action = r.GetString(2),
                RequestJson = NpgsqlDb.GetText(r, 3),
                Success = r.GetBoolean(4),
                Error = NpgsqlDb.GetText(r, 5),
                Attempt = r.GetInt32(6),
                CreatedAtUtc = NpgsqlDb.GetUtc(r, 7)
            };
        }

        private static DecisionLogEntry ReadDecision(DbDataReader r)
        {
            return new DecisionLogEntry
            {
                Method = r.GetString(0),
                ReasonCode = r.IsDBNull(1) ? (int?)null : r.GetInt32(1),
                Uid = NpgsqlDb.GetText(r, 2),
                Pn = NpgsqlDb.GetText(r, 3),
                Mpn = NpgsqlDb.GetText(r, 4),
                Supplier = NpgsqlDb.GetText(r, 5),
                LinePath = NpgsqlDb.GetText(r, 6),
                MachineId = NpgsqlDb.GetText(r, 7),
                FeederId = NpgsqlDb.GetText(r, 8),
                Result = r.GetString(9),
                Message = NpgsqlDb.GetText(r, 10),
                MatchedLockIds = NpgsqlDb.GetText(r, 11),
                CreatedAtUtc = NpgsqlDb.GetUtc(r, 12)
            };
        }
    }
}
