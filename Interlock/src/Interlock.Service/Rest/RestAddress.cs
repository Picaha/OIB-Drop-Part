using System;

namespace Interlock.Service.Rest
{
    /// <summary>
    /// The REST base address as configured. The host may be the http.sys wildcards <c>+</c> (strong) or <c>*</c> (weak), as in
    /// <c>http://+:8090/interlock/v1</c> and in the matching URL ACL. <see cref="Uri"/> does not accept them, so a wildcard
    /// host is replaced by the machine name and the binding listens on every host name (<c>StrongWildcard</c> for <c>+</c>,
    /// <c>WeakWildcard</c> for <c>*</c>). A loopback host is matched exactly; any other host uses the WCF default (strong wildcard).
    /// </summary>
    public sealed class RestAddress
    {
        private RestAddress(Uri uri, string hostKind)
        {
            Uri = uri;
            HostKind = hostKind;
        }

        public Uri Uri { get; }

        /// <summary><c>"+"</c>, <c>"*"</c>, <c>"loopback"</c> or <c>"host"</c>.</summary>
        public string HostKind { get; }

        public bool IsStrongWildcard
        {
            get { return HostKind == "+"; }
        }

        public bool IsWeakWildcard
        {
            get { return HostKind == "*"; }
        }

        public bool IsLoopback
        {
            get { return HostKind == "loopback"; }
        }

        /// <summary>Null when the text is not an absolute http or https address.</summary>
        public static RestAddress TryParse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            text = text.Trim();

            string kind = null;
            var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd > 0)
            {
                var hostStart = schemeEnd + 3;
                if (hostStart < text.Length && (text[hostStart] == '+' || text[hostStart] == '*'))
                {
                    var next = hostStart + 1;
                    if (next == text.Length || text[next] == ':' || text[next] == '/')
                    {
                        kind = text[hostStart].ToString();
                        text = text.Substring(0, hostStart) + Environment.MachineName + text.Substring(next);
                    }
                }
            }

            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return null;

            if (kind == null)
            {
                var host = uri.Host;
                kind = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]"
                    ? "loopback"
                    : "host";
            }
            return new RestAddress(uri, kind);
        }

        public static RestAddress Parse(string text)
        {
            var a = TryParse(text);
            if (a == null)
                throw new FormatException("Not an absolute http or https address: " + text);
            return a;
        }
    }
}
