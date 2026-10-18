using System;
using System.Collections.Generic;
using System.Data.Common;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using NpgsqlTypes;

namespace Interlock.Service.Data
{
    /// <summary>Users and API keys in PostgreSQL. User and key names are unique ignoring case. Lists come ordered by id.</summary>
    public sealed class NpgsqlUserRepository : IUserRepository
    {
        private const string UserColumns = "id, username, pin_hash, role, active, created_at";
        private const string KeyColumns = "id, name, key_hash, can_unlock, active, created_at";

        private readonly NpgsqlDb _db;

        public NpgsqlUserRepository(NpgsqlDb db)
        {
            if (db == null)
                throw new ArgumentNullException(nameof(db));
            _db = db;
        }

        public AppUser FindUser(string username)
        {
            if (username == null)
                return null;

            return _db.Run(c =>
            {
                using (var command = _db.Command(c, "SELECT " + UserColumns + " FROM {schema}.app_user WHERE lower(username) = lower(@username)"))
                {
                    NpgsqlDb.Add(command, "username", NpgsqlDbType.Text, username);
                    using (var reader = command.ExecuteReader())
                        return reader.Read() ? ReadUser(reader) : null;
                }
            });
        }

        public IList<AppUser> ListUsers()
        {
            return _db.Run(c =>
            {
                using (var command = _db.Command(c, "SELECT " + UserColumns + " FROM {schema}.app_user ORDER BY id"))
                {
                    var users = new List<AppUser>();
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            users.Add(ReadUser(reader));
                    }
                    return users;
                }
            });
        }

        /// <summary>Adds the user, or updates PIN hash, role and active of the user with the same name (ignoring case), keeping its id, name and created_at.</summary>
        public void SaveUser(AppUser u)
        {
            if (u == null)
                throw new ArgumentNullException(nameof(u));

            _db.Run(c =>
            {
                using (var command = _db.Command(c,
                    "INSERT INTO {schema}.app_user (username, pin_hash, role, active, created_at) "
                    + "VALUES (@username, @pin_hash, @role, @active, @created_at) "
                    + "ON CONFLICT ((lower(username))) DO UPDATE SET pin_hash = EXCLUDED.pin_hash, role = EXCLUDED.role, active = EXCLUDED.active"))
                {
                    NpgsqlDb.Add(command, "username", NpgsqlDbType.Text, u.Username);
                    NpgsqlDb.Add(command, "pin_hash", NpgsqlDbType.Text, u.PinHash);
                    NpgsqlDb.Add(command, "role", NpgsqlDbType.Text, DbEnum.ToDb(u.Role));
                    NpgsqlDb.Add(command, "active", NpgsqlDbType.Boolean, u.Active);
                    NpgsqlDb.Add(command, "created_at", NpgsqlDbType.TimestampTz, NpgsqlDb.ToUtc(u.CreatedAtUtc));
                    command.ExecuteNonQuery();
                }
            });
        }

        /// <summary>The key with this hash, active or not; callers check <c>Active</c>.</summary>
        public ApiKeyRecord FindApiKeyByHash(string keyHash)
        {
            if (keyHash == null)
                return null;

            return _db.Run(c =>
            {
                using (var command = _db.Command(c, "SELECT " + KeyColumns + " FROM {schema}.api_key WHERE key_hash = @key_hash ORDER BY id LIMIT 1"))
                {
                    NpgsqlDb.Add(command, "key_hash", NpgsqlDbType.Text, keyHash);
                    using (var reader = command.ExecuteReader())
                        return reader.Read() ? ReadKey(reader) : null;
                }
            });
        }

        public IList<ApiKeyRecord> ListApiKeys()
        {
            return _db.Run(c =>
            {
                using (var command = _db.Command(c, "SELECT " + KeyColumns + " FROM {schema}.api_key ORDER BY id"))
                {
                    var keys = new List<ApiKeyRecord>();
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            keys.Add(ReadKey(reader));
                    }
                    return keys;
                }
            });
        }

        /// <summary>
        /// Stores the key and returns the stored record with its id. A name taken already (ignoring case) or a hash stored
        /// already is a unique violation.
        /// </summary>
        public ApiKeyRecord InsertApiKey(ApiKeyRecord k)
        {
            if (k == null)
                throw new ArgumentNullException(nameof(k));

            return _db.Run(c =>
            {
                using (var command = _db.Command(c,
                    "INSERT INTO {schema}.api_key (name, key_hash, can_unlock, active, created_at) "
                    + "VALUES (@name, @key_hash, @can_unlock, @active, @created_at) RETURNING " + KeyColumns))
                {
                    NpgsqlDb.Add(command, "name", NpgsqlDbType.Text, k.Name);
                    NpgsqlDb.Add(command, "key_hash", NpgsqlDbType.Text, k.KeyHash);
                    NpgsqlDb.Add(command, "can_unlock", NpgsqlDbType.Boolean, k.CanUnlock);
                    NpgsqlDb.Add(command, "active", NpgsqlDbType.Boolean, k.Active);
                    NpgsqlDb.Add(command, "created_at", NpgsqlDbType.TimestampTz, NpgsqlDb.ToUtc(k.CreatedAtUtc));
                    using (var reader = command.ExecuteReader())
                    {
                        reader.Read();
                        return ReadKey(reader);
                    }
                }
            });
        }

        public void SetApiKeyActive(long id, bool active)
        {
            _db.Run(c =>
            {
                using (var command = _db.Command(c, "UPDATE {schema}.api_key SET active = @active WHERE id = @id"))
                {
                    NpgsqlDb.Add(command, "active", NpgsqlDbType.Boolean, active);
                    NpgsqlDb.Add(command, "id", NpgsqlDbType.Bigint, id);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static AppUser ReadUser(DbDataReader r)
        {
            return new AppUser
            {
                Id = r.GetInt64(0),
                Username = r.GetString(1),
                PinHash = r.GetString(2),
                Role = DbEnum.ParseRole(r.GetString(3)),
                Active = r.GetBoolean(4),
                CreatedAtUtc = NpgsqlDb.GetUtc(r, 5)
            };
        }

        private static ApiKeyRecord ReadKey(DbDataReader r)
        {
            return new ApiKeyRecord
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                KeyHash = r.GetString(2),
                CanUnlock = r.GetBoolean(3),
                Active = r.GetBoolean(4),
                CreatedAtUtc = NpgsqlDb.GetUtc(r, 5)
            };
        }
    }
}
