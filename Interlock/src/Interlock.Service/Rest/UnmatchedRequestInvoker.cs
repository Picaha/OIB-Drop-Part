using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Dispatcher;
using System.ServiceModel.Web;
using Interlock.Core.Api;

namespace Interlock.Service.Rest
{
    /// <summary>
    /// Answers a request no operation matches with a JSON 404, or a JSON 405 with an <c>Allow</c> header when the address exists
    /// for other methods. The addresses and methods are read from the contract, so they cannot drift from it.
    /// </summary>
    internal sealed class UnmatchedRequestInvoker : IOperationInvoker
    {
        private readonly Uri _baseAddress;
        private readonly List<KeyValuePair<UriTemplate, string>> _routes = new List<KeyValuePair<UriTemplate, string>>();

        public UnmatchedRequestInvoker(Uri baseAddress, Type contract)
        {
            var text = baseAddress.AbsoluteUri;
            _baseAddress = new Uri(text.EndsWith("/", StringComparison.Ordinal) ? text : text + "/");

            foreach (var method in contract.GetMethods())
            {
                var get = method.GetCustomAttribute<WebGetAttribute>();
                if (get != null)
                    _routes.Add(new KeyValuePair<UriTemplate, string>(new UriTemplate(get.UriTemplate), "GET"));
                var invoke = method.GetCustomAttribute<WebInvokeAttribute>();
                if (invoke != null)
                    _routes.Add(new KeyValuePair<UriTemplate, string>(new UriTemplate(invoke.UriTemplate), invoke.Method ?? "POST"));
            }
        }

        public bool IsSynchronous
        {
            get { return true; }
        }

        public object[] AllocateInputs()
        {
            return new object[1];
        }

        public object Invoke(object instance, object[] inputs, out object[] outputs)
        {
            outputs = new object[0];
            var request = inputs[0] as Message;
            var to = request == null ? null : request.Headers.To;

            var allowed = to == null
                ? new List<string>()
                : _routes.Where(r => r.Key.Match(_baseAddress, to) != null).Select(r => r.Value).Distinct().ToList();
            var version = request == null ? MessageVersion.None : request.Version;

            if (allowed.Count == 0)
                return JsonErrorHandler.Build(version, HttpStatusCode.NotFound, new ErrorDto { error = "notfound", message = "Endpoint not found" }, null);
            return JsonErrorHandler.Build(version, HttpStatusCode.MethodNotAllowed,
                new ErrorDto { error = "validation", message = "Method not allowed" }, string.Join(", ", allowed));
        }

        public IAsyncResult InvokeBegin(object instance, object[] inputs, AsyncCallback callback, object state)
        {
            throw new NotSupportedException();
        }

        public object InvokeEnd(object instance, out object[] outputs, IAsyncResult result)
        {
            throw new NotSupportedException();
        }
    }
}
