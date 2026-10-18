using System;
using System.Net;
using Interlock.Core.Api;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Security;
using Interlock.Service.Rest;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class DtoMapperTests
    {
        private static LockRecord Pu(long id, string uid, DateTime? created = null)
        {
            return new LockRecord
            {
                Id = id,
                Type = LockType.Pu,
                Target = uid,
                TargetNorm = uid.ToUpperInvariant(),
                Reason = "bad",
                Source = LockSource.Manual,
                CreatedBy = "qa1",
                CreatedAtUtc = created ?? new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc),
                Status = LockStatus.Active,
                EnforceState = EnforceState.Pending
            };
        }

        private static InterlockException Fails(TestDelegate d)
        {
            return Assert.Throws<InterlockException>(d);
        }

        // ---- DtoMapper ----

        [Test]
        public void Lock_dto_uses_db_text_and_iso_utc()
        {
            var d = DtoMapper.ToDto(Pu(1, "U1", created: new DateTime(2026, 10, 18, 1, 2, 3, DateTimeKind.Utc)));

            Assert.That(d.type, Is.EqualTo("PU"));
            Assert.That(d.enforceState, Is.EqualTo("PENDING"));
            Assert.That(d.status, Is.EqualTo("ACTIVE"));
            Assert.That(d.source, Is.EqualTo("MANUAL"));
            Assert.That(d.createdAt, Is.EqualTo("2026-10-18T01:02:03.0000000Z"));
            Assert.That(d.releasedAt, Is.Null);
            Assert.That(d.lanes, Is.Null);
        }

        [Test]
        public void Lock_dto_keeps_lanes_release_data_and_treats_unspecified_dates_as_utc()
        {
            var l = Pu(2, "U2");
            l.Type = LockType.Conveyor;
            l.Lanes = Lanes.Both;
            l.Status = LockStatus.Released;
            l.ReleasedBy = "eng";
            l.ReleasedAtUtc = new DateTime(2026, 10, 18, 4, 5, 6, DateTimeKind.Unspecified);
            l.ReleaseReason = "ok";
            l.EnforceState = EnforceState.NotNeeded;
            l.EnforceError = "boom";

            var d = DtoMapper.ToDto(l);

            Assert.That(d.lanes, Is.EqualTo("BOTH"));
            Assert.That(d.status, Is.EqualTo("RELEASED"));
            Assert.That(d.enforceState, Is.EqualTo("NOT_NEEDED"));
            Assert.That(d.releasedAt, Is.EqualTo("2026-10-18T04:05:06.0000000Z"));
            Assert.That(d.releasedBy, Is.EqualTo("eng"));
            Assert.That(d.releaseReason, Is.EqualTo("ok"));
            Assert.That(d.enforceError, Is.EqualTo("boom"));
        }

        [Test]
        public void Action_and_decision_dtos_copy_the_fields()
        {
            var a = DtoMapper.ToDto(new ActionLogEntry { Action = "LOCK_PU", RequestJson = "{}", Success = false, Error = "e", Attempt = 2, CreatedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) });
            Assert.That(a.action, Is.EqualTo("LOCK_PU"));
            Assert.That(a.request, Is.EqualTo("{}"));
            Assert.That(a.success, Is.False);
            Assert.That(a.error, Is.EqualTo("e"));
            Assert.That(a.attempt, Is.EqualTo(2));
            Assert.That(a.createdAt, Is.EqualTo("2026-01-02T03:04:05.0000000Z"));

            var d = DtoMapper.ToDto(new DecisionLogEntry { Method = "ScanCheck", ReasonCode = 7, Uid = "U1", Pn = "P", Mpn = "M", Supplier = "S", LinePath = "L", MachineId = "m", FeederId = "f", Result = "BLOCK", Message = "x", MatchedLockIds = "5,6", CreatedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) });
            Assert.That(d.method, Is.EqualTo("ScanCheck"));
            Assert.That(d.reasonCode, Is.EqualTo(7));
            Assert.That(d.matchedLockIds, Is.EqualTo("5,6"));
            Assert.That(d.result, Is.EqualTo("BLOCK"));
            Assert.That(d.createdAt, Is.EqualTo("2026-01-02T03:04:05.0000000Z"));
        }

        [Test]
        public void Unknown_type_is_validation_error()
        {
            var ex = Fails(() => DtoMapper.ToRequest(new CreateLockDto { type = "REEL" }));
            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Validation));
            Assert.That(ex.Message, Is.EqualTo("Unknown type 'REEL'"));
        }

        [Test]
        public void Unknown_lanes_is_validation_error()
        {
            var ex = Fails(() => DtoMapper.ToRequest(new CreateLockDto { type = "CONVEYOR", lanes = "UP" }));
            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Validation));
            Assert.That(ex.Message, Is.EqualTo("Unknown lanes 'UP'"));
        }

        [Test]
        public void Request_is_parsed_and_trimmed()
        {
            var r = DtoMapper.ToRequest(new CreateLockDto { type = "pu", target = "  U1\r\n", lanes = "both", linePath = " L1 ", reason = "bad reel\r\n" });

            Assert.That(r.Type, Is.EqualTo(LockType.Pu));
            Assert.That(r.Lanes, Is.EqualTo(Lanes.Both));
            Assert.That(r.Target, Is.EqualTo("U1"));
            Assert.That(r.LinePath, Is.EqualTo("L1"));
            Assert.That(r.Reason, Is.EqualTo("bad reel"));
        }

        [Test]
        public void Missing_type_and_lanes_stay_null_for_the_validator()
        {
            var r = DtoMapper.ToRequest(new CreateLockDto { target = "U1" });
            Assert.That(r.Type, Is.Null);
            Assert.That(r.Lanes, Is.Null);
        }

        [Test]
        public void Missing_body_is_validation_error()
        {
            Assert.That(Fails(() => DtoMapper.ToRequest(null)).Kind, Is.EqualTo(ErrorKind.Validation));
            Assert.That(Fails(() => DtoMapper.ToReason(null)).Kind, Is.EqualTo(ErrorKind.Validation));
        }

        [TestCase("\u0000")]
        [TestCase("\r")]
        [TestCase("\n")]
        [TestCase("\t")]
        [TestCase("\u007F")]
        [TestCase("\u001F")]
        public void Control_characters_inside_any_string_field_are_rejected(string control)
        {
            var inside = "A" + control + "B";

            AssertInvalid(() => DtoMapper.ToRequest(new CreateLockDto { type = "PU", target = inside }), "target");
            AssertInvalid(() => DtoMapper.ToRequest(new CreateLockDto { type = "PU", target = "U1", reason = inside }), "reason");
            AssertInvalid(() => DtoMapper.ToRequest(new CreateLockDto { type = "PU", target = "U1", linePath = inside }), "linePath");
            AssertInvalid(() => DtoMapper.ToRequest(new CreateLockDto { type = inside, target = "U1" }), "type");
            AssertInvalid(() => DtoMapper.ToRequest(new CreateLockDto { type = "CONVEYOR", target = "U1", lanes = inside }), "lanes");
            AssertInvalid(() => DtoMapper.ToReason(new ReasonDto { reason = inside }), "reason");
            AssertInvalid(() => DtoMapper.ToLockQuery(inside, null, null, null), "status");
            AssertInvalid(() => DtoMapper.ToLockQuery(null, inside, null, null), "type");
            AssertInvalid(() => DtoMapper.ToLockQuery(null, null, inside, null), "linePath");
            AssertInvalid(() => DtoMapper.ToLockQuery(null, null, null, inside), "q");
            AssertInvalid(() => DtoMapper.ToDecisionQuery(inside, null, null, null), "uid");
            AssertInvalid(() => DtoMapper.ToDecisionQuery(null, inside, null, null), "pn");
            AssertInvalid(() => DtoMapper.ParseRole(inside), "role");
            AssertInvalid(() => InputGuard.CleanSecret(inside, "pin"), "pin");
        }

        private static void AssertInvalid(TestDelegate d, string field)
        {
            var ex = Fails(d);
            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Validation));
            Assert.That(ex.Message, Is.EqualTo("Invalid character in " + field));
        }

        [Test]
        public void Surrounding_whitespace_including_scanner_line_breaks_is_trimmed_not_rejected()
        {
            Assert.That(InputGuard.Clean("\r\n  U1 \r\n", "target"), Is.EqualTo("U1"));
            Assert.That(InputGuard.Clean(null, "target"), Is.Null);
            Assert.That(InputGuard.Clean(" \r\n", "target"), Is.EqualTo(string.Empty));
        }

        [Test]
        public void A_secret_is_not_trimmed()
        {
            Assert.That(InputGuard.CleanSecret(" 12 34 ", "pin"), Is.EqualTo(" 12 34 "));
        }

        [Test]
        public void Lock_query_parses_filters()
        {
            var q = DtoMapper.ToLockQuery("active", "feeder", " L1 ", " abc ");
            Assert.That(q.Status, Is.EqualTo(LockStatus.Active));
            Assert.That(q.Type, Is.EqualTo(LockType.Feeder));
            Assert.That(q.LinePath, Is.EqualTo("L1"));
            Assert.That(q.Text, Is.EqualTo("abc"));

            var none = DtoMapper.ToLockQuery(null, "", null, null);
            Assert.That(none.Status, Is.Null);
            Assert.That(none.Type, Is.Null);

            Assert.That(Fails(() => DtoMapper.ToLockQuery("bogus", null, null, null)).Message, Is.EqualTo("Unknown status 'bogus'"));
            Assert.That(Fails(() => DtoMapper.ToLockQuery(null, "REEL", null, null)).Kind, Is.EqualTo(ErrorKind.Validation));
        }

        [Test]
        public void Decision_query_parses_times_as_utc()
        {
            var q = DtoMapper.ToDecisionQuery("U1", null, "2026-10-18T01:00:00Z", "2026-10-18T03:00:00+02:00");

            Assert.That(q.Uid, Is.EqualTo("U1"));
            Assert.That(q.FromUtc, Is.EqualTo(new DateTime(2026, 10, 18, 1, 0, 0, DateTimeKind.Utc)));
            Assert.That(q.ToUtc, Is.EqualTo(new DateTime(2026, 10, 18, 1, 0, 0, DateTimeKind.Utc)));
            Assert.That(q.FromUtc.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(Fails(() => DtoMapper.ToDecisionQuery(null, null, "yesterday-ish", null)).Kind, Is.EqualTo(ErrorKind.Validation));
        }

        [TestCase("12", 12L)]
        [TestCase(" 7 ", 7L)]
        public void Id_is_parsed(string text, long expected)
        {
            Assert.That(DtoMapper.ParseId(text, "lock id"), Is.EqualTo(expected));
        }

        [TestCase("abc")]
        [TestCase("-1")]
        [TestCase("")]
        [TestCase(null)]
        [TestCase("99999999999999999999")]
        public void Bad_id_is_validation_error(string text)
        {
            Assert.That(Fails(() => DtoMapper.ParseId(text, "lock id")).Kind, Is.EqualTo(ErrorKind.Validation));
        }

        [Test]
        public void Flag_and_role_are_parsed()
        {
            Assert.That(DtoMapper.ParseFlag("TRUE", "active"), Is.True);
            Assert.That(DtoMapper.ParseFlag("false", "active"), Is.False);
            Assert.That(Fails(() => DtoMapper.ParseFlag("yes", "active")).Kind, Is.EqualTo(ErrorKind.Validation));
            Assert.That(Fails(() => DtoMapper.ParseFlag(null, "active")).Kind, Is.EqualTo(ErrorKind.Validation));
            Assert.That(DtoMapper.ParseRole("qa"), Is.EqualTo(Role.Qa));
            Assert.That(Fails(() => DtoMapper.ParseRole("boss")).Message, Is.EqualTo("Unknown role 'boss'"));
        }

        [Test]
        public void User_and_key_dtos_never_carry_a_hash()
        {
            var u = DtoMapper.ToDto(new AppUser { Username = "qa1", PinHash = "pbkdf2-sha256$secret", Role = Role.Qa, Active = true });
            Assert.That(u.pin, Is.Null);
            Assert.That(u.role, Is.EqualTo("QA"));

            var k = DtoMapper.ToDto(new ApiKeyRecord { Id = 4, Name = "mes", KeyHash = "deadbeef", CanUnlock = true, Active = true }, null);
            Assert.That(k.key, Is.Null);
            Assert.That(k.id, Is.EqualTo(4));
            Assert.That(k.canUnlock, Is.True);
            Assert.That(typeof(ApiKeyDto).GetProperty("KeyHash"), Is.Null);
            Assert.That(typeof(UserDto).GetProperty("PinHash"), Is.Null);
        }

        // ---- RestErrors ----

        [TestCase(ErrorKind.Validation, 400)]
        [TestCase(ErrorKind.Unauthenticated, 401)]
        [TestCase(ErrorKind.Forbidden, 403)]
        [TestCase(ErrorKind.NotFound, 404)]
        [TestCase(ErrorKind.InvalidState, 409)]
        [TestCase(ErrorKind.Unavailable, 503)]
        public void Status_mapping(ErrorKind k, int code)
        {
            Assert.That((int)RestErrors.StatusFor(k), Is.EqualTo(code));
        }

        [Test]
        public void Interlock_exception_becomes_a_fault_with_the_kind_name_in_lower_case()
        {
            var fault = RestErrors.ToFault(new InterlockException(ErrorKind.InvalidState, "Lock is already released"), new ListLog());

            Assert.That(fault.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(fault.Detail.error, Is.EqualTo("invalidstate"));
            Assert.That(fault.Detail.message, Is.EqualTo("Lock is already released"));
        }

        [Test]
        public void Unexpected_exception_is_a_500_with_a_fixed_message_and_the_details_in_the_log()
        {
            var log = new ListLog();

            var fault = RestErrors.ToFault(new InvalidOperationException("connection string Host=db1;Password=x"), log);

            Assert.That(fault.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(fault.Detail.error, Is.EqualTo("internal"));
            Assert.That(fault.Detail.message, Is.EqualTo("Internal error"));
            Assert.That(log.Errors, Has.Count.EqualTo(1));
            StringAssert.Contains("Password=x", log.Errors[0]);
        }
    }
}
