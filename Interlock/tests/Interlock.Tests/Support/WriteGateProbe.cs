using System;
using System.Threading;
using System.Threading.Tasks;
using Interlock.Core.Locks;
using NUnit.Framework;

namespace Interlock.Tests.Support
{
    /// <summary>Checks that a call takes <see cref="LockCache.WriteGate"/> before it changes anything.</summary>
    public static class WriteGateProbe
    {
        /// <summary>
        /// Runs <paramref name="call"/> on another thread while the calling test holds the cache's write gate, and checks
        /// that it waits for the gate and has changed nothing (<paramref name="untouched"/>) until the gate is let go.
        /// </summary>
        public static void AssertWaits(LockCache cache, Action call, Func<bool> untouched)
        {
            var entered = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var holder = Task.Run(() =>
            {
                lock (cache.WriteGate)
                {
                    entered.Set();
                    release.Wait();
                }
            });

            try
            {
                Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "the holder never took the gate");

                var caller = Task.Run(call);
                Assert.That(caller.Wait(TimeSpan.FromMilliseconds(300)), Is.False, "the call did not wait for the write gate");
                Assert.That(untouched(), Is.True, "the call changed something before it had the write gate");

                release.Set();
                Assert.That(caller.Wait(TimeSpan.FromSeconds(5)), Is.True, "the call never finished after the gate was released");
            }
            finally
            {
                release.Set();
                holder.Wait();
            }
        }
    }
}
