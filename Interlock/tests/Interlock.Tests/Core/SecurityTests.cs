using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Security;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class SecurityTests
    {
        [Test] public void Pin_hash_verifies_and_has_format() { var h = PinHasher.Hash("1234"); StringAssert.StartsWith("pbkdf2-sha256$100000$", h); Assert.That(PinHasher.Verify("1234", h), Is.True); Assert.That(PinHasher.Verify("1235", h), Is.False); }
        [Test] public void Pin_hash_is_salted() => Assert.That(PinHasher.Hash("1234"), Is.Not.EqualTo(PinHasher.Hash("1234")));
        [Test] public void Malformed_hash_does_not_verify() => Assert.That(PinHasher.Verify("1234", "garbage"), Is.False);
        [Test] public void Api_key_hash_is_sha256_hex() => Assert.That(ApiKeyHasher.Hash("abc"), Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        [TestCase(Role.Operator, false)] [TestCase(Role.Qa, true)] [TestCase(Role.Engineer, true)] [TestCase(Role.Admin, true)]
        public void Release_permission_by_role(Role r, bool expected) => Assert.That(Permissions.CanRelease(Principal.ForUser("u", r)), Is.EqualTo(expected));
        [Test] public void Api_key_release_needs_flag_and_never_admin() { Assert.That(Permissions.CanRelease(Principal.ForApiKey("mes", false)), Is.False); Assert.That(Permissions.CanRelease(Principal.ForApiKey("mes", true)), Is.True); Assert.That(Permissions.CanAdmin(Principal.ForApiKey("mes", true)), Is.False); }
        [Test] public void Session_expires_after_8_hours() { var clock = new FakeClock(); var s = new SessionStore(clock, TimeSpan.FromHours(8)); var t = s.Create(Principal.ForUser("a", Role.Qa)); clock.Advance(TimeSpan.FromHours(7.9)); Assert.That(s.Resolve(t).Name, Is.EqualTo("a")); clock.Advance(TimeSpan.FromHours(0.2)); Assert.That(s.Resolve(t), Is.Null); }

        // ---- PinHasher ----

        [Test]
        public void Pin_hash_has_16_byte_salt_and_32_byte_hash()
        {
            var parts = PinHasher.Hash("1234").Split('$');

            Assert.That(parts, Has.Length.EqualTo(4));
            Assert.That(parts[0], Is.EqualTo("pbkdf2-sha256"));
            Assert.That(parts[1], Is.EqualTo("100000"));
            Assert.That(Convert.FromBase64String(parts[2]), Has.Length.EqualTo(16));
            Assert.That(Convert.FromBase64String(parts[3]), Has.Length.EqualTo(32));
        }

        [TestCase("")]
        [TestCase("a$b$c")]
        [TestCase("pbkdf2-sha256$100000$AAAA")]
        [TestCase("pbkdf2-sha256$100000$AAAA$BBBB$CCCC")]
        [TestCase("pbkdf2-sha1$100000$AAAA$BBBB")]
        [TestCase("pbkdf2-sha256$abc$AAAA$BBBB")]
        [TestCase("pbkdf2-sha256$0$AAAA$BBBB")]
        [TestCase("pbkdf2-sha256$-5$AAAA$BBBB")]
        [TestCase("pbkdf2-sha256$100000$not base64!$BBBB")]
        [TestCase("pbkdf2-sha256$100000$AAAA$not base64!")]
        [TestCase("pbkdf2-sha256$100000$$")]
        public void Malformed_variants_do_not_verify(string stored)
        {
            Assert.That(PinHasher.Verify("1234", stored), Is.False);
        }

        [Test]
        public void Null_stored_hash_does_not_verify()
        {
            Assert.That(PinHasher.Verify("1234", null), Is.False);
        }

        [Test]
        public void Null_pin_does_not_verify_and_does_not_hash()
        {
            var h = PinHasher.Hash("1234");

            Assert.That(PinHasher.Verify(null, h), Is.False);
            Assert.Throws<ArgumentNullException>(() => PinHasher.Hash(null));
        }

        [Test]
        public void Pin_hash_verifies_the_empty_pin_only_for_the_empty_pin()
        {
            var h = PinHasher.Hash("");

            Assert.That(PinHasher.Verify("", h), Is.True);
            Assert.That(PinHasher.Verify("0", h), Is.False);
        }

        [Test]
        public void Pin_verify_uses_the_stored_iteration_count()
        {
            // A hash written with a different (still valid) iteration count must still verify.
            var salt = new byte[16];
            byte[] hash;
            using (var kdf = new Rfc2898DeriveBytes("1234", salt, 1000, HashAlgorithmName.SHA256))
                hash = kdf.GetBytes(32);
            var stored = "pbkdf2-sha256$1000$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);

            Assert.That(PinHasher.Verify("1234", stored), Is.True);
            Assert.That(PinHasher.Verify("1235", stored), Is.False);
        }

        // ---- ApiKeyHasher ----

        [Test]
        public void New_key_is_64_lower_case_hex_and_unique()
        {
            var a = ApiKeyHasher.NewKey();
            var b = ApiKeyHasher.NewKey();

            Assert.That(a, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(b, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(a, Is.Not.EqualTo(b));
        }

        [Test]
        public void Api_key_hash_is_lower_case_hex_of_64_chars()
        {
            Assert.That(ApiKeyHasher.Hash(ApiKeyHasher.NewKey()), Does.Match("^[0-9a-f]{64}$"));
        }

        [Test]
        public void Api_key_hash_rejects_null()
        {
            Assert.Throws<ArgumentNullException>(() => ApiKeyHasher.Hash(null));
        }

        // ---- Principal / Permissions ----

        [Test]
        public void User_principal_has_role_and_cannot_unlock_by_flag()
        {
            var p = Principal.ForUser("anna", Role.Engineer);

            Assert.That(p.Name, Is.EqualTo("anna"));
            Assert.That(p.Role, Is.EqualTo(Role.Engineer));
            Assert.That(p.IsApiKey, Is.False);
            Assert.That(p.CanUnlock, Is.False);
        }

        [Test]
        public void Api_key_principal_has_no_role()
        {
            var p = Principal.ForApiKey("mes", true);

            Assert.That(p.Name, Is.EqualTo("mes"));
            Assert.That(p.Role, Is.Null);
            Assert.That(p.IsApiKey, Is.True);
            Assert.That(p.CanUnlock, Is.True);
        }

        [TestCase(Role.Operator, false)] [TestCase(Role.Qa, false)] [TestCase(Role.Engineer, false)] [TestCase(Role.Admin, true)]
        public void Admin_permission_by_role(Role r, bool expected)
        {
            Assert.That(Permissions.CanAdmin(Principal.ForUser("u", r)), Is.EqualTo(expected));
        }

        [Test]
        public void Any_principal_can_lock()
        {
            Assert.That(Permissions.CanLock(Principal.ForUser("u", Role.Operator)), Is.True);
            Assert.That(Permissions.CanLock(Principal.ForUser("u", Role.Admin)), Is.True);
            Assert.That(Permissions.CanLock(Principal.ForApiKey("mes", false)), Is.True);
            Assert.That(Permissions.CanLock(Principal.ForApiKey("mes", true)), Is.True);
        }

        [Test]
        public void Permission_checks_reject_null_principal()
        {
            Assert.Throws<ArgumentNullException>(() => Permissions.CanLock(null));
            Assert.Throws<ArgumentNullException>(() => Permissions.CanRelease(null));
            Assert.Throws<ArgumentNullException>(() => Permissions.CanAdmin(null));
        }

        // ---- SessionStore ----

        [Test]
        public void Token_is_64_hex_chars_and_unique()
        {
            var s = new SessionStore(new FakeClock(), TimeSpan.FromHours(8));
            var p = Principal.ForUser("a", Role.Qa);

            var t1 = s.Create(p);
            var t2 = s.Create(p);

            Assert.That(t1, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(t2, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(t1, Is.Not.EqualTo(t2));
        }

        [Test]
        public void Resolve_returns_the_principal_that_was_stored()
        {
            var s = new SessionStore(new FakeClock(), TimeSpan.FromHours(8));
            var p = Principal.ForUser("a", Role.Admin);

            Assert.That(s.Resolve(s.Create(p)), Is.SameAs(p));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("no-such-token")]
        public void Resolve_unknown_or_blank_token_is_null(string token)
        {
            var s = new SessionStore(new FakeClock(), TimeSpan.FromHours(8));

            Assert.That(s.Resolve(token), Is.Null);
        }

        [Test]
        public void Expires_at_is_creation_time_plus_lifetime()
        {
            var clock = new FakeClock();
            var s = new SessionStore(clock, TimeSpan.FromHours(8));
            var created = clock.UtcNow;
            var t = s.Create(Principal.ForUser("a", Role.Qa));
            clock.Advance(TimeSpan.FromHours(3));

            var expires = s.ExpiresAtUtc(t);

            Assert.That(expires, Is.EqualTo(created.AddHours(8)));
            Assert.That(expires.Kind, Is.EqualTo(DateTimeKind.Utc));
        }

        [Test]
        public void Expires_at_of_unknown_token_throws()
        {
            var s = new SessionStore(new FakeClock(), TimeSpan.FromHours(8));

            Assert.Throws<KeyNotFoundException>(() => s.ExpiresAtUtc("nope"));
            Assert.Throws<KeyNotFoundException>(() => s.ExpiresAtUtc(null));
        }

        [Test]
        public void Session_is_expired_exactly_at_its_expiry_time()
        {
            var clock = new FakeClock();
            var s = new SessionStore(clock, TimeSpan.FromHours(8));
            var t = s.Create(Principal.ForUser("a", Role.Qa));

            clock.Advance(TimeSpan.FromHours(8) - TimeSpan.FromTicks(1));
            Assert.That(s.Resolve(t), Is.Not.Null);

            clock.Advance(TimeSpan.FromTicks(1));
            Assert.That(s.Resolve(t), Is.Null);
        }

        [Test]
        public void Expired_session_is_removed()
        {
            var clock = new FakeClock();
            var s = new SessionStore(clock, TimeSpan.FromHours(8));
            var t = s.Create(Principal.ForUser("a", Role.Qa));

            clock.Advance(TimeSpan.FromHours(9));
            Assert.That(s.Resolve(t), Is.Null);

            // Even if the clock were wound back the session must be gone.
            clock.Advance(TimeSpan.FromHours(-9));
            Assert.That(s.Resolve(t), Is.Null);
            Assert.Throws<KeyNotFoundException>(() => s.ExpiresAtUtc(t));
        }

        [Test]
        public void Sessions_are_independent()
        {
            var clock = new FakeClock();
            var s = new SessionStore(clock, TimeSpan.FromHours(8));
            var t1 = s.Create(Principal.ForUser("a", Role.Qa));
            clock.Advance(TimeSpan.FromHours(5));
            var t2 = s.Create(Principal.ForUser("b", Role.Admin));
            clock.Advance(TimeSpan.FromHours(4));

            Assert.That(s.Resolve(t1), Is.Null);
            Assert.That(s.Resolve(t2).Name, Is.EqualTo("b"));
        }

        [Test]
        public void Create_rejects_null_principal()
        {
            var s = new SessionStore(new FakeClock(), TimeSpan.FromHours(8));

            Assert.Throws<ArgumentNullException>(() => s.Create(null));
        }

        [Test]
        public void Concurrent_create_and_resolve_is_safe()
        {
            var clock = new FakeClock();
            var s = new SessionStore(clock, TimeSpan.FromHours(8));
            var tokens = new ConcurrentBag<string>();

            Parallel.For(0, 2000, i =>
            {
                var t = s.Create(Principal.ForUser("u" + i, Role.Qa));
                tokens.Add(t);
                Assert.That(s.Resolve(t).Name, Is.EqualTo("u" + i));
                s.Resolve("missing" + i);
            });

            Assert.That(tokens.Distinct().Count(), Is.EqualTo(2000));

            clock.Advance(TimeSpan.FromHours(9));
            Parallel.ForEach(tokens, t => Assert.That(s.Resolve(t), Is.Null));
        }

        // ---- SystemClock ----

        [Test]
        public void System_clock_reports_utc_now()
        {
            IClock clock = new SystemClock();
            var before = DateTime.UtcNow;
            var now = clock.UtcNow;
            var after = DateTime.UtcNow;

            Assert.That(now.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(now, Is.InRange(before, after));
        }

        // ---- FakeClock ----

        [Test]
        public void Fake_clock_defaults_and_advances()
        {
            var c = new FakeClock();

            Assert.That(c.UtcNow, Is.EqualTo(new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(c.UtcNow.Kind, Is.EqualTo(DateTimeKind.Utc));

            c.Advance(TimeSpan.FromMinutes(5));
            Assert.That(c.UtcNow, Is.EqualTo(new DateTime(2026, 10, 18, 0, 5, 0, DateTimeKind.Utc)));
            Assert.That(c.UtcNow.Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }
}
