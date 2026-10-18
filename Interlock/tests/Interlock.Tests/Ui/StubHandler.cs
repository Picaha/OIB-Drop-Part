using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Interlock.Tests.Ui
{
    /// <summary>An HttpMessageHandler that never touches the network: it returns a canned response, throws or hangs.</summary>
    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception _throw;
        private readonly bool _hang;

        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
        public StubHandler(Exception toThrow) { _throw = toThrow; }
        private StubHandler(bool hang) { _hang = hang; }

        public static StubHandler Hanging() { return new StubHandler(true); }

        public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();
        public string LastBody { get; private set; }
        public HttpRequestMessage LastRequest { get { return Requests.Count == 0 ? null : Requests[Requests.Count - 1]; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            if (_throw != null) throw _throw;
            if (_hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body ?? "", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
