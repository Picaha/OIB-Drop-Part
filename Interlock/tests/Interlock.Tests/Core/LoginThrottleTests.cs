using System;
using System.Linq;
using System.Threading.Tasks;
using Interlock.Core.Security;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class LoginThrottleTests
    {
        private FakeClock _clock;
        private LoginThrottle _throttle;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeClock();
            _throttle = new LoginThrottle(_clock);
        }

        /// <summary>Full failed logins: reserve, then record the failure.</summary>
        private void Fail(string user, string address, int times)
        {
            for (var i = 0; i < times; i++)
            {
                Assert.That(_throttle.TryBegin(user, address), Is.True, "attempt " + (i + 1) + " should have been admitted");
                _throttle.RecordFailure(user, address);
            }
        }

        private bool Blocked(string user, string address)
        {
            if (!_throttle.TryBegin(user, address))
                return true;
            _throttle.Abandon(user, address);
            return false;
        }

        [Test]
        public void Four_failures_do_not_block_the_fifth_does()
        {
            Fail("qa1", null, 4);
            Assert.That(Blocked("qa1", null), Is.False);

            Fail("qa1", null, 1);
            Assert.That(Blocked("qa1", null), Is.True);
        }

        [Test]
        public void Attempts_in_flight_count_toward_the_limit()
        {
            var admitted = Enumerable.Range(0, 10).Count(i => _throttle.TryBegin("qa1", null));

            Assert.That(admitted, Is.EqualTo(5));
        }

        [Test]
        public void Parallel_attempts_admit_at_most_five()
        {
            var admitted = 0;
            Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 32 }, i =>
            {
                if (_throttle.TryBegin("qa1", "10.0.0.9"))
                    System.Threading.Interlocked.Increment(ref admitted);
            });

            Assert.That(admitted, Is.EqualTo(5));
        }

        [Test]
        public void Finishing_an_attempt_frees_its_slot()
        {
            for (var i = 0; i < 5; i++)
                Assert.That(_throttle.TryBegin("qa1", null), Is.True);
            Assert.That(_throttle.TryBegin("qa1", null), Is.False);

            _throttle.Abandon("qa1", null);
            Assert.That(_throttle.TryBegin("qa1", null), Is.True);

            _throttle.RecordSuccess("qa1", null);
            Assert.That(_throttle.TryBegin("qa1", null), Is.True);
        }

        [Test]
        public void A_refused_attempt_reserves_nothing()
        {
            Fail("qa1", null, 5);
            Assert.That(_throttle.TryBegin("qa1", null), Is.False);

            _clock.Advance(TimeSpan.FromSeconds(60));

            Assert.That(_throttle.TryBegin("qa1", null), Is.True);
        }

        [Test]
        public void Username_is_matched_ignoring_case_and_other_users_are_not_affected()
        {
            Fail("QA1", null, 5);

            Assert.That(Blocked("qa1", null), Is.True);
            Assert.That(Blocked("  Qa1 ", null), Is.True);
            Assert.That(Blocked("op1", null), Is.False);
        }

        [Test]
        public void Remote_address_is_throttled_across_usernames()
        {
            foreach (var user in new[] { "a", "b", "c", "d", "e" })
                Fail(user, "10.0.0.5", 1);

            Assert.That(Blocked("fresh-user", "10.0.0.5"), Is.True);
            Assert.That(Blocked("fresh-user", "10.0.0.6"), Is.False);
        }

        [Test]
        public void The_block_ends_60_seconds_after_the_failure_that_reached_the_limit()
        {
            Fail("qa1", null, 5);

            _clock.Advance(TimeSpan.FromSeconds(59));
            Assert.That(Blocked("qa1", null), Is.True);

            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(Blocked("qa1", null), Is.False);
        }

        [Test]
        public void With_spaced_failures_the_block_lasts_until_60_seconds_after_the_last_one()
        {
            // Failures at 0, 10, 20, 30 and 40 s. The oldest expires at 60 s, but the block must last until 100 s.
            for (var i = 0; i < 5; i++)
            {
                Fail("qa1", null, 1);
                if (i < 4)
                    _clock.Advance(TimeSpan.FromSeconds(10));
            }

            _clock.Advance(TimeSpan.FromSeconds(59));
            Assert.That(Blocked("qa1", null), Is.True, "at 99 s");

            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(Blocked("qa1", null), Is.False, "at 100 s");
        }

        [Test]
        public void Failures_older_than_the_window_do_not_count()
        {
            Fail("qa1", null, 4);
            _clock.Advance(TimeSpan.FromSeconds(61));
            Fail("qa1", null, 1);

            Assert.That(Blocked("qa1", null), Is.False);
        }

        [Test]
        public void A_success_clears_the_username_counter()
        {
            Fail("qa1", null, 4);
            Assert.That(_throttle.TryBegin("qa1", null), Is.True);
            _throttle.RecordSuccess("qa1", null);
            Fail("qa1", null, 4);

            Assert.That(Blocked("qa1", null), Is.False);
        }

        [Test]
        public void A_blank_username_and_no_address_are_never_blocked()
        {
            for (var i = 0; i < 20; i++)
            {
                Assert.That(_throttle.TryBegin(null, null), Is.True);
                _throttle.RecordFailure(null, null);
                Assert.That(_throttle.TryBegin("  ", null), Is.True);
                _throttle.RecordFailure("  ", null);
            }
        }

        [Test]
        public void Many_one_off_failures_are_cleaned_up_lazily_and_nothing_stays_blocked()
        {
            for (var i = 0; i < 1000; i++)
                Fail("user" + i, "addr" + i, 1);
            _clock.Advance(TimeSpan.FromMinutes(5));

            Fail("late", null, 1);

            Assert.That(Blocked("user1", "addr1"), Is.False);
        }
    }
}
