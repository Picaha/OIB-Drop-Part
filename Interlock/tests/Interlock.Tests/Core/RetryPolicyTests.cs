using System;
using Interlock.Core.Enforcement;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class RetryPolicyTests
    {
        [TestCase(1, 5)]
        [TestCase(2, 30)]
        [TestCase(3, 120)]
        public void Delay_after_failure(int failedAttempts, int seconds)
        {
            Assert.That(RetryPolicy.DelayAfterFailure(failedAttempts), Is.EqualTo(TimeSpan.FromSeconds(seconds)));
        }

        [TestCase(4)]
        [TestCase(5)]
        [TestCase(100)]
        public void No_retry_from_the_fourth_failure_on(int failedAttempts)
        {
            Assert.That(RetryPolicy.DelayAfterFailure(failedAttempts), Is.Null);
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void Fewer_than_one_failure_is_a_caller_bug(int failedAttempts)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.DelayAfterFailure(failedAttempts));
        }
    }
}
