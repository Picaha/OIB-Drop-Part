using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Interlock.Core.Enforcement;
using Interlock.Core.Locks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Text;
using Interlock.Service.ExternalControl;
using Interlock.Tests.Fakes;
using NUnit.Framework;
using www.siplace.com.OIB._2008._05.SetupCenter.Contracts.Data;
using www.siplace.com.OIB._2008._05.SetupCenter.Contracts.Service;

namespace Interlock.Tests.Service
{
    /// <summary>Test data shared by the External Control fixtures.</summary>
    internal static class ExternalControlData
    {
        public static readonly DateTime T0 = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);

        public static LockRecord L(long id, LockType type, string target, string line = null)
        {
            return new LockRecord
            {
                Id = id,
                Type = type,
                Target = target,
                TargetNorm = Normalizer.Norm(target),
                LinePath = line,
                Reason = "r" + id,
                Status = LockStatus.Active
            };
        }

        public static PackagingUnitLocation Loc(string uid, string component, string line = "Lines\\L1", string machine = "M1", string mpn = null, string supplier = null)
        {
            return new PackagingUnitLocation
            {
                PackagingUnit = new PackagingUnit { UID = uid, ComponentName = component, ManufacturePartNumber = mpn, Supplier = supplier },
                LinePath = line,
                MachineId = machine
            };
        }

        public static FeederLocation Fl(string feederId, int track = 1, string machine = "M1")
        {
            return new FeederLocation
            {
                FeederId = feederId,
                Track = track,
                Location = 7,
                TableId = "T-9",
                Machine = new MachineLocator { MachineId = machine, LinePath = "Lines\\L1" }
            };
        }

        /// <summary>A clock that fails, to drive the services into their unexpected-exception path.</summary>
        public sealed class ThrowingClock : IClock
        {
            public DateTime UtcNow
            {
                get { throw new InvalidOperationException("clock down"); }
            }
        }
    }

    [TestFixture]
    public class ExternalControlTests
    {
        private LockCache cache;
        private ListLog log;
        private FakeClock clock;
        private DecisionLogBuffer decisions;
        private SetupCenterExternalControlService sc;
        private FeederExternalControlService feeder;
        private readonly DateTime t0 = ExternalControlData.T0;

        private static LockRecord L(long id, LockType type, string target, string line = null) => ExternalControlData.L(id, type, target, line);

        private static PackagingUnitLocation Loc(string uid, string component, string line = "Lines\\L1", string machine = "M1", string mpn = null, string supplier = null) =>
            ExternalControlData.Loc(uid, component, line, machine, mpn, supplier);

        private static FeederLocation Fl(string feederId, int track = 1, string machine = "M1") => ExternalControlData.Fl(feederId, track, machine);

        [SetUp]
        public void SetUp()
        {
            cache = new LockCache();
            log = new ListLog();
            clock = new FakeClock { UtcNow = t0 };
            decisions = new DecisionLogBuffer(1000, log);
            sc = new SetupCenterExternalControlService(cache, decisions, clock, log);
            feeder = new FeederExternalControlService(cache, decisions, clock, log);
        }

        private IList<DecisionLogEntry> TakeDecisions()
        {
            var audit = new InMemoryAuditRepository();
            decisions.Flush(audit);
            return audit.Decisions;
        }

        // ---- Setup Center: PU scan check ------------------------------------------------------------------------------

        [Test]
        public void Locked_uid_is_not_ok_with_message()
        {
            cache.Replace(new[] { L(5, LockType.Pu, "U1") }, t0);

            var r = sc.GetPackagingUnitControlStatus(new[] { Loc("U1", "C1") }).Single();

            Assert.That(r.ResultState, Is.EqualTo(2));
            Assert.That(r.Messages.Single().Message, Is.EqualTo("Interlock: PU U1 locked – r5 (#5)"));
        }

        [Test]
        public void Batch_with_null_pu_and_blank_uid_does_not_throw()
        {
            var r = sc.GetPackagingUnitControlStatus(new[] { new PackagingUnitLocation(), Loc(" ", "C1"), Loc("U2", "C1") });

            Assert.That(r.Select(x => x.ResultState), Is.EqualTo(new[] { 1, 1, 1 }));
        }

        [Test]
        public void Extended_logs_reason_code()
        {
            sc.GetPackagingUnitControlStatusExtended(new GetPackagingUnitControlStatusExtendedRequest { Reason = 2, PackagingUnitLocations = new[] { Loc("U1", "C1") } });

            Assert.That(TakeDecisions().Single().ReasonCode, Is.EqualTo(2));
        }

        [Test]
        public void New_pu_data_is_unknown() =>
            Assert.That(sc.GetNewPackagingUnitData(new[] { Loc("U9", "C") }).Single().ResultState, Is.EqualTo(0));

        [Test]
        public void Feeder_status_lists_only_locked_feeders_with_copied_fields()
        {
            cache.Replace(new[] { L(3, LockType.Feeder, "F1") }, t0);

            var resp = feeder.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest { FeederLocations = new[] { Fl("F1", track: 4), Fl("F2", track: 5) } });

            var info = resp.FeederMaintenanceStatusInfos.Single();
            Assert.That(new object[] { info.FeederId, info.Track, info.ResultState }, Is.EqualTo(new object[] { "F1", 4, 1 }));
            StringAssert.Contains("(#3)", info.Reason);
        }

        [Test]
        public void Services_have_no_setup_center_dependency() =>
            Assert.That(typeof(SetupCenterExternalControlService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType), Has.None.EqualTo(typeof(ISetupCenterClient)));

        [Test]
        public void Feeder_service_has_no_setup_center_dependency() =>
            Assert.That(typeof(FeederExternalControlService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType), Has.None.EqualTo(typeof(ISetupCenterClient)));

        [Test]
        public void Unlocked_uid_is_ok_without_messages_and_echoes_the_pu()
        {
            var loc = Loc("U1", "C1");

            var r = sc.GetPackagingUnitControlStatus(new[] { loc }).Single();

            Assert.That(r.ResultState, Is.EqualTo(1));
            Assert.That(r.Messages, Is.Null);
            Assert.That(r.PackagingUnit, Is.SameAs(loc.PackagingUnit));
        }

        [Test]
        public void Locked_uid_echoes_the_pu_untouched()
        {
            cache.Replace(new[] { L(5, LockType.Pu, "U1") }, t0);
            var loc = Loc("U1", "C1", mpn: "M-1", supplier: "S-1");

            var r = sc.GetPackagingUnitControlStatus(new[] { loc }).Single();

            Assert.That(r.PackagingUnit, Is.SameAs(loc.PackagingUnit));
            Assert.That(new[] { r.PackagingUnit.UID, r.PackagingUnit.ComponentName, r.PackagingUnit.ManufacturePartNumber, r.PackagingUnit.Supplier },
                Is.EqualTo(new[] { "U1", "C1", "M-1", "S-1" }));
        }

        [Test]
        public void Several_matching_locks_give_one_message_joined_in_id_order()
        {
            cache.Replace(new[] { L(9, LockType.Pn, "C1"), L(3, LockType.Pu, "U1") }, t0);

            var r = sc.GetPackagingUnitControlStatus(new[] { Loc("U1", "C1") }).Single();

            Assert.That(r.ResultState, Is.EqualTo(2));
            Assert.That(r.Messages.Select(m => m.Message), Is.EqualTo(new[] { "Interlock: PU U1 locked – r3 (#3); Interlock: PN C1 locked – r9 (#9)" }));
        }

        [Test]
        public void Pn_mpn_and_supplier_locks_are_checked_from_the_pu_fields()
        {
            cache.Replace(new[] { L(1, LockType.Pn, "C-PN"), L(2, LockType.Mpn, "MPN-X"), L(3, LockType.Supplier, "SUP-Y") }, t0);

            var r = sc.GetPackagingUnitControlStatus(new[]
            {
                Loc("U1", " c-pn "),
                Loc("U2", "other", mpn: "mpn-x"),
                Loc("U3", "other", supplier: "sup-y"),
                Loc("U4", "other", mpn: "other", supplier: "other")
            });

            Assert.That(r.Select(x => x.ResultState), Is.EqualTo(new[] { 2, 2, 2, 1 }));
        }

        [Test]
        public void Line_path_of_the_location_scopes_the_lock_and_a_blank_one_matches_every_line()
        {
            cache.Replace(new[] { L(1, LockType.Pn, "C1", "Lines\\L1") }, t0);

            var r = sc.GetPackagingUnitControlStatus(new[]
            {
                Loc("U1", "C1", line: "lines\\l1"),
                Loc("U2", "C1", line: "Lines\\L2"),
                Loc("U3", "C1", line: ""),
                Loc("U4", "C1", line: null)
            });

            Assert.That(r.Select(x => x.ResultState), Is.EqualTo(new[] { 2, 1, 2, 2 }));
        }

        [Test]
        public void Invalid_locations_get_ok_without_messages_and_echo_the_pu_even_when_a_lock_would_match()
        {
            cache.Replace(new[] { L(1, LockType.Pn, "C1"), L(2, LockType.Pu, "U1") }, t0);
            var noPu = new PackagingUnitLocation { LinePath = "Lines\\L1" };
            var blankUid = Loc("  ", "C1");
            var nullUid = Loc(null, "C1");

            var r = sc.GetPackagingUnitControlStatus(new[] { noPu, blankUid, nullUid });

            Assert.That(r.Select(x => x.ResultState), Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(r.Select(x => x.Messages), Is.All.Null);
            Assert.That(r[0].PackagingUnit, Is.Null);
            Assert.That(r[1].PackagingUnit, Is.SameAs(blankUid.PackagingUnit));
            Assert.That(r[2].PackagingUnit, Is.SameAs(nullUid.PackagingUnit));
        }

        [Test]
        public void Null_location_gets_ok_in_its_slot()
        {
            var r = sc.GetPackagingUnitControlStatus(new[] { null, Loc("U1", "C1") });

            Assert.That(r.Select(x => x.ResultState), Is.EqualTo(new[] { 1, 1 }));
            Assert.That(r[0].PackagingUnit, Is.Null);
            Assert.That(r[0].Messages, Is.Null);
        }

        [Test]
        public void Results_keep_the_length_and_order_of_the_request()
        {
            cache.Replace(new[] { L(2, LockType.Pu, "U2"), L(4, LockType.Pu, "U4") }, t0);
            var locs = new[] { Loc("U1", "C"), Loc("U2", "C"), Loc("U3", "C"), Loc("U4", "C"), Loc("U5", "C") };

            var r = sc.GetPackagingUnitControlStatus(locs);

            Assert.That(r.Select(x => x.ResultState), Is.EqualTo(new[] { 1, 2, 1, 2, 1 }));
            Assert.That(r.Select(x => x.PackagingUnit), Is.EqualTo(locs.Select(l => l.PackagingUnit)));
            StringAssert.Contains("(#2)", r[1].Messages.Single().Message);
            StringAssert.Contains("(#4)", r[3].Messages.Single().Message);
        }

        [Test]
        public void Null_request_arrays_give_empty_non_null_arrays()
        {
            Assert.That(sc.GetPackagingUnitControlStatus(null), Is.Empty);
            Assert.That(sc.GetNewPackagingUnitData(null), Is.Empty);
            Assert.That(sc.GetPackagingUnitControlStatusExtended(null).ExternalControlResults, Is.Empty);
            Assert.That(sc.GetPackagingUnitControlStatusExtended(new GetPackagingUnitControlStatusExtendedRequest()).ExternalControlResults, Is.Empty);
            Assert.That(TakeDecisions(), Is.Empty);
        }

        [Test]
        public void Extended_answers_like_the_plain_call_and_wraps_the_results()
        {
            cache.Replace(new[] { L(5, LockType.Pu, "U1") }, t0);

            var resp = sc.GetPackagingUnitControlStatusExtended(new GetPackagingUnitControlStatusExtendedRequest
            {
                Reason = 2,
                PackagingUnitLocations = new[] { Loc("U1", "C1"), Loc("U2", "C1") }
            });

            Assert.That(resp.ExternalControlResults.Select(x => x.ResultState), Is.EqualTo(new[] { 2, 1 }));
            Assert.That(resp.ExternalControlResults[0].Messages.Single().Message, Is.EqualTo("Interlock: PU U1 locked – r5 (#5)"));
        }

        [Test]
        public void Plain_call_logs_no_reason_code()
        {
            sc.GetPackagingUnitControlStatus(new[] { Loc("U1", "C1") });

            Assert.That(TakeDecisions().Single().ReasonCode, Is.Null);
        }

        [Test]
        public void New_pu_data_has_no_pu_and_is_unknown_even_for_a_locked_uid()
        {
            cache.Replace(new[] { L(5, LockType.Pu, "U9") }, t0);

            var r = sc.GetNewPackagingUnitData(new[] { Loc("U9", "C"), null, new PackagingUnitLocation() });

            Assert.That(r.Select(x => x.ResultState), Is.EqualTo(new[] { 0, 0, 0 }));
            Assert.That(r.Select(x => x.PackagingUnit), Is.All.Null);
            Assert.That(r.Select(x => x.Messages), Is.All.Null);
        }

        [Test]
        public void Ping_answers_true() => Assert.That(sc.Ping(), Is.True);

        [Test]
        public void Components_status_and_tray_carrier_calls_answer_empty_responses()
        {
            Assert.That(sc.GetComponentsStatus(null), Is.Not.Null);
            Assert.That(sc.GetComponentsStatus(new ComponentStatusRequest()), Is.Not.Null);
            Assert.That(sc.GetTrayCarrierMappingData(null), Is.Not.Null);
            Assert.That(sc.GetTrayCarrierMappingData(new TrayCarrierMappingDataRequest()), Is.Not.Null);
            Assert.That(sc.UpdateTrayCarrierMappingData(null), Is.Not.Null);
            Assert.That(sc.UpdateTrayCarrierMappingData(new UpdateTrayCarrierMappingDataRequest()), Is.Not.Null);
            Assert.That(TakeDecisions(), Is.Empty);
        }

        // ---- decision log ---------------------------------------------------------------------------------------------

        [Test]
        public void Every_location_writes_one_decision_row_with_the_answer_given()
        {
            cache.Replace(new[] { L(5, LockType.Pn, "C1"), L(3, LockType.Pu, "U1") }, t0);
            clock.UtcNow = t0.AddMinutes(7);

            sc.GetPackagingUnitControlStatus(new[]
            {
                Loc("U1", "C1", line: "Lines\\L7", machine: "M7", mpn: "MPN-1", supplier: "SUP-1"),
                Loc("U2", "C2", mpn: "MPN-2"),
                new PackagingUnitLocation { MachineId = "M3", LinePath = "Lines\\L3" },
                null
            });

            var rows = TakeDecisions();
            Assert.That(rows.Count, Is.EqualTo(4));
            Assert.That(rows.Select(r => r.Method), Is.All.EqualTo("GetPackagingUnitControlStatus"));
            Assert.That(rows.Select(r => r.ReasonCode), Is.All.Null);
            Assert.That(rows.Select(r => r.FeederId), Is.All.Null);
            Assert.That(rows.Select(r => r.CreatedAtUtc), Is.All.EqualTo(t0.AddMinutes(7)));

            var blocked = rows[0];
            Assert.That(new[] { blocked.Uid, blocked.Pn, blocked.Mpn, blocked.Supplier, blocked.LinePath, blocked.MachineId },
                Is.EqualTo(new[] { "U1", "C1", "MPN-1", "SUP-1", "Lines\\L7", "M7" }));
            Assert.That(blocked.Result, Is.EqualTo("NOT_OK"));
            Assert.That(blocked.Message, Is.EqualTo("Interlock: PU U1 locked – r3 (#3); Interlock: PN C1 locked – r5 (#5)"));
            Assert.That(blocked.MatchedLockIds, Is.EqualTo("3,5"));

            var clear = rows[1];
            Assert.That(new[] { clear.Uid, clear.Pn, clear.Mpn, clear.Supplier, clear.LinePath, clear.MachineId },
                Is.EqualTo(new[] { "U2", "C2", "MPN-2", null, "Lines\\L1", "M1" }));
            Assert.That(clear.Result, Is.EqualTo("OK"));
            Assert.That(clear.Message, Is.Null);
            Assert.That(clear.MatchedLockIds, Is.Null);

            var noPu = rows[2];
            Assert.That(noPu.Result, Is.EqualTo("OK"));
            Assert.That(new[] { noPu.Uid, noPu.Pn, noPu.Mpn, noPu.Supplier }, Is.All.Null);
            Assert.That(new[] { noPu.LinePath, noPu.MachineId }, Is.EqualTo(new[] { "Lines\\L3", "M3" }));

            var nullLocation = rows[3];
            Assert.That(nullLocation.Result, Is.EqualTo("OK"));
            Assert.That(new[] { nullLocation.Uid, nullLocation.MachineId, nullLocation.LinePath }, Is.All.Null);
        }

        [Test]
        public void Extended_rows_carry_the_reason_code_and_the_method_name()
        {
            sc.GetPackagingUnitControlStatusExtended(new GetPackagingUnitControlStatusExtendedRequest
            {
                Reason = 4,
                PackagingUnitLocations = new[] { Loc("U1", "C1"), Loc("U2", "C1") }
            });

            var rows = TakeDecisions();
            Assert.That(rows.Select(r => r.Method), Is.All.EqualTo("GetPackagingUnitControlStatusExtended"));
            Assert.That(rows.Select(r => r.ReasonCode), Is.All.EqualTo(4));
        }

        [Test]
        public void New_pu_data_rows_are_unknown()
        {
            sc.GetNewPackagingUnitData(new[] { Loc("U9", "C9", machine: "M9") });

            var row = TakeDecisions().Single();
            Assert.That(row.Method, Is.EqualTo("GetNewPackagingUnitData"));
            Assert.That(row.Result, Is.EqualTo("UNKNOWN"));
            Assert.That(new[] { row.Uid, row.Pn, row.MachineId }, Is.EqualTo(new[] { "U9", "C9", "M9" }));
            Assert.That(row.Message, Is.Null);
            Assert.That(row.MatchedLockIds, Is.Null);
        }

        // ---- Feeder External Control ----------------------------------------------------------------------------------

        [Test]
        public void Feeder_ping_answers()
        {
            Assert.That(feeder.FeederExternalControlPing(new PingRequest()), Is.Not.Null);
            Assert.That(feeder.FeederExternalControlPing(null), Is.Not.Null);
        }

        [Test]
        public void Unlocked_feeders_get_no_entry_and_the_array_is_never_null()
        {
            cache.Replace(new[] { L(3, LockType.Feeder, "F9") }, t0);

            var resp = feeder.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest { FeederLocations = new[] { Fl("F1"), Fl("F2") } });

            Assert.That(resp.FeederMaintenanceStatusInfos, Is.Not.Null);
            Assert.That(resp.FeederMaintenanceStatusInfos, Is.Empty);
        }

        [Test]
        public void Missing_request_or_feeder_array_gives_an_empty_response()
        {
            Assert.That(feeder.GetFeederMaintenanceStatus(null).FeederMaintenanceStatusInfos, Is.Empty);
            Assert.That(feeder.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest()).FeederMaintenanceStatusInfos, Is.Empty);
            Assert.That(TakeDecisions(), Is.Empty);
        }

        [Test]
        public void Null_feeder_location_is_skipped()
        {
            cache.Replace(new[] { L(3, LockType.Feeder, "F1") }, t0);

            var resp = feeder.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest { FeederLocations = new[] { null, Fl("F1") } });

            Assert.That(resp.FeederMaintenanceStatusInfos.Select(i => i.FeederId), Is.EqualTo(new[] { "F1" }));
            Assert.That(TakeDecisions().Count, Is.EqualTo(1));
        }

        [Test]
        public void Locked_feeders_are_listed_in_request_order_matched_ignoring_case_and_whitespace()
        {
            cache.Replace(new[] { L(8, LockType.Feeder, "f-02"), L(3, LockType.Feeder, "F-01"), L(6, LockType.Pu, "F-03") }, t0);

            var resp = feeder.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest
            {
                FeederLocations = new[] { Fl(" F-02 "), Fl("F-03"), Fl("F-01"), Fl(null), Fl(" ") }
            });

            var infos = resp.FeederMaintenanceStatusInfos;
            Assert.That(infos.Select(i => i.FeederId), Is.EqualTo(new[] { " F-02 ", "F-01" }));
            Assert.That(infos.Select(i => i.ResultState), Is.All.EqualTo(1));
            Assert.That(infos[0].Reason, Is.EqualTo("Interlock: FEEDER f-02 locked – r8 (#8)"));
            Assert.That(infos[1].Reason, Is.EqualTo("Interlock: FEEDER F-01 locked – r3 (#3)"));
        }

        [Test]
        public void Locked_feeder_entry_copies_the_whole_location()
        {
            cache.Replace(new[] { L(3, LockType.Feeder, "F1") }, t0);
            var location = Fl("F1", track: 4, machine: "M5");
            location.FeederType = 12;
            location.CartridgeId = "CART";

            var info = feeder.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest { FeederLocations = new[] { location } }).FeederMaintenanceStatusInfos.Single();

            Assert.That(new object[] { info.FeederId, info.Track, info.Location, info.TableId, info.FeederType, info.CartridgeId },
                Is.EqualTo(new object[] { "F1", 4, 7, "T-9", 12, "CART" }));
            Assert.That(info.Machine, Is.SameAs(location.Machine));
            Assert.That(info.Machine.MachineId, Is.EqualTo("M5"));
        }

        [Test]
        public void Every_feeder_writes_one_decision_row()
        {
            cache.Replace(new[] { L(3, LockType.Feeder, "F1") }, t0);
            clock.UtcNow = t0.AddSeconds(30);

            feeder.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest { FeederLocations = new[] { Fl("F1", machine: "M5"), Fl("F2", machine: "M6") } });

            var rows = TakeDecisions();
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(rows.Select(r => r.Method), Is.All.EqualTo("GetFeederMaintenanceStatus"));
            Assert.That(rows.Select(r => r.ReasonCode), Is.All.Null);
            Assert.That(rows.Select(r => r.CreatedAtUtc), Is.All.EqualTo(t0.AddSeconds(30)));
            Assert.That(rows.Select(r => r.Uid), Is.All.Null);

            Assert.That(new[] { rows[0].FeederId, rows[0].MachineId, rows[0].Result, rows[0].MatchedLockIds, rows[0].Message },
                Is.EqualTo(new[] { "F1", "M5", "NOT_OK", "3", "Interlock: FEEDER F1 locked – r3 (#3)" }));
            Assert.That(new[] { rows[1].FeederId, rows[1].MachineId, rows[1].Result, rows[1].MatchedLockIds, rows[1].Message },
                Is.EqualTo(new[] { "F2", "M6", "OK", null, null }));
        }

        // ---- mapper ---------------------------------------------------------------------------------------------------

        [Test]
        public void To_pu_info_maps_the_pu_fields_and_the_line_path_of_the_location()
        {
            var loc = Loc("U1", "C1", line: "Lines\\L4", mpn: "MPN-1", supplier: "SUP-1");

            var pu = ExternalControlMapper.ToPuInfo(loc);

            Assert.That(new[] { pu.Uid, pu.ComponentName, pu.Mpn, pu.Supplier, pu.LinePath }, Is.EqualTo(new[] { "U1", "C1", "MPN-1", "SUP-1", "Lines\\L4" }));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        public void To_pu_info_keeps_a_blank_line_path_blank(string line) =>
            Assert.That(ExternalControlMapper.ToPuInfo(Loc("U1", "C1", line: line)).LinePath, Is.EqualTo(line));

        [Test]
        public void To_pu_info_without_a_pu_maps_only_the_line_path()
        {
            var pu = ExternalControlMapper.ToPuInfo(new PackagingUnitLocation { LinePath = "Lines\\L4" });

            Assert.That(new[] { pu.Uid, pu.ComponentName, pu.Mpn, pu.Supplier }, Is.All.Null);
            Assert.That(pu.LinePath, Is.EqualTo("Lines\\L4"));
        }

        [Test]
        public void To_pu_info_of_null_is_rejected() =>
            Assert.Throws<ArgumentNullException>(() => ExternalControlMapper.ToPuInfo(null));

        [Test]
        public void To_blocked_copies_every_property_of_the_feeder_location_and_its_base_types()
        {
            var src = new FeederLocation();
            var expected = new Dictionary<PropertyInfo, object>();
            var seed = 1;
            foreach (var p in WritableProperties(typeof(FeederLocation)))
            {
                var value = SampleValue(p.PropertyType, p.Name, seed++);
                if (value == null)
                    continue; // ExtensionData: the runtime type has no public constructor
                p.SetValue(src, value, null);
                expected[p] = value;
            }
            Assert.That(expected.Count, Is.GreaterThan(10), "the sample must cover FeederLocation and TableLocation");

            var info = ExternalControlMapper.ToBlocked(src, "because");

            Assert.That(info, Is.InstanceOf<FeederMaintenanceStatusInfo>());
            foreach (var pair in expected)
            {
                var actual = pair.Key.GetValue(info, null);
                if (pair.Key.PropertyType.IsValueType || pair.Key.PropertyType == typeof(string))
                    Assert.That(actual, Is.EqualTo(pair.Value), pair.Key.DeclaringType.Name + "." + pair.Key.Name);
                else
                    Assert.That(actual, Is.SameAs(pair.Value), pair.Key.DeclaringType.Name + "." + pair.Key.Name);
            }
            Assert.That(info.Reason, Is.EqualTo("because"));
            Assert.That(info.ResultState, Is.EqualTo(1));
        }

        [Test]
        public void To_blocked_covers_the_properties_the_ok_tutorial_copies_by_hand()
        {
            var machine = new MachineLocator { MachineId = "M1" };
            var divisions = new[] { new Division { DivisionNumber = 2 } };
            var src = new FeederLocation
            {
                DockingStation = new DockingStation { DockingStationId = "D1" },
                Location = 3,
                Machine = machine,
                TableId = "T",
                TableState = 5,
                Device = 6,
                FeederId = "F1",
                FeederType = 7,
                Track = 8,
                FeederTypeSpiOidLong = 9L,
                Divisions = divisions
            };

            var info = ExternalControlMapper.ToBlocked(src, "r");

            Assert.That(info.DockingStation, Is.SameAs(src.DockingStation));
            Assert.That(new object[] { info.Location, info.TableId, info.TableState, info.Device, info.FeederId, info.FeederType, info.Track, info.FeederTypeSpiOidLong },
                Is.EqualTo(new object[] { 3, "T", 5, 6, "F1", 7, 8, 9L }));
            Assert.That(info.Machine, Is.SameAs(machine));
            Assert.That(info.Divisions, Is.SameAs(divisions));
        }

        [Test]
        public void To_blocked_of_null_is_rejected() =>
            Assert.Throws<ArgumentNullException>(() => ExternalControlMapper.ToBlocked(null, "r"));

        private static IEnumerable<PropertyInfo> WritableProperties(Type from)
        {
            for (var t = from; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                        yield return p;
                }
            }
        }

        private static object SampleValue(Type type, string name, int seed)
        {
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null)
                return SampleValue(nullable, name, seed);
            if (type == typeof(int)) return seed;
            if (type == typeof(long)) return (long)seed;
            if (type == typeof(bool)) return true;
            if (type == typeof(string)) return name + seed;
            if (type.IsArray)
            {
                var array = Array.CreateInstance(type.GetElementType(), 1);
                array.SetValue(Activator.CreateInstance(type.GetElementType()), 0);
                return array;
            }
            if (type.GetConstructor(Type.EmptyTypes) != null)
                return Activator.CreateInstance(type);
            return null;
        }

        // ---- failures and attributes ----------------------------------------------------------------------------------

        [Test]
        public void Unexpected_failure_is_logged_and_becomes_a_fault_with_error_code_20000()
        {
            var badClock = new ExternalControlData.ThrowingClock();
            var badSc = new SetupCenterExternalControlService(cache, decisions, badClock, log);
            var badFeeder = new FeederExternalControlService(cache, decisions, badClock, log);
            var calls = new Dictionary<string, TestDelegate>
            {
                { "GetPackagingUnitControlStatus", () => badSc.GetPackagingUnitControlStatus(new[] { Loc("U1", "C1") }) },
                { "GetPackagingUnitControlStatusExtended", () => badSc.GetPackagingUnitControlStatusExtended(new GetPackagingUnitControlStatusExtendedRequest { PackagingUnitLocations = new[] { Loc("U1", "C1") } }) },
                { "GetNewPackagingUnitData", () => badSc.GetNewPackagingUnitData(new[] { Loc("U1", "C1") }) },
                { "GetFeederMaintenanceStatus", () => badFeeder.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest { FeederLocations = new[] { Fl("F1") } }) }
            };

            foreach (var call in calls)
            {
                var fault = Assert.Throws<FaultException<SiplaceSetupCenterFault>>(call.Value, call.Key);

                Assert.That(fault.Detail.ErrorCode, Is.EqualTo(20000), call.Key);
                Assert.That(fault.Detail.ExtendedMessage, Is.EqualTo(call.Key + " failed: clock down"), call.Key);
                Assert.That(fault.Reason.GetMatchingTranslation().Text, Is.EqualTo("clock down"), call.Key);
            }

            Assert.That(log.Errors.Count, Is.EqualTo(4));
            Assert.That(calls.Keys.All(k => log.Errors.Any(e => e.Contains(k))), Is.True);
            Assert.That(TakeDecisions(), Is.Empty);
        }

        [TestCase(typeof(SetupCenterExternalControlService))]
        [TestCase(typeof(FeederExternalControlService))]
        public void Services_are_free_threaded_singletons(Type service)
        {
            var behavior = service.GetCustomAttribute<ServiceBehaviorAttribute>();

            Assert.That(behavior, Is.Not.Null);
            Assert.That(behavior.InstanceContextMode, Is.EqualTo(InstanceContextMode.Single));
            Assert.That(behavior.ConcurrencyMode, Is.EqualTo(ConcurrencyMode.Multiple));
            Assert.That(behavior.UseSynchronizationContext, Is.False);
        }

        [Test]
        public void Concurrent_scan_checks_stay_consistent_while_the_cache_is_replaced()
        {
            var locked = new[] { L(5, LockType.Pu, "U1") };
            var clear = new LockRecord[0];
            var errors = new ConcurrentBag<string>();
            var stop = false;

            var writer = Task.Run(() =>
            {
                var flip = false;
                while (!Volatile.Read(ref stop))
                {
                    cache.Replace(flip ? locked : clear, t0);
                    flip = !flip;
                }
            });
            var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 300; i++)
                {
                    try
                    {
                        var r = sc.GetPackagingUnitControlStatus(new[] { Loc("U1", "C1") }).Single();
                        var fb = feeder.GetFeederMaintenanceStatus(new GetFeederMaintenanceStatusRequest { FeederLocations = new[] { Fl("F1") } });
                        var blocked = r.ResultState == 2;
                        if ((!blocked && r.ResultState != 1) || blocked != (r.Messages != null))
                            errors.Add("inconsistent result " + r.ResultState);
                        if (fb.FeederMaintenanceStatusInfos.Length != 0)
                            errors.Add("a feeder was blocked");
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex.ToString());
                    }
                }
            })).ToArray();

            Task.WaitAll(readers);
            Volatile.Write(ref stop, true);
            writer.Wait();

            Assert.That(errors, Is.Empty);
            Assert.That(log.Errors, Is.Empty);
        }
    }
}
