using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Interlock.Core.Errors;
using Interlock.Service.Oib;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Service
{
    /// <summary>The shared failure policy of the OIB clients, run against a fake proxy (no network).</summary>
    [TestFixture]
    public class OibConnectionTests
    {
        private sealed class FakeProxy
        {
            public int Number { get; set; }
        }

        private FakeClock _clock;
        private List<FakeProxy> _created;
        private List<FakeProxy> _closed;
        private Exception _closeFails;
        private Exception _createFails;
        private OibConnection<FakeProxy> _conn;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeClock();
            _created = new List<FakeProxy>();
            _closed = new List<FakeProxy>();
            _closeFails = null;
            _createFails = null;
            _conn = new OibConnection<FakeProxy>("Setup Center", Create, Close, _clock);
        }

        private FakeProxy Create()
        {
            if (_createFails != null)
                throw _createFails;
            lock (_created)
            {
                var p = new FakeProxy { Number = _created.Count + 1 };
                _created.Add(p);
                return p;
            }
        }

        private void Close(FakeProxy p)
        {
            lock (_closed)
                _closed.Add(p);
            if (_closeFails != null)
                throw _closeFails;
        }

        private int Ok(FakeProxy p)
        {
            return p.Number;
        }

        // ---- creation and reuse ----

        [Test]
        public void The_proxy_is_created_on_the_first_call_and_reused()
        {
            Assert.That(_created, Is.Empty, "nothing is created before the first call");

            var first = _conn.Call("Ping", Ok);
            var second = _conn.Call("Ping", Ok);

            Assert.That(new[] { first, second }, Is.EqualTo(new[] { 1, 1 }));
            Assert.That(_created, Has.Count.EqualTo(1));
        }

        [Test]
        public void Calls_from_many_threads_create_the_proxy_once()
        {
            var create = new OibConnection<FakeProxy>("Setup Center", () =>
            {
                Thread.Sleep(50);
                return Create();
            }, Close, _clock);
            var start = new ManualResetEventSlim(false);

            var tasks = Enumerable.Range(0, 8)
                .Select(i => Task.Run(() =>
                {
                    start.Wait();
                    return create.Call("Ping", Ok);
                })).ToArray();
            start.Set();
            Task.WaitAll(tasks);

            Assert.That(_created, Has.Count.EqualTo(1));
            Assert.That(tasks.Select(t => t.Result), Is.All.EqualTo(1));
        }

        // ---- connection failures ----

        [Test]
        public void A_communication_failure_drops_the_proxy_and_blocks_calls_until_the_backoff_is_over()
        {
            _conn.Call("Ping", Ok);

            var ex = Assert.Throws<OibCallException>(() => _conn.Call<int>("Ping", p => { throw new CommunicationException("link down"); }));

            Assert.That(ex.InnerException, Is.TypeOf<CommunicationException>());
            StringAssert.Contains("Setup Center", ex.Message);
            StringAssert.Contains("Ping", ex.Message);
            StringAssert.Contains("link down", ex.Message);
            Assert.That(_closed, Is.EqualTo(new[] { _created[0] }), "the dead proxy was closed");

            var blocked = Assert.Throws<OibCallException>(() => _conn.Call("Ping", Ok));
            Assert.That(blocked.Message, Is.EqualTo("Setup Center not connected"));
            Assert.That(_created, Has.Count.EqualTo(1), "no proxy is created while the backoff runs");

            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.That(_conn.Call("Ping", Ok), Is.EqualTo(2), "a new proxy after the wait");
            Assert.That(_created, Has.Count.EqualTo(2));
        }

        [Test]
        public void A_blocked_call_does_not_run_the_delegate()
        {
            Assert.Throws<OibCallException>(() => _conn.Call<int>("Ping", p => { throw new TimeoutException("slow"); }));
            var ran = false;

            Assert.Throws<OibCallException>(() => _conn.Call("Ping", p => { ran = true; return 1; }));

            Assert.That(ran, Is.False);
        }

        [Test]
        public void A_timeout_counts_as_a_connection_failure()
        {
            Assert.Throws<OibCallException>(() => _conn.Call<int>("Ping", p => { throw new TimeoutException("slow"); }));

            Assert.That(_closed, Has.Count.EqualTo(1));
            Assert.That(Assert.Throws<OibCallException>(() => _conn.Call("Ping", Ok)).Message, Is.EqualTo("Setup Center not connected"));
        }

        [Test]
        public void Other_transport_failures_count_too()
        {
            var failures = new Exception[]
            {
                new IOException("pipe"),
                new System.Net.Sockets.SocketException(10054),
                new ObjectDisposedException("channel"),
                new EndpointNotFoundException("nobody home"),
                new CommunicationObjectFaultedException("faulted")
            };

            foreach (var failure in failures)
            {
                var conn = new OibConnection<FakeProxy>("LC", Create, Close, _clock);
                Assert.Throws<OibCallException>(() => conn.Call<int>("Ping", p => { throw failure; }), failure.GetType().Name);
                Assert.That(Assert.Throws<OibCallException>(() => conn.Call("Ping", Ok)).Message, Is.EqualTo("LC not connected"), failure.GetType().Name);
            }
        }

        [Test]
        public void A_transport_failure_inside_another_exception_counts()
        {
            var wrapped = new InvalidOperationException("outer", new CommunicationException("inner"));

            Assert.Throws<OibCallException>(() => _conn.Call<int>("Ping", p => { throw wrapped; }));

            Assert.That(Assert.Throws<OibCallException>(() => _conn.Call("Ping", Ok)).Message, Is.EqualTo("Setup Center not connected"));
        }

        [Test]
        public void A_failure_to_create_the_proxy_counts_and_is_wrapped()
        {
            _createFails = new InvalidOperationException("bad endpoint");

            var ex = Assert.Throws<OibCallException>(() => _conn.Call("Ping", Ok));

            Assert.That(ex.InnerException, Is.SameAs(_createFails));
            _createFails = null;
            Assert.That(Assert.Throws<OibCallException>(() => _conn.Call("Ping", Ok)).Message, Is.EqualTo("Setup Center not connected"));
            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.That(_conn.Call("Ping", Ok), Is.EqualTo(1));
        }

        [Test]
        public void Repeated_failures_wait_longer_and_a_success_resets_the_wait()
        {
            Assert.Throws<OibCallException>(() => _conn.Call<int>("Ping", p => { throw new CommunicationException("1"); }));
            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Throws<OibCallException>(() => _conn.Call<int>("Ping", p => { throw new CommunicationException("2"); }));

            _clock.Advance(TimeSpan.FromSeconds(9));
            Assert.That(Assert.Throws<OibCallException>(() => _conn.Call("Ping", Ok)).Message, Is.EqualTo("Setup Center not connected"), "second wait is 10 s");
            _clock.Advance(TimeSpan.FromSeconds(1));
            _conn.Call("Ping", Ok);

            Assert.Throws<OibCallException>(() => _conn.Call<int>("Ping", p => { throw new CommunicationException("3"); }));
            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.That(_conn.Call("Ping", Ok), Is.GreaterThan(0), "after a success the wait is 5 s again");
        }

        private void FailOnce()
        {
            Assert.Throws<OibCallException>(() => _conn.Call<int>("Ping", p => { throw new CommunicationException("down"); }));
        }

        private void AssertBlocked()
        {
            Assert.That(Assert.Throws<OibCallException>(() => _conn.Call("Ping", Ok)).Message, Is.EqualTo("Setup Center not connected"));
        }

        [Test]
        public void A_probe_that_answers_false_does_not_reset_the_backoff()
        {
            FailOnce();
            _clock.Advance(TimeSpan.FromSeconds(5));

            var connected = _conn.Call("IsConnected", p => false, answer => answer);
            Assert.That(connected, Is.False, "the false answer is returned, not thrown");
            FailOnce();

            _clock.Advance(TimeSpan.FromSeconds(9));
            AssertBlocked();
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(_conn.Call("Ping", Ok), Is.GreaterThan(0), "the second wait is 10 s: the probe did not reset the count");
        }

        [Test]
        public void A_probe_that_never_proves_the_connection_does_not_reset_the_backoff_even_when_it_answers_true()
        {
            FailOnce();
            _clock.Advance(TimeSpan.FromSeconds(5));

            var licensed = _conn.Call("HasAdapterLicense", p => true, answer => false);
            Assert.That(licensed, Is.True);
            FailOnce();

            _clock.Advance(TimeSpan.FromSeconds(9));
            AssertBlocked();
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(_conn.Call("Ping", Ok), Is.GreaterThan(0));
        }

        [Test]
        public void A_probe_that_answers_true_resets_the_backoff()
        {
            FailOnce();
            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.That(_conn.Call("IsConnected", p => true, answer => answer), Is.True);

            FailOnce();

            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.That(_conn.Call("Ping", Ok), Is.GreaterThan(0), "after a proven connection the next wait is 5 s again");
        }

        [Test]
        public void A_probe_that_answers_false_is_not_a_connection_failure()
        {
            _conn.Call("Ping", Ok);

            Assert.That(_conn.Call("IsConnected", p => false, answer => answer), Is.False);

            Assert.That(_closed, Is.Empty, "the proxy stays");
            Assert.That(_conn.Call("Ping", Ok), Is.EqualTo(1), "and the next call is not held back");
            Assert.That(_created, Has.Count.EqualTo(1));
        }

        // ---- one-time open ----

        private const string OpenInProgress = "Cannot make a call on this channel because a call to Open() is in progress";

        /// <summary>
        /// Behaves like a WCF channel: the first call opens it (takes 50 ms), and a call made by another thread while that open is
        /// in progress fails with the WCF message.
        /// </summary>
        private sealed class ChannelProxy
        {
            private int _state; // 0 unopened, 1 opening, 2 open
            public int Opens;
            public int Active;
            public int MaxActive;

            public int Use(int sleepMs)
            {
                var before = Interlocked.CompareExchange(ref _state, 1, 0);
                if (before == 0)
                {
                    Interlocked.Increment(ref Opens);
                    Thread.Sleep(50);
                    Volatile.Write(ref _state, 2);
                }
                else if (before == 1)
                {
                    throw new InvalidOperationException(OpenInProgress);
                }

                var now = Interlocked.Increment(ref Active);
                int seen;
                while ((seen = Volatile.Read(ref MaxActive)) < now && Interlocked.CompareExchange(ref MaxActive, now, seen) != seen)
                {
                }
                Thread.Sleep(sleepMs);
                Interlocked.Decrement(ref Active);
                return 1;
            }
        }

        private Task<int>[] CallFromThreads(OibConnection<ChannelProxy> conn, int threads, int sleepMs)
        {
            var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, threads)
                .Select(i => Task.Run(() =>
                {
                    start.Wait();
                    return conn.Call("Use", p => p.Use(sleepMs));
                })).ToArray();
            start.Set();
            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException)
            {
                // The tests look at each task.
            }
            return tasks;
        }

        [Test]
        public void A_new_proxy_is_opened_once_before_other_threads_use_it()
        {
            var proxy = new ChannelProxy();
            var conn = new OibConnection<ChannelProxy>("Setup Center", () => proxy, p => { }, _clock, p => p.Use(0));

            var tasks = CallFromThreads(conn, 8, 1);

            Assert.That(tasks.Select(x => x.Status), Is.All.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(proxy.Opens, Is.EqualTo(1));
        }

        [Test]
        public void Without_an_open_step_concurrent_first_calls_hit_the_open_in_progress_failure()
        {
            // The negative twin of the test above: it documents the defect the open step fixes.
            var proxy = new ChannelProxy();
            var conn = new OibConnection<ChannelProxy>("Setup Center", () => proxy, p => { }, _clock);

            var tasks = CallFromThreads(conn, 8, 1);

            var failures = tasks.Where(x => x.IsFaulted).Select(x => x.Exception.InnerException).OfType<OibCallException>().ToList();
            Assert.That(failures, Is.Not.Empty);
            Assert.That(failures.Select(f => f.InnerException.Message), Has.All.EqualTo(OpenInProgress));
        }

        [Test]
        public void A_failed_open_counts_once_even_with_many_callers_and_is_retried_after_the_wait()
        {
            var created = new List<ChannelProxy>();
            var closed = new List<ChannelProxy>();
            var fail = true;
            var conn = new OibConnection<ChannelProxy>("Setup Center", () => { lock (created) { var p = new ChannelProxy(); created.Add(p); return p; } }, p => { lock (closed) closed.Add(p); }, _clock,
                p => { Thread.Sleep(30); if (fail) throw new System.ServiceModel.EndpointNotFoundException("no core"); });

            var tasks = CallFromThreads(conn, 8, 0);

            var errors = tasks.Select(x => x.Exception.InnerException).OfType<OibCallException>().ToList();
            Assert.That(errors, Has.Count.EqualTo(8));
            Assert.That(errors.Count(e => e.InnerException is System.ServiceModel.EndpointNotFoundException), Is.EqualTo(1), "one caller ran the open");
            Assert.That(errors.Count(e => e.Message == "Setup Center not connected"), Is.EqualTo(7));
            Assert.That(created, Has.Count.EqualTo(1));
            Assert.That(closed, Is.EqualTo(created));

            fail = false;
            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.That(conn.Call("Use", p => p.Use(0)), Is.EqualTo(1), "one failure means a 5 s wait");
            Assert.That(created, Has.Count.EqualTo(2));
        }

        [Test]
        public void A_fault_from_the_open_step_proves_the_channel_open()
        {
            var created = new List<ChannelProxy>();
            var conn = new OibConnection<ChannelProxy>("Line Control", () => { var p = new ChannelProxy(); created.Add(p); return p; }, p => { }, _clock,
                p => { throw new FaultException("unknown station"); });

            Assert.That(conn.Call("Use", p => p.Use(0)), Is.EqualTo(1));
            Assert.That(conn.Call("Use", p => p.Use(0)), Is.EqualTo(1));
            Assert.That(created, Has.Count.EqualTo(1), "the proxy was kept and no wait started");
        }

        [Test]
        public void Disposing_during_an_open_waits_for_it_and_then_closes_the_proxy()
        {
            var release = new ManualResetEventSlim(false);
            var entered = new ManualResetEventSlim(false);
            var proxy = new ChannelProxy();
            var closed = new List<ChannelProxy>();
            var conn = new OibConnection<ChannelProxy>("Setup Center", () => proxy, p => { lock (closed) closed.Add(p); }, _clock,
                p => { entered.Set(); release.Wait(); });

            var call = Task.Run(() => conn.Call("Use", p => p.Use(0)));
            Assert.That(entered.Wait(5000), Is.True);
            var dispose = Task.Run(() => conn.Dispose());
            Assert.That(dispose.Wait(150), Is.False, "Dispose waits for the open (documented)");

            release.Set();

            Assert.That(Task.WaitAll(new Task[] { call, dispose }, 5000), Is.True, "no deadlock");
            Assert.That(closed, Is.EqualTo(new[] { proxy }));
        }

        [Test]
        public void After_the_open_calls_run_concurrently()
        {
            var proxy = new ChannelProxy();
            var conn = new OibConnection<ChannelProxy>("Setup Center", () => proxy, p => { }, _clock, p => p.Use(0));
            conn.Call("Use", p => p.Use(0));

            CallFromThreads(conn, 2, 300);

            Assert.That(proxy.MaxActive, Is.EqualTo(2), "two slow calls overlapped");
        }

        [Test]
        public void Closing_a_dead_proxy_never_throws()
        {
            _closeFails = new InvalidOperationException("close failed");

            var ex = Assert.Throws<OibCallException>(() => _conn.Call<int>("Ping", p => { throw new CommunicationException("down"); }));

            StringAssert.Contains("down", ex.Message);
            Assert.That(_closed, Has.Count.EqualTo(1));
        }

        [Test]
        public void Two_calls_that_fail_on_the_same_proxy_count_as_one_failure()
        {
            var barrier = new Barrier(2);
            Func<FakeProxy, int> fail = p =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                throw new CommunicationException("down");
            };

            var a = Task.Run(() => Assert.Throws<OibCallException>(() => _conn.Call("Ping", fail)));
            var b = Task.Run(() => Assert.Throws<OibCallException>(() => _conn.Call("Ping", fail)));
            Task.WaitAll(a, b);

            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.That(_conn.Call("Ping", Ok), Is.GreaterThan(0), "one failure means a 5 s wait, not 10 s");
            Assert.That(_created, Has.Count.EqualTo(2), "only the shared proxy was replaced");
        }

        // ---- business failures ----

        [Test]
        public void A_fault_from_the_service_is_wrapped_but_keeps_the_proxy_and_the_connection()
        {
            _conn.Call("Ping", Ok);
            var fault = new FaultException("Setup Center said no");

            var ex = Assert.Throws<OibCallException>(() => _conn.Call<int>("Lock", p => { throw fault; }));

            Assert.That(ex.InnerException, Is.SameAs(fault));
            StringAssert.Contains("Setup Center said no", ex.Message);
            Assert.That(_closed, Is.Empty);
            Assert.That(_conn.Call("Ping", Ok), Is.EqualTo(1), "the same proxy answers at once");
            Assert.That(_created, Has.Count.EqualTo(1));
        }

        [Test]
        public void A_typed_fault_inside_a_communication_exception_family_is_not_a_connection_failure()
        {
            Assert.Throws<OibCallException>(() => _conn.Call<int>("Lock", p => { throw new FaultException<string>("detail", "reason"); }));

            Assert.That(_closed, Is.Empty);
            Assert.That(_conn.Call("Ping", Ok), Is.EqualTo(1));
        }

        [Test]
        public void Any_other_exception_is_wrapped_without_a_backoff()
        {
            var bug = new NullReferenceException("oops");

            var ex = Assert.Throws<OibCallException>(() => _conn.Call<int>("Lock", p => { throw bug; }));

            Assert.That(ex.InnerException, Is.SameAs(bug));
            Assert.That(_closed, Is.Empty);
            Assert.That(_conn.Call("Ping", Ok), Is.EqualTo(1));
        }

        [Test]
        public void An_oib_call_exception_from_the_delegate_passes_through_unchanged()
        {
            var own = new OibCallException("business rule");

            var ex = Assert.Throws<OibCallException>(() => _conn.Call<int>("Lock", p => { throw own; }));

            Assert.That(ex, Is.SameAs(own));
            Assert.That(_closed, Is.Empty);
        }

        [Test]
        public void The_action_overload_runs_the_delegate()
        {
            var seen = 0;

            _conn.Call("Ping", p => { seen = p.Number; });

            Assert.That(seen, Is.EqualTo(1));
        }

        // ---- dispose ----

        [Test]
        public void Disposing_closes_the_proxy_and_refuses_further_calls()
        {
            _conn.Call("Ping", Ok);

            _conn.Dispose();

            Assert.That(_closed, Is.EqualTo(new[] { _created[0] }));
            Assert.Throws<OibCallException>(() => _conn.Call("Ping", Ok));
            Assert.DoesNotThrow(() => _conn.Dispose());
            Assert.That(_closed, Has.Count.EqualTo(1), "closed once");
        }

        [Test]
        public void Disposing_before_any_call_is_harmless()
        {
            Assert.DoesNotThrow(() => _conn.Dispose());
            Assert.That(_closed, Is.Empty);
        }

        [Test]
        public void Arguments_are_checked()
        {
            Assert.Throws<ArgumentException>(() => new OibConnection<FakeProxy>(" ", Create, Close, _clock));
            Assert.Throws<ArgumentNullException>(() => new OibConnection<FakeProxy>("X", null, Close, _clock));
            Assert.Throws<ArgumentNullException>(() => new OibConnection<FakeProxy>("X", Create, null, _clock));
            Assert.Throws<ArgumentNullException>(() => new OibConnection<FakeProxy>("X", Create, Close, null));
        }
    }
}
