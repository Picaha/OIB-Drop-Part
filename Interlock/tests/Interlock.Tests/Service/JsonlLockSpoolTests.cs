using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Interlock.Core.Model;
using Interlock.Service.Data;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Service
{
    [TestFixture]
    public class JsonlLockSpoolTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 6, 30, 15, 123, DateTimeKind.Utc);

        private string _dir;
        private string _path;
        private ListLog _log;
        private JsonlLockSpool _spool;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "spool-" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "locks.jsonl");
            _log = new ListLog();
            _spool = new JsonlLockSpool(_path, _log);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        // ---- helpers ----

        private static LockRecord Pu(long id, string uid)
        {
            return new LockRecord
            {
                Id = id,
                Type = LockType.Pu,
                Target = uid,
                TargetNorm = uid.ToUpperInvariant(),
                Source = LockSource.SetupCenter,
                CreatedBy = "SetupCenter",
                Status = LockStatus.Active,
                EnforceState = EnforceState.Pending
            };
        }

        private static LockRecord Full()
        {
            return new LockRecord
            {
                Id = 0,
                Type = LockType.Conveyor,
                Target = "Line 1 / Station 2",
                TargetNorm = "LINE 1 / STATION 2",
                Lanes = Lanes.Both,
                LinePath = "Plant/Line 1",
                Reason = "Setup Center component mismatch: wrong \"component\"\r\nsecond line \\ back\\slash คำเตือน",
                Source = LockSource.Api,
                CreatedBy = "svc-user",
                CreatedAtUtc = T0,
                Status = LockStatus.Released,
                ReleasedBy = "qa1",
                ReleasedAtUtc = T0.AddMinutes(7),
                ReleaseReason = "checked",
                EnforceState = EnforceState.Failed,
                EnforceError = "OIB call failed"
            };
        }

        private static void AssertSame(LockRecord expected, LockRecord actual)
        {
            Assert.That(actual.Id, Is.EqualTo(0), "Id");
            Assert.That(actual.Type, Is.EqualTo(expected.Type), "Type");
            Assert.That(actual.Target, Is.EqualTo(expected.Target), "Target");
            Assert.That(actual.TargetNorm, Is.EqualTo(expected.TargetNorm), "TargetNorm");
            Assert.That(actual.Lanes, Is.EqualTo(expected.Lanes), "Lanes");
            Assert.That(actual.LinePath, Is.EqualTo(expected.LinePath), "LinePath");
            Assert.That(actual.Reason, Is.EqualTo(expected.Reason), "Reason");
            Assert.That(actual.Source, Is.EqualTo(expected.Source), "Source");
            Assert.That(actual.CreatedBy, Is.EqualTo(expected.CreatedBy), "CreatedBy");
            AssertSameInstant(expected.CreatedAtUtc, actual.CreatedAtUtc, "CreatedAtUtc");
            Assert.That(actual.Status, Is.EqualTo(expected.Status), "Status");
            Assert.That(actual.ReleasedBy, Is.EqualTo(expected.ReleasedBy), "ReleasedBy");
            Assert.That(actual.ReleasedAtUtc.HasValue, Is.EqualTo(expected.ReleasedAtUtc.HasValue), "ReleasedAtUtc has a value");
            if (expected.ReleasedAtUtc.HasValue)
                AssertSameInstant(expected.ReleasedAtUtc.Value, actual.ReleasedAtUtc.Value, "ReleasedAtUtc");
            Assert.That(actual.ReleaseReason, Is.EqualTo(expected.ReleaseReason), "ReleaseReason");
            Assert.That(actual.EnforceState, Is.EqualTo(expected.EnforceState), "EnforceState");
            Assert.That(actual.EnforceError, Is.EqualTo(expected.EnforceError), "EnforceError");
        }

        private static void AssertSameInstant(DateTime expected, DateTime actual, string what)
        {
            Assert.That(actual, Is.EqualTo(expected).Within(1).Milliseconds, what);
            Assert.That(actual.Kind, Is.EqualTo(expected.Kind), what + " kind");
        }

        private string[] Lines()
        {
            return File.ReadAllText(_path, Encoding.UTF8).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private void WriteLines(params string[] lines)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, string.Join("\n", lines), new UTF8Encoding(false));
        }

        // ---- round trip ----

        [Test]
        public void Jsonl_spool_round_trip()
        {
            var s = new JsonlLockSpool(Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".jsonl"));
            try
            {
                s.Append(Pu(0, "U1"));
                s.Append(Pu(0, "U2"));

                Assert.That(s.ReadAll().Select(l => l.Target), Is.EqualTo(new[] { "U1", "U2" }));

                s.Clear();
                Assert.That(s.ReadAll(), Is.Empty);
            }
            finally
            {
                s.Clear();
            }
        }

        [Test]
        public void Every_field_of_a_lock_survives_the_round_trip()
        {
            var original = Full();

            _spool.Append(original);

            AssertSame(original, _spool.ReadAll().Single());
        }

        [Test]
        public void A_lock_with_empty_optional_fields_round_trips_as_empty()
        {
            var original = Pu(0, "U1");
            original.CreatedAtUtc = T0;

            _spool.Append(original);

            var back = _spool.ReadAll().Single();
            AssertSame(original, back);
            Assert.That(back.Lanes, Is.Null);
            Assert.That(back.ReleasedAtUtc, Is.Null);
            Assert.That(back.LinePath, Is.Null);
            Assert.That(back.Reason, Is.Null);
        }

        [Test]
        public void An_unset_date_round_trips_without_failing()
        {
            var original = Pu(0, "U1");

            _spool.Append(original);

            Assert.That(_spool.ReadAll().Single().CreatedAtUtc, Is.EqualTo(default(DateTime)));
        }

        [TestCase(LockType.Feeder)]
        [TestCase(LockType.Pu)]
        [TestCase(LockType.Pn)]
        [TestCase(LockType.Mpn)]
        [TestCase(LockType.Supplier)]
        [TestCase(LockType.Conveyor)]
        public void Every_lock_type_round_trips(LockType type)
        {
            var original = Pu(0, "X");
            original.Type = type;

            _spool.Append(original);

            Assert.That(_spool.ReadAll().Single().Type, Is.EqualTo(type));
        }

        [TestCase(LockSource.Manual)]
        [TestCase(LockSource.Api)]
        [TestCase(LockSource.SetupCenter)]
        public void Every_source_round_trips(LockSource source)
        {
            var original = Pu(0, "X");
            original.Source = source;

            _spool.Append(original);

            Assert.That(_spool.ReadAll().Single().Source, Is.EqualTo(source));
        }

        [TestCase(EnforceState.Pending)]
        [TestCase(EnforceState.Applied)]
        [TestCase(EnforceState.Failed)]
        [TestCase(EnforceState.NotNeeded)]
        public void Every_enforce_state_round_trips(EnforceState state)
        {
            var original = Pu(0, "X");
            original.EnforceState = state;

            _spool.Append(original);

            Assert.That(_spool.ReadAll().Single().EnforceState, Is.EqualTo(state));
        }

        [TestCase(null)]
        [TestCase(Lanes.Left)]
        [TestCase(Lanes.Right)]
        [TestCase(Lanes.Both)]
        public void Every_lanes_value_round_trips(Lanes? lanes)
        {
            var original = Pu(0, "X");
            original.Lanes = lanes;

            _spool.Append(original);

            Assert.That(_spool.ReadAll().Single().Lanes, Is.EqualTo(lanes));
        }

        [TestCase(LockStatus.Active)]
        [TestCase(LockStatus.Released)]
        public void Every_status_round_trips(LockStatus status)
        {
            var original = Pu(0, "X");
            original.Status = status;

            _spool.Append(original);

            Assert.That(_spool.ReadAll().Single().Status, Is.EqualTo(status));
        }

        [Test]
        public void Records_come_back_in_the_order_they_were_appended_and_with_id_zero()
        {
            _spool.Append(Pu(7, "U1"));
            _spool.Append(Pu(0, "U2"));
            _spool.Append(Pu(9, "U3"));

            var all = _spool.ReadAll();

            Assert.That(all.Select(l => l.Target), Is.EqualTo(new[] { "U1", "U2", "U3" }));
            Assert.That(all.All(l => l.Id == 0), Is.True);
        }

        [Test]
        public void A_new_spool_object_on_the_same_file_reads_what_was_appended()
        {
            _spool.Append(Pu(0, "U1"));

            var other = new JsonlLockSpool(_path);

            Assert.That(other.ReadAll().Single().Target, Is.EqualTo("U1"));
        }

        // ---- file format ----

        [Test]
        public void The_file_has_one_json_object_per_line_in_utf8_without_a_bom()
        {
            _spool.Append(Full());
            _spool.Append(Pu(0, "U2"));

            var bytes = File.ReadAllBytes(_path);
            Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "a UTF-8 BOM");
            var lines = Lines();
            Assert.That(lines.Length, Is.EqualTo(2), "the quotes, backslashes and line breaks of a reason stay inside their line");
            foreach (var line in lines)
            {
                Assert.That(line, Does.StartWith("{"));
                Assert.That(line.TrimEnd('\r'), Does.EndWith("}"));
            }
            Assert.That(Encoding.UTF8.GetString(bytes), Does.Contain("คำเตือน"));
            Assert.That(bytes.Last(), Is.EqualTo((byte)'\n'), "every record ends with a line break");
        }

        [Test]
        public void Append_creates_a_missing_directory()
        {
            var nested = new JsonlLockSpool(Path.Combine(_dir, "a", "b", "locks.jsonl"));

            nested.Append(Pu(0, "U1"));

            Assert.That(nested.ReadAll().Single().Target, Is.EqualTo("U1"));
        }

        [Test]
        public void Append_has_written_the_record_to_the_file_when_it_returns()
        {
            _spool.Append(Pu(0, "U1"));

            // read through a second handle, without going through the spool: the bytes must be in the file already
            using (var read = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(read, Encoding.UTF8))
                Assert.That(reader.ReadToEnd(), Does.Contain("U1"));
        }

        // ---- missing and damaged files ----

        [Test]
        public void ReadAll_of_a_missing_file_is_empty()
        {
            Assert.That(_spool.ReadAll(), Is.Empty);
        }

        [Test]
        public void ReadAll_skips_blank_lines()
        {
            _spool.Append(Pu(0, "U1"));
            File.AppendAllText(_path, "\n   \n\t\n\n");
            _spool.Append(Pu(0, "U2"));

            Assert.That(_spool.ReadAll().Select(l => l.Target), Is.EqualTo(new[] { "U1", "U2" }));
            Assert.That(_log.Warnings, Is.Empty, "blank lines are not damage");
        }

        [Test]
        public void ReadAll_skips_a_malformed_line_and_warns_but_still_reads_the_rest()
        {
            _spool.Append(Pu(0, "U1"));
            var good = Lines().Single();
            WriteLines(
                good,
                "this is not json",
                "{\"type\":\"PU\",\"target\":",
                "{\"type\":\"NOT-A-TYPE\",\"target\":\"X\",\"targetNorm\":\"X\",\"source\":\"MANUAL\",\"status\":\"ACTIVE\",\"enforceState\":\"PENDING\"}",
                "[1,2,3]",
                "null",
                good.Replace("U1", "U2"));

            var all = _spool.ReadAll();

            Assert.That(all.Select(l => l.Target), Is.EqualTo(new[] { "U1", "U2" }));
            Assert.That(_log.Warnings.Count, Is.EqualTo(5), string.Join(" | ", _log.Warnings));
            Assert.That(_log.Warnings.All(w => w.Contains(_path) || w.Contains("line")), Is.True);
        }

        [Test]
        public void ReadAll_without_a_log_skips_malformed_lines_quietly()
        {
            _spool.Append(Pu(0, "U1"));
            File.AppendAllText(_path, "garbage\n");
            var quiet = new JsonlLockSpool(_path);

            Assert.That(quiet.ReadAll().Select(l => l.Target), Is.EqualTo(new[] { "U1" }));
        }

        [Test]
        public void A_line_with_a_unix_or_windows_line_ending_is_read()
        {
            _spool.Append(Pu(0, "U1"));
            _spool.Append(Pu(0, "U2"));
            var text = File.ReadAllText(_path, Encoding.UTF8).Replace("\n", "\r\n");
            File.WriteAllText(_path, text, new UTF8Encoding(false));

            Assert.That(_spool.ReadAll().Select(l => l.Target), Is.EqualTo(new[] { "U1", "U2" }));
        }

        [Test]
        public void A_file_saved_with_a_bom_is_read()
        {
            _spool.Append(Pu(0, "U1"));
            File.WriteAllText(_path, File.ReadAllText(_path, Encoding.UTF8), new UTF8Encoding(true));

            Assert.That(_spool.ReadAll().Select(l => l.Target), Is.EqualTo(new[] { "U1" }));
        }

        [Test]
        public void An_append_after_a_torn_last_line_does_not_glue_the_new_record_to_it()
        {
            _spool.Append(Pu(0, "U1"));
            var torn = Lines().Single();
            File.AppendAllText(_path, torn.Substring(0, torn.Length / 2), new UTF8Encoding(false));

            _spool.Append(Pu(0, "U2"));

            Assert.That(_spool.ReadAll().Select(l => l.Target), Is.EqualTo(new[] { "U1", "U2" }));
            Assert.That(_log.Warnings.Count, Is.EqualTo(1));
        }

        // ---- clear ----

        [Test]
        public void Clear_deletes_the_file()
        {
            _spool.Append(Pu(0, "U1"));
            Assert.That(File.Exists(_path), Is.True);

            _spool.Clear();

            Assert.That(File.Exists(_path), Is.False);
            Assert.That(_spool.ReadAll(), Is.Empty);
        }

        [Test]
        public void Clear_tolerates_a_missing_file_and_a_missing_directory()
        {
            Assert.DoesNotThrow(() => _spool.Clear());
            Directory.CreateDirectory(_dir);
            Assert.DoesNotThrow(() => _spool.Clear());
            Assert.DoesNotThrow(() => _spool.Clear());
        }

        [Test]
        public void Append_after_Clear_starts_a_new_file()
        {
            _spool.Append(Pu(0, "U1"));
            _spool.Clear();

            _spool.Append(Pu(0, "U2"));

            Assert.That(_spool.ReadAll().Select(l => l.Target), Is.EqualTo(new[] { "U2" }));
        }

        // ---- threads ----

        [Test]
        public void Parallel_appends_each_leave_one_whole_record()
        {
            const int total = 200;
            Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => _spool.Append(Pu(0, "U" + i)));

            var all = _spool.ReadAll();

            Assert.That(all.Count, Is.EqualTo(total));
            Assert.That(all.Select(l => l.Target).Distinct().Count(), Is.EqualTo(total));
            Assert.That(_log.Warnings, Is.Empty);
        }

        [Test]
        public void Reading_while_appending_never_sees_a_torn_record()
        {
            var done = new ManualResetEventSlim();
            var writer = Task.Run(() =>
            {
                for (var i = 0; i < 150; i++)
                    _spool.Append(Pu(0, "U" + i));
                done.Set();
            });

            var reads = 0;
            while (!done.IsSet)
            {
                _spool.ReadAll();
                reads++;
            }
            writer.Wait();

            Assert.That(reads, Is.GreaterThan(0));
            Assert.That(_log.Warnings, Is.Empty, "a reader saw a record that was not finished");
            Assert.That(_spool.ReadAll().Count, Is.EqualTo(150));
        }

        // ---- construction ----

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void The_constructor_needs_a_path(string path)
        {
            Assert.Catch<ArgumentException>(() => new JsonlLockSpool(path));
        }

        [Test]
        public void Append_rejects_a_missing_lock()
        {
            Assert.Throws<ArgumentNullException>(() => _spool.Append(null));
        }
    }
}
