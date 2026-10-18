using System;
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

        private void Fail(string user, string address, int times)
        {
            for (var i = 0; i < times; i++)
                _throttle.RecordFailure(user, address);
        }

        [Test]
        public void Four_failures_do_not_block_the_fifth_does()
        {
            Fail("qa1", null, 4);
            Assert.That(_throttle.IsBlocked("qa1", null), Is.False);

            Fail("qa1", null, 1);
            Assert.That(_throttle.IsBlocked("qa1", null), Is.True);
        }

        [Test]
        public void Username_is_matched_ignoring_case_and_other_users_are_not_affected()
        {
            Fail("QA1", null, 5);

            Assert.That(_throttle.IsBlocked("qa1", null), Is.True);
            Assert.That(_throttle.IsBlocked("  Qa1 ", null), Is.True);
            Assert.That(_throttle.IsBlocked("op1", null), Is.False);
        }

        [Test]
        public void Remote_address_is_throttled_across_usernames()
        {
            _throttle.RecordFailure("a", "10.0.0.5");
            _throttle.RecordFailure("b", "10.0.0.5");
            _throttle.RecordFailure("c", "10.0.0.5");
            _throttle.RecordFailure("d", "10.0.0.5");
            _throttle.RecordFailure("e", "10.0.0.5");

            Assert.That(_throttle.IsBlocked("fresh-user", "10.0.0.5"), Is.True);
            Assert.That(_throttle.IsBlocked("fresh-user", "10.0.0.6"), Is.False);
        }

        [Test]
        public void The_block_ends_60_seconds_after_the_last_failure()
        {
            Fail("qa1", null, 5);

            _clock.Advance(TimeSpan.FromSeconds(59));
            Assert.That(_throttle.IsBlocked("qa1", null), Is.True);

            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(_throttle.IsBlocked("qa1", null), Is.False);
        }

        [Test]
        public void Failures_older_than_the_window_do_not_count()
        {
            Fail("qa1", null, 4);
            _clock.Advance(TimeSpan.FromSeconds(61));
            Fail("qa1", null, 1);

            Assert.That(_throttle.IsBlocked("qa1", null), Is.False);
        }

        [Test]
        public void A_success_clears_the_username_counter()
        {
            Fail("qa1", null, 4);
            _throttle.RecordSuccess("qa1");
            Fail("qa1", null, 4);

            Assert.That(_throttle.IsBlocked("qa1", null), Is.False);
        }

        [Test]
        public void A_blank_username_and_no_address_are_never_blocked()
        {
            Fail(null, null, 20);
            Fail("  ", null, 20);

            Assert.That(_throttle.IsBlocked(null, null), Is.False);
            Assert.That(_throttle.IsBlocked("", null), Is.False);
        }

        [Test]
        public void Many_one_off_failures_are_cleaned_up_lazily_and_nothing_stays_blocked()
        {
            for (var i = 0; i < 1000; i++)
                _throttle.RecordFailure("user" + i, "addr" + i);
            _clock.Advance(TimeSpan.FromMinutes(5));

            _throttle.RecordFailure("late", null);

            Assert.That(_throttle.IsBlocked("user1", "addr1"), Is.False);
        }
    }
}
