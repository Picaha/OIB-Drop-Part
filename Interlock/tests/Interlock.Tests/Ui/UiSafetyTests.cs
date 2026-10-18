using System;
using System.Threading.Tasks;
using Interlock.Ui;
using NUnit.Framework;

namespace Interlock.Tests.Ui
{
    [TestFixture]
    public class UiSafetyTests
    {
        [Test]
        public async Task Success_returns_null()
        {
            Assert.That(await UiSafety.RunAsync(() => Task.FromResult(0)), Is.Null);
        }

        [Test]
        public async Task Unreachable_service_shows_service_unreachable()
        {
            var text = await UiSafety.RunAsync(() => { throw new ApiException(0, null, ApiClient.Unreachable); });
            Assert.That(text, Is.EqualTo("Service unreachable"));
        }

        [Test]
        public async Task Api_error_shows_its_message()
        {
            var text = await UiSafety.RunAsync(() => { throw new ApiException(403, "forbidden", "Release needs QA"); });
            Assert.That(text, Is.EqualTo("Release needs QA"));
        }

        [TestCase(typeof(InvalidOperationException))]
        [TestCase(typeof(ObjectDisposedException))]
        [TestCase(typeof(NullReferenceException))]
        public async Task Any_other_exception_becomes_a_neutral_text_without_throwing(Type type)
        {
            var ex = type == typeof(ObjectDisposedException) ? new ObjectDisposedException("secret-object") : (Exception)Activator.CreateInstance(type);
            var text = await UiSafety.RunAsync(async () => { await Task.Yield(); throw ex; });
            Assert.That(text, Is.EqualTo("Unexpected error - see log"));
        }

        [Test]
        public void Run_swallows_exceptions_and_reports_false()
        {
            Assert.That(UiSafety.Run(() => { throw new InvalidOperationException("dialog broke"); }), Is.False);
            Assert.That(UiSafety.Run(() => { }), Is.True);
        }

        [Test]
        public void Notice_throttle_allows_one_per_interval()
        {
            var now = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
            var t = new NoticeThrottle(TimeSpan.FromSeconds(30), () => now);
            Assert.That(t.TryAcquire(), Is.True);
            now = now.AddSeconds(10);
            Assert.That(t.TryAcquire(), Is.False);
            now = now.AddSeconds(25);
            Assert.That(t.TryAcquire(), Is.True);
        }
    }
}
