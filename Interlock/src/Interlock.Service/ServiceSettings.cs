using System;
using System.Collections.Specialized;
using System.Configuration;
using System.Globalization;
using System.IO;

namespace Interlock.Service
{
    /// <summary>
    /// The service configuration from <c>appSettings</c> and the connection string named <c>Interlock</c>. Relative paths are
    /// resolved against the directory of the executable (a Windows service starts in System32). A missing required key or a
    /// malformed value throws <see cref="ConfigurationErrorsException"/> naming the key. <see cref="ToString"/> never shows the
    /// connection string.
    /// </summary>
    public sealed class ServiceSettings
    {
        public const string SetupCenterEndpointKey = "Asm.As.Oib.SetupCenter.Proxy.Endpoint";
        public const string LineControlEndpointKey = "Asm.As.Oib.SiplacePro.LineControl.Proxy.Endpoint";
        public const string MonitoringEndpointKey = "Asm.As.Oib.Monitoring.Proxy.MonitoringEndpoint";
        public const string CoreHostKey = "Interlock.CoreHost";
        public const string EventCallbackNameKey = "Interlock.EventCallbackName";
        public const string EventCallbackPortKey = "Interlock.EventCallbackPort";
        public const string RestBaseAddressKey = "Interlock.RestBaseAddress";
        public const string DbSchemaKey = "Interlock.DbSchema";
        public const string SpoolPathKey = "Interlock.SpoolPath";
        public const string LogDirKey = "Interlock.LogDir";
        public const string OibSendTimeoutSecondsKey = "Interlock.OibSendTimeoutSeconds";
        public const string ConnectionStringName = "Interlock";

        private ServiceSettings()
        {
        }

        public string SetupCenterEndpoint { get; private set; }
        public string LineControlEndpoint { get; private set; }
        public string MonitoringEndpoint { get; private set; }
        public string CoreHost { get; private set; }
        public string EventCallbackName { get; private set; }
        public int EventCallbackPort { get; private set; }
        public string RestBaseAddress { get; private set; }
        public string DbSchema { get; private set; }

        /// <summary>Absolute path of the lock spool file.</summary>
        public string SpoolPath { get; private set; }

        /// <summary>Absolute path of the log directory.</summary>
        public string LogDir { get; private set; }

        public TimeSpan OibSendTimeout { get; private set; }

        /// <summary>Secret: never log it.</summary>
        public string ConnectionString { get; private set; }

        /// <summary>The settings of this executable's configuration file.</summary>
        public static ServiceSettings FromConfigFile()
        {
            return Load(ConfigurationManager.AppSettings, ConfigurationManager.ConnectionStrings);
        }

        public static ServiceSettings Load(NameValueCollection appSettings, ConnectionStringSettingsCollection cs)
        {
            var a = appSettings ?? new NameValueCollection();
            return new ServiceSettings
            {
                SetupCenterEndpoint = Required(a, SetupCenterEndpointKey),
                LineControlEndpoint = Required(a, LineControlEndpointKey),
                MonitoringEndpoint = Required(a, MonitoringEndpointKey),
                CoreHost = Required(a, CoreHostKey),
                EventCallbackName = Optional(a, EventCallbackNameKey, "Interlock.SetupCenterEvents"),
                EventCallbackPort = Number(a, EventCallbackPortKey, 8092, 1, 65535),
                RestBaseAddress = RestBase(a),
                DbSchema = Optional(a, DbSchemaKey, "interlock"),
                SpoolPath = NextToExe(Optional(a, SpoolPathKey, Path.Combine("spool", "locks.jsonl"))),
                LogDir = NextToExe(Optional(a, LogDirKey, "logs")),
                OibSendTimeout = TimeSpan.FromSeconds(Number(a, OibSendTimeoutSecondsKey, 30, 5, 120)),
                ConnectionString = ConnectionStringByName(cs)
            };
        }

        public override string ToString()
        {
            return "SetupCenter=" + SetupCenterEndpoint + ", LineControl=" + LineControlEndpoint + ", Monitoring=" + MonitoringEndpoint
                + ", CoreHost=" + CoreHost + ", EventCallback=" + EventCallbackName + ":" + EventCallbackPort.ToString(CultureInfo.InvariantCulture)
                + ", Rest=" + RestBaseAddress + ", DbSchema=" + DbSchema + ", Spool=" + SpoolPath + ", LogDir=" + LogDir
                + ", OibSendTimeout=" + OibSendTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s";
        }

        private static string Required(NameValueCollection a, string key)
        {
            var value = a[key];
            if (string.IsNullOrWhiteSpace(value))
                throw new ConfigurationErrorsException("The appSettings key '" + key + "' is required.");
            return value.Trim();
        }

        private static string Optional(NameValueCollection a, string key, string fallback)
        {
            var value = a[key];
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string RestBase(NameValueCollection a)
        {
            var value = Optional(a, RestBaseAddressKey, "http://+:8090/interlock/v1");
            if (Rest.RestAddress.TryParse(value) == null)
                throw new ConfigurationErrorsException("The appSettings key '" + RestBaseAddressKey
                    + "' must be an absolute http or https address; the host may be + or *.");
            return value;
        }

        private static int Number(NameValueCollection a, string key, int fallback, int min, int max)
        {
            var value = a[key];
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            int n;
            if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < min || n > max)
            {
                throw new ConfigurationErrorsException("The appSettings key '" + key + "' must be a whole number from "
                    + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture) + ".");
            }
            return n;
        }

        private static string ConnectionStringByName(ConnectionStringSettingsCollection cs)
        {
            var entry = cs == null ? null : cs[ConnectionStringName];
            if (entry == null || string.IsNullOrWhiteSpace(entry.ConnectionString))
                throw new ConfigurationErrorsException("The connection string named '" + ConnectionStringName + "' is required.");
            return entry.ConnectionString;
        }

        private static string NextToExe(string path)
        {
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
        }
    }
}
