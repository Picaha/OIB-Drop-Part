using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Text;
using Interlock.Service.Data;
using Npgsql;
using NUnit.Framework;

namespace Interlock.Tests.Integration
{
    /// <summary>
    /// The Npgsql repositories against a real PostgreSQL database. Set <c>INTERLOCK_TEST_DB</c> to the connection string of a
    /// NON-PRODUCTION database; without it every test is ignored. The fixture drops and recreates schema
    /// <c>interlock_test</c> from <c>db/001_interlock_schema.sql</c>, and empties every table before each test.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class RepositoryTests
    {
        private const string Schema = "interlock_test";
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 8, 0, 0, 123, DateTimeKind.Utc);
        private static readonly DateTime T = T0.AddMinutes(30);
        private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

        private NpgsqlDb _db;
        private NpgsqlLockRepository _repo;
        private NpgsqlAuditRepository _audit;
        private NpgsqlUserRepository _users;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var connectionString = Environment.GetEnvironmentVariable("INTERLOCK_TEST_DB");
            if (string.IsNullOrWhiteSpace(connectionString))
                Assert.Ignore("INTERLOCK_TEST_DB is not set; the integration tests need a non-production PostgreSQL database.");

            _db = new NpgsqlDb(connectionString, Schema);
            Execute("DROP SCHEMA IF EXISTS " + Schema + " CASCADE");
            RunSchemaScript();
            _repo = new NpgsqlLockRepository(_db);
            _audit = new NpgsqlAuditRepository(_db);
            _users = new NpgsqlUserRepository(_db);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (_db != null)
                Execute("DROP SCHEMA IF EXISTS " + Schema + " CASCADE");
        }

        [SetUp]
        public void SetUp()
        {
            Execute("TRUNCATE " + string.Join(", ", new[] { "lock_applied_pu", "action_log", "decision_log", "app_user", "api_key", "lock" }
                .Select(t => Schema + "." + t)) + " RESTART IDENTITY");
        }

        // ---- helpers ----

        private void RunSchemaScript()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "db", "001_interlock_schema.sql");
            var script = File.ReadAllText(path)
                .Replace("CREATE SCHEMA IF NOT EXISTS interlock;", "CREATE SCHEMA IF NOT EXISTS " + Schema + ";")
                .Replace("interlock.", Schema + ".");
            Execute(script);
        }

        private void Execute(string sql)
        {
            using (var connection = _db.Open())
            using (var command = new NpgsqlCommand(sql, connection))
                command.ExecuteNonQuery();
        }

        private object Scalar(string sql)
        {
            using (var connection = _db.Open())
            using (var command = new NpgsqlCommand(sql.Replace("{schema}", Schema), connection))
                return command.ExecuteScalar();
        }

        private static LockRecord L(LockType type, string target, string line = null, Lanes? lanes = null, string reason = "reason")
        {
            return new LockRecord
            {
                Type = type,
                Target = target,
                TargetNorm = Normalizer.Norm(target),
                LinePath = line,
                Lanes = lanes,
                Reason = reason,
                Source = LockSource.Manual,
                CreatedBy = "tester",
                CreatedAtUtc = T0,
                Status = LockStatus.Active,
                EnforceState = EnforceState.Pending
            };
        }

        private static LockRecord Pu(long id, string uid)
        {
            var l = L(LockType.Pu, uid);
            l.Id = id;
            return l;
        }

        private static LockRecord Pn(long id, string pn, string line = null)
        {
            var l = L(LockType.Pn, pn, line);
            l.Id = id;
            return l;
        }

        private static void AssertSameLock(LockRecord actual, LockRecord expected)
        {
            Assert.That(actual.Id, Is.EqualTo(expected.Id), "Id");
            Assert.That(actual.Type, Is.EqualTo(expected.Type), "Type");
            Assert.That(actual.Target, Is.EqualTo(expected.Target), "Target");
            Assert.That(actual.TargetNorm, Is.EqualTo(expected.TargetNorm), "TargetNorm");
            Assert.That(actual.Lanes, Is.EqualTo(expected.Lanes), "Lanes");
            Assert.That(actual.LinePath, Is.EqualTo(expected.LinePath), "LinePath");
            Assert.That(actual.Reason, Is.EqualTo(expected.Reason), "Reason");
            Assert.That(actual.Source, Is.EqualTo(expected.Source), "Source");
            Assert.That(actual.CreatedBy, Is.EqualTo(expected.CreatedBy), "CreatedBy");
            Assert.That(actual.CreatedAtUtc, Is.EqualTo(expected.CreatedAtUtc), "CreatedAtUtc");
            Assert.That(actual.CreatedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc), "CreatedAtUtc.Kind");
            Assert.That(actual.Status, Is.EqualTo(expected.Status), "Status");
            Assert.That(actual.ReleasedBy, Is.EqualTo(expected.ReleasedBy), "ReleasedBy");
            Assert.That(actual.ReleasedAtUtc, Is.EqualTo(expected.ReleasedAtUtc), "ReleasedAtUtc");
            Assert.That(actual.ReleaseReason, Is.EqualTo(expected.ReleaseReason), "ReleaseReason");
            Assert.That(actual.EnforceState, Is.EqualTo(expected.EnforceState), "EnforceState");
            Assert.That(actual.EnforceError, Is.EqualTo(expected.EnforceError), "EnforceError");
        }

        // ---- schema ----

        [Test]
        public void Schema_script_can_run_again_and_keeps_the_data()
        {
            _repo.Insert(Pu(0, "U1"));

            Assert.DoesNotThrow(RunSchemaScript);
            Assert.That(_repo.LoadActive().Single().Target, Is.EqualTo("U1"));
        }

        [Test]
        public void Schema_rejects_enum_text_outside_the_allowed_values()
        {
            const string insertLock = "INSERT INTO {schema}.lock (lock_type, target, target_norm, lanes, reason, source, created_by, created_at, status, enforce_state) VALUES ";

            var cases = new Dictionary<string, string>
            {
                { "lock_type", insertLock + "('BOGUS', 'X', 'X', NULL, 'r', 'MANUAL', 't', now(), 'ACTIVE', 'PENDING')" },
                { "lanes", insertLock + "('CONVEYOR', 'X', 'X', 'UP', 'r', 'MANUAL', 't', now(), 'ACTIVE', 'PENDING')" },
                { "source", insertLock + "('PU', 'X', 'X', NULL, 'r', 'manual', 't', now(), 'ACTIVE', 'PENDING')" },
                { "status", insertLock + "('PU', 'X', 'X', NULL, 'r', 'MANUAL', 't', now(), 'active', 'PENDING')" },
                { "enforce_state", insertLock + "('PU', 'X', 'X', NULL, 'r', 'MANUAL', 't', now(), 'ACTIVE', 'DONE')" },
                { "role", "INSERT INTO {schema}.app_user (username, pin_hash, role, active, created_at) VALUES ('u', 'h', 'ROOT', true, now())" }
            };

            foreach (var c in cases)
            {
                var ex = Assert.Throws<PostgresException>(() => Scalar(c.Value), c.Key);
                Assert.That(ex.SqlState, Is.EqualTo("23514"), c.Key);
            }

            Assert.DoesNotThrow(() => Scalar(insertLock + "('CONVEYOR', 'X', 'X', 'BOTH', 'r', 'SETUPCENTER', 't', now(), 'RELEASED', 'NOT_NEEDED')"));
        }

        [Test]
        public void Timestamps_are_stored_as_utc_instants()
        {
            _repo.Insert(Pu(0, "U1"));

            var stored = Scalar("SELECT to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.MS') FROM {schema}.lock");

            Assert.That(stored, Is.EqualTo("2026-10-18 08:00:00.123"));
        }

        // ---- lock: insert, identity, reads ----

        [Test]
        public void Insert_and_load_active_round_trip()
        {
            var l = _repo.Insert(Pu(0, "U1"));
            Assert.That(l.Id, Is.GreaterThan(0));

            var back = _repo.LoadActive().Single();

            Assert.That(back.TargetNorm, Is.EqualTo("U1"));
            Assert.That(back.CreatedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
            AssertSameLock(back, l);
        }

        [Test]
        public void Insert_returns_the_stored_row_with_every_field_and_leaves_the_input_alone()
        {
            var input = new LockRecord
            {
                Type = LockType.Conveyor,
                Target = "Lines\\L1\\S1",
                TargetNorm = "LINES\\L1\\S1",
                Lanes = Lanes.Left,
                Reason = "Jam",
                Source = LockSource.Api,
                CreatedBy = "mes",
                CreatedAtUtc = T0,
                Status = LockStatus.Active,
                EnforceState = EnforceState.Failed,
                EnforceError = "boom"
            };

            var stored = _repo.Insert(input);

            Assert.That(input.Id, Is.EqualTo(0), "the caller's object is not changed");
            Assert.That(stored, Is.Not.SameAs(input));
            var expected = input.Clone();
            expected.Id = stored.Id;
            AssertSameLock(stored, expected);
            AssertSameLock(_repo.Get(stored.Id), expected);
        }

        [Test]
        public void Insert_computes_target_norm_from_the_target()
        {
            var input = Pu(0, " u1\r\n");
            input.TargetNorm = null;

            var stored = _repo.Insert(input);

            Assert.That(stored.Target, Is.EqualTo(" u1\r\n"), "the target is stored as given");
            Assert.That(stored.TargetNorm, Is.EqualTo("U1"));
            Assert.That(_repo.FindActive(LockType.Pu, "u1", null, null).Id, Is.EqualTo(stored.Id));
        }

        [Test]
        public void Insert_keeps_released_fields_as_given()
        {
            var input = Pu(0, "U1");
            input.Status = LockStatus.Released;
            input.ReleasedBy = "qa1";
            input.ReleasedAtUtc = T;
            input.ReleaseReason = "done";

            var stored = _repo.Insert(input);

            var expected = input.Clone();
            expected.Id = stored.Id;
            AssertSameLock(_repo.Get(stored.Id), expected);
            Assert.That(_repo.Get(stored.Id).ReleasedAtUtc.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
        }

        [Test]
        public void Duplicate_active_throws_duplicate_exception()
        {
            _repo.Insert(Pu(0, "U1"));

            var ex = Assert.Throws<DuplicateActiveLockException>(() => _repo.Insert(Pu(0, "U1")));

            Assert.That(ex.InnerException, Is.TypeOf<PostgresException>());
            Assert.That(((PostgresException)ex.InnerException).SqlState, Is.EqualTo("23505"));
            Assert.That(_repo.LoadActive().Count, Is.EqualTo(1));
        }

        [Test]
        public void Duplicate_identity_is_found_after_normalising_the_target()
        {
            _repo.Insert(Pu(0, "U1"));

            Assert.Throws<DuplicateActiveLockException>(() => _repo.Insert(Pu(0, " u1\r\n")));
        }

        [Test]
        public void Identity_is_type_target_line_and_lanes()
        {
            _repo.Insert(L(LockType.Pn, "X", line: "Lines\\L1"));

            Assert.DoesNotThrow(() => _repo.Insert(L(LockType.Mpn, "X", line: "Lines\\L1")), "other type");
            Assert.DoesNotThrow(() => _repo.Insert(L(LockType.Pn, "Y", line: "Lines\\L1")), "other target");
            Assert.DoesNotThrow(() => _repo.Insert(L(LockType.Pn, "X", line: "Lines\\L2")), "other line");
            Assert.DoesNotThrow(() => _repo.Insert(L(LockType.Pn, "X")), "plant-wide is another scope");
            Assert.DoesNotThrow(() => _repo.Insert(L(LockType.Conveyor, "S1", lanes: Lanes.Left)));
            Assert.DoesNotThrow(() => _repo.Insert(L(LockType.Conveyor, "S1", lanes: Lanes.Right)), "other lanes");

            Assert.Throws<DuplicateActiveLockException>(() => _repo.Insert(L(LockType.Conveyor, "S1", lanes: Lanes.Left)));
            Assert.Throws<DuplicateActiveLockException>(() => _repo.Insert(L(LockType.Pn, "X")), "null line equals null line");
            Assert.Throws<DuplicateActiveLockException>(() => _repo.Insert(L(LockType.Pn, "X", line: "Lines\\L1")));
        }

        [Test]
        public void Released_allows_new_active()
        {
            var a = _repo.Insert(Pu(0, "U1"));
            _repo.MarkReleased(a.Id, "qa1", T, "ok");

            Assert.DoesNotThrow(() => _repo.Insert(Pu(0, "U1")));
            Assert.That(_repo.GetLatestReleaseUtc(LockType.Pu, "U1"), Is.EqualTo(T).Within(TimeSpan.FromMilliseconds(1)));
        }

        [Test]
        public void FindActive_normalises_its_arguments_and_ignores_released_rows()
        {
            var active = _repo.Insert(L(LockType.Pn, "C1", line: "Lines\\L1"));
            var released = _repo.Insert(Pu(0, "GONE"));
            _repo.MarkReleased(released.Id, "qa1", T, "ok");

            AssertSameLock(_repo.FindActive(LockType.Pn, " c1\r\n", " LINES\\l1\r\n", null), active);
            Assert.That(_repo.FindActive(LockType.Pn, "C1", "Lines\\L2", null), Is.Null);
            Assert.That(_repo.FindActive(LockType.Pn, "C1", null, null), Is.Null);
            Assert.That(_repo.FindActive(LockType.Mpn, "C1", "Lines\\L1", null), Is.Null);
            Assert.That(_repo.FindActive(LockType.Pn, "C1", "Lines\\L1", Lanes.Both), Is.Null);
            Assert.That(_repo.FindActive(LockType.Pu, "GONE", null, null), Is.Null);
            Assert.That(_repo.FindActive(LockType.Pu, " ", null, null), Is.Null);
            Assert.That(_repo.FindActive(LockType.Pu, null, null, null), Is.Null);
        }

        [Test]
        public void FindActive_treats_a_blank_line_as_plant_wide_and_compares_lanes()
        {
            var plantWide = _repo.Insert(L(LockType.Pn, "C1"));
            var left = _repo.Insert(L(LockType.Conveyor, "Lines\\L1\\S1", lanes: Lanes.Left));

            Assert.That(_repo.FindActive(LockType.Pn, "C1", "  ", null).Id, Is.EqualTo(plantWide.Id));
            Assert.That(_repo.FindActive(LockType.Pn, "C1", null, null).Id, Is.EqualTo(plantWide.Id));
            Assert.That(_repo.FindActive(LockType.Conveyor, "lines\\l1\\s1", null, Lanes.Left).Id, Is.EqualTo(left.Id));
            Assert.That(_repo.FindActive(LockType.Conveyor, "Lines\\L1\\S1", null, Lanes.Right), Is.Null);
            Assert.That(_repo.FindActive(LockType.Conveyor, "Lines\\L1\\S1", null, null), Is.Null);
        }

        [Test]
        public void Get_returns_the_row_or_null()
        {
            var stored = _repo.Insert(Pn(0, "C1", "Lines\\L1"));

            AssertSameLock(_repo.Get(stored.Id), stored);
            Assert.That(_repo.Get(stored.Id + 100), Is.Null);
        }

        [Test]
        public void LoadActive_lists_only_active_rows_ordered_by_id()
        {
            var a = _repo.Insert(Pu(0, "A")).Id;
            var b = _repo.Insert(Pu(0, "B")).Id;
            var c = _repo.Insert(Pu(0, "C")).Id;
            _repo.MarkReleased(b, "qa1", T, "ok");

            Assert.That(_repo.LoadActive().Select(l => l.Id), Is.EqualTo(new[] { a, c }));
        }

        // ---- lock: release and enforce state ----

        [Test]
        public void Mark_released_sets_pending_and_load_pending_releases()
        {
            var a = _repo.Insert(Pu(0, "U1"));
            _repo.SetEnforceState(a.Id, EnforceState.Applied, null);
            _repo.MarkReleased(a.Id, "qa1", T, "ok");

            Assert.That(_repo.LoadPendingReleases().Single().Id, Is.EqualTo(a.Id));
            _repo.SetEnforceState(a.Id, EnforceState.Applied, null);
            Assert.That(_repo.LoadPendingReleases(), Is.Empty);
        }

        [Test]
        public void MarkReleased_stores_who_when_and_why()
        {
            var id = _repo.Insert(Pu(0, "U1")).Id;

            _repo.MarkReleased(id, "qa1", T, "fixed");

            var row = _repo.Get(id);
            Assert.That(row.Status, Is.EqualTo(LockStatus.Released));
            Assert.That(row.ReleasedBy, Is.EqualTo("qa1"));
            Assert.That(row.ReleasedAtUtc, Is.EqualTo(T));
            Assert.That(row.ReleasedAtUtc.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(row.ReleaseReason, Is.EqualTo("fixed"));
            Assert.That(row.EnforceState, Is.EqualTo(EnforceState.Pending));
            Assert.That(_repo.LoadActive(), Is.Empty);
        }

        [Test]
        public void MarkReleased_leaves_a_row_that_is_not_active_untouched()
        {
            var id = _repo.Insert(Pu(0, "U1")).Id;
            _repo.MarkReleased(id, "qa1", T, "fixed");
            _repo.SetEnforceState(id, EnforceState.Applied, null);

            _repo.MarkReleased(id, "qa2", T.AddMinutes(9), "again");

            var row = _repo.Get(id);
            Assert.That(row.ReleasedBy, Is.EqualTo("qa1"));
            Assert.That(row.ReleasedAtUtc, Is.EqualTo(T));
            Assert.That(row.ReleaseReason, Is.EqualTo("fixed"));
            Assert.That(row.EnforceState, Is.EqualTo(EnforceState.Applied), "a second release must not set the lock pending again");
            Assert.That(_repo.LoadPendingReleases(), Is.Empty);
        }

        [Test]
        public void LoadPendingReleases_lists_released_pending_rows_ordered_by_id()
        {
            var a = _repo.Insert(Pu(0, "A")).Id;
            var b = _repo.Insert(Pu(0, "B")).Id;
            var c = _repo.Insert(Pu(0, "C")).Id;
            _repo.Insert(Pu(0, "D"));
            _repo.MarkReleased(c, "qa1", T, "ok");
            _repo.MarkReleased(a, "qa1", T, "ok");
            _repo.MarkReleased(b, "qa1", T, "ok");
            _repo.SetEnforceState(b, EnforceState.Failed, "x");

            Assert.That(_repo.LoadPendingReleases().Select(l => l.Id), Is.EqualTo(new[] { a, c }));
        }

        [Test]
        public void SetEnforceState_sets_and_clears_the_error()
        {
            var id = _repo.Insert(Pu(0, "U1")).Id;

            _repo.SetEnforceState(id, EnforceState.Failed, "boom");
            Assert.That(_repo.Get(id).EnforceState, Is.EqualTo(EnforceState.Failed));
            Assert.That(_repo.Get(id).EnforceError, Is.EqualTo("boom"));

            _repo.SetEnforceState(id, EnforceState.NotNeeded, null);
            Assert.That(_repo.Get(id).EnforceState, Is.EqualTo(EnforceState.NotNeeded));
            Assert.That(_repo.Get(id).EnforceError, Is.Null);
        }

        [Test]
        public void Unknown_ids_read_as_missing_and_updates_do_nothing()
        {
            Assert.That(_repo.Get(42), Is.Null);
            Assert.DoesNotThrow(() => _repo.MarkReleased(42, "qa1", T, "ok"));
            Assert.DoesNotThrow(() => _repo.SetEnforceState(42, EnforceState.Failed, "x"));
            Assert.That(_repo.Query(new LockQuery()), Is.Empty);
        }

        // ---- lock: applied PUs and coverage ----

        [Test]
        public void Applied_pu_coverage()
        {
            var pn = _repo.Insert(Pn(0, "C1"));
            _repo.AddAppliedPu(pn.Id, "a1", T);

            Assert.That(_repo.IsUidCoveredByOtherActiveLock("A1", exceptLockId: 999), Is.True);
            Assert.That(_repo.IsUidCoveredByOtherActiveLock("A1", pn.Id), Is.False);
            _repo.CloseAppliedPu(pn.Id, "a1", T);
            Assert.That(_repo.IsUidCoveredByOtherActiveLock("A1", 999), Is.False);
        }

        [Test]
        public void AddAppliedPu_is_idempotent_per_open_row()
        {
            var pn = _repo.Insert(Pn(0, "C1"));

            _repo.AddAppliedPu(pn.Id, "a1", T);
            _repo.AddAppliedPu(pn.Id, " A1\r\n", T.AddMinutes(1));

            Assert.That(_repo.GetOpenAppliedPus(pn.Id), Is.EqualTo(new[] { "a1" }), "the first row is kept as stored");

            _repo.CloseAppliedPu(pn.Id, "A1", T.AddMinutes(2));
            _repo.AddAppliedPu(pn.Id, "A1", T.AddMinutes(3));

            Assert.That(_repo.GetOpenAppliedPus(pn.Id), Is.EqualTo(new[] { "A1" }), "a closed row does not block a new open one");
            Assert.That(Scalar("SELECT count(*) FROM {schema}.lock_applied_pu"), Is.EqualTo(2L));
        }

        [Test]
        public void Applied_pus_are_listed_open_until_closed()
        {
            var one = _repo.Insert(Pn(0, "C1")).Id;
            var two = _repo.Insert(Pn(0, "C2")).Id;
            _repo.AddAppliedPu(one, "A", T);
            _repo.AddAppliedPu(one, "B", T);
            _repo.AddAppliedPu(two, "a", T);

            Assert.That(_repo.GetOpenAppliedPus(one), Is.EqualTo(new[] { "A", "B" }));

            _repo.CloseAppliedPu(one, " a\r\n", T.AddMinutes(1));

            Assert.That(_repo.GetOpenAppliedPus(one), Is.EqualTo(new[] { "B" }));
            Assert.That(_repo.GetOpenAppliedPus(two), Is.EqualTo(new[] { "a" }), "only that lock's rows are closed");
            Assert.That(_repo.GetOpenAppliedPus(two + 100), Is.Empty);
            Assert.That(Scalar("SELECT to_char(released_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.MS') FROM {schema}.lock_applied_pu WHERE uid = 'A'"),
                Is.EqualTo("2026-10-18 08:31:00.123"));
            Assert.DoesNotThrow(() => _repo.CloseAppliedPu(one, "  ", T), "a blank uid closes nothing");
            Assert.That(_repo.GetOpenAppliedPus(one), Is.EqualTo(new[] { "B" }));
        }

        [Test]
        public void Uid_is_covered_by_another_active_pu_lock_only()
        {
            var u1 = _repo.Insert(Pu(0, "U1")).Id;
            var gone = _repo.Insert(Pu(0, "U2")).Id;
            _repo.MarkReleased(gone, "qa1", T, "ok");
            _repo.Insert(Pn(0, "U3"));

            Assert.That(_repo.IsUidCoveredByOtherActiveLock(" u1\r\n", u1 + 100), Is.True);
            Assert.That(_repo.IsUidCoveredByOtherActiveLock("U1", u1), Is.False, "the lock itself does not count");
            Assert.That(_repo.IsUidCoveredByOtherActiveLock("U2", 999), Is.False, "a released PU lock does not cover");
            Assert.That(_repo.IsUidCoveredByOtherActiveLock("U3", 999), Is.False, "a lock of another type does not cover");
            Assert.That(_repo.IsUidCoveredByOtherActiveLock("  ", 999), Is.False);
            Assert.That(_repo.IsUidCoveredByOtherActiveLock(null, 999), Is.False);
        }

        [Test]
        public void An_applied_row_of_a_released_lock_does_not_cover()
        {
            var pn = _repo.Insert(Pn(0, "C1"));
            _repo.AddAppliedPu(pn.Id, "A1", T);
            _repo.MarkReleased(pn.Id, "qa1", T, "ok");

            Assert.That(_repo.IsUidCoveredByOtherActiveLock("A1", 999), Is.False);
        }

        // ---- lock: release history ----

        [Test]
        public void GetLatestReleaseUtc_is_the_newest_release_of_that_type_and_target()
        {
            var first = _repo.Insert(Pu(0, "A")).Id;
            _repo.MarkReleased(first, "qa1", T0.AddMinutes(1), "ok");
            var second = _repo.Insert(Pu(0, "A")).Id;
            _repo.MarkReleased(second, "qa1", T0.AddMinutes(9), "ok");
            _repo.Insert(Pu(0, "A"));
            var other = _repo.Insert(Pu(0, "B")).Id;
            _repo.MarkReleased(other, "qa1", T0.AddHours(5), "ok");

            var latest = _repo.GetLatestReleaseUtc(LockType.Pu, "A");
            Assert.That(latest, Is.EqualTo(T0.AddMinutes(9)));
            Assert.That(latest.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(_repo.GetLatestReleaseUtc(LockType.Pu, " a\r\n"), Is.EqualTo(T0.AddMinutes(9)));
            Assert.That(_repo.GetLatestReleaseUtc(LockType.Pn, "A"), Is.Null);
            Assert.That(_repo.GetLatestReleaseUtc(LockType.Pu, "NEVER"), Is.Null);
            Assert.That(_repo.GetLatestReleaseUtc(LockType.Pu, " "), Is.Null);
        }

        // ---- lock: query ----

        [Test]
        public void Query_filters_by_status_type_line_text()
        {
            _repo.Insert(Pu(0, "U1"));
            _repo.Insert(Pn(0, "C1", "Lines\\L1"));
            var c2 = _repo.Insert(Pn(0, "C2"));
            _repo.MarkReleased(c2.Id, "qa1", T, "done");

            Assert.That(_repo.Query(new LockQuery { Status = LockStatus.Active, Type = LockType.Pn }).Select(l => l.Target), Is.EqualTo(new[] { "C1" }));
            Assert.That(_repo.Query(new LockQuery { LinePath = "Lines\\L1" }).Select(l => l.Target), Is.EqualTo(new[] { "C1" }));
            Assert.That(_repo.Query(new LockQuery { Text = "u1" }).Select(l => l.Target), Is.EqualTo(new[] { "U1" }));
        }

        [Test]
        public void Query_filters_by_enforce_state_normalised_line_and_reason_text()
        {
            _repo.Insert(L(LockType.Pu, "U1", reason: "Reel damaged"));
            _repo.Insert(L(LockType.Pn, "C1", line: "Lines\\L1", reason: "Wrong part"));
            var released = _repo.Insert(L(LockType.Pn, "C2", reason: "Old"));
            _repo.MarkReleased(released.Id, "qa1", T0, "done");
            var failed = _repo.Insert(L(LockType.Mpn, "M1", reason: "Bad lot"));
            _repo.SetEnforceState(failed.Id, EnforceState.Failed, "x");

            Assert.That(_repo.Query(new LockQuery()).Select(l => l.Target), Is.EqualTo(new[] { "U1", "C1", "C2", "M1" }), "ordered by id");
            Assert.That(_repo.Query(null).Count, Is.EqualTo(4), "no query is no filter");
            Assert.That(_repo.Query(new LockQuery { Status = LockStatus.Released }).Select(l => l.Target), Is.EqualTo(new[] { "C2" }));
            Assert.That(_repo.Query(new LockQuery { EnforceState = EnforceState.Failed }).Select(l => l.Target), Is.EqualTo(new[] { "M1" }));
            Assert.That(_repo.Query(new LockQuery { LinePath = " lines\\l1\r\n" }).Select(l => l.Target), Is.EqualTo(new[] { "C1" }));
            Assert.That(_repo.Query(new LockQuery { LinePath = "  " }).Count, Is.EqualTo(4), "a blank line filter means no filter");
            Assert.That(_repo.Query(new LockQuery { Text = "WRONG" }).Select(l => l.Target), Is.EqualTo(new[] { "C1" }));
            Assert.That(_repo.Query(new LockQuery { Text = "  " }).Count, Is.EqualTo(4), "a blank text filter means no filter");
            Assert.That(_repo.Query(new LockQuery { Text = "no such text" }), Is.Empty);
        }

        [Test]
        public void Query_text_treats_like_wildcards_and_backslash_as_plain_characters()
        {
            _repo.Insert(Pu(0, "A%B"));
            _repo.Insert(Pu(0, "A_B"));
            _repo.Insert(Pu(0, "AXB"));
            _repo.Insert(Pu(0, "A\\B"));

            Assert.That(_repo.Query(new LockQuery { Text = "%" }).Select(l => l.Target), Is.EqualTo(new[] { "A%B" }));
            Assert.That(_repo.Query(new LockQuery { Text = "a_b" }).Select(l => l.Target), Is.EqualTo(new[] { "A_B" }));
            Assert.That(_repo.Query(new LockQuery { Text = "\\" }).Select(l => l.Target), Is.EqualTo(new[] { "A\\B" }));
            Assert.That(_repo.Query(new LockQuery { Text = "a" }).Count, Is.EqualTo(4));
        }

        [Test]
        public void Query_limit_is_capped_between_1_and_5000()
        {
            var a = _repo.Insert(Pu(0, "A")).Id;
            var b = _repo.Insert(Pu(0, "B")).Id;
            _repo.Insert(Pu(0, "C"));

            Assert.That(_repo.Query(new LockQuery { Limit = 2 }).Select(l => l.Id), Is.EqualTo(new[] { a, b }));
            Assert.That(_repo.Query(new LockQuery { Limit = 0 }).Select(l => l.Id), Is.EqualTo(new[] { a }));
            Assert.That(_repo.Query(new LockQuery { Limit = -5 }).Select(l => l.Id), Is.EqualTo(new[] { a }));
            Assert.That(_repo.Query(new LockQuery { Limit = int.MaxValue }).Count, Is.EqualTo(3));
        }

        // ---- audit ----

        [Test]
        public void Audit_and_users_round_trip()
        {
            var lockId = _repo.Insert(Pu(0, "U1")).Id;
            var action = new ActionLogEntry
            {
                LockId = lockId,
                Action = Actions.LOCK_PU,
                RequestJson = "{\"uids\":[\"U1\"]}",
                Success = false,
                Error = "LockPackagingUnits failed",
                Attempt = 2,
                CreatedAtUtc = T
            };
            _audit.AddAction(action);

            var storedAction = _audit.GetActions(lockId).Single();
            Assert.That(storedAction.Id, Is.GreaterThan(0));
            Assert.That(storedAction.LockId, Is.EqualTo(lockId));
            Assert.That(storedAction.Action, Is.EqualTo(action.Action));
            Assert.That(storedAction.RequestJson, Is.EqualTo(action.RequestJson));
            Assert.That(storedAction.Success, Is.False);
            Assert.That(storedAction.Error, Is.EqualTo(action.Error));
            Assert.That(storedAction.Attempt, Is.EqualTo(2));
            Assert.That(storedAction.CreatedAtUtc, Is.EqualTo(T));
            Assert.That(storedAction.CreatedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc));

            _audit.AddDecisions(new List<DecisionLogEntry>
            {
                new DecisionLogEntry { Method = "GetPackagingUnitControlStatus", Uid = "U1", Result = "LOCKED", CreatedAtUtc = T0 },
                new DecisionLogEntry { Method = "GetPackagingUnitControlStatus", Uid = "U2", Result = "OK", CreatedAtUtc = T0 }
            });
            Assert.That(_audit.QueryDecisions(new DecisionQuery { Uid = "u1" }).Select(d => d.Uid), Is.EqualTo(new[] { "U1" }));

            _users.SaveUser(new AppUser { Username = "qa1", PinHash = "pbkdf2-sha256$100000$s$h", Role = Role.Qa, Active = true, CreatedAtUtc = T0 });
            var user = _users.FindUser("QA1");
            Assert.That(user.Username, Is.EqualTo("qa1"));
            Assert.That(user.Id, Is.GreaterThan(0));
            Assert.That(user.PinHash, Is.EqualTo("pbkdf2-sha256$100000$s$h"));
            Assert.That(user.Role, Is.EqualTo(Role.Qa));
            Assert.That(user.Active, Is.True);
            Assert.That(user.CreatedAtUtc, Is.EqualTo(T0));
            Assert.That(user.CreatedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc));

            var key = _users.InsertApiKey(new ApiKeyRecord { Name = "mes", KeyHash = "abc123", CanUnlock = true, Active = true, CreatedAtUtc = T0 });
            Assert.That(key.Id, Is.GreaterThan(0));
            var found = _users.FindApiKeyByHash("abc123");
            Assert.That(found.Id, Is.EqualTo(key.Id));
            Assert.That(found.Name, Is.EqualTo("mes"));
            Assert.That(found.CanUnlock, Is.True);
            Assert.That(found.Active, Is.True);
            Assert.That(found.CreatedAtUtc, Is.EqualTo(T0));

            _users.SetApiKeyActive(key.Id, false);
            Assert.That(_users.FindApiKeyByHash("abc123").Active, Is.False);
        }

        [Test]
        public void Actions_are_listed_per_lock_ordered_by_id_and_keep_nulls()
        {
            var one = _repo.Insert(Pu(0, "A")).Id;
            var two = _repo.Insert(Pu(0, "B")).Id;
            var input = new ActionLogEntry { LockId = one, Action = Actions.LOCK_PU, Success = true, Attempt = 1, CreatedAtUtc = T0 };
            _audit.AddAction(input);
            _audit.AddAction(new ActionLogEntry { LockId = two, Action = Actions.LOCK_FEEDER_ONLINE, Success = true, Attempt = 1, CreatedAtUtc = T0 });
            _audit.AddAction(new ActionLogEntry { LockId = one, Action = Actions.UNLOCK_PU, Success = true, Attempt = 1, CreatedAtUtc = T0 });
            _audit.AddAction(new ActionLogEntry { LockId = null, Action = Actions.SWEEP, Success = true, Attempt = 1, CreatedAtUtc = T0 });

            var actions = _audit.GetActions(one);

            Assert.That(actions.Select(a => a.Action), Is.EqualTo(new[] { Actions.LOCK_PU, Actions.UNLOCK_PU }));
            Assert.That(actions[0].Id, Is.LessThan(actions[1].Id));
            Assert.That(actions[0].RequestJson, Is.Null);
            Assert.That(actions[0].Error, Is.Null);
            Assert.That(input.Id, Is.EqualTo(0), "the caller's entry is not changed");
            Assert.That(_audit.GetActions(two + 100), Is.Empty);
            Assert.That(Scalar("SELECT count(*) FROM {schema}.action_log WHERE lock_id IS NULL"), Is.EqualTo(1L));
        }

        [Test]
        public void Decisions_round_trip_every_field_and_nulls()
        {
            var full = new DecisionLogEntry
            {
                Method = "GetPackagingUnitControlStatus",
                ReasonCode = 3,
                Uid = "U1",
                Pn = "P1",
                Mpn = "M1",
                Supplier = "ACME",
                LinePath = "Lines\\L1",
                MachineId = "MC1",
                FeederId = "F1",
                Result = "LOCKED",
                Message = "Interlock: PU U1 locked – damaged (#7)",
                MatchedLockIds = "7,9",
                CreatedAtUtc = T0.AddMinutes(1)
            };
            var empty = new DecisionLogEntry { Method = "IsFeederAllowed", Result = "OK", CreatedAtUtc = T0 };
            _audit.AddDecisions(new List<DecisionLogEntry> { full, empty });

            var rows = _audit.QueryDecisions(new DecisionQuery());

            Assert.That(rows.Count, Is.EqualTo(2));
            var a = rows[0];
            Assert.That(a.Method, Is.EqualTo(full.Method));
            Assert.That(a.ReasonCode, Is.EqualTo(3));
            Assert.That(a.Uid, Is.EqualTo(full.Uid));
            Assert.That(a.Pn, Is.EqualTo(full.Pn));
            Assert.That(a.Mpn, Is.EqualTo(full.Mpn));
            Assert.That(a.Supplier, Is.EqualTo(full.Supplier));
            Assert.That(a.LinePath, Is.EqualTo(full.LinePath));
            Assert.That(a.MachineId, Is.EqualTo(full.MachineId));
            Assert.That(a.FeederId, Is.EqualTo(full.FeederId));
            Assert.That(a.Result, Is.EqualTo(full.Result));
            Assert.That(a.Message, Is.EqualTo(full.Message));
            Assert.That(a.MatchedLockIds, Is.EqualTo(full.MatchedLockIds));
            Assert.That(a.CreatedAtUtc, Is.EqualTo(full.CreatedAtUtc));
            Assert.That(a.CreatedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc));

            var b = rows[1];
            Assert.That(b.Method, Is.EqualTo("IsFeederAllowed"));
            Assert.That(b.ReasonCode, Is.Null);
            Assert.That(new[] { b.Uid, b.Pn, b.Mpn, b.Supplier, b.LinePath, b.MachineId, b.FeederId, b.Message, b.MatchedLockIds }, Is.All.Null);
        }

        [Test]
        public void QueryDecisions_filters_uid_and_pn_ignoring_case_and_surrounding_whitespace()
        {
            _audit.AddDecisions(new List<DecisionLogEntry>
            {
                new DecisionLogEntry { Method = "m", Uid = "u1\r\n", Pn = " P1 ", Result = "OK", CreatedAtUtc = T0 },
                new DecisionLogEntry { Method = "m", Uid = "U2", Pn = "p1", Result = "OK", CreatedAtUtc = T0.AddMinutes(1) },
                new DecisionLogEntry { Method = "m", Uid = "U3", Pn = "P2", Result = "OK", CreatedAtUtc = T0.AddMinutes(2) }
            });

            Assert.That(_audit.QueryDecisions(new DecisionQuery { Uid = "U1" }).Select(d => d.Uid), Is.EqualTo(new[] { "u1\r\n" }));
            Assert.That(_audit.QueryDecisions(new DecisionQuery { Uid = " u2\t" }).Select(d => d.Uid), Is.EqualTo(new[] { "U2" }));
            Assert.That(_audit.QueryDecisions(new DecisionQuery { Pn = "p1" }).Select(d => d.Uid), Is.EqualTo(new[] { "U2", "u1\r\n" }));
            Assert.That(_audit.QueryDecisions(new DecisionQuery { Uid = "u2", Pn = "P2" }), Is.Empty);
            Assert.That(_audit.QueryDecisions(new DecisionQuery { Uid = "  ", Pn = "" }).Count, Is.EqualTo(3), "a blank filter means no filter");
            Assert.That(_audit.QueryDecisions(null).Count, Is.EqualTo(3));
        }

        [Test]
        public void QueryDecisions_time_range_is_inclusive()
        {
            _audit.AddDecisions(new List<DecisionLogEntry>
            {
                new DecisionLogEntry { Method = "m", Uid = "U1", Result = "OK", CreatedAtUtc = T0 },
                new DecisionLogEntry { Method = "m", Uid = "U2", Result = "OK", CreatedAtUtc = T0.AddMinutes(10) },
                new DecisionLogEntry { Method = "m", Uid = "U3", Result = "OK", CreatedAtUtc = T0.AddMinutes(20) }
            });

            Assert.That(_audit.QueryDecisions(new DecisionQuery { FromUtc = T0.AddMinutes(10) }).Select(d => d.Uid), Is.EqualTo(new[] { "U3", "U2" }));
            Assert.That(_audit.QueryDecisions(new DecisionQuery { ToUtc = T0.AddMinutes(10) }).Select(d => d.Uid), Is.EqualTo(new[] { "U2", "U1" }));
            Assert.That(_audit.QueryDecisions(new DecisionQuery { FromUtc = T0.AddMinutes(5), ToUtc = T0.AddMinutes(15) }).Select(d => d.Uid), Is.EqualTo(new[] { "U2" }));
        }

        [Test]
        public void QueryDecisions_returns_the_newest_rows_first_capped_at_limit()
        {
            _audit.AddDecisions(new List<DecisionLogEntry>
            {
                new DecisionLogEntry { Method = "m", Uid = "OLD", Result = "OK", CreatedAtUtc = T0 },
                new DecisionLogEntry { Method = "m", Uid = "NEW", Result = "OK", CreatedAtUtc = T0.AddMinutes(10) },
                new DecisionLogEntry { Method = "m", Uid = "MID", Result = "OK", CreatedAtUtc = T0.AddMinutes(5) }
            });
            _audit.AddDecisions(new List<DecisionLogEntry> { new DecisionLogEntry { Method = "m", Uid = "NEW2", Result = "OK", CreatedAtUtc = T0.AddMinutes(10) } });

            Assert.That(_audit.QueryDecisions(new DecisionQuery()).Select(d => d.Uid), Is.EqualTo(new[] { "NEW2", "NEW", "MID", "OLD" }),
                "newest first; a time tie goes to the later insert");
            Assert.That(_audit.QueryDecisions(new DecisionQuery { Limit = 2 }).Select(d => d.Uid), Is.EqualTo(new[] { "NEW2", "NEW" }));
            Assert.That(_audit.QueryDecisions(new DecisionQuery { Limit = 0 }).Select(d => d.Uid), Is.EqualTo(new[] { "NEW2" }));
        }

        [Test]
        public void AddDecisions_writes_the_whole_batch_or_nothing()
        {
            var batch = new List<DecisionLogEntry>
            {
                new DecisionLogEntry { Method = "m", Uid = "U1", Result = "OK", CreatedAtUtc = T0 },
                new DecisionLogEntry { Method = null, Uid = "U2", Result = "OK", CreatedAtUtc = T0 },
                new DecisionLogEntry { Method = "m", Uid = "U3", Result = "OK", CreatedAtUtc = T0 }
            };

            var ex = Assert.Throws<PostgresException>(() => _audit.AddDecisions(batch), "a bad row is not a database outage");

            Assert.That(ex.SqlState, Is.EqualTo("23502"));
            Assert.That(_audit.QueryDecisions(new DecisionQuery()), Is.Empty, "the rows before the bad one are rolled back");

            batch[1].Method = "m";
            _audit.AddDecisions(batch);
            Assert.That(_audit.QueryDecisions(new DecisionQuery()).Count, Is.EqualTo(3), "the kept batch is written on retry");
        }

        [Test]
        public void AddDecisions_accepts_an_empty_batch_and_rejects_null()
        {
            Assert.DoesNotThrow(() => _audit.AddDecisions(new List<DecisionLogEntry>()));
            Assert.Throws<ArgumentNullException>(() => _audit.AddDecisions(null));
            Assert.Throws<ArgumentException>(() => _audit.AddDecisions(new List<DecisionLogEntry> { null }));
            Assert.That(_audit.QueryDecisions(new DecisionQuery()), Is.Empty);
        }

        // ---- users and API keys ----

        [Test]
        public void FindUser_ignores_case_and_returns_null_for_unknown_users()
        {
            _users.SaveUser(new AppUser { Username = "Qa1", PinHash = "h", Role = Role.Qa, Active = true, CreatedAtUtc = T0 });

            Assert.That(_users.FindUser("qA1").Username, Is.EqualTo("Qa1"));
            Assert.That(_users.FindUser("nobody"), Is.Null);
            Assert.That(_users.FindUser(null), Is.Null);
        }

        [Test]
        public void SaveUser_updates_the_user_with_the_same_name_keeping_id_name_and_created_at()
        {
            _users.SaveUser(new AppUser { Username = "qa1", PinHash = "old", Role = Role.Qa, Active = true, CreatedAtUtc = T0 });
            _users.SaveUser(new AppUser { Username = "op1", PinHash = "h", Role = Role.Operator, Active = true, CreatedAtUtc = T0 });
            var firstId = _users.FindUser("qa1").Id;

            _users.SaveUser(new AppUser { Username = "QA1", PinHash = "new", Role = Role.Engineer, Active = false, CreatedAtUtc = T });

            var qa = _users.FindUser("qa1");
            Assert.That(_users.ListUsers().Count, Is.EqualTo(2));
            Assert.That(qa.Id, Is.EqualTo(firstId));
            Assert.That(qa.Username, Is.EqualTo("qa1"));
            Assert.That(qa.PinHash, Is.EqualTo("new"));
            Assert.That(qa.Role, Is.EqualTo(Role.Engineer));
            Assert.That(qa.Active, Is.False);
            Assert.That(qa.CreatedAtUtc, Is.EqualTo(T0), "created_at is kept");
        }

        [Test]
        public void ListUsers_is_ordered_by_id()
        {
            _users.SaveUser(new AppUser { Username = "zed", PinHash = "h", Role = Role.Admin, Active = true, CreatedAtUtc = T0 });
            _users.SaveUser(new AppUser { Username = "amy", PinHash = "h", Role = Role.Operator, Active = true, CreatedAtUtc = T0 });

            var users = _users.ListUsers();

            Assert.That(users.Select(u => u.Username), Is.EqualTo(new[] { "zed", "amy" }));
            Assert.That(users.Select(u => u.Role), Is.EqualTo(new[] { Role.Admin, Role.Operator }));
        }

        [Test]
        public void Api_keys_are_inserted_found_by_hash_and_can_be_deactivated()
        {
            var created = _users.InsertApiKey(new ApiKeyRecord { Name = "mes", KeyHash = "abc123", CanUnlock = true, Active = true, CreatedAtUtc = T0 });
            var erp = _users.InsertApiKey(new ApiKeyRecord { Name = "erp", KeyHash = "def456", CanUnlock = false, Active = true, CreatedAtUtc = T });

            Assert.That(erp.Id, Is.GreaterThan(created.Id));
            Assert.That(erp.Name, Is.EqualTo("erp"));
            Assert.That(erp.KeyHash, Is.EqualTo("def456"));
            Assert.That(erp.CanUnlock, Is.False);
            Assert.That(erp.CreatedAtUtc, Is.EqualTo(T));
            Assert.That(_users.FindApiKeyByHash("abc123").Name, Is.EqualTo("mes"));
            Assert.That(_users.FindApiKeyByHash("nope"), Is.Null);
            Assert.That(_users.FindApiKeyByHash(null), Is.Null);
            Assert.That(_users.ListApiKeys().Select(k => k.Name), Is.EqualTo(new[] { "mes", "erp" }));

            _users.SetApiKeyActive(created.Id, false);

            Assert.That(_users.FindApiKeyByHash("abc123").Active, Is.False, "an inactive key is still found; callers check Active");
            Assert.That(_users.ListApiKeys().Single(k => k.Name == "erp").Active, Is.True);

            _users.SetApiKeyActive(created.Id, true);
            Assert.That(_users.FindApiKeyByHash("abc123").Active, Is.True);
            Assert.DoesNotThrow(() => _users.SetApiKeyActive(999, false));
        }

        [Test]
        public void Api_key_names_are_unique_ignoring_case_and_the_violation_is_not_an_outage()
        {
            _users.InsertApiKey(new ApiKeyRecord { Name = "mes", KeyHash = "abc123", Active = true, CreatedAtUtc = T0 });

            var ex = Assert.Throws<PostgresException>(() =>
                _users.InsertApiKey(new ApiKeyRecord { Name = "MES", KeyHash = "zzz", Active = true, CreatedAtUtc = T0 }));

            Assert.That(ex.SqlState, Is.EqualTo("23505"));
            Assert.That(_users.ListApiKeys().Count, Is.EqualTo(1));
        }
    }
}
