using System;
using Interlock.Core.Model;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class DbEnumTests
    {
        [Test]
        public void Round_trips_every_value()
        {
            foreach (LockType v in Enum.GetValues(typeof(LockType)))
                Assert.That(DbEnum.ParseLockType(DbEnum.ToDb(v)), Is.EqualTo(v));
            foreach (LockStatus v in Enum.GetValues(typeof(LockStatus)))
                Assert.That(DbEnum.ParseStatus(DbEnum.ToDb(v)), Is.EqualTo(v));
            foreach (LockSource v in Enum.GetValues(typeof(LockSource)))
                Assert.That(DbEnum.ParseSource(DbEnum.ToDb(v)), Is.EqualTo(v));
            foreach (EnforceState v in Enum.GetValues(typeof(EnforceState)))
                Assert.That(DbEnum.ParseEnforceState(DbEnum.ToDb(v)), Is.EqualTo(v));
            foreach (Lanes v in Enum.GetValues(typeof(Lanes)))
                Assert.That(DbEnum.ParseLanes(DbEnum.ToDb(v)), Is.EqualTo(v));
            foreach (Role v in Enum.GetValues(typeof(Role)))
                Assert.That(DbEnum.ParseRole(DbEnum.ToDb(v)), Is.EqualTo(v));
        }

        [Test]
        public void Special_names()
        {
            Assert.That(DbEnum.ToDb(EnforceState.NotNeeded), Is.EqualTo("NOT_NEEDED"));
            Assert.That(DbEnum.ToDb(LockSource.SetupCenter), Is.EqualTo("SETUPCENTER"));
            Assert.That(DbEnum.ToDb(LockType.Pu), Is.EqualTo("PU"));
        }

        [Test]
        public void Parse_is_case_insensitive()
        {
            Assert.That(DbEnum.ParseLanes("both"), Is.EqualTo(Lanes.Both));
        }

        [Test]
        public void Parse_unknown_throws()
        {
            Assert.Throws<FormatException>(() => DbEnum.ParseLockType("REEL"));
        }
    }
}
