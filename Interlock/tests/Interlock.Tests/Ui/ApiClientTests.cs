using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Interlock.Core.Api;
using Interlock.Ui;
using NUnit.Framework;

namespace Interlock.Tests.Ui
{
    [TestFixture]
    public class ApiClientTests
    {
        private const string Base = "http://x/interlock/v1";

        private static ApiClient Client(StubHandler h) { return new ApiClient(Base, h); }

        [Test]
        public void Error_body_becomes_api_exception()
        {
            var c = Client(new StubHandler(HttpStatusCode.Forbidden, "{\"error\":\"forbidden\",\"message\":\"Release needs QA\"}"));
            var ex = Assert.ThrowsAsync<ApiException>(() => c.Release(1, "r"));
            Assert.That(ex.Status, Is.EqualTo(403));
            Assert.That(ex.Error, Is.EqualTo("forbidden"));
            Assert.That(ex.Message, Is.EqualTo("Release needs QA"));
        }

        [Test]
        public async Task Bearer_token_sent_and_status_query_has_no_empty_parameters()
        {
            var h = new StubHandler(HttpStatusCode.OK, "[]");
            var c = new ApiClient(Base, h) { Token = "t1" };
            await c.GetLocks("ACTIVE", null, null, null);
            Assert.That(h.LastRequest.Headers.Authorization.ToString(), Is.EqualTo("Bearer t1"));
            StringAssert.EndsWith("/locks?status=ACTIVE", h.LastRequest.RequestUri.ToString());
        }

        [Test]
        public async Task No_token_means_no_authorization_header()
        {
            var h = new StubHandler(HttpStatusCode.OK, "{\"setupCenterConnected\":true}");
            await Client(h).GetHealth();
            Assert.That(h.LastRequest.Headers.Authorization, Is.Null);
            StringAssert.EndsWith("/interlock/v1/health", h.LastRequest.RequestUri.ToString());
        }

        [Test]
        public async Task Query_values_are_url_encoded_and_blank_ones_omitted()
        {
            var h = new StubHandler(HttpStatusCode.OK, "[]");
            await Client(h).GetLocks("ACTIVE", "  ", "Line 1/A&B", "a b+c=d");
            Assert.That(h.LastRequest.RequestUri.Query, Is.EqualTo("?status=ACTIVE&linePath=Line%201%2FA%26B&q=a%20b%2Bc%3Dd"));
        }

        [Test]
        public async Task Empty_filter_set_gives_no_query_string()
        {
            var h = new StubHandler(HttpStatusCode.OK, "[]");
            await Client(h).GetLocks(null, null, null, null);
            StringAssert.EndsWith("/locks", h.LastRequest.RequestUri.ToString());
        }

        [Test]
        public async Task Decisions_dates_are_sent_as_utc_iso()
        {
            var h = new StubHandler(HttpStatusCode.OK, "[]");
            await Client(h).GetDecisions("U1", null, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), null);
            Assert.That(h.LastRequest.RequestUri.Query, Does.StartWith("?uid=U1&from=2026-10-01T08%3A00%3A00.0000000Z"));
            Assert.That(h.LastRequest.RequestUri.Query, Does.Not.Contain("to="));
        }

        [Test]
        public void Unauthorized_event_raised_once_per_call()
        {
            var c = new ApiClient(Base, new StubHandler(HttpStatusCode.Unauthorized, "{\"error\":\"unauthenticated\",\"message\":\"Session expired\"}")) { Token = "t" };
            var raised = 0;
            c.Unauthorized += (s, e) => raised++;
            var ex = Assert.ThrowsAsync<ApiException>(() => c.GetLocks("ACTIVE", null, null, null));
            Assert.That(ex.Status, Is.EqualTo(401));
            Assert.That(raised, Is.EqualTo(1));
        }

        [Test]
        public void Failed_login_does_not_raise_unauthorized()
        {
            var c = new ApiClient(Base, new StubHandler(HttpStatusCode.Unauthorized, "{\"error\":\"unauthenticated\",\"message\":\"Invalid username or PIN\"}"));
            var raised = 0;
            c.Unauthorized += (s, e) => raised++;
            var ex = Assert.ThrowsAsync<ApiException>(() => c.Login("u", "1234"));
            Assert.That(ex.Message, Is.EqualTo("Invalid username or PIN"));
            Assert.That(raised, Is.EqualTo(0));
        }

        [Test]
        public void Network_error_is_status_zero()
        {
            var c = Client(new StubHandler(new HttpRequestException("boom: secret internals")));
            var ex = Assert.ThrowsAsync<ApiException>(() => c.GetHealth());
            Assert.That(ex.Status, Is.EqualTo(0));
            Assert.That(ex.Message, Is.EqualTo("Cannot reach the Interlock service"));
        }

        [Test]
        public void Timeout_is_status_zero()
        {
            var c = new ApiClient(Base, StubHandler.Hanging()) { Timeout = TimeSpan.FromMilliseconds(100) };
            var ex = Assert.ThrowsAsync<ApiException>(() => c.GetHealth());
            Assert.That(ex.Status, Is.EqualTo(0));
            Assert.That(ex.Message, Is.EqualTo("Cannot reach the Interlock service"));
        }

        [Test]
        public void Default_timeout_is_15_seconds()
        {
            Assert.That(Client(new StubHandler(HttpStatusCode.OK, "[]")).Timeout, Is.EqualTo(TimeSpan.FromSeconds(15)));
        }

        [Test]
        public void Html_error_body_gets_a_fallback_message_never_the_raw_text()
        {
            var c = Client(new StubHandler(HttpStatusCode.InternalServerError, "<html><body>Stack trace at Foo.Bar() secret</body></html>"));
            var ex = Assert.ThrowsAsync<ApiException>(() => c.GetHealth());
            Assert.That(ex.Status, Is.EqualTo(500));
            Assert.That(ex.Message, Does.Not.Contain("<html>"));
            Assert.That(ex.Message, Does.Not.Contain("Stack trace"));
            Assert.That(ex.Message, Is.Not.Empty);
        }

        [Test]
        public void Error_message_is_truncated_to_200_chars()
        {
            var c = Client(new StubHandler(HttpStatusCode.BadRequest, "{\"error\":\"validation\",\"message\":\"" + new string('x', 500) + "\"}"));
            var ex = Assert.ThrowsAsync<ApiException>(() => c.GetHealth());
            Assert.That(ex.Message.Length, Is.EqualTo(200));
        }

        [Test]
        public void Success_body_that_is_not_json_is_an_api_exception()
        {
            var c = Client(new StubHandler(HttpStatusCode.OK, "<html>proxy login page</html>"));
            var ex = Assert.ThrowsAsync<ApiException>(() => c.GetHealth());
            Assert.That(ex.Status, Is.EqualTo(200));
            Assert.That(ex.Message, Does.Not.Contain("<html>"));
        }

        [Test]
        public async Task Create_posts_json_body_and_reads_the_lock()
        {
            var h = new StubHandler(HttpStatusCode.Created, "{\"id\":7,\"type\":\"PU\",\"target\":\"PU1\",\"enforceState\":\"PENDING\"}");
            var l = await Client(h).Create(new CreateLockDto { type = "PU", target = "PU1", reason = "r" });
            Assert.That(l.id, Is.EqualTo(7));
            Assert.That(h.LastRequest.Method, Is.EqualTo(HttpMethod.Post));
            StringAssert.EndsWith("/locks", h.LastRequest.RequestUri.ToString());
            StringAssert.Contains("\"target\":\"PU1\"", h.LastBody);
            Assert.That(h.LastRequest.Content.Headers.ContentType.MediaType, Is.EqualTo("application/json"));
        }

        [Test]
        public async Task Release_and_retry_use_the_documented_routes()
        {
            var h = new StubHandler(HttpStatusCode.OK, "{\"id\":3}");
            var c = Client(h);
            await c.Release(3, "fixed");
            StringAssert.EndsWith("/locks/3/release", h.LastRequest.RequestUri.ToString());
            StringAssert.Contains("\"reason\":\"fixed\"", h.LastBody);
            await c.Retry(3);
            StringAssert.EndsWith("/locks/3/retry", h.LastRequest.RequestUri.ToString());
            Assert.That(h.LastRequest.Method, Is.EqualTo(HttpMethod.Post));
        }

        [Test]
        public async Task Login_sends_credentials_and_returns_session()
        {
            var h = new StubHandler(HttpStatusCode.OK, "{\"token\":\"abc\",\"role\":\"QA\",\"expiresAt\":\"2026-10-19T10:00:00.0000000Z\"}");
            var s = await Client(h).Login("bob", "1234");
            Assert.That(s.token, Is.EqualTo("abc"));
            Assert.That(s.role, Is.EqualTo("QA"));
            StringAssert.Contains("\"username\":\"bob\"", h.LastBody);
            StringAssert.EndsWith("/session", h.LastRequest.RequestUri.ToString());
        }

        [Test]
        public async Task Api_key_active_uses_put_with_query()
        {
            var h = new StubHandler(HttpStatusCode.OK, "{\"id\":5,\"active\":false}");
            await Client(h).SetApiKeyActive(5, false);
            Assert.That(h.LastRequest.Method, Is.EqualTo(HttpMethod.Put));
            StringAssert.EndsWith("/admin/apikeys/5?active=false", h.LastRequest.RequestUri.ToString());
        }

        [Test]
        public async Task Lists_deserialize()
        {
            var c = Client(new StubHandler(HttpStatusCode.OK, "[\"L1\",\"L2\"]"));
            var lines = await c.GetLines();
            Assert.That(lines.ToArray(), Is.EqualTo(new[] { "L1", "L2" }));
        }

        [Test]
        public void Oversized_response_is_rejected()
        {
            var c = Client(new StubHandler(HttpStatusCode.OK, "[\"" + new string('a', ApiClient.MaxResponseChars + 10) + "\"]"));
            var ex = Assert.ThrowsAsync<ApiException>(() => c.GetLines());
            Assert.That(ex.Message, Does.Contain("too large"));
        }

        [Test]
        public void Bad_base_url_is_rejected()
        {
            Assert.Throws<ArgumentException>(() => new ApiClient("not a url"));
            Assert.Throws<ArgumentException>(() => new ApiClient("ftp://x/y"));
        }
    }
}
