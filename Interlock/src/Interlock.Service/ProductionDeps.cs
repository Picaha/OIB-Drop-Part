using System;
using System.Collections.Generic;
using Interlock.Core.Enforcement;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Service.Data;
using Interlock.Service.ExternalControl;
using Interlock.Service.Oib;
using Interlock.Service.Rest;
using Npgsql;

namespace Interlock.Service
{
    /// <summary>The real outside world: Serilog file, PostgreSQL, the JSONL spool, the OIB smart proxies and the WCF hosts.</summary>
    public sealed class ProductionDeps : ICompositionDeps
    {
        private readonly ServiceSettings _s;
        private readonly bool _echoToConsole;

        public ProductionDeps(ServiceSettings settings, bool echoToConsole)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _s = settings;
            _echoToConsole = echoToConsole;
        }

        public IClock Clock { get; } = new SystemClock();

        public ILog CreateLog()
        {
            return new SerilogLog(_s.LogDir, _echoToConsole);
        }

        public CompositionRepositories CreateRepositories(ILog log)
        {
            log.Info("Database: " + DescribeDatabase(_s.ConnectionString) + ", schema " + _s.DbSchema);
            var db = new NpgsqlDb(_s.ConnectionString, _s.DbSchema);
            return new CompositionRepositories
            {
                Locks = new NpgsqlLockRepository(db),
                Audit = new NpgsqlAuditRepository(db),
                Users = new NpgsqlUserRepository(db)
            };
        }

        public ILockSpool CreateSpool(ILog log)
        {
            log.Info("Lock spool: " + _s.SpoolPath);
            return new JsonlLockSpool(_s.SpoolPath, log);
        }

        public CompositionClients CreateClients(ILog log)
        {
            return new CompositionClients
            {
                SetupCenter = new SetupCenterClient(_s.SetupCenterEndpoint, Clock, _s.OibSendTimeout, log),
                LineControl = new LineControlClient(_s.LineControlEndpoint, Clock, _s.OibSendTimeout),
                Monitoring = new MonitoringClient(_s.MonitoringEndpoint, Clock, _s.OibSendTimeout)
            };
        }

        public IRefreshableStationDirectory CreateStations(IMonitoringClient monitoring, ISetupCenterClient setupCenter, ILog log)
        {
            return new StationDirectory(monitoring, setupCenter, log);
        }

        public IListener CreateExternalControl(LockCache cache, DecisionLogBuffer decisions, IClock clock, ILog log)
        {
            return new ExternalControlHost(
                new SetupCenterExternalControlService(cache, decisions, clock, log),
                new FeederExternalControlService(cache, decisions, clock, log),
                log);
        }

        public IListener CreateRest(IInterlockRestApi api, ILog log)
        {
            return new RestHost(api, _s.RestBaseAddress, log);
        }

        public ILockEventSource CreateSubscriber(Action<IList<PuLockReport>> onReports, ILog log)
        {
            return new SetupCenterEventSubscriber(_s.SetupCenterEndpoint, _s.CoreHost, _s.EventCallbackName, _s.EventCallbackPort,
                onReports, log, Clock, _s.OibSendTimeout);
        }

        /// <summary>Host, port and database only: never the user name or the password.</summary>
        internal static string DescribeDatabase(string connectionString)
        {
            try
            {
                var b = new NpgsqlConnectionStringBuilder(connectionString);
                return "host " + b.Host + ":" + b.Port + ", database " + b.Database;
            }
            catch (Exception)
            {
                return "(connection string not readable)";
            }
        }
    }
}
