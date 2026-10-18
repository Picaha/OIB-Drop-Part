using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Text;
using Npgsql;
using NpgsqlTypes;

namespace Interlock.Service.Data
{
    /// <summary>
    /// Locks in PostgreSQL (tables <c>lock</c> and <c>lock_applied_pu</c>). <c>target_norm</c> and <c>uid_norm</c> are
    /// computed here with <see cref="Normalizer.Norm"/>. Lists come ordered by id. Updates to an unknown id do nothing.
    /// </summary>
    public sealed class NpgsqlLockRepository : ILockRepository
    {
        private const string ActiveIndex = "lock_active_uq";
        private const string UniqueViolation = "23505";

        private const string Columns =
            "id, lock_type, target, target_norm, lanes, line_path, reason, source, created_by, created_at, "
            + "status, released_by, released_at, release_reason, enforce_state, enforce_error";

        private const string SelectLocks = "SELECT " + Columns + " FROM {schema}.lock";
        private const string TrimChars = NpgsqlDb.TrimChars;

        private readonly NpgsqlDb _db;

        public NpgsqlLockRepository(NpgsqlDb db)
        {
            if (db == null)
                throw new ArgumentNullException(nameof(db));
            _db = db;
        }

        public IList<LockRecord> LoadActive()
        {
            return _db.Run(c =>
            {
                using (var command = _db.Command(c, SelectLocks + " WHERE status = 'ACTIVE' ORDER BY id"))
                    return ReadLocks(command);
            });
        }

        public IList<LockRecord> LoadPendingReleases()
        {
            return _db.Run(c =>
            {
                using (var command = _db.Command(c, SelectLocks + " WHERE status = 'RELEASED' AND enforce_state = 'PENDING' ORDER BY id"))
                    return ReadLocks(command);
            });
        }

        /// <summary>Stores the lock as given, with <c>target_norm = Norm(target)</c>, and returns the stored row.</summary>
        public LockRecord Insert(LockRecord l)
        {
            if (l == null)
                throw new ArgumentNullException(nameof(l));

            return _db.Run(c =>
            {
                using (var command = _db.Command(c,
                    "INSERT INTO {schema}.lock (lock_type, target, target_norm, lanes, line_path, reason, source, created_by, "
                    + "created_at, status, released_by, released_at, release_reason, enforce_state, enforce_error) "
                    + "VALUES (@lock_type, @target, @target_norm, @lanes, @line_path, @reason, @source, @created_by, "
                    + "@created_at, @status, @released_by, @released_at, @release_reason, @enforce_state, @enforce_error) "
                    + "RETURNING " + Columns))
                {
                    NpgsqlDb.Add(command, "lock_type", NpgsqlDbType.Text, DbEnum.ToDb(l.Type));
                    NpgsqlDb.Add(command, "target", NpgsqlDbType.Text, l.Target);
                    NpgsqlDb.Add(command, "target_norm", NpgsqlDbType.Text, Normalizer.Norm(l.Target));
                    NpgsqlDb.Add(command, "lanes", NpgsqlDbType.Text, LanesText(l.Lanes));
                    NpgsqlDb.Add(command, "line_path", NpgsqlDbType.Text, l.LinePath);
                    NpgsqlDb.Add(command, "reason", NpgsqlDbType.Text, l.Reason);
                    NpgsqlDb.Add(command, "source", NpgsqlDbType.Text, DbEnum.ToDb(l.Source));
                    NpgsqlDb.Add(command, "created_by", NpgsqlDbType.Text, l.CreatedBy);
                    NpgsqlDb.Add(command, "created_at", NpgsqlDbType.TimestampTz, NpgsqlDb.ToUtc(l.CreatedAtUtc));
                    NpgsqlDb.Add(command, "status", NpgsqlDbType.Text, DbEnum.ToDb(l.Status));
                    NpgsqlDb.Add(command, "released_by", NpgsqlDbType.Text, l.ReleasedBy);
                    NpgsqlDb.Add(command, "released_at", NpgsqlDbType.TimestampTz, NpgsqlDb.ToUtcOrNull(l.ReleasedAtUtc));
                    NpgsqlDb.Add(command, "release_reason", NpgsqlDbType.Text, l.ReleaseReason);
                    NpgsqlDb.Add(command, "enforce_state", NpgsqlDbType.Text, DbEnum.ToDb(l.EnforceState));
                    NpgsqlDb.Add(command, "enforce_error", NpgsqlDbType.Text, l.EnforceError);

                    try
                    {
                        return ReadLocks(command)[0];
                    }
                    catch (PostgresException e) when (e.SqlState == UniqueViolation && e.ConstraintName == ActiveIndex)
                    {
                        throw new DuplicateActiveLockException("An active lock with the same identity already exists.", e);
                    }
                }
            });
        }

        /// <summary>Compares the normalised target, the normalised line (blank or null = plant-wide) and the lanes, as <c>lock_active_uq</c> does.</summary>
        public LockRecord FindActive(LockType t, string targetNorm, string linePath, Lanes? lanes)
        {
            var target = Normalizer.Norm(targetNorm);
            if (target == null)
                return null;

            return _db.Run(c =>
            {
                using (var command = _db.Command(c, SelectLocks
                    + " WHERE status = 'ACTIVE' AND lock_type = @lock_type AND target_norm = @target_norm"
                    + " AND upper(btrim(coalesce(line_path, ''), " + TrimChars + ")) = upper(@line_path)"
                    + " AND coalesce(lanes, '') = @lanes ORDER BY id LIMIT 1"))
                {
                    NpgsqlDb.Add(command, "lock_type", NpgsqlDbType.Text, DbEnum.ToDb(t));
                    NpgsqlDb.Add(command, "target_norm", NpgsqlDbType.Text, target);
                    NpgsqlDb.Add(command, "line_path", NpgsqlDbType.Text, Normalizer.Norm(linePath) ?? string.Empty);
                    NpgsqlDb.Add(command, "lanes", NpgsqlDbType.Text, LanesText(lanes) ?? string.Empty);
                    return FirstOrNull(ReadLocks(command));
                }
            });
        }

        public LockRecord Get(long id)
        {
            return _db.Run(c =>
            {
                using (var command = _db.Command(c, SelectLocks + " WHERE id = @id"))
                {
                    NpgsqlDb.Add(command, "id", NpgsqlDbType.Bigint, id);
                    return FirstOrNull(ReadLocks(command));
                }
            });
        }

        /// <summary>
        /// Status, type and enforce state by equality; the line normalised (blank = no filter); the text as a case-insensitive
        /// substring of the target or the reason, with LIKE wildcards taken literally (blank = no filter). Ordered by id,
        /// at most <c>Limit</c> rows, kept within 1..5000.
        /// </summary>
        public IList<LockRecord> Query(LockQuery q)
        {
            q = q ?? new LockQuery();

            var sql = new StringBuilder(SelectLocks + " WHERE true");
            var parameters = new List<NpgsqlParameter>();
            if (q.Status.HasValue)
            {
                sql.Append(" AND status = @status");
                parameters.Add(NpgsqlDb.Param("status", NpgsqlDbType.Text, DbEnum.ToDb(q.Status.Value)));
            }
            if (q.Type.HasValue)
            {
                sql.Append(" AND lock_type = @lock_type");
                parameters.Add(NpgsqlDb.Param("lock_type", NpgsqlDbType.Text, DbEnum.ToDb(q.Type.Value)));
            }
            if (q.EnforceState.HasValue)
            {
                sql.Append(" AND enforce_state = @enforce_state");
                parameters.Add(NpgsqlDb.Param("enforce_state", NpgsqlDbType.Text, DbEnum.ToDb(q.EnforceState.Value)));
            }
            if (!string.IsNullOrWhiteSpace(q.LinePath))
            {
                sql.Append(" AND upper(btrim(line_path, " + TrimChars + ")) = upper(@line_path)");
                parameters.Add(NpgsqlDb.Param("line_path", NpgsqlDbType.Text, q.LinePath.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(q.Text))
            {
                sql.Append(@" AND (target ILIKE @text ESCAPE E'\\' OR reason ILIKE @text ESCAPE E'\\')");
                parameters.Add(NpgsqlDb.Param("text", NpgsqlDbType.Text, "%" + EscapeLike(q.Text) + "%"));
            }
            sql.Append(" ORDER BY id LIMIT @limit");
            parameters.Add(NpgsqlDb.Param("limit", NpgsqlDbType.Integer, NpgsqlDb.ClampLimit(q.Limit)));

            return _db.Run(c =>
            {
                using (var command = _db.Command(c, sql.ToString()))
                {
                    command.Parameters.AddRange(parameters.ToArray());
                    return ReadLocks(command);
                }
            });
        }

        /// <summary>Releases an ACTIVE lock and sets it PENDING so the unlock is enforced; any other row stays as it is.</summary>
        public void MarkReleased(long id, string by, DateTime atUtc, string reason)
        {
            _db.Run(c =>
            {
                using (var command = _db.Command(c,
                    "UPDATE {schema}.lock SET status = 'RELEASED', released_by = @released_by, released_at = @released_at, "
                    + "release_reason = @release_reason, enforce_state = 'PENDING' WHERE id = @id AND status = 'ACTIVE'"))
                {
                    NpgsqlDb.Add(command, "released_by", NpgsqlDbType.Text, by);
                    NpgsqlDb.Add(command, "released_at", NpgsqlDbType.TimestampTz, NpgsqlDb.ToUtc(atUtc));
                    NpgsqlDb.Add(command, "release_reason", NpgsqlDbType.Text, reason);
                    NpgsqlDb.Add(command, "id", NpgsqlDbType.Bigint, id);
                    command.ExecuteNonQuery();
                }
            });
        }

        /// <summary>Sets the state of any lock, released ones included: the worker enforces releases too.</summary>
        public void SetEnforceState(long id, EnforceState s, string error)
        {
            _db.Run(c =>
            {
                using (var command = _db.Command(c,
                    "UPDATE {schema}.lock SET enforce_state = @enforce_state, enforce_error = @enforce_error WHERE id = @id"))
                {
                    NpgsqlDb.Add(command, "enforce_state", NpgsqlDbType.Text, DbEnum.ToDb(s));
                    NpgsqlDb.Add(command, "enforce_error", NpgsqlDbType.Text, error);
                    NpgsqlDb.Add(command, "id", NpgsqlDbType.Bigint, id);
                    command.ExecuteNonQuery();
                }
            });
        }

        /// <summary>Records the PU as given, unless the lock has an open row for it already. A blank uid records nothing.</summary>
        public void AddAppliedPu(long lockId, string uid, DateTime atUtc)
        {
            var uidNorm = Normalizer.Norm(uid);
            if (uidNorm == null)
                return;

            _db.Run(c =>
            {
                using (var command = _db.Command(c,
                    "INSERT INTO {schema}.lock_applied_pu (lock_id, uid, uid_norm, applied_at) VALUES (@lock_id, @uid, @uid_norm, @applied_at) "
                    + "ON CONFLICT (lock_id, uid_norm) WHERE released_at IS NULL DO NOTHING"))
                {
                    NpgsqlDb.Add(command, "lock_id", NpgsqlDbType.Bigint, lockId);
                    NpgsqlDb.Add(command, "uid", NpgsqlDbType.Text, uid);
                    NpgsqlDb.Add(command, "uid_norm", NpgsqlDbType.Text, uidNorm);
                    NpgsqlDb.Add(command, "applied_at", NpgsqlDbType.TimestampTz, NpgsqlDb.ToUtc(atUtc));
                    command.ExecuteNonQuery();
                }
            });
        }

        /// <summary>The uids of the lock's open rows as they were stored, ordered by id.</summary>
        public IList<string> GetOpenAppliedPus(long lockId)
        {
            return _db.Run(c =>
            {
                using (var command = _db.Command(c,
                    "SELECT uid FROM {schema}.lock_applied_pu WHERE lock_id = @lock_id AND released_at IS NULL ORDER BY id"))
                {
                    NpgsqlDb.Add(command, "lock_id", NpgsqlDbType.Bigint, lockId);
                    var uids = new List<string>();
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            uids.Add(reader.GetString(0));
                    }
                    return uids;
                }
            });
        }

        /// <summary>Closes every open row of the lock for this PU (compared normalised). A blank uid closes nothing.</summary>
        public void CloseAppliedPu(long lockId, string uid, DateTime atUtc)
        {
            var uidNorm = Normalizer.Norm(uid);
            if (uidNorm == null)
                return;

            _db.Run(c =>
            {
                using (var command = _db.Command(c,
                    "UPDATE {schema}.lock_applied_pu SET released_at = @released_at "
                    + "WHERE lock_id = @lock_id AND uid_norm = @uid_norm AND released_at IS NULL"))
                {
                    NpgsqlDb.Add(command, "released_at", NpgsqlDbType.TimestampTz, NpgsqlDb.ToUtc(atUtc));
                    NpgsqlDb.Add(command, "lock_id", NpgsqlDbType.Bigint, lockId);
                    NpgsqlDb.Add(command, "uid_norm", NpgsqlDbType.Text, uidNorm);
                    command.ExecuteNonQuery();
                }
            });
        }

        /// <summary>An ACTIVE PU lock on the uid, or an open applied row of an ACTIVE lock, other than <paramref name="exceptLockId"/>.</summary>
        public bool IsUidCoveredByOtherActiveLock(string uidNorm, long exceptLockId)
        {
            var uid = Normalizer.Norm(uidNorm);
            if (uid == null)
                return false;

            return _db.Run(c =>
            {
                using (var command = _db.Command(c,
                    "SELECT EXISTS (SELECT 1 FROM {schema}.lock "
                    + "WHERE status = 'ACTIVE' AND lock_type = 'PU' AND target_norm = @uid AND id <> @except_id) "
                    + "OR EXISTS (SELECT 1 FROM {schema}.lock_applied_pu a JOIN {schema}.lock l ON l.id = a.lock_id "
                    + "WHERE a.released_at IS NULL AND a.uid_norm = @uid AND l.status = 'ACTIVE' AND l.id <> @except_id)"))
                {
                    NpgsqlDb.Add(command, "uid", NpgsqlDbType.Text, uid);
                    NpgsqlDb.Add(command, "except_id", NpgsqlDbType.Bigint, exceptLockId);
                    return (bool)command.ExecuteScalar();
                }
            });
        }

        public DateTime? GetLatestReleaseUtc(LockType t, string targetNorm)
        {
            var target = Normalizer.Norm(targetNorm);
            if (target == null)
                return null;

            return _db.Run(c =>
            {
                using (var command = _db.Command(c,
                    "SELECT max(released_at) FROM {schema}.lock WHERE status = 'RELEASED' AND lock_type = @lock_type AND target_norm = @target_norm"))
                {
                    NpgsqlDb.Add(command, "lock_type", NpgsqlDbType.Text, DbEnum.ToDb(t));
                    NpgsqlDb.Add(command, "target_norm", NpgsqlDbType.Text, target);
                    using (var reader = command.ExecuteReader())
                    {
                        reader.Read();
                        return NpgsqlDb.GetNullableUtc(reader, 0);
                    }
                }
            });
        }

        // ---- internals ----

        private static IList<LockRecord> ReadLocks(NpgsqlCommand command)
        {
            var locks = new List<LockRecord>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    locks.Add(ReadLock(reader));
            }
            return locks;
        }

        private static LockRecord ReadLock(DbDataReader r)
        {
            return new LockRecord
            {
                Id = r.GetInt64(0),
                Type = DbEnum.ParseLockType(r.GetString(1)),
                Target = r.GetString(2),
                TargetNorm = r.GetString(3),
                Lanes = r.IsDBNull(4) ? (Lanes?)null : DbEnum.ParseLanes(r.GetString(4)),
                LinePath = NpgsqlDb.GetText(r, 5),
                Reason = r.GetString(6),
                Source = DbEnum.ParseSource(r.GetString(7)),
                CreatedBy = r.GetString(8),
                CreatedAtUtc = NpgsqlDb.GetUtc(r, 9),
                Status = DbEnum.ParseStatus(r.GetString(10)),
                ReleasedBy = NpgsqlDb.GetText(r, 11),
                ReleasedAtUtc = NpgsqlDb.GetNullableUtc(r, 12),
                ReleaseReason = NpgsqlDb.GetText(r, 13),
                EnforceState = DbEnum.ParseEnforceState(r.GetString(14)),
                EnforceError = NpgsqlDb.GetText(r, 15)
            };
        }

        private static LockRecord FirstOrNull(IList<LockRecord> rows)
        {
            return rows.Count == 0 ? null : rows[0];
        }

        private static string LanesText(Lanes? lanes)
        {
            return lanes.HasValue ? DbEnum.ToDb(lanes.Value) : null;
        }

        /// <summary>The text with the LIKE wildcards and the escape character (backslash) taken literally.</summary>
        private static string EscapeLike(string text)
        {
            return text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        }
    }
}
