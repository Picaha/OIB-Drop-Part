using System;
using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using Interlock.Service;
using NUnit.Framework;

namespace Interlock.Tests.Service
{
    [TestFixture]
    public sealed class ServiceSettingsTests
    {
        private static NameValueCollection Required()
        {
            return new NameValueCollection
            {
                { "Asm.As.Oib.SetupCenter.Proxy.Endpoint", "net.tcp://oib:1406/Asm.As.Oib.SetupCenter/SiplaceSetupCenter/Reliable" },
                { "Asm.As.Oib.SiplacePro.LineControl.Proxy.Endpoint", "net.tcp://oib:1406/Asm.As.Oib.SiplacePro.LineControl/Secure" },
                { "Asm.As.Oib.Monitoring.Proxy.MonitoringEndpoint", "net.tcp://oib:1406/Monitoring/ReliableSecure" },
                { "Interlock.CoreHost", "oibcore" }
            };
        }

        private static NameValueCollection With(string key, string value)
        {
            var s = Required();
            s[key] = value;
            return s;
        }

        private static NameValueCollection Without(string key)
        {
            var s = Required();
            s.Remove(key);
            return s;
        }

        private static ConnectionStringSettingsCollection Cs(string name, string value)
        {
            return Cs(Tuple.Create(name, value));
        }

        private static ConnectionStringSettingsCollection Cs(params Tuple<string, string>[] entries)
        {
            var c = new ConnectionStringSettingsCollection();
            foreach (var e in entries)
                c.Add(new ConnectionStringSettings(e.Item1, e.Item2));
            return c;
        }

        private static string ExeDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        [Test]
        public void Defaults_applied()
        {
            var s = ServiceSettings.Load(Required(), Cs("Interlock", "Host=x"));
            Assert.That(s.RestBaseAddress, Is.EqualTo("http://+:8090/interlock/v1"));
            Assert.That(s.DbSchema, Is.EqualTo("interlock"));
            Assert.That(s.EventCallbackPort, Is.EqualTo(8092));
            Assert.That(s.EventCallbackName, Is.EqualTo("Interlock.SetupCenterEvents"));
            Assert.That(s.OibSendTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(s.SpoolPath, Is.EqualTo(Path.Combine(ExeDir, "spool", "locks.jsonl")));
            Assert.That(s.LogDir, Is.EqualTo(Path.Combine(ExeDir, "logs")));
        }

        [Test]
        public void Required_values_are_read()
        {
            var s = ServiceSettings.Load(Required(), Cs("Interlock", "Host=x"));
            Assert.That(s.SetupCenterEndpoint, Is.EqualTo("net.tcp://oib:1406/Asm.As.Oib.SetupCenter/SiplaceSetupCenter/Reliable"));
            Assert.That(s.LineControlEndpoint, Is.EqualTo("net.tcp://oib:1406/Asm.As.Oib.SiplacePro.LineControl/Secure"));
            Assert.That(s.MonitoringEndpoint, Is.EqualTo("net.tcp://oib:1406/Monitoring/ReliableSecure"));
            Assert.That(s.CoreHost, Is.EqualTo("oibcore"));
            Assert.That(s.ConnectionString, Is.EqualTo("Host=x"));
        }

        [Test]
        public void Missing_endpoint_names_key()
        {
            var ex = Assert.Throws<ConfigurationErrorsException>(() => ServiceSettings.Load(new NameValueCollection(), Cs("Interlock", "Host=x")));
            StringAssert.Contains("Asm.As.Oib.SetupCenter.Proxy.Endpoint", ex.Message);
        }

        [TestCase("Asm.As.Oib.SetupCenter.Proxy.Endpoint")]
        [TestCase("Asm.As.Oib.SiplacePro.LineControl.Proxy.Endpoint")]
        [TestCase("Asm.As.Oib.Monitoring.Proxy.MonitoringEndpoint")]
        [TestCase("Interlock.CoreHost")]
        public void Each_missing_or_blank_required_key_is_named(string key)
        {
            var missing = Assert.Throws<ConfigurationErrorsException>(() => ServiceSettings.Load(Without(key), Cs("Interlock", "Host=x")));
            StringAssert.Contains(key, missing.Message);
            var blank = Assert.Throws<ConfigurationErrorsException>(() => ServiceSettings.Load(With(key, "  "), Cs("Interlock", "Host=x")));
            StringAssert.Contains(key, blank.Message);
        }

        [Test]
        public void Connection_string_read_by_name_not_position()
        {
            var s = ServiceSettings.Load(Required(), Cs(Tuple.Create("DropProduction", "Host=a"), Tuple.Create("Interlock", "Host=b")));
            Assert.That(s.ConnectionString, Is.EqualTo("Host=b"));
        }

        [Test]
        public void Missing_connection_string_names_it_and_never_falls_back_to_another()
        {
            var ex = Assert.Throws<ConfigurationErrorsException>(() => ServiceSettings.Load(Required(), Cs("DropProduction", "Host=a")));
            StringAssert.Contains("Interlock", ex.Message);
            StringAssert.DoesNotContain("Host=a", ex.Message);
            Assert.Throws<ConfigurationErrorsException>(() => ServiceSettings.Load(Required(), Cs("Interlock", " ")));
            Assert.Throws<ConfigurationErrorsException>(() => ServiceSettings.Load(Required(), null));
        }

        [Test]
        public void Optional_values_override_the_defaults()
        {
            var a = Required();
            a["Interlock.EventCallbackName"] = "Cb";
            a["Interlock.EventCallbackPort"] = "9100";
            a["Interlock.RestBaseAddress"] = "http://localhost:9000/x";
            a["Interlock.DbSchema"] = "interlock_test";
            a["Interlock.SpoolPath"] = @"C:\data\spool.jsonl";
            a["Interlock.LogDir"] = "mylogs";
            a["Interlock.OibSendTimeoutSeconds"] = "60";
            var s = ServiceSettings.Load(a, Cs("Interlock", "Host=x"));
            Assert.That(s.EventCallbackName, Is.EqualTo("Cb"));
            Assert.That(s.EventCallbackPort, Is.EqualTo(9100));
            Assert.That(s.RestBaseAddress, Is.EqualTo("http://localhost:9000/x"));
            Assert.That(s.DbSchema, Is.EqualTo("interlock_test"));
            Assert.That(s.SpoolPath, Is.EqualTo(@"C:\data\spool.jsonl"));
            Assert.That(s.LogDir, Is.EqualTo(Path.Combine(ExeDir, "mylogs")));
            Assert.That(s.OibSendTimeout, Is.EqualTo(TimeSpan.FromSeconds(60)));
        }

        [Test]
        public void Blank_optional_values_take_the_default()
        {
            var s = ServiceSettings.Load(With("Interlock.EventCallbackPort", " "), Cs("Interlock", "Host=x"));
            Assert.That(s.EventCallbackPort, Is.EqualTo(8092));
        }

        [TestCase("5", 5)]
        [TestCase("120", 120)]
        [TestCase(" 45 ", 45)]
        public void Send_timeout_inside_the_bounds_is_kept(string text, int seconds)
        {
            var s = ServiceSettings.Load(With("Interlock.OibSendTimeoutSeconds", text), Cs("Interlock", "Host=x"));
            Assert.That(s.OibSendTimeout, Is.EqualTo(TimeSpan.FromSeconds(seconds)));
        }

        [TestCase("Interlock.OibSendTimeoutSeconds", "4")]
        [TestCase("Interlock.OibSendTimeoutSeconds", "121")]
        [TestCase("Interlock.OibSendTimeoutSeconds", "0")]
        [TestCase("Interlock.OibSendTimeoutSeconds", "abc")]
        [TestCase("Interlock.OibSendTimeoutSeconds", "30.5")]
        [TestCase("Interlock.EventCallbackPort", "0")]
        [TestCase("Interlock.EventCallbackPort", "65536")]
        [TestCase("Interlock.EventCallbackPort", "80a")]
        [TestCase("Interlock.EventCallbackPort", "99999999999")]
        public void Malformed_or_out_of_range_numbers_name_the_key(string key, string value)
        {
            var ex = Assert.Throws<ConfigurationErrorsException>(() => ServiceSettings.Load(With(key, value), Cs("Interlock", "Host=x")));
            StringAssert.Contains(key, ex.Message);
        }

        [TestCase("http://+:8090/interlock/v1")]
        [TestCase("http://*:9000/x")]
        [TestCase("https://server1:8443/interlock/v1")]
        public void Wildcard_and_named_rest_addresses_are_accepted(string address)
        {
            var s = ServiceSettings.Load(With("Interlock.RestBaseAddress", address), Cs("Interlock", "Host=x"));
            Assert.That(s.RestBaseAddress, Is.EqualTo(address));
        }

        [TestCase("not a url")]
        [TestCase("ftp://+:21/x")]
        [TestCase("http://+:99999/x")]
        [TestCase("/interlock/v1")]
        public void Malformed_rest_addresses_name_the_key(string address)
        {
            var ex = Assert.Throws<ConfigurationErrorsException>(() => ServiceSettings.Load(With("Interlock.RestBaseAddress", address), Cs("Interlock", "Host=x")));
            StringAssert.Contains("Interlock.RestBaseAddress", ex.Message);
        }

        [Test]
        public void The_text_form_never_shows_the_connection_string()
        {
            var s = ServiceSettings.Load(Required(), Cs("Interlock", "Host=db;Password=Secret123"));
            StringAssert.DoesNotContain("Secret123", s.ToString());
        }
    }
}
