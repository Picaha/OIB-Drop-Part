using System;
using System.Collections.Generic;
using Asm.As.Oib.SiplaceSetupCenter.Contracts.Message;
using Asm.As.Oib.SiplaceSetupCenter.Proxy.Business.Objects;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Service.Oib
{
    /// <summary>One Setup Center proxy as the subscriber uses it. A seam so the subscriber's state machine can be tested without OIB.</summary>
    internal interface ISetupCenterSession : IDisposable
    {
        event Action<LockStateChangedReportRequest> LockStateChanged;

        event Action SubscriptionStateInvalid;

        /// <summary>True when the subscription to the core events was made.</summary>
        bool Subscribe(string coreHost, string callbackName, int callbackPort);

        void Unsubscribe();
    }

    /// <summary>
    /// Subscribes to Setup Center's <c>LockStateChanged</c> notices and hands them to a callback as <see cref="PuLockReport"/>s.
    /// <see cref="Start"/> subscribes once and is idempotent; if the subscription cannot be made, or the proxy reports
    /// <c>SubscriptionStateInvalid</c>, <see cref="Tick"/> (the composition calls it every 30 s) makes a new one, spaced out by a
    /// <see cref="ReconnectBackoff"/> after failures. Nothing here throws into the caller or into the OIB thread that raises the
    /// events: every failure is logged. <see cref="Stop"/> unsubscribes quietly.
    /// </summary>
    public sealed class SetupCenterEventSubscriber : IDisposable
    {
        private readonly Func<ISetupCenterSession> _newSession;
        private readonly string _coreHost;
        private readonly string _callbackName;
        private readonly int _callbackPort;
        private readonly Action<IList<PuLockReport>> _onReports;
        private readonly ILog _log;
        private readonly ReconnectBackoff _backoff;
        private readonly object _gate = new object();
        private volatile Attached _current;
        private bool _started;

        /// <param name="endpointUri">The Setup Center proxy endpoint (<c>Asm.As.Oib.SetupCenter.Proxy.Endpoint</c>).</param>
        /// <param name="coreHost">The computer that runs the OIB core.</param>
        /// <param name="callbackName">Part of the callback address the core sends the events to.</param>
        /// <param name="callbackPort">The port the proxy listens on for the events.</param>
        /// <param name="onReports">Called on the OIB thread with the reports of one notice; keep it short and non-blocking.</param>
        /// <param name="clock">For the backoff; the system clock when omitted.</param>
        public SetupCenterEventSubscriber(string endpointUri, string coreHost, string callbackName, int callbackPort, Action<IList<PuLockReport>> onReports, ILog log, IClock clock = null)
            : this(NewProxySessionFactory(endpointUri), coreHost, callbackName, callbackPort, onReports, log, clock ?? new SystemClock())
        {
        }

        internal SetupCenterEventSubscriber(Func<ISetupCenterSession> newSession, string coreHost, string callbackName, int callbackPort, Action<IList<PuLockReport>> onReports, ILog log, IClock clock)
        {
            if (newSession == null)
                throw new ArgumentNullException(nameof(newSession));
            if (string.IsNullOrWhiteSpace(coreHost))
                throw new ArgumentException("The OIB core host is required.", nameof(coreHost));
            if (string.IsNullOrWhiteSpace(callbackName))
                throw new ArgumentException("The callback name is required.", nameof(callbackName));
            if (onReports == null)
                throw new ArgumentNullException(nameof(onReports));
            if (log == null)
                throw new ArgumentNullException(nameof(log));
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            _newSession = newSession;
            _coreHost = coreHost;
            _callbackName = callbackName;
            _callbackPort = callbackPort;
            _onReports = onReports;
            _log = log;
            _backoff = new ReconnectBackoff(clock);
        }

        /// <summary>True while a subscription is made and the proxy has not reported it invalid.</summary>
        public bool IsSubscribed
        {
            get
            {
                var current = _current;
                return current != null && current.Subscribed && !current.Invalid;
            }
        }

        /// <summary>Subscribes. Safe to call again; a failure is logged and left to <see cref="Tick"/>.</summary>
        public void Start()
        {
            lock (_gate)
            {
                _started = true;
                EnsureSubscribed();
            }
        }

        /// <summary>Makes a new subscription when there is none or the proxy called it invalid. Does nothing before <see cref="Start"/> or after <see cref="Stop"/>.</summary>
        public void Tick()
        {
            lock (_gate)
            {
                if (_started)
                    EnsureSubscribed();
            }
        }

        /// <summary>Unsubscribes and releases the proxy; never throws. <see cref="Start"/> can be called again afterwards.</summary>
        public void Stop()
        {
            lock (_gate)
            {
                _started = false;
                Release(_current, true);
                _current = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void EnsureSubscribed()
        {
            if (IsSubscribed)
                return;

            // A subscription the proxy gave up on is dropped first. It lost nothing the backoff should count.
            Release(_current, false);
            _current = null;

            if (!_backoff.CanTry)
                return;

            Attached attached = null;
            try
            {
                var session = _newSession();
                attached = new Attached(this, session);
                _current = attached;
                if (!session.Subscribe(_coreHost, _callbackName, _callbackPort))
                    throw new InvalidOperationException("Setup Center did not accept the subscription.");
                attached.Subscribed = true;
            }
            catch (Exception ex)
            {
                Release(attached, false);
                _current = null;
                _backoff.Failed();
                _log.Warn("Setup Center event subscription failed, will try again: " + ex.Message);
                return;
            }

            _backoff.Succeeded();
            _log.Info("Subscribed to Setup Center lock events (callback " + _callbackName + ", port " + _callbackPort + ").");
        }

        /// <summary>Detaches the handlers first, so nothing the dying proxy raises reaches the callback.</summary>
        private void Release(Attached attached, bool unsubscribe)
        {
            if (attached == null)
                return;

            attached.Detach();
            if (unsubscribe && attached.Subscribed)
            {
                try
                {
                    attached.Session.Unsubscribe();
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not unsubscribe from Setup Center lock events: " + ex.Message);
                }
            }
            try
            {
                attached.Session.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn("Could not release the Setup Center events proxy: " + ex.Message);
            }
        }

        /// <summary>Runs on the OIB thread: must never throw.</summary>
        private void OnLockStateChanged(LockStateChangedReportRequest request)
        {
            try
            {
                var reports = SetupCenterMapper.ToReports(request);
                if (reports.Count > 0)
                    _onReports(reports);
            }
            catch (Exception ex)
            {
                _log.Error("Handling a Setup Center lock state change failed.", ex);
            }
        }

        /// <summary>Runs on the OIB thread: must never throw, and must not wait for the lock <see cref="Tick"/> holds during a subscribe.</summary>
        private void OnSubscriptionStateInvalid(Attached attached)
        {
            try
            {
                attached.Invalid = true;
                _log.Warn("Setup Center reports the event subscription invalid; it is renewed on the next tick.");
            }
            catch (Exception)
            {
                // A logger that fails must not reach the OIB thread.
            }
        }

        private static Func<ISetupCenterSession> NewProxySessionFactory(string endpointUri)
        {
            var address = OibEndpoints.SecureAddress(endpointUri);
            return () => new ProxySession(new SiplaceSetupCenter(address, OibEndpoints.SecureBinding(endpointUri)));
        }

        /// <summary>One session with its handlers, so they can be taken off again.</summary>
        private sealed class Attached
        {
            private readonly SetupCenterEventSubscriber _owner;
            private readonly Action<LockStateChangedReportRequest> _onLockState;
            private readonly Action _onInvalid;
            private int _detached;

            public Attached(SetupCenterEventSubscriber owner, ISetupCenterSession session)
            {
                _owner = owner;
                Session = session;
                _onLockState = request =>
                {
                    if (_detached == 0)
                        _owner.OnLockStateChanged(request);
                };
                _onInvalid = () =>
                {
                    if (_detached == 0)
                        _owner.OnSubscriptionStateInvalid(this);
                };
                session.LockStateChanged += _onLockState;
                session.SubscriptionStateInvalid += _onInvalid;
            }

            public ISetupCenterSession Session { get; }

            public volatile bool Subscribed;

            public volatile bool Invalid;

            public void Detach()
            {
                System.Threading.Interlocked.Exchange(ref _detached, 1);
                Session.LockStateChanged -= _onLockState;
                Session.SubscriptionStateInvalid -= _onInvalid;
            }
        }

        /// <summary>The real session: one <see cref="SiplaceSetupCenter"/> proxy.</summary>
        private sealed class ProxySession : ISetupCenterSession
        {
            private readonly SiplaceSetupCenter _proxy;

            public ProxySession(SiplaceSetupCenter proxy)
            {
                _proxy = proxy;
                // The service has no UI thread to marshal the events to.
                _proxy.MarshalEventsToObjectCreatorThread = false;
                _proxy.LockStateChanged += RaiseLockStateChanged;
                _proxy.SubscriptionStateInvalid += RaiseSubscriptionStateInvalid;
            }

            public event Action<LockStateChangedReportRequest> LockStateChanged;

            public event Action SubscriptionStateInvalid;

            public bool Subscribe(string coreHost, string callbackName, int callbackPort)
            {
                return _proxy.SubscribeToCoreEvents(coreHost, callbackName, callbackPort);
            }

            public void Unsubscribe()
            {
                _proxy.UnsubscribeToCoreEvents();
            }

            public void Dispose()
            {
                _proxy.LockStateChanged -= RaiseLockStateChanged;
                _proxy.SubscriptionStateInvalid -= RaiseSubscriptionStateInvalid;
                _proxy.Dispose();
            }

            private void RaiseLockStateChanged(LockStateChangedReportRequest request)
            {
                var handler = LockStateChanged;
                if (handler != null)
                    handler(request);
            }

            private void RaiseSubscriptionStateInvalid(SubscriptionStateInvalidDetails details)
            {
                var handler = SubscriptionStateInvalid;
                if (handler != null)
                    handler();
            }
        }
    }
}
