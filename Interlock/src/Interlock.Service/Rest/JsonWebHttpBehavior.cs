using System;
using System.Net;
using System.Runtime.Serialization.Json;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Dispatcher;
using System.ServiceModel.Web;
using Interlock.Core.Api;
using Interlock.Core.Ports;

namespace Interlock.Service.Rest
{
    /// <summary>
    /// <see cref="WebHttpBehavior"/> whose errors are always JSON. WCF's own error handler answers a body it cannot read, an
    /// unknown address or a wrong method with an HTML help page; this one replaces it with an <see cref="ErrorDto"/> body, so a
    /// client can treat every non-2xx response the same way.
    /// </summary>
    public sealed class JsonWebHttpBehavior : WebHttpBehavior
    {
        private readonly ILog _log;

        public JsonWebHttpBehavior(ILog log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));
            _log = log;
            HelpEnabled = false;
        }

        public override void ApplyDispatchBehavior(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher)
        {
            base.ApplyDispatchBehavior(endpoint, endpointDispatcher);

            // WCF answers an unknown address or a wrong method with an HTML page from this operation, not through the error handler.
            var unhandled = endpointDispatcher.DispatchRuntime.UnhandledDispatchOperation;
            if (unhandled != null)
                unhandled.Invoker = new UnmatchedRequestInvoker(endpoint.Address.Uri, typeof(IInterlockRestApi));
        }

        protected override void AddServerErrorHandlers(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher)
        {
            endpointDispatcher.ChannelDispatcher.ErrorHandlers.Add(new JsonErrorHandler(_log));
        }
    }

    internal sealed class JsonErrorHandler : IErrorHandler
    {
        private readonly ILog _log;

        public JsonErrorHandler(ILog log)
        {
            _log = log;
        }

        public bool HandleError(Exception error)
        {
            return true;
        }

        public void ProvideFault(Exception error, MessageVersion version, ref Message fault)
        {
            HttpStatusCode status;
            ErrorDto body;

            var web = error as WebFaultException<ErrorDto>;
            if (web != null)
            {
                status = web.StatusCode;
                body = web.Detail;
            }
            else
            {
                var mapped = Classify(error);
                status = mapped.Item1;
                body = mapped.Item2;
                if (status == HttpStatusCode.InternalServerError)
                    _log.Error("REST request failed: " + error.GetType().Name + ": " + error.Message, error);
            }

            fault = Build(version, status, body, null);
        }

        internal static Message Build(MessageVersion version, HttpStatusCode status, ErrorDto body, string allow)
        {
            var message = Message.CreateMessage(version, (string)null, body, new DataContractJsonSerializer(typeof(ErrorDto)));
            message.Properties.Add(WebBodyFormatMessageProperty.Name, new WebBodyFormatMessageProperty(WebContentFormat.Json));
            var http = new HttpResponseMessageProperty { StatusCode = status };
            http.Headers[HttpResponseHeader.ContentType] = "application/json; charset=utf-8";
            if (allow != null)
                http.Headers[HttpResponseHeader.Allow] = allow;
            message.Properties.Add(HttpResponseMessageProperty.Name, http);
            return message;
        }

        // What reaches here besides our own faults comes from WCF before an operation runs: an unreadable body, an unknown
        // address or a wrong method. Anything else is a bug.
        private static Tuple<HttpStatusCode, ErrorDto> Classify(Exception error)
        {
            var web = error as WebFaultException;
            if (web != null)
                return Tuple.Create(web.StatusCode, Dto(web.StatusCode == HttpStatusCode.NotFound ? "notfound" : "validation", error.Message));
            if (error is EndpointNotFoundException)
                return Tuple.Create(HttpStatusCode.NotFound, Dto("notfound", "Not found"));
            if (error is ProtocolException || error is FaultException || error is System.Runtime.Serialization.SerializationException
                || error is System.Xml.XmlException || error is FormatException || error is ArgumentException)
                return Tuple.Create(HttpStatusCode.BadRequest, Dto("validation", "Malformed request"));
            return Tuple.Create(HttpStatusCode.InternalServerError, Dto("internal", RestErrors.InternalErrorMessage));
        }

        private static ErrorDto Dto(string error, string message)
        {
            return new ErrorDto { error = error, message = message };
        }
    }
}
