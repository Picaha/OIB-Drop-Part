using System;
using System.Linq;
using System.Net;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Web;
using Interlock.Core.Api;
using Interlock.Core.Errors;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Security;
using Interlock.Service.Health;

namespace Interlock.Service.Rest
{
    /// <summary>
    /// The REST API on top of the Core services. Every operation turns any exception into the JSON fault of
    /// <see cref="RestErrors.ToFault"/>, authenticates first (except <c>session</c> and <c>health</c>) and delegates; the rules live in Core.
    /// </summary>
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Multiple, UseSynchronizationContext = false)]
    public sealed class InterlockRestApi : IInterlockRestApi
    {
        private readonly LockService _locks;
        private readonly AuthService _auth;
        private readonly IAuditRepository _audit;
        private readonly IStationDirectory _stations;
        private readonly HealthMonitor _health;
        private readonly LoginThrottle _throttle;
        private readonly ILog _log;

        public InterlockRestApi(LockService locks, AuthService auth, IAuditRepository audit, IStationDirectory stations,
            HealthMonitor health, LoginThrottle throttle, ILog log)
        {
            if (locks == null) throw new ArgumentNullException(nameof(locks));
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (audit == null) throw new ArgumentNullException(nameof(audit));
            if (stations == null) throw new ArgumentNullException(nameof(stations));
            if (health == null) throw new ArgumentNullException(nameof(health));
            if (throttle == null) throw new ArgumentNullException(nameof(throttle));
            if (log == null) throw new ArgumentNullException(nameof(log));

            _locks = locks;
            _auth = auth;
            _audit = audit;
            _stations = stations;
            _health = health;
            _throttle = throttle;
            _log = log;
        }

        public SessionDto Login(SessionRequestDto request)
        {
            return Run(() =>
            {
                if (request == null)
                    throw new InterlockException(ErrorKind.Validation, "A request body is required");
                var username = InputGuard.Clean(request.username, "username");
                var pin = InputGuard.CleanSecret(request.pin, "pin");
                var address = RemoteAddress();

                if (!_throttle.TryBegin(username, address))
                    throw new InterlockException(ErrorKind.Unauthenticated, "Too many attempts, try again later");

                // Every reserved attempt ends as a failure, a success or abandoned, whatever happens in between.
                var ended = false;
                LoginResult result;
                try
                {
                    result = _auth.Login(username, pin);
                    _throttle.RecordSuccess(username, address);
                    ended = true;
                }
                catch (InterlockException ex)
                {
                    if (ex.Kind == ErrorKind.Unauthenticated)
                    {
                        _throttle.RecordFailure(username, address);
                        ended = true;
                    }
                    throw;
                }
                finally
                {
                    if (!ended)
                        _throttle.Abandon(username, address);
                }

                return new SessionDto
                {
                    token = result.Token,
                    role = DbEnum.ToDb(result.Role),
                    expiresAt = DtoMapper.Iso(result.ExpiresAtUtc)
                };
            });
        }

        public LockDto CreateLock(CreateLockDto request)
        {
            return Run(() =>
            {
                var p = Authenticate();
                var result = _locks.CreateLock(p, DtoMapper.ToRequest(request));
                SetStatus(result.Created ? HttpStatusCode.Created : HttpStatusCode.OK);
                return DtoMapper.ToDto(result.Lock);
            });
        }

        public LockDto[] QueryLocks(string status, string type, string linePath, string q)
        {
            return Run(() =>
            {
                var p = Authenticate();
                return _locks.Query(p, DtoMapper.ToLockQuery(status, type, linePath, q)).Select(l => DtoMapper.ToDto(l)).ToArray();
            });
        }

        public LockDetailDto GetLock(string id)
        {
            return Run(() =>
            {
                var p = Authenticate();
                return DtoMapper.ToDto(_locks.Get(p, DtoMapper.ParseId(id, "lock id")));
            });
        }

        public LockDto ReleaseLock(string id, ReasonDto request)
        {
            return Run(() =>
            {
                var p = Authenticate();
                var lockId = DtoMapper.ParseId(id, "lock id");
                return DtoMapper.ToDto(_locks.Release(p, lockId, DtoMapper.ToReason(request)));
            });
        }

        public LockDto RetryLock(string id)
        {
            return Run(() =>
            {
                var p = Authenticate();
                return DtoMapper.ToDto(_locks.Retry(p, DtoMapper.ParseId(id, "lock id")));
            });
        }

        public DecisionDto[] QueryDecisions(string uid, string pn, string from, string to)
        {
            return Run(() =>
            {
                Authenticate();
                var query = DtoMapper.ToDecisionQuery(uid, pn, from, to);
                try
                {
                    return _audit.QueryDecisions(query).Select(d => DtoMapper.ToDto(d)).ToArray();
                }
                catch (DbUnavailableException)
                {
                    throw new InterlockException(ErrorKind.Unavailable, "Database unavailable");
                }
            });
        }

        public HealthDto GetHealth()
        {
            return Run(() => _health.Snapshot());
        }

        public string[] GetLines()
        {
            return Run(() =>
            {
                Authenticate();
                return _stations.LinePaths.ToArray();
            });
        }

        public StationDto[] GetStations()
        {
            return Run(() =>
            {
                Authenticate();
                return _stations.Stations.Select(s => DtoMapper.ToDto(s)).ToArray();
            });
        }

        public UserDto[] ListUsers()
        {
            return Run(() =>
            {
                var p = Authenticate();
                return _auth.ListUsers(p).Select(u => DtoMapper.ToDto(u)).ToArray();
            });
        }

        public UserDto SaveUser(UserDto request)
        {
            return Run(() =>
            {
                var p = Authenticate();
                if (request == null)
                    throw new InterlockException(ErrorKind.Validation, "A request body is required");
                var username = InputGuard.Clean(request.username, "username");
                var role = DtoMapper.ParseRole(request.role);
                var pin = InputGuard.CleanSecret(request.pin, "pin");

                var active = request.active ?? ActiveForMissingFlag(p, username);
                _auth.SaveUser(p, username, role, active, pin);
                return new UserDto { username = username, role = DbEnum.ToDb(role), active = active };
            });
        }

        public ApiKeyDto[] ListApiKeys()
        {
            return Run(() =>
            {
                var p = Authenticate();
                return _auth.ListApiKeys(p).Select(k => DtoMapper.ToDto(k, null)).ToArray();
            });
        }

        public ApiKeyDto CreateApiKey(ApiKeyDto request)
        {
            return Run(() =>
            {
                var p = Authenticate();
                if (request == null)
                    throw new InterlockException(ErrorKind.Validation, "A request body is required");
                var name = InputGuard.Clean(request.name, "name");

                var created = _auth.CreateApiKey(p, name, request.canUnlock);
                return DtoMapper.ToDto(created.Record, created.PlainKey);
            });
        }

        public ApiKeyDto SetApiKeyActive(string id, string active)
        {
            return Run(() =>
            {
                var p = Authenticate();
                var keyId = DtoMapper.ParseId(id, "API key id");
                var flag = DtoMapper.ParseFlag(active, "active");

                _auth.SetApiKeyActive(p, keyId, flag);
                var record = _auth.ListApiKeys(p).FirstOrDefault(k => k.Id == keyId);
                if (record == null)
                    throw new InterlockException(ErrorKind.NotFound, "API key not found");
                return DtoMapper.ToDto(record, null);
            });
        }

        // No "active" in the request: a new user is active, an existing user keeps the current value.
        private bool ActiveForMissingFlag(Principal p, string username)
        {
            var existing = _auth.ListUsers(p).FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            return existing == null || existing.Active;
        }

        private T Run<T>(Func<T> operation)
        {
            try
            {
                return operation();
            }
            catch (Exception ex)
            {
                throw RestErrors.ToFault(ex, _log);
            }
        }

        /// <summary>A bearer token wins over an API key when both are sent; the check itself is <see cref="AuthService.Authenticate"/>.</summary>
        private Principal Authenticate()
        {
            var context = WebOperationContext.Current;
            if (context == null)
                throw new InterlockException(ErrorKind.Unauthenticated, "Authentication required");

            var headers = context.IncomingRequest.Headers;
            return _auth.Authenticate(BearerToken(headers["Authorization"]), headers["X-Api-Key"]);
        }

        private static string BearerToken(string authorization)
        {
            const string scheme = "Bearer ";
            if (authorization == null)
                return null;
            var value = authorization.Trim();
            return value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? value.Substring(scheme.Length).Trim() : null;
        }

        private static void SetStatus(HttpStatusCode code)
        {
            var context = WebOperationContext.Current;
            if (context != null)
                context.OutgoingResponse.StatusCode = code;
        }

        private static string RemoteAddress()
        {
            try
            {
                var context = OperationContext.Current;
                if (context != null && context.IncomingMessageProperties.ContainsKey(RemoteEndpointMessageProperty.Name))
                    return ((RemoteEndpointMessageProperty)context.IncomingMessageProperties[RemoteEndpointMessageProperty.Name]).Address;
            }
            catch (Exception)
            {
                // No address to throttle by: the username alone is used.
            }
            return null;
        }
    }
}
