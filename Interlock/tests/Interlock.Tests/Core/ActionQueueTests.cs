using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Interlock.Core.Enforcement;
using Interlock.Core.Model;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class ActionQueueTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);

        private ActionQueue _queue;

        [SetUp]
        public void SetUp()
        {
            _queue = new ActionQueue();
        }

        private static EnforcementJob Job(long lockId, DateTime notBeforeUtc, JobKind kind = JobKind.Apply)
        {
            return new EnforcementJob
            {
                Lock = new LockRecord { Id = lockId, TargetNorm = "T" + lockId },
                Kind = kind,
                NotBeforeUtc = notBeforeUtc
            };
        }

        [Test]
        public void New_queue_is_empty()
        {
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_queue.TakeDue(T0), Is.Empty);
        }

        [Test]
        public void Any_finds_a_queued_job_whether_or_not_it_is_due_and_removes_nothing()
        {
            _queue.Enqueue(Job(1, T0));
            _queue.Enqueue(Job(2, T0.AddHours(1), JobKind.Reblock));

            Assert.That(_queue.Any(j => j.Lock.Id == 1), Is.True);
            Assert.That(_queue.Any(j => j.Lock.Id == 2 && j.Kind == JobKind.Reblock), Is.True);
            Assert.That(_queue.Any(j => j.Lock.Id == 2 && j.Kind == JobKind.Apply), Is.False);
            Assert.That(_queue.Any(j => j.Lock.Id == 3), Is.False);
            Assert.That(_queue.Count, Is.EqualTo(2));
        }

        [Test]
        public void Any_is_false_for_an_empty_queue_and_for_a_job_that_was_taken()
        {
            Assert.That(_queue.Any(j => true), Is.False);

            _queue.Enqueue(Job(1, T0));
            _queue.TakeDue(T0);

            Assert.That(_queue.Any(j => true), Is.False);
        }

        [Test]
        public void Any_rejects_a_missing_predicate()
        {
            Assert.Throws<ArgumentNullException>(() => _queue.Any(null));
        }

        [Test]
        public void TakeDue_returns_only_due_jobs_in_order()
        {
            var late = Job(1, T0.AddSeconds(10));
            var first = Job(2, T0);
            var second = Job(3, T0);
            _queue.Enqueue(late);
            _queue.Enqueue(first);
            _queue.Enqueue(second);

            var due = _queue.TakeDue(T0);

            Assert.That(due, Is.EqualTo(new[] { first, second }));
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void TakeDue_orders_by_time_then_insertion()
        {
            var a = Job(1, T0.AddSeconds(5));
            var b = Job(2, T0.AddSeconds(1));
            var c = Job(3, T0.AddSeconds(5));
            var d = Job(4, T0.AddSeconds(1));
            foreach (var j in new[] { a, b, c, d })
                _queue.Enqueue(j);

            Assert.That(_queue.TakeDue(T0.AddSeconds(10)), Is.EqualTo(new[] { b, d, a, c }));
        }

        [Test]
        public void A_job_due_exactly_now_is_due()
        {
            var job = Job(1, T0);
            _queue.Enqueue(job);

            Assert.That(_queue.TakeDue(T0.AddTicks(-1)), Is.Empty);
            Assert.That(_queue.TakeDue(T0), Is.EqualTo(new[] { job }));
        }

        [Test]
        public void TakeDue_removes_the_jobs_it_returns()
        {
            _queue.Enqueue(Job(1, T0));

            Assert.That(_queue.TakeDue(T0).Count, Is.EqualTo(1));
            Assert.That(_queue.TakeDue(T0), Is.Empty);
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void A_job_taken_and_enqueued_again_waits_for_its_new_time()
        {
            var job = Job(1, T0);
            _queue.Enqueue(job);
            _queue.TakeDue(T0);

            job.FailedAttempts++;
            job.NotBeforeUtc = T0.AddSeconds(5);
            _queue.Enqueue(job);

            Assert.That(_queue.TakeDue(T0.AddSeconds(4)), Is.Empty);
            Assert.That(_queue.TakeDue(T0.AddSeconds(5)), Is.EqualTo(new[] { job }));
        }

        [Test]
        public void Cancel_removes_jobs_for_lock()
        {
            var other = Job(6, T0);
            _queue.Enqueue(Job(5, T0, JobKind.Apply));
            _queue.Enqueue(other);
            _queue.Enqueue(Job(5, T0.AddMinutes(1), JobKind.Reblock));

            _queue.Cancel(5);

            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(_queue.TakeDue(T0.AddHours(1)), Is.EqualTo(new[] { other }));
        }

        [Test]
        public void Cancel_of_an_unknown_lock_changes_nothing()
        {
            _queue.Enqueue(Job(5, T0));

            _queue.Cancel(99);

            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void Cancel_zero_never_cancels_spooled_locks()
        {
            _queue.Enqueue(Job(0, T0));
            _queue.Enqueue(Job(0, T0));

            _queue.Cancel(0);

            Assert.That(_queue.Count, Is.EqualTo(2));
        }

        [Test]
        public void CancelWhere_removes_the_matching_jobs_and_returns_their_number()
        {
            var keep = Job(5, T0);
            _queue.Enqueue(Job(0, T0));
            _queue.Enqueue(keep);
            _queue.Enqueue(Job(0, T0.AddMinutes(1), JobKind.Release));

            var removed = _queue.CancelWhere(j => j.Lock.Id == 0);

            Assert.That(removed, Is.EqualTo(2));
            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(_queue.TakeDue(T0.AddHours(1)), Is.EqualTo(new[] { keep }));
        }

        [Test]
        public void CancelWhere_that_matches_nothing_changes_nothing()
        {
            _queue.Enqueue(Job(5, T0));

            Assert.That(_queue.CancelWhere(j => false), Is.EqualTo(0));
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void CancelWhere_can_cancel_spooled_jobs_that_Cancel_zero_leaves_alone()
        {
            _queue.Enqueue(Job(0, T0));

            _queue.Cancel(0);
            Assert.That(_queue.Count, Is.EqualTo(1));

            Assert.That(_queue.CancelWhere(j => j.Lock.Id == 0), Is.EqualTo(1));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void CancelWhere_rejects_a_missing_predicate()
        {
            Assert.Throws<ArgumentNullException>(() => _queue.CancelWhere(null));
        }

        [Test]
        public void CancelWhere_runs_safely_beside_enqueue_and_take()
        {
            const int total = 2000;
            var enqueue = Task.Run(() =>
            {
                for (var i = 0; i < total; i++)
                    _queue.Enqueue(Job(i % 2 == 0 ? 0 : i, T0));
            });
            var removed = 0;
            var cancel = Task.Run(() =>
            {
                for (var i = 0; i < 200; i++)
                    removed += _queue.CancelWhere(j => j.Lock.Id == 0);
            });
            Task.WaitAll(enqueue, cancel);
            removed += _queue.CancelWhere(j => j.Lock.Id == 0);

            Assert.That(removed, Is.EqualTo(total / 2));
            Assert.That(_queue.Count, Is.EqualTo(total / 2));
        }

        [Test]
        public void Enqueue_rejects_a_missing_job_or_lock()
        {
            Assert.Throws<ArgumentNullException>(() => _queue.Enqueue(null));
            Assert.Throws<ArgumentException>(() => _queue.Enqueue(new EnforcementJob()));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Parallel_enqueue_then_take_returns_every_job_exactly_once()
        {
            const int total = 4000;
            Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = 8 },
                i => _queue.Enqueue(Job(i + 1, T0)));

            Assert.That(_queue.Count, Is.EqualTo(total));
            var taken = _queue.TakeDue(T0);

            Assert.That(taken.Count, Is.EqualTo(total));
            Assert.That(taken.Select(j => j.Lock.Id).OrderBy(id => id), Is.EqualTo(Enumerable.Range(1, total).Select(i => (long)i)));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Concurrent_takers_never_get_the_same_job_twice()
        {
            const int total = 4000;
            for (var i = 0; i < total; i++)
                _queue.Enqueue(Job(i + 1, T0.AddMilliseconds(i)));

            // every taker walks the same timeline, so several of them overlap on each slice of due jobs
            var taken = new ConcurrentBag<long>();
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (var ms = 0; ms < total; ms++)
                {
                    foreach (var job in _queue.TakeDue(T0.AddMilliseconds(ms)))
                        taken.Add(job.Lock.Id);
                }
            })).ToArray();
            Task.WaitAll(tasks);

            Assert.That(taken.Count, Is.EqualTo(total));
            Assert.That(taken.Distinct().Count(), Is.EqualTo(total));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }
    }
}
