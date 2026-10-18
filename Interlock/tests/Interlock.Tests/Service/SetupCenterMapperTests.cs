// The tests build contract objects with members the OIB marks obsolete (Machines, LineName, PackagingUnitLocation.MachineId and
// LinePath, PackagingUnitLocationsOnFeeders) because the mapper still reads them.
#pragma warning disable CS0618
using System;
using System.Collections.Generic;
using System.Linq;
using Asm.As.Oib.SiplaceSetupCenter.Contracts.Data;
using Asm.As.Oib.SiplaceSetupCenter.Contracts.Message;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Service.Oib;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Service
{
    /// <summary>
    /// The pure SetupCenter mapper, run on real OIB contract objects, plus the reconnect backoff the brief tests here.
    /// </summary>
    [TestFixture]
    public class SetupCenterMapperTests
    {
        // ---- helpers ----

        private static ComponentLocationState State(string uid, string name = null, string linePath = null, string machineId = null)
        {
            return new ComponentLocationState
            {
                PackagingUnit = uid == null ? null : new PackagingUnit { UID = uid, ComponentName = name },
                LinePath = linePath,
                MachineId = machineId
            };
        }

        private static MachineSetup Setup(string machineId, params ComponentLocationState[] states)
        {
            return new MachineSetup
            {
                MachineLocator = new MachineLocator { MachineId = machineId },
                Tables = new List<Table> { new Table { ComponentLocationStates = states.ToList() } }
            };
        }

        private static PackagingUnitLocation FeederLocationOf(string feederId, string machineId = "m1", string lineName = "L1")
        {
            return new PackagingUnitLocation
            {
                ComponentLocation = new ComponentLocation
                {
                    FeederId = feederId,
                    FeederType = 7,
                    Track = 3,
                    Location = 2,
                    Device = 1,
                    Machine = new MachineLocator { MachineId = machineId, LineName = lineName }
                }
            };
        }

        private static GetPackagingUnitLocationsOnFeedersResponse OnFeeders(string key, PackagingUnitLocation loc)
        {
            return new GetPackagingUnitLocationsOnFeedersResponse
            {
                PackagingUnitLocationsOnFeeders = new Dictionary<string, PackagingUnitLocation> { { key, loc } }
            };
        }

        // ---- ToPuInfo ----

        [Test]
        public void Pu_info_maps_identity_fields()
        {
            var pu = new PackagingUnit { UID = "U1", ComponentName = "PN1", ManufacturePartNumber = "MPN1", Supplier = "Sup" };

            var info = SetupCenterMapper.ToPuInfo(pu, "Lines\\L1");

            Assert.That(
                new[] { info.Uid, info.ComponentName, info.Mpn, info.Supplier, info.LinePath },
                Is.EqualTo(new[] { "U1", "PN1", "MPN1", "Sup", "Lines\\L1" }));
        }

        [Test]
        public void Pu_info_requires_a_packaging_unit()
        {
            Assert.Throws<ArgumentNullException>(() => SetupCenterMapper.ToPuInfo(null, "Lines\\L1"));
        }

        // ---- ToMachines / ToLinePaths ----

        [Test]
        public void Machines_fall_back_to_line_path()
        {
            var c = new Configuration
            {
                LineConfigurations = new List<LineConfiguration>
                {
                    new LineConfiguration
                    {
                        LinePath = "Lines\\L1",
                        Machines = new List<MachineLocator> { new MachineLocator { MachineId = "m1", LinePath = "" } }
                    }
                }
            };

            var machines = SetupCenterMapper.ToMachines(c);

            Assert.That(machines.Single().MachineId, Is.EqualTo("m1"));
            Assert.That(machines.Single().LinePath, Is.EqualTo("Lines\\L1"));
        }

        [Test]
        public void Machines_keep_their_own_line_path_when_it_is_set()
        {
            var c = new Configuration
            {
                LineConfigurations = new List<LineConfiguration>
                {
                    new LineConfiguration
                    {
                        LinePath = "Lines\\L1",
                        Machines = new List<MachineLocator> { new MachineLocator { MachineId = "m1", LinePath = "Lines\\Other" } }
                    }
                }
            };

            Assert.That(SetupCenterMapper.ToMachines(c).Single().LinePath, Is.EqualTo("Lines\\Other"));
        }

        [Test]
        public void Machines_come_from_the_machine_configurations_first_then_the_old_list_once_each()
        {
            var c = new Configuration
            {
                LineConfigurations = new List<LineConfiguration>
                {
                    new LineConfiguration
                    {
                        LinePath = "Lines\\L1",
                        Machines = new List<MachineLocator>
                        {
                            new MachineLocator { MachineId = "m1", LinePath = "Lines\\Old" },
                            new MachineLocator { MachineId = "m3" }
                        },
                        MachineConfigurations = new List<MachineConfiguration>
                        {
                            null,
                            new MachineConfiguration { MachineLocator = null },
                            new MachineConfiguration { MachineLocator = new MachineLocator { MachineId = "m1", LinePath = "Lines\\Other" } },
                            new MachineConfiguration { MachineLocator = new MachineLocator { MachineId = "m2" } }
                        }
                    }
                }
            };

            var machines = SetupCenterMapper.ToMachines(c);

            Assert.That(
                machines.Select(m => m.MachineId + "@" + m.LinePath),
                Is.EqualTo(new[] { "m1@Lines\\Other", "m2@Lines\\L1", "m3@Lines\\L1" }),
                "the replacement member wins for a machine listed in both");
        }

        [Test]
        public void Machines_skip_blank_ids_and_tolerate_missing_parts()
        {
            var c = new Configuration
            {
                LineConfigurations = new List<LineConfiguration>
                {
                    null,
                    new LineConfiguration { LinePath = "Lines\\L0", Machines = null },
                    new LineConfiguration
                    {
                        LinePath = "Lines\\L1",
                        Machines = new List<MachineLocator>
                        {
                            null,
                            new MachineLocator { MachineId = " " },
                            new MachineLocator { MachineId = null },
                            new MachineLocator { MachineId = "m2" }
                        }
                    }
                }
            };

            Assert.That(SetupCenterMapper.ToMachines(c).Select(m => m.MachineId), Is.EqualTo(new[] { "m2" }));
            Assert.That(SetupCenterMapper.ToMachines(null), Is.Empty);
            Assert.That(SetupCenterMapper.ToMachines(new Configuration()), Is.Empty);
        }

        [Test]
        public void Line_paths_are_distinct_and_skip_blanks()
        {
            var c = new Configuration
            {
                LineConfigurations = new List<LineConfiguration>
                {
                    new LineConfiguration { LinePath = "Lines\\L1" },
                    null,
                    new LineConfiguration { LinePath = "lines\\l1" },
                    new LineConfiguration { LinePath = "  " },
                    new LineConfiguration { LinePath = null },
                    new LineConfiguration { LinePath = "Lines\\L2" }
                }
            };

            Assert.That(SetupCenterMapper.ToLinePaths(c), Is.EqualTo(new[] { "Lines\\L1", "Lines\\L2" }));
            Assert.That(SetupCenterMapper.ToLinePaths(null), Is.Empty);
        }

        // ---- ToMountedPus ----

        [Test]
        public void Mounted_pus_skip_empty_locations()
        {
            var setups = new Dictionary<string, MachineSetup>
            {
                ["m1"] = Setup("m1", State("A", "C1", "Lines\\L1"), State(null), State(" "))
            };

            var r = SetupCenterMapper.ToMountedPus(setups);

            Assert.That(r.Single().Pu.Uid, Is.EqualTo("A"));
            Assert.That(r.Single().Pu.LinePath, Is.EqualTo("Lines\\L1"));
            Assert.That(r.Single().MachineId, Is.EqualTo("m1"));
        }

        [Test]
        public void Mounted_pus_carry_the_part_data()
        {
            var state = State("A", "PN1", "Lines\\L1");
            state.PackagingUnit.ManufacturePartNumber = "MPN1";
            state.PackagingUnit.Supplier = "Sup";

            var pu = SetupCenterMapper.ToMountedPus(new Dictionary<string, MachineSetup> { ["m1"] = Setup("m1", state) }).Single().Pu;

            Assert.That(new[] { pu.ComponentName, pu.Mpn, pu.Supplier }, Is.EqualTo(new[] { "PN1", "MPN1", "Sup" }));
        }

        [Test]
        public void Mounted_pus_take_the_machine_from_the_state_else_the_dictionary_key()
        {
            var setups = new Dictionary<string, MachineSetup>
            {
                ["key1"] = Setup("key1", State("A", machineId: "stateMachine"), State("B", machineId: "  "), State("C"))
            };

            var r = SetupCenterMapper.ToMountedPus(setups);

            Assert.That(r.Select(p => p.Pu.Uid + "@" + p.MachineId), Is.EqualTo(new[] { "A@stateMachine", "B@key1", "C@key1" }));
        }

        [Test]
        public void Mounted_pus_prefer_the_machine_of_the_component_location_over_the_old_members()
        {
            var state = State("A", linePath: "Lines\\Old", machineId: "old");
            state.ComponentLocation = new ComponentLocation { Machine = new MachineLocator { MachineId = "m7", LinePath = "Lines\\L7" } };

            var r = SetupCenterMapper.ToMountedPus(new Dictionary<string, MachineSetup> { ["key"] = Setup("key", state) }).Single();

            Assert.That(r.MachineId, Is.EqualTo("m7"));
            Assert.That(r.Pu.LinePath, Is.EqualTo("Lines\\L7"));
        }

        [Test]
        public void Mounted_pus_use_the_old_members_for_what_the_component_location_leaves_blank()
        {
            var state = State("A", linePath: "Lines\\Old", machineId: "old");
            state.ComponentLocation = new ComponentLocation { Machine = new MachineLocator { MachineId = " ", LinePath = null } };

            var r = SetupCenterMapper.ToMountedPus(new Dictionary<string, MachineSetup> { ["key"] = Setup("key", state) }).Single();

            Assert.That(r.MachineId, Is.EqualTo("old"));
            Assert.That(r.Pu.LinePath, Is.EqualTo("Lines\\Old"));
        }

        [Test]
        public void Mounted_pus_tolerate_missing_setups_tables_and_states()
        {
            var setups = new Dictionary<string, MachineSetup>
            {
                ["m0"] = null,
                ["m1"] = new MachineSetup { Tables = null },
                ["m2"] = new MachineSetup { Tables = new List<Table> { null, new Table { ComponentLocationStates = null } } },
                ["m3"] = new MachineSetup { Tables = new List<Table> { new Table { ComponentLocationStates = new List<ComponentLocationState> { null, State("Z") } } } }
            };

            Assert.That(SetupCenterMapper.ToMountedPus(setups).Select(p => p.Pu.Uid), Is.EqualTo(new[] { "Z" }));
            Assert.That(SetupCenterMapper.ToMountedPus(null), Is.Empty);
        }

        // ---- ToFeederMount ----

        [Test]
        public void Feeder_mount_is_null_when_the_feeder_is_not_in_the_answer()
        {
            Assert.That(SetupCenterMapper.ToFeederMount("F1", null), Is.Null);
            Assert.That(SetupCenterMapper.ToFeederMount("F1", new GetPackagingUnitLocationsOnFeedersResponse()), Is.Null);
            Assert.That(SetupCenterMapper.ToFeederMount("F1", OnFeeders("F2", FeederLocationOf("F2"))), Is.Null);
            Assert.That(SetupCenterMapper.ToFeederMount("F1", OnFeeders("F1", null)), Is.Null);
            Assert.That(SetupCenterMapper.ToFeederMount(" ", OnFeeders("F1", FeederLocationOf("F1"))), Is.Null);
        }

        [Test]
        public void Feeder_mount_keeps_the_native_location_and_prefers_the_machine_of_its_component_location()
        {
            var loc = FeederLocationOf("F1");
            loc.MachineId = "mach";
            loc.LinePath = "Lines\\L1";

            var m = SetupCenterMapper.ToFeederMount("F1", OnFeeders("F1", loc));

            Assert.That(m.Native, Is.SameAs(loc));
            Assert.That(
                new[] { m.FeederId, m.MachineId, m.LinePath },
                Is.EqualTo(new[] { "F1", "m1", "Lines\\L1" }),
                "the machine id comes from the component location, the blank line path falls back to the old member");
        }

        [Test]
        public void Feeder_mount_prefers_the_location_list_and_takes_the_first_location_with_a_component_location()
        {
            var bare = new PackagingUnitLocation();
            var located = FeederLocationOf("F1");
            var response = new GetPackagingUnitLocationsOnFeedersResponse
            {
                PackagingUnitLocationListOnFeeders = new Dictionary<string, List<PackagingUnitLocation>>
                {
                    { "F1", new List<PackagingUnitLocation> { null, bare, located, FeederLocationOf("F1", "other") } }
                },
                PackagingUnitLocationsOnFeeders = new Dictionary<string, PackagingUnitLocation> { { "F1", FeederLocationOf("F1", "legacy") } }
            };

            var m = SetupCenterMapper.ToFeederMount("F1", response);

            Assert.That(m.Native, Is.SameAs(located));
            Assert.That(m.MachineId, Is.EqualTo("m1"));
        }

        [Test]
        public void Feeder_mount_uses_the_old_single_location_answer_when_the_list_has_nothing()
        {
            var legacy = FeederLocationOf("F1", "legacy");
            var response = new GetPackagingUnitLocationsOnFeedersResponse
            {
                PackagingUnitLocationListOnFeeders = new Dictionary<string, List<PackagingUnitLocation>> { { "F1", new List<PackagingUnitLocation>() } },
                PackagingUnitLocationsOnFeeders = new Dictionary<string, PackagingUnitLocation> { { "F1", legacy } }
            };

            Assert.That(SetupCenterMapper.ToFeederMount("F1", response).Native, Is.SameAs(legacy));
        }

        [Test]
        public void Feeder_mount_is_null_when_no_returned_location_has_a_component_location()
        {
            var bare = new PackagingUnitLocation();
            var onlyBare = new GetPackagingUnitLocationsOnFeedersResponse
            {
                PackagingUnitLocationListOnFeeders = new Dictionary<string, List<PackagingUnitLocation>> { { "F1", new List<PackagingUnitLocation> { null, bare } } },
                PackagingUnitLocationsOnFeeders = new Dictionary<string, PackagingUnitLocation> { { "F1", new PackagingUnitLocation() } }
            };

            Assert.That(SetupCenterMapper.ToFeederMount("F1", onlyBare), Is.Null);
            Assert.That(SetupCenterMapper.ToFeederMount("F1", OnFeeders("F1", new PackagingUnitLocation())), Is.Null, "the old single-location answer without a component location");
        }

        [Test]
        public void Feeder_mount_goes_on_to_the_old_answer_when_the_list_has_no_component_location()
        {
            var legacy = FeederLocationOf("F1", "legacy");
            var response = new GetPackagingUnitLocationsOnFeedersResponse
            {
                PackagingUnitLocationListOnFeeders = new Dictionary<string, List<PackagingUnitLocation>> { { "F1", new List<PackagingUnitLocation> { new PackagingUnitLocation() } } },
                PackagingUnitLocationsOnFeeders = new Dictionary<string, PackagingUnitLocation> { { "F1", legacy } }
            };

            Assert.That(SetupCenterMapper.ToFeederMount("F1", response).Native, Is.SameAs(legacy));
        }

        [Test]
        public void Feeder_mount_falls_back_to_the_component_location_machine()
        {
            var loc = FeederLocationOf("F1", "m9");
            loc.ComponentLocation.Machine.LinePath = "Lines\\L9";

            var m = SetupCenterMapper.ToFeederMount("F1", OnFeeders("F1", loc));

            Assert.That(new[] { m.MachineId, m.LinePath }, Is.EqualTo(new[] { "m9", "Lines\\L9" }));
        }

        [Test]
        public void Feeder_mount_finds_the_feeder_when_the_answer_differs_in_case_or_padding()
        {
            var m = SetupCenterMapper.ToFeederMount(" f1 ", OnFeeders("F1", FeederLocationOf("F1")));

            Assert.That(m, Is.Not.Null);
            Assert.That(m.FeederId, Is.EqualTo("f1"));
        }

        // ---- ToLockInfo / ToFeederLocation ----

        [Test]
        public void Lock_info_copies_location_fields()
        {
            var loc = new PackagingUnitLocation { ComponentLocation = new ComponentLocation { FeederId = "F1", FeederType = 7, Track = 3, Location = 2, Device = 1, Machine = new MachineLocator { MachineId = "m1", LineName = "L1" } } };

            var info = SetupCenterMapper.ToLockInfo(new FeederMount { FeederId = "F1", Native = loc }, "bad feeder");

            Assert.That(
                new object[] { info.FeederId, info.FeederType, info.Track, info.Location, info.Device, info.Machine.MachineId, info.Reason },
                Is.EqualTo(new object[] { "F1", 7, 3, 2, 1, "m1", "bad feeder" }));
        }

        [Test]
        public void Lock_info_copies_the_other_shared_properties_and_the_machine_fields()
        {
            var loc = FeederLocationOf("F1");
            loc.ComponentLocation.TableId = "T-9";
            loc.ComponentLocation.IsConstantTable = true;
            loc.ComponentLocation.TableState = 4;
            loc.ComponentLocation.CartridgeId = "C-1";
            loc.ComponentLocation.Divisions = new List<Division> { new Division { DivisionNumber = 1 }, new Division { DivisionNumber = 2 } };
            loc.ComponentLocation.Machine.LinePath = "Lines\\L1";
            loc.ComponentLocation.Machine.MachineName = "Mach 1";
            loc.ComponentLocation.Machine.MachineType = "X4";

            var info = SetupCenterMapper.ToLockInfo(new FeederMount { FeederId = "F1", Native = loc }, "why");

            Assert.That(
                new object[] { info.TableId, info.IsConstantTable, info.TableState, info.CartridgeId },
                Is.EqualTo(new object[] { "T-9", true, 4, "C-1" }));
            Assert.That(info.Divisions.Select(d => d.DivisionNumber), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(info.Machine, Is.Not.SameAs(loc.ComponentLocation.Machine), "the machine is copied, not shared");
            Assert.That(
                new[] { info.Machine.MachineId, info.Machine.LineName, info.Machine.LinePath, info.Machine.MachineName, info.Machine.MachineType },
                Is.EqualTo(new[] { "m1", "L1", "Lines\\L1", "Mach 1", "X4" }));
        }

        [Test]
        public void Lock_info_uses_the_mount_feeder_id_when_the_location_has_none()
        {
            var loc = FeederLocationOf(null);

            var info = SetupCenterMapper.ToLockInfo(new FeederMount { FeederId = "F1", Native = loc }, "why");

            Assert.That(info.FeederId, Is.EqualTo("F1"));
        }

        [Test]
        public void Lock_info_rejects_a_mount_it_cannot_use()
        {
            Assert.Throws<ArgumentNullException>(() => SetupCenterMapper.ToLockInfo(null, "why"));
            Assert.Throws<ArgumentException>(() => SetupCenterMapper.ToLockInfo(new FeederMount { FeederId = "F1", Native = "not a location" }, "why"));
            Assert.Throws<OibCallException>(() => SetupCenterMapper.ToLockInfo(new FeederMount { FeederId = "F1", Native = new PackagingUnitLocation() }, "why"));
        }

        [Test]
        public void Feeder_location_copies_the_shared_fields_into_a_plain_feeder_location()
        {
            var loc = FeederLocationOf("F1");
            loc.ComponentLocation.TableId = "T-9";

            var fl = SetupCenterMapper.ToFeederLocation(new FeederMount { FeederId = "F1", Native = loc });

            Assert.That(fl.GetType(), Is.EqualTo(typeof(FeederLocation)));
            Assert.That(
                new object[] { fl.FeederId, fl.FeederType, fl.Track, fl.Location, fl.Device, fl.Machine.MachineId, fl.Machine.LineName, fl.TableId },
                Is.EqualTo(new object[] { "F1", 7, 3, 2, 1, "m1", "L1", "T-9" }));
            Assert.That(fl.Machine, Is.Not.SameAs(loc.ComponentLocation.Machine));
        }

        [Test]
        public void Feeder_location_rejects_a_mount_it_cannot_use()
        {
            Assert.Throws<ArgumentNullException>(() => SetupCenterMapper.ToFeederLocation(null));
            Assert.Throws<ArgumentException>(() => SetupCenterMapper.ToFeederLocation(new FeederMount { FeederId = "F1", Native = 42 }));
            Assert.Throws<OibCallException>(() => SetupCenterMapper.ToFeederLocation(new FeederMount { FeederId = "F1", Native = new PackagingUnitLocation() }));
        }

        [Test]
        public void A_lock_request_has_a_line_when_the_machine_names_it_by_line_name_or_line_path()
        {
            Assert.That(SetupCenterMapper.HasLine(new LockFeederLocationInfo { Machine = new MachineLocator { LinePath = "Lines\\L1" } }), Is.True);
            Assert.That(SetupCenterMapper.HasLine(new LockFeederLocationInfo { Machine = new MachineLocator { LineName = "L1" } }), Is.True);
            Assert.That(SetupCenterMapper.HasLine(new LockFeederLocationInfo { Machine = new MachineLocator { LineName = " ", LinePath = "" } }), Is.False);
            Assert.That(SetupCenterMapper.HasLine(new LockFeederLocationInfo { Machine = null }), Is.False);
            Assert.That(SetupCenterMapper.HasLine(null), Is.False);
        }

        // ---- ToReports ----

        [Test]
        public void Reports_map_lock_infos()
        {
            var local = new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Local);
            var request = new LockStateChangedReportRequest(new LockStateChangedReport
            {
                PackagingUnits = new List<PackagingUnit>
                {
                    new PackagingUnit
                    {
                        UID = "A",
                        LockInfos = new List<LockInfo> { new LockInfo { Source = 9, Reason = 1, Date = local, Message = "mismatch" } }
                    }
                }
            });

            var reports = SetupCenterMapper.ToReports(request);

            Assert.That(reports.Single().Uid, Is.EqualTo("A"));
            var info = reports.Single().LockInfos.Single();
            Assert.That(info.Source, Is.EqualTo(9));
            Assert.That(info.Reason, Is.EqualTo(1));
            Assert.That(info.DateUtc, Is.EqualTo(local.ToUniversalTime()));
            Assert.That(info.DateUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(info.Message, Is.EqualTo("mismatch"));
        }

        [Test]
        public void Reports_keep_utc_dates_and_treat_unspecified_dates_as_utc()
        {
            var utc = new DateTime(2026, 10, 18, 3, 0, 0, DateTimeKind.Utc);
            var unspecified = new DateTime(2026, 10, 18, 4, 0, 0, DateTimeKind.Unspecified);
            var request = new LockStateChangedReportRequest(new LockStateChangedReport
            {
                PackagingUnits = new List<PackagingUnit>
                {
                    new PackagingUnit
                    {
                        UID = "A",
                        LockInfos = new List<LockInfo> { new LockInfo { Date = utc }, new LockInfo { Date = unspecified } }
                    }
                }
            });

            var dates = SetupCenterMapper.ToReports(request).Single().LockInfos.Select(i => i.DateUtc).ToList();

            Assert.That(dates[0], Is.EqualTo(utc));
            Assert.That(dates[0].Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(dates[1], Is.EqualTo(new DateTime(2026, 10, 18, 4, 0, 0, DateTimeKind.Utc)));
            Assert.That(dates[1].Kind, Is.EqualTo(DateTimeKind.Utc));
        }

        [Test]
        public void Reports_ignore_null_requests_resources_and_units_and_units_without_uid()
        {
            Assert.That(SetupCenterMapper.ToReports(null), Is.Empty);
            Assert.That(SetupCenterMapper.ToReports(new LockStateChangedReportRequest()), Is.Empty);
            Assert.That(SetupCenterMapper.ToReports(new LockStateChangedReportRequest(new LockStateChangedReport { PackagingUnits = null })), Is.Empty);

            var request = new LockStateChangedReportRequest(new LockStateChangedReport
            {
                PackagingUnits = new List<PackagingUnit>
                {
                    null,
                    new PackagingUnit { UID = null },
                    new PackagingUnit { UID = "  " },
                    new PackagingUnit { UID = "B" }
                }
            });

            Assert.That(SetupCenterMapper.ToReports(request).Select(r => r.Uid), Is.EqualTo(new[] { "B" }));
        }

        [Test]
        public void Reports_give_a_unit_without_lock_infos_an_empty_list()
        {
            var request = new LockStateChangedReportRequest(new LockStateChangedReport
            {
                PackagingUnits = new List<PackagingUnit>
                {
                    new PackagingUnit { UID = "A", LockInfos = null },
                    new PackagingUnit { UID = "B", LockInfos = new List<LockInfo> { null, new LockInfo { Reason = 28 } } }
                }
            });

            var reports = SetupCenterMapper.ToReports(request);

            Assert.That(reports[0].LockInfos, Is.Empty);
            Assert.That(reports[1].LockInfos.Select(i => i.Reason), Is.EqualTo(new[] { 28 }));
        }

        // ---- ThrowIfFailed ----

        [Test]
        public void Throw_if_failed_joins_messages()
        {
            var ex = Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", new[] { 1, 2 }, new[] { "not in verification" }));

            StringAssert.Contains("not in verification", ex.Message);
        }

        [Test]
        public void Throw_if_failed_names_the_action_and_separates_messages_with_semicolons()
        {
            var ex = Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", new[] { 2 }, new[] { "first", null, " ", "second" }));

            StringAssert.Contains("LOCK_PU", ex.Message);
            StringAssert.Contains("first; second", ex.Message);
        }

        [Test]
        public void Throw_if_failed_reports_the_state_when_no_message_comes_with_it()
        {
            var ex = Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", new[] { 2 }, null));

            StringAssert.Contains("LOCK_PU", ex.Message);
            StringAssert.Contains("2", ex.Message);
        }

        [Test]
        public void Throw_if_failed_accepts_all_ok_results()
        {
            Assert.DoesNotThrow(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", new[] { 1, 1 }, new[] { "ignored" }));
        }

        [Test]
        public void Throw_if_failed_treats_a_missing_result_as_a_failure()
        {
            var none = Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", new int[0], null));
            StringAssert.Contains("no result", none.Message);
            Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", (IEnumerable<int>)null, null));
        }

        private static PackagingUnitResult Result(string uid, int state = 1, params string[] messages)
        {
            return new PackagingUnitResult
            {
                ResultState = state,
                PackagingUnit = uid == null ? null : new PackagingUnit { UID = uid },
                Messages = messages.Select(m => new PackagingUnitResultMessage(m)).ToList()
            };
        }

        private static PackagingUnitResponse Response(params PackagingUnitResult[] results)
        {
            return new PackagingUnitResponse { Results = results.ToList() };
        }

        [Test]
        public void Throw_if_failed_reads_a_packaging_unit_response()
        {
            var bad = Response(Result("A"), Result("B", 2, "PU unknown"));

            var ex = Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", bad, new[] { "A", "B" }));
            StringAssert.Contains("PU unknown", ex.Message);

            Assert.DoesNotThrow(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", Response(Result("A")), new[] { "A" }));
        }

        [Test]
        public void Throw_if_failed_needs_a_result_for_a_packaging_unit_response()
        {
            var none = Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", (PackagingUnitResponse)null, new[] { "A", "B" }));
            StringAssert.Contains("no result", none.Message);
            StringAssert.Contains("A, B", none.Message);
            Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", new PackagingUnitResponse(), new[] { "A" }));
            Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", new PackagingUnitResponse { Results = null }, new[] { "A" }));
        }

        [Test]
        public void Throw_if_failed_rejects_fewer_results_than_requested_uids()
        {
            var ex = Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", Response(Result("A")), new[] { "A", "B" }));

            StringAssert.Contains("no confirmation for B", ex.Message);
        }

        [Test]
        public void Throw_if_failed_names_the_uid_that_has_no_result()
        {
            var response = Response(Result("A"), Result("C"));

            var ex = Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", response, new[] { "A", "B", "C" }));

            Assert.That(ex.Message, Is.EqualTo("LOCK_PU failed: no confirmation for B"), "only the missing uid is named");
        }

        [Test]
        public void Throw_if_failed_accepts_results_in_any_order_and_extra_results()
        {
            var response = Response(Result("C"), Result("X"), Result("A"), Result("B"));

            Assert.DoesNotThrow(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", response, new[] { "A", "B", "C" }));
        }

        [Test]
        public void Throw_if_failed_ignores_results_about_uids_that_were_not_requested()
        {
            var response = Response(Result("A"), Result("X", 2, "not ours"));

            Assert.DoesNotThrow(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", response, new[] { "A" }));
        }

        [Test]
        public void Throw_if_failed_matches_uids_trimmed_and_ignoring_case_and_ignores_repeated_requests()
        {
            var response = Response(Result(" a "), Result("B"));

            Assert.DoesNotThrow(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", response, new[] { "A", "a", " b", "  ", null }));
        }

        [Test]
        public void Throw_if_failed_counts_results_that_carry_no_packaging_unit()
        {
            Assert.DoesNotThrow(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", Response(Result(null), Result(null)), new[] { "A", "B" }));
            Assert.DoesNotThrow(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", Response(Result(null), Result(null), Result(null)), new[] { "A", "B" }));
            Assert.DoesNotThrow(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", Response(Result("A"), Result(null)), new[] { "A", "B" }), "one named, one anonymous");
            Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", Response(Result(null)), new[] { "A", "B" }));
            Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", Response(Result("A"), Result(null)), new[] { "A", "B", "C" }));
        }

        [Test]
        public void Throw_if_failed_takes_messages_only_from_failed_results()
        {
            var response = Response(Result("A", 1, "all good"), Result("B", 2, "boom"), Result("C", 1, "also fine"));

            var ex = Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", response, new[] { "A", "B", "C" }));

            StringAssert.Contains("boom", ex.Message);
            StringAssert.DoesNotContain("all good", ex.Message);
            StringAssert.DoesNotContain("also fine", ex.Message);
        }

        [Test]
        public void Throw_if_failed_fails_on_an_anonymous_failed_result_and_a_null_result()
        {
            Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", Response(Result("A"), Result(null, 2, "bad")), new[] { "A" }));
            Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_PU", Response(Result("A"), null), new[] { "A" }));
        }

        [Test]
        public void Throw_if_failed_reads_the_online_feeder_responses()
        {
            var lockBad = new LockOnlineFeedersResponse
            {
                LockOnlineFeedersResults = new List<LockOnlineFeedersResult> { new LockOnlineFeedersResult { ResultState = 2, Messages = new List<string> { "feeder busy" } } }
            };
            var unlockBad = new UnlockOnlineFeedersResponse
            {
                UnlockOnlineFeedersResults = new List<UnlockOnlineFeedersResult> { new UnlockOnlineFeedersResult { ResultState = 2, Messages = new List<string> { "not locked" } } }
            };

            StringAssert.Contains("feeder busy", Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_FEEDER", lockBad)).Message);
            StringAssert.Contains("not locked", Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("UNLOCK_FEEDER", unlockBad)).Message);
            Assert.DoesNotThrow(() => SetupCenterMapper.ThrowIfFailed("LOCK_FEEDER", new LockOnlineFeedersResponse { LockOnlineFeedersResults = new List<LockOnlineFeedersResult> { new LockOnlineFeedersResult { ResultState = 1 } } }));
            Assert.DoesNotThrow(() => SetupCenterMapper.ThrowIfFailed("UNLOCK_FEEDER", new UnlockOnlineFeedersResponse { UnlockOnlineFeedersResults = new List<UnlockOnlineFeedersResult> { new UnlockOnlineFeedersResult { ResultState = 1 } } }));
            Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_FEEDER", (LockOnlineFeedersResponse)null));
            Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("UNLOCK_FEEDER", (UnlockOnlineFeedersResponse)null));
        }

        [Test]
        public void Throw_if_failed_takes_online_feeder_messages_only_from_failed_results()
        {
            var lockMixed = new LockOnlineFeedersResponse
            {
                LockOnlineFeedersResults = new List<LockOnlineFeedersResult>
                {
                    new LockOnlineFeedersResult { ResultState = 1, Messages = new List<string> { "locked fine" } },
                    new LockOnlineFeedersResult { ResultState = 2, Messages = new List<string> { "feeder busy" } }
                }
            };
            var unlockMixed = new UnlockOnlineFeedersResponse
            {
                UnlockOnlineFeedersResults = new List<UnlockOnlineFeedersResult>
                {
                    new UnlockOnlineFeedersResult { ResultState = 1, Messages = new List<string> { "unlocked fine" } },
                    new UnlockOnlineFeedersResult { ResultState = 2, Messages = new List<string> { "not locked" } }
                }
            };

            var lockEx = Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("LOCK_FEEDER", lockMixed));
            var unlockEx = Assert.Throws<OibCallException>(() => SetupCenterMapper.ThrowIfFailed("UNLOCK_FEEDER", unlockMixed));

            StringAssert.Contains("feeder busy", lockEx.Message);
            StringAssert.DoesNotContain("locked fine", lockEx.Message);
            StringAssert.Contains("not locked", unlockEx.Message);
            StringAssert.DoesNotContain("unlocked fine", unlockEx.Message);
        }

        // ---- ReconnectBackoff (the brief puts its test here) ----

        [Test]
        public void Backoff_doubles_to_five_minutes()
        {
            var clock = new FakeClock();
            var b = new ReconnectBackoff(clock);
            for (int i = 0; i < 8; i++)
                b.Failed();
            Assert.That(b.CanTry, Is.False);
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.That(b.CanTry, Is.True);
            b.Succeeded();
            b.Failed();
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.That(b.CanTry, Is.True);
        }
    }
}
