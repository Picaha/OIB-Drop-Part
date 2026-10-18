using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Interlock.Core.Api;

namespace Interlock.Ui
{
    /// <summary>
    /// The REST client of the Interlock service (HttpClient + DataContractJsonSerializer). Every call is a Task so the forms
    /// can await it. A failed call throws <see cref="ApiException"/> with a message that is safe to show to an operator;
    /// the token and the PIN are never put into a message.
    /// </summary>
    public sealed class ApiClient : IDisposable
    {
        public const int MaxMessageChars = 200;
        /// <summary>A response larger than this is not used (a wrong server or a proxy page, not our service).</summary>
        public const int MaxResponseChars = 8 * 1024 * 1024;
        public const string Unreachable = "Cannot reach the Interlock service";

        private readonly string _base;
        private readonly HttpClient _http;

        public ApiClient(string baseUrl, HttpMessageHandler handler = null)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("The service address must be an http or https URL", nameof(baseUrl));

            _base = uri.ToString().TrimEnd('/');
            _http = new HttpClient(handler ?? new HttpClientHandler(), true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            Timeout = TimeSpan.FromSeconds(15);
        }

        /// <summary>The bearer token of the logged-in session; null or empty when not logged in.</summary>
        public string Token { get; set; }

        /// <summary>The limit of one call.</summary>
        public TimeSpan Timeout { get; set; }

        /// <summary>Raised (once per call, possibly on a pool thread) when an authenticated call gets 401: the session is gone.</summary>
        public event EventHandler Unauthorized;

        public void Dispose() { _http.Dispose(); }

        // ---- calls -------------------------------------------------------------------------------------------------

        public Task<SessionDto> Login(string username, string pin)
        {
            return Send<SessionDto>(HttpMethod.Post, "session", new SessionRequestDto { username = username, pin = pin }, false);
        }

        public async Task<IList<LockDto>> GetLocks(string status, string type, string linePath, string q)
        {
            var path = "locks" + Query("status", status, "type", type, "linePath", linePath, "q", q);
            return await Send<LockDto[]>(HttpMethod.Get, path, null, true).ConfigureAwait(false) ?? new LockDto[0];
        }

        public Task<LockDetailDto> GetLock(long id)
        {
            return Send<LockDetailDto>(HttpMethod.Get, "locks/" + Num(id), null, true);
        }

        public Task<LockDto> Create(CreateLockDto d)
        {
            return Send<LockDto>(HttpMethod.Post, "locks", d, true);
        }

        public Task<LockDto> Release(long id, string reason)
        {
            return Send<LockDto>(HttpMethod.Post, "locks/" + Num(id) + "/release", new ReasonDto { reason = reason }, true);
        }

        public Task<LockDto> Retry(long id)
        {
            return Send<LockDto>(HttpMethod.Post, "locks/" + Num(id) + "/retry", null, true);
        }

        public async Task<IList<DecisionDto>> GetDecisions(string uid, string pn, DateTime? from, DateTime? to)
        {
            var path = "decisions" + Query("uid", uid, "pn", pn, "from", Iso(from), "to", Iso(to));
            return await Send<DecisionDto[]>(HttpMethod.Get, path, null, true).ConfigureAwait(false) ?? new DecisionDto[0];
        }

        /// <summary>No authentication needed.</summary>
        public Task<HealthDto> GetHealth()
        {
            return Send<HealthDto>(HttpMethod.Get, "health", null, false);
        }

        public async Task<IList<string>> GetLines()
        {
            return await Send<string[]>(HttpMethod.Get, "lines", null, true).ConfigureAwait(false) ?? new string[0];
        }

        public async Task<IList<StationDto>> GetStations()
        {
            return await Send<StationDto[]>(HttpMethod.Get, "stations", null, true).ConfigureAwait(false) ?? new StationDto[0];
        }

        public async Task<IList<UserDto>> GetUsers()
        {
            return await Send<UserDto[]>(HttpMethod.Get, "admin/users", null, true).ConfigureAwait(false) ?? new UserDto[0];
        }

        public Task<UserDto> SaveUser(UserDto user)
        {
            return Send<UserDto>(HttpMethod.Post, "admin/users", user, true);
        }

        public async Task<IList<ApiKeyDto>> GetApiKeys()
        {
            return await Send<ApiKeyDto[]>(HttpMethod.Get, "admin/apikeys", null, true).ConfigureAwait(false) ?? new ApiKeyDto[0];
        }

        /// <summary>The returned DTO carries the plain key once; it cannot be read again.</summary>
        public Task<ApiKeyDto> CreateApiKey(ApiKeyDto key)
        {
            return Send<ApiKeyDto>(HttpMethod.Post, "admin/apikeys", key, true);
        }

        public Task<ApiKeyDto> SetApiKeyActive(long id, bool active)
        {
            return Send<ApiKeyDto>(HttpMethod.Put, "admin/apikeys/" + Num(id) + "?active=" + (active ? "true" : "false"), null, true);
        }

        // ---- plumbing ----------------------------------------------------------------------------------------------

        private static string Num(long id) { return id.ToString(CultureInfo.InvariantCulture); }

        /// <summary>"?a=1&amp;b=2" from name/value pairs; blank values are left out; no pairs gives "".</summary>
        private static string Query(params string[] pairs)
        {
            var sb = new StringBuilder();
            for (var i = 0; i + 1 < pairs.Length; i += 2)
            {
                var value = pairs[i + 1];
                if (string.IsNullOrWhiteSpace(value)) continue;
                sb.Append(sb.Length == 0 ? '?' : '&').Append(pairs[i]).Append('=').Append(Uri.EscapeDataString(value.Trim()));
            }
            return sb.ToString();
        }

        private static string Iso(DateTime? t)
        {
            return t.HasValue ? t.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : null;
        }

        private async Task<T> Send<T>(HttpMethod method, string path, object body, bool authenticated) where T : class
        {
            var request = new HttpRequestMessage(method, _base + "/" + path);
            if (authenticated && !string.IsNullOrEmpty(Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            if (body != null)
                request.Content = new StringContent(Serialize(body), Encoding.UTF8, "application/json");
            else if (method != HttpMethod.Get)
                request.Content = new StringContent("", Encoding.UTF8, "application/json");

            HttpStatusCode status;
            string text;
            try
            {
                using (var cts = new CancellationTokenSource(Timeout))
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    status = response.StatusCode;
                    var length = response.Content.Headers.ContentLength;
                    if (length.HasValue && length.Value > MaxResponseChars * 4L)
                        throw new ApiException((int)status, null, "The service answer is too large");
                    text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (ApiException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is WebException || ex is IOException
                || ex is ObjectDisposedException || ex is InvalidOperationException || ex is UriFormatException || ex is ArgumentException)
            {
                // Details (host names, stack text) are deliberately not shown to the operator.
                throw new ApiException(0, null, Unreachable);
            }

            if (text != null && text.Length > MaxResponseChars)
                throw new ApiException((int)status, null, "The service answer is too large");

            if ((int)status >= 200 && (int)status < 300)
            {
                try { return Deserialize<T>(text); }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    throw new ApiException((int)status, null, "The service answered with something unexpected");
                }
            }

            var error = ToException((int)status, text);
            if (status == HttpStatusCode.Unauthorized && authenticated)
            {
                var handler = Unauthorized;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            throw error;
        }

        private static ApiException ToException(int status, string text)
        {
            string code = null, message = null;
            try
            {
                var dto = Deserialize<ErrorDto>(text);
                if (dto != null) { code = dto.error; message = dto.message; }
            }
            catch (Exception) { /* not our JSON: use the fallback below */ }

            if (string.IsNullOrWhiteSpace(message))
                message = Fallback(status);
            message = message.Trim();
            if (message.Length > MaxMessageChars) message = message.Substring(0, MaxMessageChars);
            return new ApiException(status, code, message);
        }

        private static string Fallback(int status)
        {
            switch (status)
            {
                case 400: return "The service rejected the request";
                case 401: return "Please sign in again";
                case 403: return "You are not allowed to do this";
                case 404: return "Not found";
                case 503: return "The service is not available right now";
                default: return status >= 500 ? "The service reported an error (HTTP " + status + ")" : "The request failed (HTTP " + status + ")";
            }
        }

        private static string Serialize(object value)
        {
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(value.GetType()).WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static T Deserialize<T>(string text) where T : class
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
        }
    }
}
