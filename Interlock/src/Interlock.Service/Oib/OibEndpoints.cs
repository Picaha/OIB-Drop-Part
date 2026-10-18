using System;
using System.ServiceModel;
using System.ServiceModel.Channels;
using Asm.As.Oib.Common.Utilities;

namespace Interlock.Service.Oib
{
    /// <summary>
    /// The secure-endpoint pattern of the OIB samples (<c>Form1.GetMonitoringAdapters</c>): the endpoint carries the DNS identity
    /// <c>ASM.SW.Service</c>, and the binding comes from <see cref="BindingParameters"/> with the central client certificate
    /// settings of the application configuration. The binding follows the endpoint name (<c>.../Reliable</c> is reliable,
    /// <c>.../Secure</c> uses transport security).
    /// The three smart proxies expose no channel credentials. The shipped proxy assemblies set
    /// <c>X509RevocationMode</c> on their own channel factories, so the clients have nothing to set here.
    /// </summary>
    public static class OibEndpoints
    {
        /// <summary>The DNS identity of the OIB services' certificate.</summary>
        public const string ServiceDnsIdentity = "ASM.SW.Service";

        public static EndpointAddress SecureAddress(string uri)
        {
            RequireUri(uri);
            return new EndpointAddress(new Uri(uri), EndpointIdentity.CreateDnsIdentity(ServiceDnsIdentity));
        }

        /// <summary>
        /// How long the proxies may take to open a channel. Against a closed port the proxies keep retrying until their timeouts are
        /// used up. Measured on a closed loopback port: with the OIB defaults <c>IsConnected</c> took 52 s to answer false and
        /// <c>GetConfiguration</c> over 2 min to throw; with 10 s and 5 s (below) they took about 7 s and 2 s.
        /// </summary>
        public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

        public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// A new binding each call: a binding must not be shared between channel factories. The open and close timeouts are cut to
        /// <see cref="OpenTimeout"/> and <see cref="CloseTimeout"/> (never lengthened); send and receive timeouts stay at the OIB
        /// values, since they bound calls that really take long.
        /// </summary>
        public static Binding SecureBinding(string uri)
        {
            RequireUri(uri);
            var binding = EndpointHelper.CreateBindingFromParameters(new BindingParameters(uri, true));
            if (binding.OpenTimeout > OpenTimeout)
                binding.OpenTimeout = OpenTimeout;
            if (binding.CloseTimeout > CloseTimeout)
                binding.CloseTimeout = CloseTimeout;
            return binding;
        }

        private static void RequireUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                throw new ArgumentException("An OIB endpoint address is required.", nameof(uri));
        }
    }
}
