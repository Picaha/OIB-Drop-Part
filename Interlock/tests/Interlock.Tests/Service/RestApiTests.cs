using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Text;
using Interlock.Core.Api;
using Interlock.Core.Enforcement;
using Interlock.Core.Errors;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Security;
using Interlock.Service.Health;
using Interlock.Service.Rest;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Service
{
    /// <summary>
    /// Drives the REST API through a real <c>WebServiceHost</c> on a free local port (the Temporary_Listen_Addresses prefix needs
    /// no URL ACL) with in-memory fakes behind it. A platform that refuses to bind makes the fixture ignored, not failed.
    /// </summary>
    [TestFixture]
    public class RestApiTests
    {
        private sealed class Resp
        {
            public HttpStatusCode StatusCode;
            public string Body;
            public string ContentType;
        }

        private sealed class ThrowingAudit : IAuditRepository
        {
            public void AddAction(ActionLogEntry e) { }
            public IList<ActionLogEntry> GetActions(long lockId) { return new List<ActionLogEntry>(); }
            public void AddDecisions(IList<DecisionLogEntry> e) { }
            public IList<DecisionLogEntry> QueryDecisions(DecisionQuery q) { throw new InvalidOperationException("secret detail Host=db1"); }
        }

        private static readonly Principal Admin = Principal.ForUser("admin", Role.Admin);

        private InMemoryLockRepository _repo;
        private InMemoryAuditRepository _audit;
        private InMemoryUserRepository _users;
        private FakeStationDirectory _stations;
        private FakeClock _clock;
        private ListLog _log;
        private LockCache _cache;
        private ActionQueue _queue;
        private AlarmBoard _alarms;
        private AuthService _auth;
        private LockService _lockService;
        private CacheRefresher _refresher;
        private HealthMonitor _health;
        private RestHost _host;
        private HttpClient _http;
        private string _baseUrl;

        [SetUp]
        public void SetUp()
        {
            _repo = new InMemoryLockRepository();
            _audit = new InMemoryAuditRepository();
            _users = new InMemoryUserRepository();
            _stations = new FakeStationDirectory();
            _clock = new FakeClock();
            _log = new ListLog();
            _cache = new LockCache();
            _queue = new ActionQueue();
            _alarms = new AlarmBoard();
            var spool = new MemorySpool();
            _auth = new AuthService(_users, new SessionStore(_clock, TimeSpan.FromHours(8)), _clock);
            _lockService = new LockService(_repo, _audit, _cache, _queue, _stations, spool, _clock, _log);
            _refresher = new CacheRefresher(_repo, spool, _cache, _queue, _clock, _log);
            _refresher.Tick();
            _health = new HealthMonitor(new FakeSetupCenterClient(), new FakeLineControlClient(), new FakeMonitoringClient(),
                _repo, _refresher, _cache, _queue, _alarms, _clock, _log);
            _health.Tick();

            _auth.CreateInitialUser("admin", Role.Admin, "1234");
            _auth.CreateInitialUser("qa1", Role.Qa, "1234");
            _auth.CreateInitialUser("op1", Role.Operator, "1234");

            StartHost(_audit);
        }

        [TearDown]
        public void TearDown()
        {
            if (_http != null)
                _http.Dispose();
            if (_host != null)
                _host.Dispose();
        }

        private void StartHost(IAuditRepository audit)
        {
            var api = new InterlockRestApi(_lockService, _auth, audit, _stations, _health, new LoginThrottle(_clock), _log);
            var port = FreePort();
            _baseUrl = "http://localhost:" + port + "/Temporary_Listen_Addresses/interlock-" + Guid.NewGuid().ToString("N") + "/interlock/v1";
            _host = new RestHost(api, _baseUrl, _log);
            try
            {
                _host.Open();
            }
            catch (Exception ex)
            {
                _host = null;
                Assert.Ignore("The platform refused to bind the REST test host: " + ex.GetType().Name + ": " + ex.Message);
            }

            _http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        // ---- helpers ----

        private Resp Send(HttpMethod method, string path, string json = null, string bearer = null, string apiKey = null, string contentType = "application/json")
        {
            var request = new HttpRequestMessage(method, _baseUrl + "/" + path);
            if (json != null)
                request.Content = new StringContent(json, Encoding.UTF8, contentType);
            if (bearer != null)
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
            if (apiKey != null)
                request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

            using (var response = _http.SendAsync(request).GetAwaiter().GetResult())
            {
                return new Resp
                {
                    StatusCode = response.StatusCode,
                    Body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                    ContentType = response.Content.Headers.ContentType == null ? null : response.Content.Headers.ContentType.MediaType
                };
            }
        }

        private Resp Get(string path, string bearer = null, string apiKey = null)
        {
            return Send(HttpMethod.Get, path, null, bearer, apiKey);
        }

        private Resp Post(string path, string json, string bearer = null, string apiKey = null)
        {
            return Send(HttpMethod.Post, path, json, bearer, apiKey);
        }

        private static T Parse<T>(Resp r)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(r.Body)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
        }

        /// <summary>A JSON object from name/value pairs; a string value is escaped so control characters survive as \uXXXX.</summary>
        private static string Obj(params object[] pairs)
        {
            var sb = new StringBuilder("{");
            for (var i = 0; i < pairs.Length; i += 2)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(pairs[i]).Append("\":");
                var v = pairs[i + 1];
                if (v is string) Quote(sb, (string)v);
                else if (v is bool) sb.Append((bool)v ? "true" : "false");
                else sb.Append(v);
            }
            return sb.Append('}').ToString();
        }

        private static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                if (c == '"' || c == '\\' || c < 0x20 || c == 0x7F || c == '/')
                    sb.Append("\\u").Append(((int)c).ToString("x4"));
                else
                    sb.Append(c);
            }
            sb.Append('"');
        }

        private string Login(string user, string pin = "1234")
        {
            var r = Post("session", Obj("username", user, "pin", pin));
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK), r.Body);
            return Parse<SessionDto>(r).token;
        }

        private Resp CreatePu(string token, string target = "U1", string reason = "bad")
        {
            return Post("locks", Obj("type", "PU", "target", target, "reason", reason), bearer: token);
        }

        private static void AssertError(Resp r, HttpStatusCode status, string error)
        {
            Assert.That(r.StatusCode, Is.EqualTo(status), r.Body);
            StringAssert.Contains("\"error\":\"" + error + "\"", r.Body);
            StringAssert.StartsWith("application/json", r.ContentType);
        }

        // ---- the tests ----

        [Test]
        public void Login_then_create_then_get()
        {
            var s = Parse<SessionDto>(Post("session", Obj("username", "qa1", "pin", "1234")));
            Assert.That(s.role, Is.EqualTo("QA"));
            Assert.That(s.token, Is.Not.Empty);
            Assert.That(s.expiresAt, Is.EqualTo("2026-10-18T08:00:00.0000000Z"));

            var created = CreatePu(s.token, "U1");
            Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            var lockDto = Parse<LockDto>(created);
            Assert.That(lockDto.type, Is.EqualTo("PU"));
            Assert.That(lockDto.status, Is.EqualTo("ACTIVE"));
            Assert.That(lockDto.source, Is.EqualTo("MANUAL"));
            Assert.That(lockDto.createdBy, Is.EqualTo("qa1"));

            var again = CreatePu(s.token, "u1");
            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(Parse<LockDto>(again).id, Is.EqualTo(lockDto.id));

            var detail = Get("locks/" + lockDto.id, s.token);
            Assert.That(detail.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var d = Parse<LockDetailDto>(detail);
            Assert.That(d.@lock.id, Is.EqualTo(lockDto.id));
            Assert.That(d.actions, Is.Not.Null);
            StringAssert.Contains("\"lock\":{", detail.Body);
        }

        [Test]
        public void Query_filters_locks()
        {
            var token = Login("qa1");
            CreatePu(token, "U1");
            Post("locks", Obj("type", "PN", "target", "PN-7", "reason", "recall"), bearer: token);

            Assert.That(Parse<LockDto[]>(Get("locks", token)), Has.Length.EqualTo(2));
            var pn = Parse<LockDto[]>(Get("locks?type=PN&status=ACTIVE", token));
            Assert.That(pn, Has.Length.EqualTo(1));
            Assert.That(pn[0].target, Is.EqualTo("PN-7"));
            Assert.That(Parse<LockDto[]>(Get("locks?q=recall", token)), Has.Length.EqualTo(1));
            Assert.That(Parse<LockDto[]>(Get("locks?status=RELEASED", token)), Is.Empty);
            AssertError(Get("locks?status=bogus", token), HttpStatusCode.BadRequest, "validation");
        }

        [Test]
        public void Missing_credentials_is_401_json()
        {
            var r = Get("locks");

            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            StringAssert.Contains("\"error\":\"unauthenticated\"", r.Body);
            StringAssert.StartsWith("application/json", r.ContentType);
        }

        [Test]
        public void Every_protected_endpoint_demands_credentials()
        {
            foreach (var path in new[] { "locks", "locks/1", "decisions", "lines", "stations", "admin/users", "admin/apikeys" })
                AssertError(Get(path), HttpStatusCode.Unauthorized, "unauthenticated");
            AssertError(Post("locks", Obj("type", "PU", "target", "U1", "reason", "x")), HttpStatusCode.Unauthorized, "unauthenticated");
            AssertError(Post("locks/1/release", Obj("reason", "x")), HttpStatusCode.Unauthorized, "unauthenticated");
            AssertError(Post("locks/1/retry", "{}"), HttpStatusCode.Unauthorized, "unauthenticated");
            AssertError(Post("admin/users", Obj("username", "x")), HttpStatusCode.Unauthorized, "unauthenticated");
            AssertError(Post("admin/apikeys", Obj("name", "x")), HttpStatusCode.Unauthorized, "unauthenticated");
            AssertError(Send(HttpMethod.Put, "admin/apikeys/1?active=false"), HttpStatusCode.Unauthorized, "unauthenticated");
        }

        [Test]
        public void Invalid_token_is_401()
        {
            AssertError(Get("locks", "not-a-real-token"), HttpStatusCode.Unauthorized, "unauthenticated");
        }

        [Test]
        public void Operator_release_is_403()
        {
            var op = Login("op1");
            var id = Parse<LockDto>(CreatePu(op)).id;

            var r = Post("locks/" + id + "/release", Obj("reason", "x"), op);

            AssertError(r, HttpStatusCode.Forbidden, "forbidden");
        }

        [Test]
        public void Qa_release_works_then_a_second_release_is_409_and_an_unknown_lock_is_404()
        {
            var qa = Login("qa1");
            var id = Parse<LockDto>(CreatePu(qa)).id;

            var released = Post("locks/" + id + "/release", Obj("reason", "reel checked"), qa);
            Assert.That(released.StatusCode, Is.EqualTo(HttpStatusCode.OK), released.Body);
            var dto = Parse<LockDto>(released);
            Assert.That(dto.status, Is.EqualTo("RELEASED"));
            Assert.That(dto.releasedBy, Is.EqualTo("qa1"));
            Assert.That(dto.releaseReason, Is.EqualTo("reel checked"));
            Assert.That(dto.releasedAt, Is.Not.Null);

            AssertError(Post("locks/" + id + "/release", Obj("reason", "again"), qa), HttpStatusCode.Conflict, "invalidstate");
            AssertError(Post("locks/9999/release", Obj("reason", "x"), qa), HttpStatusCode.NotFound, "notfound");
            AssertError(Get("locks/9999", qa), HttpStatusCode.NotFound, "notfound");
        }

        [Test]
        public void Release_without_a_reason_or_body_is_400()
        {
            var qa = Login("qa1");
            var id = Parse<LockDto>(CreatePu(qa)).id;

            AssertError(Post("locks/" + id + "/release", Obj("reason", "  "), qa), HttpStatusCode.BadRequest, "validation");
            AssertError(Post("locks/" + id + "/release", "", qa), HttpStatusCode.BadRequest, "validation");
        }

        [Test]
        public void Retry_of_a_lock_that_has_not_failed_is_409_and_of_a_failed_lock_is_200()
        {
            var qa = Login("qa1");
            var id = Parse<LockDto>(CreatePu(qa)).id;
            AssertError(Post("locks/" + id + "/retry", "{}", qa), HttpStatusCode.Conflict, "invalidstate");

            _repo.SetEnforceState(id, EnforceState.Failed, "oib down");
            var r = Post("locks/" + id + "/retry", "{}", qa);

            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK), r.Body);
            Assert.That(Parse<LockDto>(r).enforceState, Is.EqualTo("PENDING"));
        }

        [Test]
        public void Retry_works_without_a_request_body_or_content_type()
        {
            var qa = Login("qa1");
            var id = Parse<LockDto>(CreatePu(qa)).id;
            _repo.SetEnforceState(id, EnforceState.Failed, "x");

            var r = Send(HttpMethod.Post, "locks/" + id + "/retry", bearer: qa);

            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK), r.Body);
        }

        [Test]
        public void Api_key_header_works()
        {
            var key = _auth.CreateApiKey(Admin, "mes-line1", false);

            var r = Post("locks", Obj("type", "PU", "target", "U1", "reason", "bad"), apiKey: key.PlainKey);

            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.Created), r.Body);
            var dto = Parse<LockDto>(r);
            Assert.That(dto.source, Is.EqualTo("API"));
            Assert.That(dto.createdBy, Is.EqualTo("mes-line1"));
        }

        [Test]
        public void An_api_key_without_unlock_cannot_release_but_one_with_it_can()
        {
            var plain = _auth.CreateApiKey(Admin, "plain", false).PlainKey;
            var unlock = _auth.CreateApiKey(Admin, "unlocker", true).PlainKey;
            var id = Parse<LockDto>(Post("locks", Obj("type", "PU", "target", "U1", "reason", "bad"), apiKey: plain)).id;

            AssertError(Post("locks/" + id + "/release", Obj("reason", "x"), apiKey: plain), HttpStatusCode.Forbidden, "forbidden");
            Assert.That(Post("locks/" + id + "/release", Obj("reason", "x"), apiKey: unlock).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public void An_unknown_api_key_is_401()
        {
            AssertError(Get("locks", apiKey: "nope"), HttpStatusCode.Unauthorized, "unauthenticated");
        }

        [Test]
        public void When_both_are_sent_the_bearer_token_wins()
        {
            var good = _auth.CreateApiKey(Admin, "k1", false).PlainKey;
            var qa = Login("qa1");

            var badBearerGoodKey = Get("locks", "garbage-token", good);
            var goodBearerBadKey = Post("locks", Obj("type", "PU", "target", "U1", "reason", "x"), qa, "garbage-key");

            AssertError(badBearerGoodKey, HttpStatusCode.Unauthorized, "unauthenticated");
            Assert.That(goodBearerBadKey.StatusCode, Is.EqualTo(HttpStatusCode.Created), goodBearerBadKey.Body);
            Assert.That(Parse<LockDto>(goodBearerBadKey).source, Is.EqualTo("MANUAL"));
        }

        [Test]
        public void Health_needs_no_auth()
        {
            var r = Get("health");

            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var h = Parse<HealthDto>(r);
            Assert.That(h.dbOk, Is.True);
            Assert.That(h.setupCenterConnected, Is.True);
            Assert.That(h.setupCenterLicense, Is.True);
            Assert.That(h.lineControlLicense, Is.True);
            Assert.That(h.monitoringOk, Is.True);
            Assert.That(h.cacheLoadedAt, Is.Not.Null);
            StringAssert.Contains("\"queueLength\":0", r.Body);
            StringAssert.Contains("\"cacheRefreshFailures\":0", r.Body);
        }

        [Test]
        public void Health_shows_alarms_and_cache_errors_without_leaking_internals()
        {
            _alarms.Raise("Setup Center unreachable", _clock.UtcNow);
            _repo.Down = true;
            _refresher.Tick();

            var r = Get("health");

            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var h = Parse<HealthDto>(r);
            Assert.That(h.dbOk, Is.False);
            Assert.That(h.cacheRefreshFailures, Is.EqualTo(1));
            Assert.That(h.alarms.Single(), Does.EndWith("Setup Center unreachable"));
            StringAssert.DoesNotContain("   at ", r.Body);
            StringAssert.DoesNotContain("Password", r.Body);
        }

        [Test]
        public void Malformed_json_is_400_json()
        {
            var qa = Login("qa1");

            AssertError(Post("locks", "{ this is not json", qa), HttpStatusCode.BadRequest, "validation");
            AssertError(Post("session", "<xml/>"), HttpStatusCode.BadRequest, "validation");
            AssertError(Post("locks", "[1,2,3]", qa), HttpStatusCode.BadRequest, "validation");
        }

        [Test]
        public void A_missing_body_is_400_json()
        {
            var qa = Login("qa1");

            AssertError(Send(HttpMethod.Post, "locks", bearer: qa), HttpStatusCode.BadRequest, "validation");
            AssertError(Send(HttpMethod.Post, "session"), HttpStatusCode.BadRequest, "validation");
        }

        [Test]
        public void A_text_plain_content_type_is_still_read_as_json()
        {
            var r = Send(HttpMethod.Post, "session", Obj("username", "qa1", "pin", "1234"), contentType: "text/plain");

            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK), r.Body);
        }

        [Test]
        public void A_non_numeric_id_is_400()
        {
            var qa = Login("qa1");

            AssertError(Get("locks/abc", qa), HttpStatusCode.BadRequest, "validation");
            AssertError(Post("locks/abc/release", Obj("reason", "x"), qa), HttpStatusCode.BadRequest, "validation");
        }

        [Test]
        public void Unknown_address_is_a_json_404_and_a_wrong_method_a_json_error()
        {
            var qa = Login("qa1");

            var notFound = Get("nothing/here", qa);
            var wrongMethod = Send(HttpMethod.Delete, "locks", bearer: qa);

            AssertError(notFound, HttpStatusCode.NotFound, "notfound");
            AssertError(wrongMethod, HttpStatusCode.MethodNotAllowed, "validation");
            StringAssert.DoesNotContain("<html", notFound.Body.ToLowerInvariant());
            StringAssert.DoesNotContain("<html", wrongMethod.Body.ToLowerInvariant());
        }

        [Test]
        public void Unknown_type_and_missing_fields_are_400_with_the_message()
        {
            var qa = Login("qa1");

            var unknown = Post("locks", Obj("type", "REEL", "target", "U1", "reason", "x"), qa);
            AssertError(unknown, HttpStatusCode.BadRequest, "validation");
            StringAssert.Contains("Unknown type 'REEL'", unknown.Body);

            AssertError(Post("locks", Obj("type", "PU", "target", "", "reason", "x"), qa), HttpStatusCode.BadRequest, "validation");
            AssertError(Post("locks", Obj("type", "PU", "target", "U1", "reason", ""), qa), HttpStatusCode.BadRequest, "validation");
        }

        [Test]
        public void Database_down_is_503_for_writes_and_decisions()
        {
            var qa = Login("qa1");
            _repo.Down = true;
            _audit.Down = true;

            AssertError(CreatePu(qa), HttpStatusCode.ServiceUnavailable, "unavailable");
            AssertError(Get("locks", qa), HttpStatusCode.ServiceUnavailable, "unavailable");
            AssertError(Get("decisions?uid=U1", qa), HttpStatusCode.ServiceUnavailable, "unavailable");
        }

        [Test]
        public void Login_while_the_user_store_is_down_is_503()
        {
            _users.Down = true;

            AssertError(Post("session", Obj("username", "qa1", "pin", "1234")), HttpStatusCode.ServiceUnavailable, "unavailable");
        }

        [Test]
        public void Wrong_pin_and_unknown_user_are_401()
        {
            AssertError(Post("session", Obj("username", "qa1", "pin", "9999")), HttpStatusCode.Unauthorized, "unauthenticated");
            AssertError(Post("session", Obj("username", "ghost", "pin", "1234")), HttpStatusCode.Unauthorized, "unauthenticated");
        }

        [Test]
        public void Decisions_are_listed_for_a_signed_in_caller()
        {
            _audit.AddDecisions(new List<DecisionLogEntry>
            {
                new DecisionLogEntry { Method = "ScanCheck", Uid = "U1", Result = "BLOCK", Message = "Interlock: PU U1 locked", MatchedLockIds = "5", CreatedAtUtc = new DateTime(2026, 10, 18, 1, 0, 0, DateTimeKind.Utc) },
                new DecisionLogEntry { Method = "ScanCheck", Uid = "U2", Result = "OK", CreatedAtUtc = new DateTime(2026, 10, 18, 2, 0, 0, DateTimeKind.Utc) }
            });
            var qa = Login("qa1");

            var one = Parse<DecisionDto[]>(Get("decisions?uid=u1", qa));
            Assert.That(one, Has.Length.EqualTo(1));
            Assert.That(one[0].result, Is.EqualTo("BLOCK"));
            Assert.That(one[0].matchedLockIds, Is.EqualTo("5"));
            Assert.That(one[0].createdAt, Is.EqualTo("2026-10-18T01:00:00.0000000Z"));

            var late = Parse<DecisionDto[]>(Get("decisions?from=2026-10-18T01:30:00Z", qa));
            Assert.That(late.Single().uid, Is.EqualTo("U2"));
            AssertError(Get("decisions?from=notadate", qa), HttpStatusCode.BadRequest, "validation");
        }

        [Test]
        public void Lines_and_stations_come_from_the_directory()
        {
            _stations.LinePaths = new List<string> { "Plant\\Line1" };
            _stations.Stations = new List<StationRef> { new StationRef { LinePath = "Plant\\Line1", StationPath = "Plant\\Line1\\S1" } };
            var qa = Login("qa1");

            Assert.That(Parse<string[]>(Get("lines", qa)), Is.EqualTo(new[] { "Plant\\Line1" }));
            var stations = Parse<StationDto[]>(Get("stations", qa));
            Assert.That(stations.Single().stationPath, Is.EqualTo("Plant\\Line1\\S1"));
            Assert.That(stations.Single().linePath, Is.EqualTo("Plant\\Line1"));
        }

        [Test]
        public void An_unexpected_exception_is_500_internal_error_with_details_only_in_the_log()
        {
            _host.Dispose();
            _http.Dispose();
            StartHost(new ThrowingAudit());
            var qa = Login("qa1");

            var r = Get("decisions", qa);

            AssertError(r, HttpStatusCode.InternalServerError, "internal");
            StringAssert.Contains("\"message\":\"Internal error\"", r.Body);
            StringAssert.DoesNotContain("secret detail", r.Body);
            StringAssert.DoesNotContain("db1", r.Body);
            Assert.That(_log.Errors.Any(e => e.Contains("secret detail")), Is.True);
        }

        // ---- R36b: control characters ----

        [Test]
        public void Control_characters_inside_a_field_are_400_for_every_string_field()
        {
            var qa = Login("qa1");

            foreach (var bad in new[] { "A\0B", "A\r\nB", "A\u007FB" })
            {
                AssertInvalid(Post("locks", Obj("type", "PU", "target", bad, "reason", "x"), qa), "target");
                AssertInvalid(Post("locks", Obj("type", "PU", "target", "U1", "reason", bad), qa), "reason");
                AssertInvalid(Post("locks", Obj("type", "PU", "target", "U1", "reason", "x", "linePath", bad), qa), "linePath");
                AssertInvalid(Post("locks", Obj("type", bad, "target", "U1", "reason", "x"), qa), "type");
                AssertInvalid(Post("locks/1/release", Obj("reason", bad), qa), "reason");
                AssertInvalid(Post("admin/users", Obj("username", bad, "role", "QA", "active", true, "pin", "1234"), Login("admin")), "username");
                AssertInvalid(Post("admin/apikeys", Obj("name", bad, "canUnlock", false), Login("admin")), "name");
                AssertInvalid(Post("session", Obj("username", bad, "pin", "1234")), "username");
                AssertInvalid(Post("session", Obj("username", "qa1", "pin", bad)), "pin");
            }

            AssertInvalid(Get("locks?q=a%00b", qa), "q");
            AssertInvalid(Get("locks?linePath=a%0Ab", qa), "linePath");
            AssertInvalid(Get("decisions?uid=a%0Db", qa), "uid");
            Assert.That(_repo.Query(new LockQuery()), Is.Empty);
        }

        private static void AssertInvalid(Resp r, string field)
        {
            AssertError(r, HttpStatusCode.BadRequest, "validation");
            StringAssert.Contains("Invalid character in " + field, r.Body);
        }

        [Test]
        public void Scanner_line_breaks_around_a_value_are_trimmed()
        {
            var qa = Login("qa1");

            var r = Post("locks", Obj("type", "PU", "target", "U7\r\n", "reason", "\r\nbad reel\r\n"), qa);

            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.Created), r.Body);
            var dto = Parse<LockDto>(r);
            Assert.That(dto.target, Is.EqualTo("U7"));
            Assert.That(dto.reason, Is.EqualTo("bad reel"));
            Assert.That(Parse<LockDto[]>(Get("locks?q=U7%0D%0A", qa)), Has.Length.EqualTo(1));
        }

        // ---- admin endpoints and R36a ----

        [Test]
        public void Admin_endpoints_need_an_admin_user()
        {
            var qa = Login("qa1");
            var key = _auth.CreateApiKey(Admin, "k", true).PlainKey;

            AssertError(Get("admin/users", qa), HttpStatusCode.Forbidden, "forbidden");
            AssertError(Get("admin/apikeys", qa), HttpStatusCode.Forbidden, "forbidden");
            AssertError(Get("admin/apikeys", apiKey: key), HttpStatusCode.Forbidden, "forbidden");
            AssertError(Post("admin/users", Obj("username", "n", "role", "QA", "active", true, "pin", "1234"), qa), HttpStatusCode.Forbidden, "forbidden");
            AssertError(Post("admin/apikeys", Obj("name", "n", "canUnlock", false), qa), HttpStatusCode.Forbidden, "forbidden");
            AssertError(Send(HttpMethod.Put, "admin/apikeys/1?active=false", bearer: qa), HttpStatusCode.Forbidden, "forbidden");
        }

        [Test]
        public void Admin_lists_never_contain_a_pin_hash_or_a_key_hash()
        {
            var admin = Login("admin");
            var created = _auth.CreateApiKey(Admin, "mes", true);
            var keyHash = _users.ListApiKeys().Single().KeyHash;
            var pinHashes = _users.ListUsers().Select(u => u.PinHash).ToList();

            var users = Get("admin/users", admin);
            var keys = Get("admin/apikeys", admin);

            Assert.That(users.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(keys.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            foreach (var body in new[] { users.Body, keys.Body })
            {
                StringAssert.DoesNotContain("hash", body.ToLowerInvariant());
                StringAssert.DoesNotContain("pbkdf2", body.ToLowerInvariant());
                StringAssert.DoesNotContain(keyHash, body);
                StringAssert.DoesNotContain(created.PlainKey, body);
                foreach (var h in pinHashes)
                    StringAssert.DoesNotContain(h, body);
            }
            StringAssert.DoesNotContain("\"pin\"", users.Body);
            StringAssert.DoesNotContain("\"key\"", keys.Body);
            Assert.That(Parse<UserDto[]>(users).Select(u => u.username), Is.EquivalentTo(new[] { "admin", "qa1", "op1" }));
            Assert.That(Parse<ApiKeyDto[]>(keys).Single().name, Is.EqualTo("mes"));
        }

        [Test]
        public void Saving_a_user_and_creating_a_key_never_echo_secrets_except_the_new_key_once()
        {
            var admin = Login("admin");

            var user = Post("admin/users", Obj("username", "eng1", "role", "engineer", "active", true, "pin", "5678"), admin);
            Assert.That(user.StatusCode, Is.EqualTo(HttpStatusCode.OK), user.Body);
            StringAssert.DoesNotContain("5678", user.Body);
            Assert.That(Parse<UserDto>(user).role, Is.EqualTo("ENGINEER"));
            Assert.That(Login("eng1", "5678"), Is.Not.Empty);

            var key = Post("admin/apikeys", Obj("name", "mes", "canUnlock", true), admin);
            Assert.That(key.StatusCode, Is.EqualTo(HttpStatusCode.OK), key.Body);
            var dto = Parse<ApiKeyDto>(key);
            Assert.That(dto.key, Is.Not.Empty);
            Assert.That(dto.canUnlock, Is.True);
            Assert.That(dto.active, Is.True);
            StringAssert.DoesNotContain(_users.ListApiKeys().Single().KeyHash, key.Body);
            Assert.That(Post("locks", Obj("type", "PU", "target", "U1", "reason", "x"), apiKey: dto.key).StatusCode, Is.EqualTo(HttpStatusCode.Created));

            AssertError(Post("admin/users", Obj("username", "eng2", "role", "boss", "active", true, "pin", "5678"), admin), HttpStatusCode.BadRequest, "validation");
            AssertError(Post("admin/apikeys", Obj("name", "MES", "canUnlock", false), admin), HttpStatusCode.BadRequest, "validation");
        }

        [Test]
        public void An_api_key_can_be_deactivated_and_reactivated()
        {
            var admin = Login("admin");
            var created = Parse<ApiKeyDto>(Post("admin/apikeys", Obj("name", "mes", "canUnlock", false), admin));

            var off = Send(HttpMethod.Put, "admin/apikeys/" + created.id + "?active=false", bearer: admin);
            Assert.That(off.StatusCode, Is.EqualTo(HttpStatusCode.OK), off.Body);
            Assert.That(Parse<ApiKeyDto>(off).active, Is.False);
            StringAssert.DoesNotContain("\"key\"", off.Body);
            AssertError(Get("locks", apiKey: created.key), HttpStatusCode.Unauthorized, "unauthenticated");

            var on = Send(HttpMethod.Put, "admin/apikeys/" + created.id + "?active=TRUE", bearer: admin);
            Assert.That(Parse<ApiKeyDto>(on).active, Is.True);
            Assert.That(Get("locks", apiKey: created.key).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public void Setting_a_key_active_validates_its_input()
        {
            var admin = Login("admin");

            AssertError(Send(HttpMethod.Put, "admin/apikeys/999?active=false", bearer: admin), HttpStatusCode.NotFound, "notfound");
            AssertError(Send(HttpMethod.Put, "admin/apikeys/abc?active=false", bearer: admin), HttpStatusCode.BadRequest, "validation");
            AssertError(Send(HttpMethod.Put, "admin/apikeys/1?active=maybe", bearer: admin), HttpStatusCode.BadRequest, "validation");
            AssertError(Send(HttpMethod.Put, "admin/apikeys/1", bearer: admin), HttpStatusCode.BadRequest, "validation");
        }

        // ---- R37: login throttling ----

        [Test]
        public void Five_wrong_pins_block_further_logins_for_the_user_for_60_seconds()
        {
            for (var i = 0; i < 5; i++)
                AssertError(Post("session", Obj("username", "qa1", "pin", "000" + i)), HttpStatusCode.Unauthorized, "unauthenticated");

            var blocked = Post("session", Obj("username", "QA1", "pin", "1234"));
            AssertError(blocked, HttpStatusCode.Unauthorized, "unauthenticated");
            StringAssert.Contains("\"message\":\"Too many attempts, try again later\"", blocked.Body);

            _clock.Advance(TimeSpan.FromSeconds(59));
            StringAssert.Contains("Too many attempts", Post("session", Obj("username", "qa1", "pin", "1234")).Body);

            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(Post("session", Obj("username", "qa1", "pin", "1234")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public void The_throttle_does_not_stop_requests_that_use_a_token()
        {
            var qa = Login("qa1");
            for (var i = 0; i < 6; i++)
                Post("session", Obj("username", "ghost", "pin", "x" + i));

            Assert.That(Get("locks", qa).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }
}
