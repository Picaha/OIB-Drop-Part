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

        /// <summary>The send timeout bindings get unless the caller asks for another.</summary>
        public static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(30);

        public static readonly TimeSpan MinSendTimeout = TimeSpan.FromSeconds(5);

        public static readonly TimeSpan MaxSendTimeout = TimeSpan.FromSeconds(120);

        /// <summary>
        /// A new binding each call: a binding must not be shared between channel factories. Three timeouts are cut (never
        /// lengthened): open to <see cref="OpenTimeout"/>, close to <see cref="CloseTimeout"/>, and send to
        /// <paramref name="sendTimeout"/> (<see cref="DefaultSendTimeout"/> when null, kept between <see cref="MinSendTimeout"/> and
        /// <see cref="MaxSendTimeout"/>). The OIB default send timeout is 10 minutes: a service that accepts the connection and then
        /// hangs would hold the calling thread that long. The receive timeout is left alone; on a reliable session it is the idle
        /// lifetime of the session, not a call limit.
        /// </summary>
        public static Binding SecureBinding(string uri, TimeSpan? sendTimeout = null)
        {
            RequireUri(uri);
            var send = ClampSendTimeout(sendTimeout);
            var binding = EndpointHelper.CreateBindingFromParameters(new BindingParameters(uri, true));
            if (binding.OpenTimeout > OpenTimeout)
                binding.OpenTimeout = OpenTimeout;
            if (binding.CloseTimeout > CloseTimeout)
                binding.CloseTimeout = CloseTimeout;
            if (binding.SendTimeout > send)
                binding.SendTimeout = send;
            return binding;
        }

        /// <summary>The send timeout a binding gets for <paramref name="requested"/>: the default when null, else limited to 5..120 s.</summary>
        public static TimeSpan ClampSendTimeout(TimeSpan? requested)
        {
            if (requested == null)
                return DefaultSendTimeout;
            if (requested.Value < MinSendTimeout)
                return MinSendTimeout;
            return requested.Value > MaxSendTimeout ? MaxSendTimeout : requested.Value;
        }

        private static void RequireUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                throw new ArgumentException("An OIB endpoint address is required.", nameof(uri));
        }
    }
}
