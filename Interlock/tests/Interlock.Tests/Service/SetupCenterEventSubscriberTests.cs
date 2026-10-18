using System;
using System.Collections.Generic;
using System.Linq;
using Asm.As.Oib.SiplaceSetupCenter.Contracts.Data;
using Asm.As.Oib.SiplaceSetupCenter.Contracts.Message;
using Interlock.Core.Model;
using Interlock.Service.Oib;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Service
{
    /// <summary>The event subscriber's state machine, run against a fake session (no OIB endpoint is called).</summary>
    [TestFixture]
    public class SetupCenterEventSubscriberTests
    {
        private sealed class FakeSession : ISetupCenterSession
        {
            public event Action<LockStateChangedReportRequest> LockStateChanged;

            public event Action SubscriptionStateInvalid;

            public bool SubscribeResult = true;
            public Exception SubscribeThrows;
            public Exception UnsubscribeThrows;
            public Exception DisposeThrows;
            public readonly List<string> SubscribeArgs = new List<string>();
            public int Unsubscribed;
            public int Disposed;

            public bool Subscribe(string coreHost, string callbackName, int callbackPort)
            {
                SubscribeArgs.Add(coreHost + "|" + callbackName + "|" + callbackPort);
                if (SubscribeThrows != null)
                    throw SubscribeThrows;
                return SubscribeResult;
            }

            public void Unsubscribe()
            {
                Unsubscribed++;
                if (UnsubscribeThrows != null)
                    throw UnsubscribeThrows;
            }

            public void Dispose()
            {
                Disposed++;
                if (DisposeThrows != null)
                    throw DisposeThrows;
            }

            public void RaiseLockStateChanged(LockStateChangedReportRequest request)
            {
                var handler = LockStateChanged;
                if (handler != null)
                    handler(request);
            }

            public void RaiseInvalid()
            {
                var handler = SubscriptionStateInvalid;
                if (handler != null)
                    handler();
            }

            public int HandlerCount
            {
                get { return (LockStateChanged == null ? 0 : LockStateChanged.GetInvocationList().Length) + (SubscriptionStateInvalid == null ? 0 : SubscriptionStateInvalid.GetInvocationList().Length); }
            }
        }

        private FakeClock _clock;
        private ListLog _log;
        private List<FakeSession> _sessions;
        private List<IList<PuLockReport>> _reports;
        private Action<IList<PuLockReport>> _onReports;
        private Action<FakeSession> _configureNext;
        private SetupCenterEventSubscriber _sub;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeClock();
            _log = new ListLog();
            _sessions = new List<FakeSession>();
            _reports = new List<IList<PuLockReport>>();
            _onReports = r => _reports.Add(r);
            _configureNext = null;
            _sub = NewSubscriber();
        }

        private SetupCenterEventSubscriber NewSubscriber()
        {
            return new SetupCenterEventSubscriber(NewSession, "core1", "Interlock.SetupCenterEvents", 8092, r => _onReports(r), _log, _clock);
        }

        private ISetupCenterSession NewSession()
        {
            var s = new FakeSession();
            if (_configureNext != null)
                _configureNext(s);
            _sessions.Add(s);
            return s;
        }

        private static LockStateChangedReportRequest RequestFor(string uid)
        {
            return new LockStateChangedReportRequest(new LockStateChangedReport
            {
                PackagingUnits = new List<PackagingUnit>
                {
                    new PackagingUnit { UID = uid, LockInfos = new List<LockInfo> { new LockInfo { Source = 9, Reason = 1, Date = new DateTime(2026, 10, 18, 3, 0, 0, DateTimeKind.Utc), Message = "mismatch" } } }
                }
            });
        }

        // ---- start / subscribe ----

        [Test]
        public void Nothing_happens_before_start()
        {
            _sub.Tick();

            Assert.That(_sessions, Is.Empty);
            Assert.That(_sub.IsSubscribed, Is.False);
        }

        [Test]
        public void Start_subscribes_with_the_callback_settings()
        {
            _sub.Start();

            Assert.That(_sessions, Has.Count.EqualTo(1));
            Assert.That(_sessions[0].SubscribeArgs, Is.EqualTo(new[] { "core1|Interlock.SetupCenterEvents|8092" }));
            Assert.That(_sub.IsSubscribed, Is.True);
        }

        [Test]
        public void Start_is_idempotent()
        {
            _sub.Start();
            _sub.Start();
            _sub.Tick();

            Assert.That(_sessions, Has.Count.EqualTo(1));
            Assert.That(_sessions[0].SubscribeArgs, Has.Count.EqualTo(1));
        }

        [Test]
        public void A_failed_subscribe_is_logged_and_retried_by_tick_after_the_backoff()
        {
            _configureNext = s => s.SubscribeResult = false;

            Assert.DoesNotThrow(() => _sub.Start());

            Assert.That(_sub.IsSubscribed, Is.False);
            Assert.That(_log.Warnings, Has.Count.EqualTo(1));
            Assert.That(_sessions[0].Disposed, Is.EqualTo(1), "the unused session is released");

            _sub.Tick();
            Assert.That(_sessions, Has.Count.EqualTo(1), "the backoff holds the retry back");

            _clock.Advance(TimeSpan.FromSeconds(5));
            _configureNext = null;
            _sub.Tick();

            Assert.That(_sessions, Has.Count.EqualTo(2));
            Assert.That(_sub.IsSubscribed, Is.True);
        }

        [Test]
        public void A_subscribe_that_throws_is_logged_not_thrown_and_backs_off()
        {
            _configureNext = s => s.SubscribeThrows = new InvalidOperationException("core down");

            Assert.DoesNotThrow(() => _sub.Start());

            Assert.That(_sub.IsSubscribed, Is.False);
            Assert.That(_log.Warnings.Concat(_log.Errors).Any(m => m.Contains("core down")), Is.True, "the cause is logged");
            Assert.That(_sessions[0].Disposed, Is.EqualTo(1));

            _clock.Advance(TimeSpan.FromSeconds(4));
            _sub.Tick();
            Assert.That(_sessions, Has.Count.EqualTo(1));
            _clock.Advance(TimeSpan.FromSeconds(1));
            _sub.Tick();
            Assert.That(_sessions, Has.Count.EqualTo(2));
        }

        [Test]
        public void A_failing_session_factory_is_treated_like_a_failed_subscribe()
        {
            var sub = new SetupCenterEventSubscriber(() => { throw new InvalidOperationException("bad endpoint"); }, "core1", "cb", 1, r => { }, _log, _clock);

            Assert.DoesNotThrow(sub.Start);

            Assert.That(sub.IsSubscribed, Is.False);
            Assert.That(_log.Warnings.Concat(_log.Errors).Any(m => m.Contains("bad endpoint")), Is.True);
        }

        [Test]
        public void Repeated_failures_wait_longer_each_time()
        {
            _configureNext = s => s.SubscribeResult = false;
            _sub.Start();
            _clock.Advance(TimeSpan.FromSeconds(5));
            _sub.Tick();
            Assert.That(_sessions, Has.Count.EqualTo(2));

            _clock.Advance(TimeSpan.FromSeconds(9));
            _sub.Tick();
            Assert.That(_sessions, Has.Count.EqualTo(2), "the second wait is 10 s");
            _clock.Advance(TimeSpan.FromSeconds(1));
            _sub.Tick();
            Assert.That(_sessions, Has.Count.EqualTo(3));
        }

        // ---- events ----

        [Test]
        public void A_lock_state_change_reaches_the_callback_as_reports()
        {
            _sub.Start();

            _sessions[0].RaiseLockStateChanged(RequestFor("A"));

            Assert.That(_reports, Has.Count.EqualTo(1));
            Assert.That(_reports[0].Single().Uid, Is.EqualTo("A"));
            Assert.That(_reports[0].Single().LockInfos.Single().Reason, Is.EqualTo(1));
        }

        [Test]
        public void A_throwing_callback_is_logged_and_never_reaches_the_oib_thread()
        {
            _onReports = r => { throw new InvalidOperationException("db down"); };
            _sub.Start();

            Assert.DoesNotThrow(() => _sessions[0].RaiseLockStateChanged(RequestFor("A")));

            Assert.That(_log.Errors, Has.Count.EqualTo(1));
        }

        [Test]
        public void A_request_without_reports_does_not_call_back()
        {
            _sub.Start();

            _sessions[0].RaiseLockStateChanged(null);
            _sessions[0].RaiseLockStateChanged(new LockStateChangedReportRequest());
            _sessions[0].RaiseLockStateChanged(RequestFor(" "));

            Assert.That(_reports, Is.Empty);
            Assert.That(_log.Errors, Is.Empty);
        }

        // ---- invalid subscription ----

        [Test]
        public void An_invalid_subscription_is_replaced_by_the_next_tick()
        {
            _sub.Start();

            _sessions[0].RaiseInvalid();

            Assert.That(_sub.IsSubscribed, Is.False);
            Assert.That(_log.Warnings, Has.Count.EqualTo(1));

            _sub.Tick();

            Assert.That(_sessions, Has.Count.EqualTo(2));
            Assert.That(_sessions[0].Disposed, Is.EqualTo(1), "the old session is released");
            Assert.That(_sessions[1].SubscribeArgs, Has.Count.EqualTo(1));
            Assert.That(_sub.IsSubscribed, Is.True);
        }

        [Test]
        public void A_replaced_session_no_longer_reports_anything()
        {
            _sub.Start();
            _sessions[0].RaiseInvalid();
            _sub.Tick();

            _sessions[0].RaiseLockStateChanged(RequestFor("OLD"));
            _sessions[0].RaiseInvalid();

            Assert.That(_reports, Is.Empty);
            Assert.That(_sub.IsSubscribed, Is.True, "the stale session cannot invalidate the new one");
            Assert.That(_sessions[0].HandlerCount, Is.EqualTo(0), "its handlers were detached");
        }

        [Test]
        public void A_tick_leaves_a_healthy_subscription_alone()
        {
            _sub.Start();

            _sub.Tick();
            _sub.Tick();

            Assert.That(_sessions, Has.Count.EqualTo(1));
            Assert.That(_sessions[0].Disposed, Is.EqualTo(0));
        }

        [Test]
        public void An_invalid_event_during_subscribe_wins_over_the_subscribe_result()
        {
            var sub = new SetupCenterEventSubscriber(() => new RaisingSession(), "core1", "cb", 1, r => { }, _log, _clock);

            sub.Start();

            Assert.That(sub.IsSubscribed, Is.False);
        }

        private sealed class RaisingSession : ISetupCenterSession
        {
            public event Action<LockStateChangedReportRequest> LockStateChanged { add { } remove { } }

            public event Action SubscriptionStateInvalid;

            public bool Subscribe(string coreHost, string callbackName, int callbackPort)
            {
                SubscriptionStateInvalid();
                return true;
            }

            public void Unsubscribe() { }

            public void Dispose() { }
        }

        // ---- stop ----

        [Test]
        public void Stop_unsubscribes_and_releases_the_session()
        {
            _sub.Start();

            _sub.Stop();

            Assert.That(_sessions[0].Unsubscribed, Is.EqualTo(1));
            Assert.That(_sessions[0].Disposed, Is.EqualTo(1));
            Assert.That(_sub.IsSubscribed, Is.False);
            Assert.That(_sessions[0].HandlerCount, Is.EqualTo(0));
        }

        [Test]
        public void Stop_never_throws_and_tick_does_not_resubscribe_afterwards()
        {
            _configureNext = s => { s.UnsubscribeThrows = new InvalidOperationException("gone"); s.DisposeThrows = new InvalidOperationException("gone too"); };
            _sub.Start();

            Assert.DoesNotThrow(() => _sub.Stop());
            Assert.DoesNotThrow(() => _sub.Stop());
            _sub.Tick();

            Assert.That(_sessions, Has.Count.EqualTo(1));
            Assert.That(_sessions[0].Unsubscribed, Is.EqualTo(1));
        }

        [Test]
        public void Start_after_stop_subscribes_again()
        {
            _sub.Start();
            _sub.Stop();

            _sub.Start();

            Assert.That(_sessions, Has.Count.EqualTo(2));
            Assert.That(_sub.IsSubscribed, Is.True);
        }

        [Test]
        public void Dispose_stops()
        {
            _sub.Start();

            _sub.Dispose();

            Assert.That(_sessions[0].Unsubscribed, Is.EqualTo(1));
            Assert.That(_sub.IsSubscribed, Is.False);
        }

        [Test]
        public void Arguments_are_checked()
        {
            Func<ISetupCenterSession> factory = NewSession;
            Assert.Throws<ArgumentNullException>(() => new SetupCenterEventSubscriber((Func<ISetupCenterSession>)null, "core", "cb", 1, r => { }, _log, _clock));
            Assert.Throws<ArgumentException>(() => new SetupCenterEventSubscriber(factory, " ", "cb", 1, r => { }, _log, _clock));
            Assert.Throws<ArgumentException>(() => new SetupCenterEventSubscriber(factory, "core", "", 1, r => { }, _log, _clock));
            Assert.Throws<ArgumentNullException>(() => new SetupCenterEventSubscriber(factory, "core", "cb", 1, null, _log, _clock));
            Assert.Throws<ArgumentNullException>(() => new SetupCenterEventSubscriber(factory, "core", "cb", 1, r => { }, null, _clock));
            Assert.Throws<ArgumentNullException>(() => new SetupCenterEventSubscriber(factory, "core", "cb", 1, r => { }, _log, null));
        }

        [Test]
        public void The_public_constructor_checks_the_endpoint_without_connecting()
        {
            Assert.Throws<ArgumentException>(() => new SetupCenterEventSubscriber(" ", "core", "cb", 1, r => { }, _log));

            using (var sub = new SetupCenterEventSubscriber("net.tcp://localhost:1406/Asm.As.Oib.SetupCenter/SiplaceSetupCenter/Reliable", "core", "cb", 1, r => { }, _log))
                Assert.That(sub.IsSubscribed, Is.False, "construction subscribes to nothing");
        }
    }
}
