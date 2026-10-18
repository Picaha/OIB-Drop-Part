using System;
using System.Linq;
using Interlock.Core.Enforcement;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class AlarmBoardTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);

        private AlarmBoard _board;

        [SetUp]
        public void SetUp()
        {
            _board = new AlarmBoard();
        }

        [Test]
        public void New_board_has_no_alarms()
        {
            Assert.That(_board.Recent(10), Is.Empty);
        }

        [Test]
        public void Alarm_is_formatted_as_iso_time_then_text()
        {
            _board.Raise("Lock #1 FAILED: boom", T0);

            Assert.That(_board.Recent(1), Is.EqualTo(new[] { T0.ToString("o") + " Lock #1 FAILED: boom" }));
            Assert.That(_board.Recent(1)[0], Does.StartWith("2026-10-18T08:00:00.0000000Z "));
        }

        [Test]
        public void Recent_is_newest_first_and_capped_by_max()
        {
            _board.Raise("one", T0);
            _board.Raise("two", T0.AddSeconds(1));
            _board.Raise("three", T0.AddSeconds(2));

            Assert.That(_board.Recent(2).Select(a => a.Substring(a.IndexOf(' ') + 1)), Is.EqualTo(new[] { "three", "two" }));
            Assert.That(_board.Recent(10).Count, Is.EqualTo(3));
        }

        [TestCase(0)]
        [TestCase(-3)]
        public void Recent_with_no_room_is_empty(int max)
        {
            _board.Raise("one", T0);

            Assert.That(_board.Recent(max), Is.Empty);
        }

        [Test]
        public void Keeps_only_the_last_fifty()
        {
            for (var i = 1; i <= 60; i++)
                _board.Raise("alarm " + i, T0.AddSeconds(i));

            var recent = _board.Recent(1000);

            Assert.That(recent.Count, Is.EqualTo(50));
            Assert.That(recent[0], Does.EndWith("alarm 60"));
            Assert.That(recent[49], Does.EndWith("alarm 11"));
        }

        [Test]
        public void Recent_returns_a_copy()
        {
            _board.Raise("one", T0);

            _board.Recent(10).Clear();

            Assert.That(_board.Recent(10).Count, Is.EqualTo(1));
        }
    }
}
