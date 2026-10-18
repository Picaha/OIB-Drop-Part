using System;
using Interlock.Service.Oib;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Service
{
    [TestFixture]
    public class ReconnectBackoffTests
    {
        private FakeClock _clock;
        private ReconnectBackoff _backoff;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeClock();
            _backoff = new ReconnectBackoff(_clock);
        }

        [Test]
        public void A_new_backoff_allows_a_try()
        {
            Assert.That(_backoff.CanTry, Is.True);
        }

        [TestCase(1, 5)]
        [TestCase(2, 10)]
        [TestCase(3, 20)]
        [TestCase(4, 40)]
        [TestCase(5, 80)]
        [TestCase(6, 160)]
        [TestCase(7, 300)]
        [TestCase(8, 300)]
        [TestCase(50, 300)]
        [TestCase(5000, 300)]
        public void The_wait_after_the_nth_failure_doubles_up_to_five_minutes(int failures, int waitSeconds)
        {
            for (int i = 0; i < failures; i++)
                _backoff.Failed();

            Assert.That(_backoff.CanTry, Is.False, "right after the failure");

            _clock.Advance(TimeSpan.FromSeconds(waitSeconds) - TimeSpan.FromMilliseconds(1));
            Assert.That(_backoff.CanTry, Is.False, "just before the wait is over");

            _clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.That(_backoff.CanTry, Is.True, "when the wait is over");
        }

        [Test]
        public void The_wait_counts_from_the_latest_failure()
        {
            _backoff.Failed();
            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.That(_backoff.CanTry, Is.True);

            _backoff.Failed();

            Assert.That(_backoff.CanTry, Is.False);
            _clock.Advance(TimeSpan.FromSeconds(9));
            Assert.That(_backoff.CanTry, Is.False);
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(_backoff.CanTry, Is.True);
        }

        [Test]
        public void Success_starts_the_schedule_over()
        {
            _backoff.Failed();
            _backoff.Failed();
            _backoff.Failed();

            _backoff.Succeeded();

            Assert.That(_backoff.CanTry, Is.True, "a success allows a try at once");
            _backoff.Failed();
            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.That(_backoff.CanTry, Is.True, "the first failure after a success waits 5 s again");
        }

        [Test]
        public void Without_a_clock_there_is_no_backoff()
        {
            Assert.Throws<ArgumentNullException>(() => new ReconnectBackoff(null));
        }
    }
}
