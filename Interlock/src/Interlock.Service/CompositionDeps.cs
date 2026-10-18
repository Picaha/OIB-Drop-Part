using System;
using System.Collections.Generic;
using Interlock.Core.Enforcement;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Service.Rest;

namespace Interlock.Service
{
    /// <summary>Something that listens for callers (External Control, REST). <see cref="Open"/> throws when it cannot listen.</summary>
    public interface IListener : IDisposable
    {
        void Open();

        /// <summary>Never throws; does nothing when not open.</summary>
        void Close();

        /// <summary>As <see cref="Close()"/>, waiting at most <paramref name="timeout"/>, then aborting.</summary>
        void Close(TimeSpan timeout);
    }

    /// <summary>The station directory with its refresh (never throws).</summary>
    public interface IRefreshableStationDirectory : IStationDirectory
    {
        void Refresh();
    }

    /// <summary>The Setup Center lock-event subscription. None of the members throws.</summary>
    public interface ILockEventSource : IDisposable
    {
        void Start();
        void Tick();
        void Stop();
    }

    public sealed class CompositionRepositories
    {
        public ILockRepository Locks { get; set; }
        public IAuditRepository Audit { get; set; }
        public IUserRepository Users { get; set; }
    }

    public sealed class CompositionClients
    {
        public ISetupCenterClient SetupCenter { get; set; }
        public ILineControlClient LineControl { get; set; }
        public IMonitoringClient Monitoring { get; set; }
    }

    /// <summary>
    /// The objects that touch the outside world (log file, database, spool file, OIB, listeners). <see cref="Composition"/> builds
    /// everything else itself, so a test can replace these with in-memory fakes. Anything returned that is
    /// <see cref="IDisposable"/> is disposed by the composition when it stops.
    /// </summary>
    public interface ICompositionDeps
    {
        IClock Clock { get; }

        ILog CreateLog();

        CompositionRepositories CreateRepositories(ILog log);

        ILockSpool CreateSpool(ILog log);

        CompositionClients CreateClients(ILog log);

        IRefreshableStationDirectory CreateStations(IMonitoringClient monitoring, ISetupCenterClient setupCenter, ILog log);

        IListener CreateExternalControl(LockCache cache, DecisionLogBuffer decisions, IClock clock, ILog log);

        IListener CreateRest(IInterlockRestApi api, ILog log);

        ILockEventSource CreateSubscriber(Action<IList<PuLockReport>> onReports, ILog log);
    }
}
