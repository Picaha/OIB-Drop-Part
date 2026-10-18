using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Text;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class LockCacheTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime T1 = T0.AddSeconds(30);

        private LockCache _cache;

        [SetUp]
        public void SetUp()
        {
            _cache = new LockCache();
        }

        private static LockRecord L(long id, LockType type = LockType.Pu, string target = null, string line = null,
            Lanes? lanes = null, LockStatus status = LockStatus.Active)
        {
            target = target ?? "U" + id;
            return new LockRecord
            {
                Id = id,
                Type = type,
                Target = target,
                TargetNorm = Normalizer.Norm(target),
                LinePath = line,
                Lanes = lanes,
                Reason = "r" + id,
                Status = status
            };
        }

        [Test]
        public void New_cache_is_empty_and_not_loaded()
        {
            Assert.That(_cache.Snapshot, Is.Empty);
            Assert.That(_cache.LoadedAtUtc, Is.Null);
        }

        [Test]
        public void Replace_then_snapshot_contains_only_new_set()
        {
            _cache.Replace(new[] { L(1), L(2) }, T0);
            _cache.Replace(new[] { L(3) }, T1);

            Assert.That(_cache.Snapshot.Select(l => l.Id), Is.EqualTo(new long[] { 3 }));
            Assert.That(_cache.LoadedAtUtc, Is.EqualTo(T1));
        }

        [Test]
        public void Upsert_does_not_change_loaded_time()
        {
            _cache.Replace(new[] { L(1) }, T0);
            _cache.Upsert(L(2));

            Assert.That(_cache.LoadedAtUtc, Is.EqualTo(T0));
        }

        [Test]
        public void Upsert_with_id_replaces_entry_with_same_id()
        {
            _cache.Replace(new[] { L(1), L(2) }, T0);
            var changed = L(1, target: "OTHER");

            _cache.Upsert(changed);

            Assert.That(_cache.Snapshot.Count, Is.EqualTo(2));
            Assert.That(_cache.Snapshot.Single(l => l.Id == 1).Target, Is.EqualTo("OTHER"));
        }

        [Test]
        public void Upsert_with_new_id_appends()
        {
            _cache.Replace(new[] { L(1) }, T0);
            _cache.Upsert(L(2));

            Assert.That(_cache.Snapshot.Select(l => l.Id), Is.EquivalentTo(new long[] { 1, 2 }));
        }

        [Test]
        public void Upsert_spooled_entry_replaced_by_identity_not_id()
        {
            _cache.Upsert(L(0, target: "U1"));
            _cache.Upsert(L(0, target: "u1 "));

            Assert.That(_cache.Snapshot.Count, Is.EqualTo(1));
            Assert.That(_cache.Snapshot.Single().Target, Is.EqualTo("u1 "));
        }

        [Test]
        public void Upsert_spooled_identity_includes_type_line_and_lanes()
        {
            _cache.Upsert(L(0, LockType.Pn, "A", line: "Lines\\L1"));
            _cache.Upsert(L(0, LockType.Mpn, "A", line: "Lines\\L1"));
            _cache.Upsert(L(0, LockType.Pn, "A", line: "Lines\\L2"));
            _cache.Upsert(L(0, LockType.Pn, "A"));
            _cache.Upsert(L(0, LockType.Conveyor, "A", lanes: Lanes.Left));
            _cache.Upsert(L(0, LockType.Conveyor, "A", lanes: Lanes.Right));

            Assert.That(_cache.Snapshot.Count, Is.EqualTo(6));
        }

        [Test]
        public void Upsert_spooled_identity_treats_blank_line_path_as_unset()
        {
            _cache.Upsert(L(0, LockType.Pn, "A"));
            _cache.Upsert(L(0, LockType.Pn, "A", line: "  "));
            _cache.Upsert(L(0, LockType.Pn, "A", line: " lines\\l1"));
            _cache.Upsert(L(0, LockType.Pn, "A", line: "LINES\\L1 "));

            Assert.That(_cache.Snapshot.Count, Is.EqualTo(2));
        }

        [Test]
        public void UpsertIfPresent_replaces_entry_with_same_id()
        {
            _cache.Replace(new[] { L(1), L(2) }, T0);

            var replaced = _cache.UpsertIfPresent(L(1, target: "OTHER"));

            Assert.That(replaced, Is.True);
            Assert.That(_cache.Snapshot.Count, Is.EqualTo(2));
            Assert.That(_cache.Snapshot.Single(l => l.Id == 1).Target, Is.EqualTo("OTHER"));
        }

        [Test]
        public void UpsertIfPresent_does_not_add_an_unknown_id()
        {
            _cache.Replace(new[] { L(1) }, T0);

            var replaced = _cache.UpsertIfPresent(L(2));

            Assert.That(replaced, Is.False);
            Assert.That(_cache.Snapshot.Select(l => l.Id), Is.EqualTo(new long[] { 1 }));
        }

        [Test]
        public void UpsertIfPresent_replaces_a_spooled_entry_by_identity_and_never_adds()
        {
            Assert.That(_cache.UpsertIfPresent(L(0, target: "U1")), Is.False);
            Assert.That(_cache.Snapshot, Is.Empty);

            _cache.Upsert(L(0, target: "U1"));
            Assert.That(_cache.UpsertIfPresent(L(0, target: "u1 ")), Is.True);
            Assert.That(_cache.UpsertIfPresent(L(0, target: "U2")), Is.False);

            Assert.That(_cache.Snapshot.Count, Is.EqualTo(1));
            Assert.That(_cache.Snapshot.Single().Target, Is.EqualTo("u1 "));
        }

        [Test]
        public void UpsertIfPresent_does_not_change_loaded_time()
        {
            _cache.Replace(new[] { L(1) }, T0);
            _cache.UpsertIfPresent(L(1));

            Assert.That(_cache.LoadedAtUtc, Is.EqualTo(T0));
        }

        [Test]
        public void Remove_by_id()
        {
            _cache.Replace(new[] { L(1), L(2) }, T0);
            _cache.Remove(1);

            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(2));
        }

        [Test]
        public void Remove_unknown_id_is_a_no_op()
        {
            _cache.Replace(new[] { L(1) }, T0);
            _cache.Remove(99);

            Assert.That(_cache.Snapshot.Single().Id, Is.EqualTo(1));
        }

        [Test]
        public void Snapshot_is_stable_while_writer_changes_cache()
        {
            _cache.Replace(new[] { L(1) }, T0);
            var s = _cache.Snapshot;

            _cache.Upsert(L(2));
            _cache.Remove(1);
            _cache.Replace(new[] { L(9) }, T1);

            Assert.That(s.Count, Is.EqualTo(1));
            Assert.That(s.Single().Id, Is.EqualTo(1));
        }

        [Test]
        public void FindActive_normalises_arguments()
        {
            _cache.Replace(new[] { L(1, LockType.Pn, "pn-1", line: "Lines\\L1") }, T0);

            var found = _cache.FindActive(LockType.Pn, "  Pn-1 ", " lines\\l1", null);

            Assert.That(found, Is.Not.Null);
            Assert.That(found.Id, Is.EqualTo(1));
        }

        [Test]
        public void FindActive_matches_on_type_line_and_lanes()
        {
            _cache.Replace(new[]
            {
                L(1, LockType.Pn, "A", line: "Lines\\L1"),
                L(2, LockType.Pn, "A"),
                L(3, LockType.Conveyor, "A", lanes: Lanes.Left),
                L(4, LockType.Conveyor, "A", lanes: Lanes.Both)
            }, T0);

            Assert.That(_cache.FindActive(LockType.Pn, "A", "Lines\\L1", null).Id, Is.EqualTo(1));
            Assert.That(_cache.FindActive(LockType.Pn, "A", null, null).Id, Is.EqualTo(2));
            Assert.That(_cache.FindActive(LockType.Pn, "A", "  ", null).Id, Is.EqualTo(2));
            Assert.That(_cache.FindActive(LockType.Conveyor, "A", null, Lanes.Left).Id, Is.EqualTo(3));
            Assert.That(_cache.FindActive(LockType.Conveyor, "A", null, Lanes.Both).Id, Is.EqualTo(4));
            Assert.That(_cache.FindActive(LockType.Conveyor, "A", null, Lanes.Right), Is.Null);
            Assert.That(_cache.FindActive(LockType.Conveyor, "A", null, null), Is.Null);
            Assert.That(_cache.FindActive(LockType.Mpn, "A", null, null), Is.Null);
            Assert.That(_cache.FindActive(LockType.Pn, "A", "Lines\\L9", null), Is.Null);
        }

        [Test]
        public void FindActive_ignores_released_entries()
        {
            _cache.Replace(new[] { L(1, target: "U1", status: LockStatus.Released) }, T0);

            Assert.That(_cache.FindActive(LockType.Pu, "U1", null, null), Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void FindActive_never_matches_a_blank_target(string target)
        {
            var blank = L(1, target: "U1");
            blank.TargetNorm = null;
            _cache.Replace(new[] { blank }, T0);

            Assert.That(_cache.FindActive(LockType.Pu, target, null, null), Is.Null);
        }

        [Test]
        public void Concurrent_writers_do_not_lose_updates_and_readers_see_consistent_snapshots()
        {
            const int writers = 8;
            const int perWriter = 200;
            var failures = 0;
            var stop = new CancellationTokenSource();

            var reader = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var s = _cache.Snapshot;
                    if (s.Select(l => l.Id).Distinct().Count() != s.Count)
                        Interlocked.Increment(ref failures);
                }
            });

            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                for (var i = 1; i <= perWriter; i++)
                    _cache.Upsert(L(w * 1000 + i));
            })).ToArray();
            Task.WaitAll(tasks);
            stop.Cancel();
            reader.Wait();

            Assert.That(failures, Is.EqualTo(0));
            Assert.That(_cache.Snapshot.Count, Is.EqualTo(writers * perWriter));
        }

        [Test]
        public void WriteGate_is_one_dedicated_object_that_does_not_block_the_cache()
        {
            Assert.That(_cache.WriteGate, Is.Not.Null);
            Assert.That(_cache.WriteGate, Is.SameAs(_cache.WriteGate));

            var entered = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var holder = Task.Run(() =>
            {
                lock (_cache.WriteGate)
                {
                    entered.Set();
                    release.Wait();
                }
            });

            try
            {
                Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "the holder never took the gate");

                var writer = Task.Run(() =>
                {
                    _cache.Upsert(L(1));
                    _cache.Remove(2);
                    _cache.Replace(new[] { L(3) }, T0);
                });

                Assert.That(writer.Wait(TimeSpan.FromSeconds(5)), Is.True, "cache members must not wait for the write gate");
                Assert.That(_cache.Snapshot.Select(l => l.Id), Is.EqualTo(new long[] { 3 }));
            }
            finally
            {
                release.Set();
                holder.Wait();
            }
        }
    }
}
