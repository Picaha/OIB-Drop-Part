using System;
using System.Collections.Generic;
using System.ServiceModel;
using System.ServiceModel.Description;
using Asm.As.Oib.Common.Utilities;
using Interlock.Core.Ports;

namespace Interlock.Service.ExternalControl
{
    /// <summary>
    /// Hosts the two External Control services for Setup Center. The endpoints come from the <c>system.serviceModel</c> section of
    /// the application configuration (the service names are the full names of the implementation types), so the binding and the
    /// addresses can be changed on site without recompiling. Each endpoint address goes through <c>EndpointHelper.ReplaceLocalhost</c>
    /// (localhost becomes the machine name, so Setup Center on another computer can reach it) and each host is opened with
    /// <c>EndpointHelper.OpenServiceHost</c>: the pattern of the OIB tutorials.
    /// </summary>
    public sealed class ExternalControlHost : IListener
    {
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

        private readonly object _gate = new object();
        private readonly object[] _services;
        private readonly ILog _log;
        private List<ServiceHost> _hosts;

        public ExternalControlHost(SetupCenterExternalControlService sc, FeederExternalControlService feeder, ILog log)
        {
            if (sc == null) throw new ArgumentNullException(nameof(sc));
            if (feeder == null) throw new ArgumentNullException(nameof(feeder));
            if (log == null) throw new ArgumentNullException(nameof(log));

            _services = new object[] { sc, feeder };
            _log = log;
        }

        /// <summary>
        /// Opens both hosts and logs the address each endpoint listens on. When one cannot be opened (no configuration, address in
        /// use, no URL ACL) the failure is logged, any host that was already open is aborted, and the exception is rethrown
        /// unchanged: the caller decides what a service that cannot listen means. After a failed Open the host can be opened again.
        /// </summary>
        public void Open()
        {
            lock (_gate)
            {
                if (_hosts != null)
                    throw new InvalidOperationException("The External Control host is already open.");

                var hosts = new List<ServiceHost>();
                var current = string.Empty;
                try
                {
                    foreach (var service in _services)
                    {
                        current = service.GetType().Name;
                        var host = new ServiceHost(service);
                        hosts.Add(host);
                        foreach (ServiceEndpoint endpoint in host.Description.Endpoints)
                            endpoint.Address = EndpointHelper.ReplaceLocalhost(endpoint.Address);

                        EndpointHelper.OpenServiceHost(host);
                    }

                    // Only when both are listening.
                    foreach (var host in hosts)
                    {
                        foreach (ServiceEndpoint endpoint in host.Description.Endpoints)
                            _log.Info("External Control listening: " + endpoint.Contract.Name + " at " + endpoint.ListenUri.AbsoluteUri);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("External Control could not start (" + current + "): " + ex.Message, ex);
                    foreach (var host in hosts)
                        Abort(host);
                    throw;
                }
                _hosts = hosts;
            }
        }

        /// <summary>
        /// Stops listening. Never throws, and logs nothing unless a host would not close within the timeout and had to be aborted.
        /// Calling it on a host that is not open does nothing.
        /// </summary>
        public void Close()
        {
            Close(CloseTimeout);
        }

        /// <summary>As <see cref="Close()"/>, sharing at most <paramref name="timeout"/> (never more than 5 s per host) before aborting.</summary>
        public void Close(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            List<ServiceHost> hosts;
            lock (_gate)
            {
                hosts = _hosts;
                _hosts = null;
            }
            if (hosts == null)
                return;

            foreach (var host in hosts)
            {
                try
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero)
                        throw new TimeoutException("No time left to close the host.");
                    host.Close(left < CloseTimeout ? left : CloseTimeout);
                }
                catch (Exception ex)
                {
                    _log.Warn("An External Control host did not close cleanly and was aborted: " + ex.Message);
                    Abort(host);
                }
            }
        }

        public void Dispose()
        {
            Close();
        }

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
    }
}
