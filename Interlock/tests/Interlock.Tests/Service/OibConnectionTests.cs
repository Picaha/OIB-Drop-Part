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
