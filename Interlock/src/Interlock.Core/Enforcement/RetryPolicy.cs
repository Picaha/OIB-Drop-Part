using System;

namespace Interlock.Core.Enforcement
{
    public static class RetryPolicy
    {
        /// <summary>The wait before the next try after this many failed calls (1 or more); null once the retries are used up.</summary>
        public static TimeSpan? DelayAfterFailure(int failedAttempts)
        {
            if (failedAttempts < 1)
                throw new ArgumentOutOfRangeException(nameof(failedAttempts), failedAttempts, "At least one failed attempt is needed.");

            switch (failedAttempts)
            {
                case 1: return TimeSpan.FromSeconds(5);
                case 2: return TimeSpan.FromSeconds(30);
                case 3: return TimeSpan.FromMinutes(2);
                default: return null;
            }
        }
    }
}
