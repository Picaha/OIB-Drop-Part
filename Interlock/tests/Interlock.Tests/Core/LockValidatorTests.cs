using System.Collections.Generic;
using Interlock.Core.Errors;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class LockValidatorTests
    {
        private const string Station = "Lines\\L1\\S1";

        private static IList<StationRef> Stations()
        {
            return new List<StationRef>
            {
                new StationRef { LinePath = "Lines\\L1", StationPath = Station },
                new StationRef { LinePath = "Lines\\L1", StationPath = "Lines\\L1\\S2" }
            };
        }

        private static CreateLockRequest Valid(LockType type)
        {
            var r = new CreateLockRequest { Type = type, Target = "T1", Reason = "why" };
            if (type == LockType.Conveyor)
            {
                r.Target = Station;
                r.Lanes = Lanes.Left;
            }
            return r;
        }

        private static InterlockException Rejected(CreateLockRequest r, IList<StationRef> stations = null)
        {
            return Assert.Throws<InterlockException>(() => LockValidator.Validate(r, stations ?? Stations()));
        }

        private static void AssertValidation(CreateLockRequest r, IList<StationRef> stations = null)
        {
            Assert.That(Rejected(r, stations).Kind, Is.EqualTo(ErrorKind.Validation));
        }

        [Test]
        public void Missing_type_rejected()
        {
            var r = Valid(LockType.Pu);
            r.Type = null;
            AssertValidation(r);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Blank_target_rejected(string target)
        {
            var r = Valid(LockType.Pu);
            r.Target = target;
            AssertValidation(r);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Blank_reason_rejected(string reason)
        {
            var r = Valid(LockType.Pu);
            r.Reason = reason;
            AssertValidation(r);
        }

        [Test]
        public void Conveyor_requires_lanes()
        {
            var r = Valid(LockType.Conveyor);
            r.Lanes = null;
            AssertValidation(r);
        }

        [TestCase(LockType.Feeder)]
        [TestCase(LockType.Pu)]
        [TestCase(LockType.Pn)]
        [TestCase(LockType.Mpn)]
        [TestCase(LockType.Supplier)]
        public void Lanes_rejected_for_non_conveyor(LockType type)
        {
            var r = Valid(type);
            r.Lanes = Lanes.Both;
            AssertValidation(r);
        }

        [TestCase(LockType.Feeder)]
        [TestCase(LockType.Pu)]
        [TestCase(LockType.Conveyor)]
        public void Line_path_rejected_for_types_that_do_not_scope_by_line(LockType type)
        {
            var r = Valid(type);
            r.LinePath = "Lines\\L1";
            AssertValidation(r);
        }

        [TestCase(LockType.Feeder)]
        [TestCase(LockType.Pu)]
        public void Blank_line_path_counts_as_not_set(LockType type)
        {
            var r = Valid(type);
            r.LinePath = "   ";
            Assert.DoesNotThrow(() => LockValidator.Validate(r, Stations()));
        }

        [Test]
        public void Conveyor_requires_known_station()
        {
            var r = Valid(LockType.Conveyor);
            r.Target = "Lines\\L1\\S9";
            AssertValidation(r);
        }

        [Test]
        public void Conveyor_with_empty_station_list_is_unavailable()
        {
            var ex = Rejected(Valid(LockType.Conveyor), new List<StationRef>());
            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Unavailable));
            Assert.That(ex.Message, Is.EqualTo("Station list not available"));
        }

        [Test]
        public void Conveyor_with_null_station_list_is_unavailable()
        {
            var ex = Assert.Throws<InterlockException>(() => LockValidator.Validate(Valid(LockType.Conveyor), null));
            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Unavailable));
        }

        [Test]
        public void Request_errors_are_reported_before_station_list_availability()
        {
            var r = Valid(LockType.Conveyor);
            r.Lanes = null;
            AssertValidation(r, new List<StationRef>());
        }

        [Test]
        public void Empty_station_list_does_not_affect_non_conveyor_types()
        {
            Assert.DoesNotThrow(() => LockValidator.Validate(Valid(LockType.Pu), new List<StationRef>()));
        }

        [Test]
        public void Valid_requests_pass()
        {
            var pn = Valid(LockType.Pn);
            pn.LinePath = "Lines\\L1";
            var supplier = Valid(LockType.Supplier);
            supplier.LinePath = "Lines\\L2";
            var mpn = Valid(LockType.Mpn);
            mpn.LinePath = "Lines\\L1";
            var conveyor = Valid(LockType.Conveyor);
            conveyor.Target = Station.ToUpperInvariant();
            conveyor.Lanes = Lanes.Both;

            Assert.DoesNotThrow(() => LockValidator.Validate(Valid(LockType.Pu), Stations()));
            Assert.DoesNotThrow(() => LockValidator.Validate(Valid(LockType.Feeder), Stations()));
            Assert.DoesNotThrow(() => LockValidator.Validate(Valid(LockType.Pn), Stations()));
            Assert.DoesNotThrow(() => LockValidator.Validate(pn, Stations()));
            Assert.DoesNotThrow(() => LockValidator.Validate(mpn, Stations()));
            Assert.DoesNotThrow(() => LockValidator.Validate(supplier, Stations()));
            Assert.DoesNotThrow(() => LockValidator.Validate(conveyor, Stations()));
        }
    }
}
