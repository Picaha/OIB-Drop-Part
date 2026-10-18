using System;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Web;
using Interlock.Core.Ports;

namespace Interlock.Service.Rest
{
    /// <summary>
    /// Hosts the REST API: a <see cref="WebServiceHost"/> with a <see cref="WebHttpBinding"/> on the base address
    /// (for example <c>http://+:8080/interlock/v1</c>). The binding is plain HTTP; switching to HTTPS is a change of the base
    /// address and the binding's security mode, which a later configuration step can make.
    /// </summary>
    public sealed class RestHost : IListener
    {
        private const int MaxBodyBytes = 64 * 1024;
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

        private readonly object _gate = new object();
        private readonly IInterlockRestApi _api;
        private readonly string _baseAddress;
        private readonly ILog _log;
        private WebServiceHost _host;

        public RestHost(IInterlockRestApi api, string baseAddress, ILog log)
        {
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (string.IsNullOrWhiteSpace(baseAddress)) throw new ArgumentException("A base address is required.", nameof(baseAddress));
            if (log == null) throw new ArgumentNullException(nameof(log));

            _api = api;
            _baseAddress = baseAddress;
            _log = log;
        }

        /// <summary>Starts listening. A failure (address in use, no URL ACL) is logged and rethrown unchanged; the host can be opened again.</summary>
        public void Open()
        {
            lock (_gate)
            {
                if (_host != null)
                    throw new InvalidOperationException("The REST host is already open.");

                WebServiceHost host = null;
                try
                {
                    var address = RestAddress.Parse(_baseAddress);
                    host = new WebServiceHost(_api, address.Uri);
                    var endpoint = host.AddServiceEndpoint(typeof(IInterlockRestApi), NewBinding(address), string.Empty);
                    endpoint.Behaviors.Add(new JsonWebHttpBehavior(_log));
                    host.Open();
                    _log.Info("REST API listening at " + _baseAddress);
                    _host = host;
                }
                catch (Exception ex)
                {
                    _log.Error("REST API could not start at " + _baseAddress + ": " + ex.Message, ex);
                    if (host != null)
                        Abort(host);
                    throw;
                }
            }
        }

        /// <summary>Stops listening. Never throws; a host that does not close within the timeout is aborted. Does nothing when not open.</summary>
        public void Close()
        {
            Close(CloseTimeout);
        }

        /// <summary>As <see cref="Close()"/>, waiting at most <paramref name="timeout"/> (and never more than 5 s) before aborting.</summary>
        public void Close(TimeSpan timeout)
        {
            WebServiceHost host;
            lock (_gate)
            {
                host = _host;
                _host = null;
            }
            if (host == null)
                return;

            try
            {
                if (timeout <= TimeSpan.Zero)
                    throw new TimeoutException("No time left to close the REST host.");
                host.Close(timeout < CloseTimeout ? timeout : CloseTimeout);
            }
            catch (Exception ex)
            {
                _log.Warn("The REST host did not close cleanly and was aborted: " + ex.Message);
                Abort(host);
            }
        }

        public void Dispose()
        {
            Close();
        }

        private static WebHttpBinding NewBinding(RestAddress address)
        {
            var binding = new WebHttpBinding(WebHttpSecurityMode.None)
            {
                MaxReceivedMessageSize = MaxBodyBytes,
                MaxBufferSize = MaxBodyBytes,
                ContentTypeMapper = new JsonContentTypeMapper(),
                HostNameComparisonMode = address.IsLoopback ? HostNameComparisonMode.Exact
                    : address.IsWeakWildcard ? HostNameComparisonMode.WeakWildcard
                    : HostNameComparisonMode.StrongWildcard
            };
            binding.ReaderQuotas.MaxStringContentLength = MaxBodyBytes;
            return binding;
        }

        // A loopback host is registered as given (Exact), which is also what lets the Temporary_Listen_Addresses prefix bind without
        // a URL ACL. This is not an access control: a remote client that sends "Host: localhost" may still be matched. "+" and any
        // other host (the machine name) use the strong wildcard, "*" the weak one (see RestAddress). Restrict access with the firewall.

        private static void Abort(ServiceHost host)
        {
            try
            {
                host.Abort();
            }
            catch (Exception)
            {
                // A host that cannot even be aborted is gone; nothing is left to report.
            }
        }

        /// <summary>Reads every request body as JSON, so a client that forgets the Content-Type header still gets a proper answer.</summary>
        private sealed class JsonContentTypeMapper : WebContentTypeMapper
        {
            public override WebContentFormat GetMessageFormatForContentType(string contentType)
            {
                return WebContentFormat.Json;
            }
        }
    }
}
