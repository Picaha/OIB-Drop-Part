using System;
using Interlock.Core.Api;
using Interlock.Ui;
using NUnit.Framework;

namespace Interlock.Tests.Ui
{
    [TestFixture]
    public class MainLogicTests
    {
        private static Session S(string role) { return Session.FromLogin("u", new SessionDto { role = role }); }
        private static LockDto L(string status, string enforce) { return new LockDto { id = 1, status = status, enforceState = enforce }; }

        [Test]
        public void Refresh_gate_never_overlaps()
        {
            var g = new RefreshGate();
            Assert.That(g.TryEnter(), Is.True);
            Assert.That(g.TryEnter(), Is.False, "second tick while running is skipped");
            g.Exit();
            Assert.That(g.TryEnter(), Is.True);
        }

        [Test]
        public void Refresh_gate_exit_without_enter_is_harmless()
        {
            var g = new RefreshGate();
            g.Exit();
            Assert.That(g.TryEnter(), Is.True);
        }

        [Test]
        public void Release_enabled_only_for_releasers_with_an_active_selection()
        {
            Assert.That(MainRules.CanRelease(S("QA"), L("ACTIVE", "APPLIED")), Is.True);
            Assert.That(MainRules.CanRelease(S("ADMIN"), L("ACTIVE", "FAILED")), Is.True);
            Assert.That(MainRules.CanRelease(S("OPERATOR"), L("ACTIVE", "APPLIED")), Is.False);
            Assert.That(MainRules.CanRelease(S("QA"), L("RELEASED", "APPLIED")), Is.False);
            Assert.That(MainRules.CanRelease(S("QA"), null), Is.False);
            Assert.That(MainRules.CanRelease(null, L("ACTIVE", "APPLIED")), Is.False);
        }

        [Test]
        public void Retry_only_for_failed_rows()
        {
            Assert.That(MainRules.CanRetry(L("ACTIVE", "FAILED")), Is.True);
            Assert.That(MainRules.CanRetry(L("ACTIVE", "PENDING")), Is.False);
            Assert.That(MainRules.CanRetry(null), Is.False);
        }

        [Test]
        public void Filter_all_maps_to_null()
        {
            Assert.That(MainRules.FilterValue("All"), Is.Null);
            Assert.That(MainRules.FilterValue(null), Is.Null);
            Assert.That(MainRules.FilterValue(" "), Is.Null);
            Assert.That(MainRules.FilterValue("PU"), Is.EqualTo("PU"));
        }

        [Test]
        public void Dates_show_local_time_and_fall_back_to_raw_text()
        {
            var utc = new DateTime(2026, 10, 19, 1, 2, 3, DateTimeKind.Utc);
            var shown = MainRules.LocalTime(utc.ToString("o"));
            Assert.That(shown, Is.EqualTo(utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That(MainRules.LocalTime("not a date"), Is.EqualTo("not a date"));
            Assert.That(MainRules.LocalTime(null), Is.EqualTo(""));
        }

        [Test]
        public void Health_summary_comes_only_from_dto_fields()
        {
            var h = new HealthDto { setupCenterConnected = true, setupCenterLicense = true, lineControlLicense = null, dbOk = false, failedLocks = 2, alarms = new[] { "T1 newest", "T0 older" } };
            var s = HealthSummary.From(h);
            Assert.That(s.SetupCenter, Is.EqualTo(Dot.Ok));
            Assert.That(s.LineControl, Is.EqualTo(Dot.Unknown));
            Assert.That(s.Db, Is.EqualTo(Dot.Bad));
            Assert.That(s.Failed, Is.EqualTo(2));
            Assert.That(s.LatestAlarm, Is.EqualTo("T1 newest"));
        }

        [Test]
        public void Health_summary_dot_rules()
        {
            Assert.That(HealthSummary.From(new HealthDto { setupCenterConnected = false, setupCenterLicense = true }).SetupCenter, Is.EqualTo(Dot.Bad));
            Assert.That(HealthSummary.From(new HealthDto { setupCenterConnected = true, setupCenterLicense = false }).SetupCenter, Is.EqualTo(Dot.Bad));
            Assert.That(HealthSummary.From(new HealthDto { setupCenterConnected = true, setupCenterLicense = null }).SetupCenter, Is.EqualTo(Dot.Unknown));
            Assert.That(HealthSummary.From(new HealthDto { lineControlLicense = false }).LineControl, Is.EqualTo(Dot.Bad));
            Assert.That(HealthSummary.From(new HealthDto { lineControlLicense = true }).LineControl, Is.EqualTo(Dot.Ok));
            Assert.That(HealthSummary.From(new HealthDto()).LatestAlarm, Is.EqualTo(""));
        }

        [Test]
        public void Health_summary_db_dot_includes_cache_refresh_problem()
        {
            Assert.That(HealthSummary.From(new HealthDto { dbOk = true }).Db, Is.EqualTo(Dot.Ok));
            Assert.That(HealthSummary.From(new HealthDto { dbOk = true, cacheRefreshFailures = 3, cacheRefreshError = "timeout" }).Db, Is.EqualTo(Dot.Bad));
            Assert.That(HealthSummary.From(null).Db, Is.EqualTo(Dot.Unknown));
        }
    }
}
