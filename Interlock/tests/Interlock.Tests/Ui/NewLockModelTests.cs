using Interlock.Ui;
using NUnit.Framework;

namespace Interlock.Tests.Ui
{
    [TestFixture]
    public class NewLockModelTests
    {
        [Test] public void Conveyor_shows_station_and_lanes_only() { var m = new NewLockModel { Type = "CONVEYOR" }; Assert.That(new[] { m.ShowStationPicker, m.ShowLanes, m.ShowLinePicker }, Is.EqualTo(new[] { true, true, false })); }
        [Test] public void Pn_whole_plant_sends_null_line() { var d = new NewLockModel { Type = "PN", Target = " C1\r\n", LinePath = null, Reason = "r" }.ToDto(); Assert.That(d.target, Is.EqualTo("C1")); Assert.That(d.linePath, Is.Null); Assert.That(d.lanes, Is.Null); }
        [Test] public void Conveyor_target_is_station_path() { var d = new NewLockModel { Type = "CONVEYOR", StationPath = "Lines\\L1\\S1", Lanes = "BOTH", LinePath = "ignored", Reason = "r" }.ToDto(); Assert.That(new[] { d.target, d.lanes, d.linePath }, Is.EqualTo(new[] { "Lines\\L1\\S1", "BOTH", null })); }
        [Test] public void Pu_hides_line_picker() { var m = new NewLockModel { Type = "PU", Target = "U1", LinePath = "Lines\\L1", Reason = "r" }; Assert.That(m.ShowLinePicker, Is.False); Assert.That(m.ToDto().linePath, Is.Null); }

        [TestCase("PN", false, false, true)]
        [TestCase("MPN", false, false, true)]
        [TestCase("SUPPLIER", false, false, true)]
        [TestCase("PU", false, false, false)]
        [TestCase("FEEDER", false, false, false)]
        [TestCase("CONVEYOR", true, true, false)]
        public void Visibility_rules_per_type(string type, bool station, bool lanes, bool line)
        {
            var m = new NewLockModel { Type = type };
            Assert.That(new[] { m.ShowStationPicker, m.ShowLanes, m.ShowLinePicker }, Is.EqualTo(new[] { station, lanes, line }));
            Assert.That(m.ShowTarget, Is.EqualTo(!station));
        }

        [Test]
        public void Line_is_sent_for_supplier_and_lanes_default_to_both_for_conveyor()
        {
            Assert.That(new NewLockModel { Type = "SUPPLIER", Target = "S", LinePath = "Lines\\L1", Reason = "r" }.ToDto().linePath, Is.EqualTo("Lines\\L1"));
            Assert.That(new NewLockModel { Type = "CONVEYOR", StationPath = "S", Reason = "r" }.ToDto().lanes, Is.EqualTo("BOTH"));
        }

        [Test]
        public void Scanner_text_is_trimmed_including_cr_lf_and_the_reason_too()
        {
            var d = new NewLockModel { Type = "FEEDER", Target = "\t F-01 \r\n", Reason = "  bad reel \n" }.ToDto();
            Assert.That(d.target, Is.EqualTo("F-01"));
            Assert.That(d.reason, Is.EqualTo("bad reel"));
            Assert.That(d.type, Is.EqualTo("FEEDER"));
        }

        [Test]
        public void Validate_requires_reason_and_target()
        {
            Assert.That(new NewLockModel { Type = "PU", Target = "U", Reason = " \r\n" }.Validate(), Is.EqualTo("Enter a reason"));
            Assert.That(new NewLockModel { Type = "PU", Target = " ", Reason = "r" }.Validate(), Is.EqualTo("Enter or scan the target"));
            Assert.That(new NewLockModel { Type = "CONVEYOR", StationPath = null, Reason = "r" }.Validate(), Is.EqualTo("Choose a station"));
            Assert.That(new NewLockModel { Type = "PU", Target = "U", Reason = "r" }.Validate(), Is.Null);
            Assert.That(new NewLockModel { Type = "CONVEYOR", StationPath = "S", Reason = "r" }.Validate(), Is.Null);
            Assert.That(new NewLockModel { Type = null, Target = "U", Reason = "r" }.Validate(), Is.EqualTo("Choose a lock type"));
        }
    }

    [TestFixture]
    public class DecisionFilterTests
    {
        [Test]
        public void From_is_start_of_local_day_and_to_is_inclusive_end_of_day_in_utc()
        {
            var f = new DecisionFilter { From = new System.DateTime(2026, 1, 5, 13, 30, 0), To = new System.DateTime(2026, 1, 6, 9, 0, 0) };
            var from = f.FromUtc();
            var to = f.ToUtc();
            Assert.That(from.Value.Kind, Is.EqualTo(System.DateTimeKind.Utc));
            Assert.That(from.Value, Is.EqualTo(new System.DateTime(2026, 1, 5, 0, 0, 0, System.DateTimeKind.Local).ToUniversalTime()));
            Assert.That(to.Value.Kind, Is.EqualTo(System.DateTimeKind.Utc));
            Assert.That(to.Value, Is.EqualTo(new System.DateTime(2026, 1, 7, 0, 0, 0, System.DateTimeKind.Local).AddTicks(-1).ToUniversalTime()));
        }

        [Test]
        public void Unset_dates_give_null_and_blank_text_gives_null()
        {
            var f = new DecisionFilter { Uid = "  ", Pn = " P1 " };
            Assert.That(f.FromUtc(), Is.Null);
            Assert.That(f.ToUtc(), Is.Null);
            Assert.That(f.UidValue, Is.Null);
            Assert.That(f.PnValue, Is.EqualTo("P1"));
        }

        [Test]
        public void Inverted_range_is_reported()
        {
            Assert.That(new DecisionFilter { From = new System.DateTime(2026, 2, 2), To = new System.DateTime(2026, 2, 1) }.Validate(), Is.EqualTo("'From' is after 'To'"));
            Assert.That(new DecisionFilter { From = new System.DateTime(2026, 2, 1), To = new System.DateTime(2026, 2, 1) }.Validate(), Is.Null);
        }

        [Test]
        public void Rows_are_newest_first_and_capped_at_500()
        {
            var list = new System.Collections.Generic.List<Interlock.Core.Api.DecisionDto>();
            for (var i = 0; i < 600; i++)
                list.Add(new Interlock.Core.Api.DecisionDto { uid = i.ToString(), createdAt = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc).AddMinutes(i).ToString("o") });
            var rows = DecisionFilter.NewestFirst(list);
            Assert.That(rows.Count, Is.EqualTo(500));
            Assert.That(rows[0].uid, Is.EqualTo("599"));
            Assert.That(rows[499].uid, Is.EqualTo("100"));
            Assert.That(DecisionFilter.NewestFirst(null).Count, Is.EqualTo(0));
        }
    }

    [TestFixture]
    public class AdminRulesTests
    {
        [Test]
        public void New_user_needs_a_name_a_role_and_a_pin_of_four_characters()
        {
            Assert.That(AdminRules.ValidateUser(true, " ", "QA", "1234"), Is.EqualTo("Enter the username"));
            Assert.That(AdminRules.ValidateUser(true, "bob", null, "1234"), Is.EqualTo("Choose a role"));
            Assert.That(AdminRules.ValidateUser(true, "bob", "QA", ""), Is.EqualTo("A new user needs a PIN of at least 4 characters"));
            Assert.That(AdminRules.ValidateUser(true, "bob", "QA", "123"), Is.EqualTo("A new user needs a PIN of at least 4 characters"));
            Assert.That(AdminRules.ValidateUser(true, "bob", "QA", "1234"), Is.Null);
        }

        [Test]
        public void Edit_keeps_the_pin_when_empty_but_checks_a_given_one()
        {
            Assert.That(AdminRules.ValidateUser(false, "bob", "QA", ""), Is.Null);
            Assert.That(AdminRules.ValidateUser(false, "bob", "QA", "12"), Is.EqualTo("The PIN must have at least 4 characters"));
            Assert.That(AdminRules.ValidateUser(false, "bob", "QA", "1234"), Is.Null);
        }

        [Test]
        public void User_dto_sends_pin_only_when_given_and_trims_the_name()
        {
            var keep = AdminRules.ToUserDto(" bob ", "QA", false, "");
            Assert.That(keep.username, Is.EqualTo("bob"));
            Assert.That(keep.pin, Is.Null);
            Assert.That(keep.active, Is.False);
            Assert.That(AdminRules.ToUserDto("bob", "QA", true, "1234").pin, Is.EqualTo("1234"));
        }

        [Test]
        public void Roles_and_key_name_rules()
        {
            Assert.That(AdminRules.Roles, Is.EqualTo(new[] { "OPERATOR", "QA", "ENGINEER", "ADMIN" }));
            Assert.That(AdminRules.ValidateKeyName("  "), Is.EqualTo("Enter a name for the key"));
            Assert.That(AdminRules.ValidateKeyName("mes"), Is.Null);
            Assert.That(AdminRules.CanOpen(null), Is.False);
        }

        [Test]
        public void Created_key_is_shown_once_and_then_gone()
        {
            var d = new KeyOnceDisplay();
            Assert.That(d.HasKey, Is.False);
            d.Show("abc123");
            Assert.That(d.HasKey, Is.True);
            Assert.That(d.Key, Is.EqualTo("abc123"));
            d.Clear();
            Assert.That(d.HasKey, Is.False);
            Assert.That(d.Key, Is.Null);
            d.Show("");
            Assert.That(d.HasKey, Is.False);
            StringAssert.Contains("cannot be shown again", KeyOnceDisplay.Warning);
        }
    }
}
