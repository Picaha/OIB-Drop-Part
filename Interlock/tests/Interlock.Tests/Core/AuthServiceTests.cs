using System;
using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Security;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class AuthServiceTests
    {
        // PBKDF2 at 100 000 rounds is slow enough to matter across this many tests: hash "1234" once.
        private static readonly string Hash1234 = PinHasher.Hash("1234");

        private static readonly Principal Admin = Principal.ForUser("root", Role.Admin);
        private static readonly Principal Qa = Principal.ForUser("qa1", Role.Qa);

        private FakeClock clock;
        private InMemoryUserRepository users;
        private SessionStore sessions;
        private AuthService auth;

        [SetUp]
        public void SetUp()
        {
            clock = new FakeClock();
            users = new InMemoryUserRepository();
            sessions = new SessionStore(clock, TimeSpan.FromHours(8));
            auth = new AuthService(users, sessions, clock);
        }

        private static AppUser User(string name, Role role, string pinHash, bool active = true)
        {
            return new AppUser
            {
                Username = name,
                PinHash = pinHash,
                Role = role,
                Active = active,
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
        }

        private void SeedAdmin(string name = "root", bool active = true)
        {
            users.SaveUser(User(name, Role.Admin, Hash1234, active));
        }

        private static InterlockException Fails(TestDelegate act)
        {
            return Assert.Throws<InterlockException>(act);
        }

        // ---- Login ----

        [Test] public void Login_ok_returns_token_and_role() { users.SaveUser(User("qa1", Role.Qa, PinHasher.Hash("1234"))); var r = auth.Login("QA1", "1234"); Assert.That(r.Role, Is.EqualTo(Role.Qa)); Assert.That(auth.Authenticate(r.Token, null).Name, Is.EqualTo("qa1")); }

        [TestCase("qa1", "9999")] [TestCase("nobody", "1234")]
        public void Login_failures_share_message(string u, string pin) { users.SaveUser(User("qa1", Role.Qa, Hash1234)); var ex = Assert.Throws<InterlockException>(() => auth.Login(u, pin)); Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Unauthenticated)); Assert.That(ex.Message, Is.EqualTo("Invalid username or PIN")); }

        [Test] public void Inactive_user_cannot_login() { users.SaveUser(User("old", Role.Qa, PinHasher.Hash("1234"), active: false)); Assert.That(Assert.Throws<InterlockException>(() => auth.Login("old", "1234")).Message, Is.EqualTo("Invalid username or PIN")); }

        [Test] public void Initial_user_can_log_in() { auth.CreateInitialUser("admin", Role.Admin, "4321"); Assert.That(auth.Login("admin", "4321").Role, Is.EqualTo(Role.Admin)); }

        [Test]
        public void Inactive_user_failure_is_unauthenticated()
        {
            users.SaveUser(User("old", Role.Qa, Hash1234, active: false));

            Assert.That(Fails(() => auth.Login("old", "1234")).Kind, Is.EqualTo(ErrorKind.Unauthenticated));
        }

        [TestCase(null, "1234")]
        [TestCase("", "1234")]
        [TestCase("   ", "1234")]
        [TestCase("qa1", null)]
        [TestCase("qa1", "")]
        [TestCase("qa1", "   ")]
        [TestCase(null, null)]
        public void Login_with_missing_username_or_pin_is_unauthenticated(string username, string pin)
        {
            users.SaveUser(User("qa1", Role.Qa, Hash1234));

            var ex = Fails(() => auth.Login(username, pin));

            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Unauthenticated));
            Assert.That(ex.Message, Is.EqualTo("Invalid username or PIN"));
        }

        [Test]
        public void Login_result_carries_the_session_expiry()
        {
            users.SaveUser(User("qa1", Role.Qa, Hash1234));

            var r = auth.Login("qa1", "1234");

            Assert.That(r.ExpiresAtUtc, Is.EqualTo(clock.UtcNow + TimeSpan.FromHours(8)));
            Assert.That(r.ExpiresAtUtc, Is.EqualTo(sessions.ExpiresAtUtc(r.Token)));
        }

        [Test]
        public void Login_principal_uses_the_stored_username_and_role()
        {
            users.SaveUser(User("Eng1", Role.Engineer, Hash1234));

            var p = auth.Authenticate(auth.Login("ENG1", "1234").Token, null);

            Assert.That(p.Name, Is.EqualTo("Eng1"));
            Assert.That(p.Role, Is.EqualTo(Role.Engineer));
            Assert.That(p.IsApiKey, Is.False);
        }

        [Test]
        public void Login_trims_the_username_but_not_the_pin()
        {
            users.SaveUser(User("qa1", Role.Qa, Hash1234));

            Assert.That(auth.Login("  qa1 ", "1234").Role, Is.EqualTo(Role.Qa));
            Assert.That(Fails(() => auth.Login("qa1", " 1234")).Kind, Is.EqualTo(ErrorKind.Unauthenticated));
        }

        [Test]
        public void Each_login_gets_its_own_token()
        {
            users.SaveUser(User("qa1", Role.Qa, Hash1234));

            Assert.That(auth.Login("qa1", "1234").Token, Is.Not.EqualTo(auth.Login("qa1", "1234").Token));
        }

        // ---- Authenticate ----

        [Test] public void Api_key_authenticates_until_deactivated() { var k = auth.CreateApiKey(Admin, "mes", true); Assert.That(auth.Authenticate(null, k.PlainKey).CanUnlock, Is.True); auth.SetApiKeyActive(Admin, k.Record.Id, false); Assert.Throws<InterlockException>(() => auth.Authenticate(null, k.PlainKey)); }

        [Test] public void No_credentials_is_unauthenticated() => Assert.That(Assert.Throws<InterlockException>(() => auth.Authenticate(null, null)).Kind, Is.EqualTo(ErrorKind.Unauthenticated));

        [TestCase(null, "")]
        [TestCase("", null)]
        [TestCase("  ", "  ")]
        public void Blank_credentials_count_as_absent(string token, string key)
        {
            Assert.That(Fails(() => auth.Authenticate(token, key)).Kind, Is.EqualTo(ErrorKind.Unauthenticated));
        }

        [Test]
        public void Inactive_api_key_is_unauthenticated()
        {
            var k = auth.CreateApiKey(Admin, "mes", true);
            auth.SetApiKeyActive(Admin, k.Record.Id, false);

            Assert.That(Fails(() => auth.Authenticate(null, k.PlainKey)).Kind, Is.EqualTo(ErrorKind.Unauthenticated));
        }

        [Test]
        public void Reactivated_api_key_authenticates_again()
        {
            var k = auth.CreateApiKey(Admin, "mes", false);
            auth.SetApiKeyActive(Admin, k.Record.Id, false);
            auth.SetApiKeyActive(Admin, k.Record.Id, true);

            Assert.That(auth.Authenticate(null, k.PlainKey).Name, Is.EqualTo("mes"));
        }

        [Test]
        public void Unknown_api_key_is_unauthenticated()
        {
            auth.CreateApiKey(Admin, "mes", true);

            Assert.That(Fails(() => auth.Authenticate(null, ApiKeyHasher.NewKey())).Kind, Is.EqualTo(ErrorKind.Unauthenticated));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Api_key_principal_has_the_key_name_flag_and_no_role(bool canUnlock)
        {
            var k = auth.CreateApiKey(Admin, "mes", canUnlock);

            var p = auth.Authenticate(null, k.PlainKey);

            Assert.That(p.Name, Is.EqualTo("mes"));
            Assert.That(p.IsApiKey, Is.True);
            Assert.That(p.CanUnlock, Is.EqualTo(canUnlock));
            Assert.That(p.Role, Is.Null);
        }

        [Test]
        public void Invalid_bearer_token_is_unauthenticated_even_with_a_valid_api_key()
        {
            var k = auth.CreateApiKey(Admin, "mes", true);

            Assert.That(Fails(() => auth.Authenticate("not-a-token", k.PlainKey)).Kind, Is.EqualTo(ErrorKind.Unauthenticated));
        }

        [Test]
        public void Expired_bearer_token_is_unauthenticated_even_with_a_valid_api_key()
        {
            users.SaveUser(User("qa1", Role.Qa, Hash1234));
            var token = auth.Login("qa1", "1234").Token;
            var k = auth.CreateApiKey(Admin, "mes", true);
            clock.Advance(TimeSpan.FromHours(8));

            Assert.That(Fails(() => auth.Authenticate(token, k.PlainKey)).Kind, Is.EqualTo(ErrorKind.Unauthenticated));
        }

        [Test]
        public void Valid_bearer_token_wins_over_the_api_key()
        {
            users.SaveUser(User("qa1", Role.Qa, Hash1234));
            var token = auth.Login("qa1", "1234").Token;
            var k = auth.CreateApiKey(Admin, "mes", true);

            var p = auth.Authenticate(token, k.PlainKey);

            Assert.That(p.Name, Is.EqualTo("qa1"));
            Assert.That(p.IsApiKey, Is.False);
        }

        [Test]
        public void Blank_bearer_token_falls_back_to_the_api_key()
        {
            var k = auth.CreateApiKey(Admin, "mes", true);

            Assert.That(auth.Authenticate("  ", k.PlainKey).Name, Is.EqualTo("mes"));
        }

        [Test]
        public void Bearer_token_authenticates_without_touching_the_database()
        {
            users.SaveUser(User("qa1", Role.Qa, Hash1234));
            var token = auth.Login("qa1", "1234").Token;
            users.Down = true;

            Assert.That(auth.Authenticate(token, null).Name, Is.EqualTo("qa1"));
        }

        // ---- Admin permission ----

        [Test] public void Non_admin_cannot_manage_users() => Assert.That(Assert.Throws<InterlockException>(() => auth.ListUsers(Qa)).Kind, Is.EqualTo(ErrorKind.Forbidden));

        [TestCase(Role.Operator)]
        [TestCase(Role.Qa)]
        [TestCase(Role.Engineer)]
        public void Every_admin_call_is_forbidden_for_non_admin_roles(Role role)
        {
            AssertAllAdminCallsForbidden(Principal.ForUser("u", role));
        }

        [Test]
        public void Every_admin_call_is_forbidden_for_api_keys()
        {
            AssertAllAdminCallsForbidden(Principal.ForApiKey("mes", true));
        }

        private void AssertAllAdminCallsForbidden(Principal p)
        {
            TestDelegate[] calls =
            {
                () => auth.ListUsers(p),
                () => auth.SaveUser(p, "new", Role.Qa, true, "1234"),
                () => auth.CreateApiKey(p, "mes", false),
                () => auth.ListApiKeys(p),
                () => auth.SetApiKeyActive(p, 1, false)
            };

            foreach (var call in calls)
                Assert.That(Fails(call).Kind, Is.EqualTo(ErrorKind.Forbidden));

            Assert.That(users.ListUsers(), Is.Empty);
            Assert.That(users.ListApiKeys(), Is.Empty);
        }

        [Test]
        public void Admin_calls_reject_a_null_principal()
        {
            Assert.Throws<ArgumentNullException>(() => auth.ListUsers(null));
            Assert.Throws<ArgumentNullException>(() => auth.SaveUser(null, "new", Role.Qa, true, "1234"));
            Assert.Throws<ArgumentNullException>(() => auth.CreateApiKey(null, "mes", false));
            Assert.Throws<ArgumentNullException>(() => auth.ListApiKeys(null));
            Assert.Throws<ArgumentNullException>(() => auth.SetApiKeyActive(null, 1, false));
        }

        // ---- ListUsers ----

        [Test]
        public void ListUsers_returns_every_user_without_pin_hashes()
        {
            SeedAdmin();
            users.SaveUser(User("qa1", Role.Qa, Hash1234, active: false));

            var list = auth.ListUsers(Admin);

            Assert.That(list.Select(u => u.Username), Is.EqualTo(new[] { "root", "qa1" }));
            Assert.That(list.Select(u => u.Active), Is.EqualTo(new[] { true, false }));
            Assert.That(list.Select(u => u.Role), Is.EqualTo(new[] { Role.Admin, Role.Qa }));
            Assert.That(list.All(u => u.PinHash == null), Is.True);
            Assert.That(users.FindUser("root").PinHash, Is.EqualTo(Hash1234), "the stored hash must be untouched");
        }

        // ---- SaveUser: create ----

        [Test]
        public void SaveUser_creates_a_user_who_can_log_in()
        {
            auth.SaveUser(Admin, "op1", Role.Operator, true, "5678");

            var r = auth.Login("op1", "5678");

            Assert.That(r.Role, Is.EqualTo(Role.Operator));
            var stored = users.FindUser("op1");
            Assert.That(stored.Active, Is.True);
            Assert.That(stored.PinHash, Does.StartWith("pbkdf2-sha256$100000$"));
            Assert.That(stored.PinHash, Does.Not.Contain("5678"));
            Assert.That(stored.CreatedAtUtc, Is.EqualTo(clock.UtcNow));
        }

        [Test]
        public void SaveUser_can_create_an_inactive_user()
        {
            auth.SaveUser(Admin, "op1", Role.Operator, false, "5678");

            Assert.That(users.FindUser("op1").Active, Is.False);
            Assert.That(Fails(() => auth.Login("op1", "5678")).Kind, Is.EqualTo(ErrorKind.Unauthenticated));
        }

        [Test]
        public void SaveUser_trims_the_username()
        {
            auth.SaveUser(Admin, "  op1  ", Role.Operator, true, "5678");

            Assert.That(users.ListUsers().Single().Username, Is.EqualTo("op1"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void SaveUser_needs_a_username(string username)
        {
            var ex = Fails(() => auth.SaveUser(Admin, username, Role.Operator, true, "5678"));

            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Validation));
            Assert.That(users.ListUsers(), Is.Empty);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("123")]
        public void SaveUser_new_user_needs_a_pin_of_at_least_4_characters(string pin)
        {
            var ex = Fails(() => auth.SaveUser(Admin, "op1", Role.Operator, true, pin));

            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Validation));
            Assert.That(users.ListUsers(), Is.Empty);
        }

        [Test]
        public void SaveUser_accepts_a_4_character_pin()
        {
            auth.SaveUser(Admin, "op1", Role.Operator, true, "ab12");

            Assert.That(auth.Login("op1", "ab12").Role, Is.EqualTo(Role.Operator));
        }

        // ---- SaveUser: update ----

        [Test]
        public void SaveUser_with_a_null_pin_keeps_the_existing_hash()
        {
            users.SaveUser(User("qa1", Role.Qa, Hash1234));

            auth.SaveUser(Admin, "qa1", Role.Engineer, true, null);

            Assert.That(users.FindUser("qa1").PinHash, Is.EqualTo(Hash1234));
            Assert.That(auth.Login("qa1", "1234").Role, Is.EqualTo(Role.Engineer));
        }

        [TestCase("")]
        [TestCase("   ")]
        public void SaveUser_with_a_blank_pin_keeps_the_existing_hash(string pin)
        {
            users.SaveUser(User("qa1", Role.Qa, Hash1234));

            auth.SaveUser(Admin, "qa1", Role.Qa, true, pin);

            Assert.That(users.FindUser("qa1").PinHash, Is.EqualTo(Hash1234));
        }

        [Test]
        public void SaveUser_with_a_new_pin_replaces_the_hash()
        {
            users.SaveUser(User("qa1", Role.Qa, Hash1234));

            auth.SaveUser(Admin, "qa1", Role.Qa, true, "9876");

            Assert.That(auth.Login("qa1", "9876").Role, Is.EqualTo(Role.Qa));
            Assert.That(Fails(() => auth.Login("qa1", "1234")).Kind, Is.EqualTo(ErrorKind.Unauthenticated));
        }

        [Test]
        public void SaveUser_with_a_short_pin_on_an_existing_user_is_a_validation_error_and_changes_nothing()
        {
            users.SaveUser(User("qa1", Role.Qa, Hash1234));

            var ex = Fails(() => auth.SaveUser(Admin, "qa1", Role.Engineer, false, "12"));

            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Validation));
            var stored = users.FindUser("qa1");
            Assert.That(stored.PinHash, Is.EqualTo(Hash1234));
            Assert.That(stored.Role, Is.EqualTo(Role.Qa));
            Assert.That(stored.Active, Is.True);
        }

        [Test]
        public void SaveUser_updates_role_and_active_and_keeps_created_at()
        {
            var created = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            var original = User("qa1", Role.Qa, Hash1234);
            original.CreatedAtUtc = created;
            users.SaveUser(original);
            clock.Advance(TimeSpan.FromDays(30));

            auth.SaveUser(Admin, "qa1", Role.Engineer, false, null);

            var stored = users.FindUser("qa1");
            Assert.That(stored.Role, Is.EqualTo(Role.Engineer));
            Assert.That(stored.Active, Is.False);
            Assert.That(stored.CreatedAtUtc, Is.EqualTo(created));
        }

        [Test]
        public void SaveUser_matches_an_existing_user_ignoring_case_and_keeps_the_stored_name()
        {
            users.SaveUser(User("qa1", Role.Qa, Hash1234));
            var id = users.FindUser("qa1").Id;

            auth.SaveUser(Admin, " QA1 ", Role.Engineer, true, null);

            var all = users.ListUsers();
            Assert.That(all, Has.Count.EqualTo(1));
            Assert.That(all[0].Username, Is.EqualTo("qa1"));
            Assert.That(all[0].Id, Is.EqualTo(id));
            Assert.That(all[0].Role, Is.EqualTo(Role.Engineer));
            Assert.That(all[0].PinHash, Is.EqualTo(Hash1234));
        }

        // ---- SaveUser: last admin ----

        [Test]
        public void The_last_active_admin_cannot_be_deactivated()
        {
            SeedAdmin();

            var ex = Fails(() => auth.SaveUser(Admin, "root", Role.Admin, false, null));

            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.InvalidState));
            Assert.That(ex.Message, Is.EqualTo("Cannot remove the last active admin"));
            Assert.That(users.FindUser("root").Active, Is.True);
        }

        [Test]
        public void The_last_active_admin_cannot_be_demoted()
        {
            SeedAdmin();

            var ex = Fails(() => auth.SaveUser(Admin, "root", Role.Qa, true, null));

            Assert.That(ex.Kind, Is.EqualTo(ErrorKind.InvalidState));
            Assert.That(ex.Message, Is.EqualTo("Cannot remove the last active admin"));
            Assert.That(users.FindUser("root").Role, Is.EqualTo(Role.Admin));
        }

        [Test]
        public void The_last_active_admin_cannot_be_demoted_by_a_different_case_username()
        {
            SeedAdmin();

            Assert.That(Fails(() => auth.SaveUser(Admin, "ROOT", Role.Operator, true, null)).Kind, Is.EqualTo(ErrorKind.InvalidState));
        }

        [Test]
        public void An_inactive_admin_does_not_count_as_another_admin()
        {
            SeedAdmin();
            SeedAdmin("old", active: false);

            Assert.That(Fails(() => auth.SaveUser(Admin, "root", Role.Admin, false, null)).Kind, Is.EqualTo(ErrorKind.InvalidState));
        }

        [Test]
        public void An_active_non_admin_does_not_count_as_another_admin()
        {
            SeedAdmin();
            users.SaveUser(User("eng1", Role.Engineer, Hash1234));

            Assert.That(Fails(() => auth.SaveUser(Admin, "root", Role.Admin, false, null)).Kind, Is.EqualTo(ErrorKind.InvalidState));
        }

        [Test]
        public void An_admin_can_be_deactivated_or_demoted_while_another_active_admin_remains()
        {
            SeedAdmin("root");
            SeedAdmin("second");

            auth.SaveUser(Admin, "root", Role.Admin, false, null);
            Assert.That(users.FindUser("root").Active, Is.False);

            // "second" is now the last one, so it is protected again.
            Assert.That(Fails(() => auth.SaveUser(Admin, "second", Role.Qa, true, null)).Kind, Is.EqualTo(ErrorKind.InvalidState));

            auth.SaveUser(Admin, "root", Role.Admin, true, null);
            auth.SaveUser(Admin, "second", Role.Qa, true, null);
            Assert.That(users.FindUser("second").Role, Is.EqualTo(Role.Qa));
        }

        [Test]
        public void The_last_active_admin_can_still_change_its_pin_or_stay_as_it_is()
        {
            SeedAdmin();

            auth.SaveUser(Admin, "root", Role.Admin, true, "7777");

            Assert.That(auth.Login("root", "7777").Role, Is.EqualTo(Role.Admin));
        }

        [Test]
        public void An_inactive_admin_can_be_demoted_even_with_no_active_admin()
        {
            SeedAdmin("root");
            SeedAdmin("old", active: false);

            auth.SaveUser(Admin, "old", Role.Qa, false, null);

            Assert.That(users.FindUser("old").Role, Is.EqualTo(Role.Qa));
        }

        // ---- CreateApiKey / ListApiKeys / SetApiKeyActive ----

        [Test]
        public void CreateApiKey_returns_the_plain_key_once_and_stores_only_its_hash()
        {
            var k = auth.CreateApiKey(Admin, "mes", true);

            Assert.That(k.PlainKey, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(k.Record.Id, Is.GreaterThan(0));
            Assert.That(k.Record.Name, Is.EqualTo("mes"));
            Assert.That(k.Record.CanUnlock, Is.True);
            Assert.That(k.Record.Active, Is.True);
            Assert.That(k.Record.CreatedAtUtc, Is.EqualTo(clock.UtcNow));

            var stored = users.ListApiKeys().Single();
            Assert.That(stored.KeyHash, Is.EqualTo(ApiKeyHasher.Hash(k.PlainKey)));
            Assert.That(stored.KeyHash, Is.Not.EqualTo(k.PlainKey));
            Assert.That(users.FindApiKeyByHash(k.PlainKey), Is.Null, "the plain key itself must not be a stored value");
        }

        [Test]
        public void Each_api_key_is_different()
        {
            var a = auth.CreateApiKey(Admin, "a", false);
            var b = auth.CreateApiKey(Admin, "b", false);

            Assert.That(a.PlainKey, Is.Not.EqualTo(b.PlainKey));
            Assert.That(auth.Authenticate(null, a.PlainKey).Name, Is.EqualTo("a"));
            Assert.That(auth.Authenticate(null, b.PlainKey).Name, Is.EqualTo("b"));
        }

        [Test]
        public void CreateApiKey_trims_the_name()
        {
            var k = auth.CreateApiKey(Admin, "  mes  ", false);

            Assert.That(k.Record.Name, Is.EqualTo("mes"));
            Assert.That(users.ListApiKeys().Single().Name, Is.EqualTo("mes"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void CreateApiKey_needs_a_name(string name)
        {
            Assert.That(Fails(() => auth.CreateApiKey(Admin, name, false)).Kind, Is.EqualTo(ErrorKind.Validation));
            Assert.That(users.ListApiKeys(), Is.Empty);
        }

        [Test]
        public void CreateApiKey_rejects_a_duplicate_name_ignoring_case_even_if_inactive()
        {
            var first = auth.CreateApiKey(Admin, "mes", false);
            auth.SetApiKeyActive(Admin, first.Record.Id, false);

            Assert.That(Fails(() => auth.CreateApiKey(Admin, " MES ", true)).Kind, Is.EqualTo(ErrorKind.Validation));
            Assert.That(users.ListApiKeys(), Has.Count.EqualTo(1));
        }

        [Test]
        public void ListApiKeys_returns_active_and_inactive_keys()
        {
            var a = auth.CreateApiKey(Admin, "a", false);
            auth.CreateApiKey(Admin, "b", true);
            auth.SetApiKeyActive(Admin, a.Record.Id, false);

            var list = auth.ListApiKeys(Admin);

            Assert.That(list.Select(k => k.Name), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(list.Select(k => k.Active), Is.EqualTo(new[] { false, true }));
        }

        [Test]
        public void SetApiKeyActive_for_an_unknown_id_is_not_found()
        {
            auth.CreateApiKey(Admin, "mes", false);

            Assert.That(Fails(() => auth.SetApiKeyActive(Admin, 999, false)).Kind, Is.EqualTo(ErrorKind.NotFound));
        }

        // ---- CreateInitialUser ----

        [Test]
        public void CreateInitialUser_trims_the_username_and_stamps_the_creation_time()
        {
            auth.CreateInitialUser("  admin ", Role.Admin, "4321");

            var stored = users.ListUsers().Single();
            Assert.That(stored.Username, Is.EqualTo("admin"));
            Assert.That(stored.Active, Is.True);
            Assert.That(stored.Role, Is.EqualTo(Role.Admin));
            Assert.That(stored.CreatedAtUtc, Is.EqualTo(clock.UtcNow));
            Assert.That(stored.PinHash, Does.StartWith("pbkdf2-sha256$100000$"));
        }

        [TestCase(null, "4321")]
        [TestCase("", "4321")]
        [TestCase("  ", "4321")]
        [TestCase("admin", null)]
        [TestCase("admin", "")]
        [TestCase("admin", "   ")]
        [TestCase("admin", "123")]
        public void CreateInitialUser_validates_like_a_new_user(string username, string pin)
        {
            Assert.That(Fails(() => auth.CreateInitialUser(username, Role.Admin, pin)).Kind, Is.EqualTo(ErrorKind.Validation));
            Assert.That(users.ListUsers(), Is.Empty);
        }

        [Test]
        public void CreateInitialUser_does_not_overwrite_an_existing_user()
        {
            auth.CreateInitialUser("admin", Role.Admin, "4321");

            Assert.That(Fails(() => auth.CreateInitialUser("ADMIN", Role.Operator, "9999")).Kind, Is.EqualTo(ErrorKind.Validation));

            Assert.That(users.ListUsers(), Has.Count.EqualTo(1));
            Assert.That(auth.Login("admin", "4321").Role, Is.EqualTo(Role.Admin));
        }

        // ---- database outage ----

        [Test]
        public void Database_outage_is_reported_as_unavailable()
        {
            var key = auth.CreateApiKey(Admin, "mes", true);
            SeedAdmin();
            users.Down = true;

            TestDelegate[] calls =
            {
                () => auth.Login("root", "1234"),
                () => auth.Authenticate(null, key.PlainKey),
                () => auth.ListUsers(Admin),
                () => auth.SaveUser(Admin, "new", Role.Qa, true, "1234"),
                () => auth.CreateApiKey(Admin, "other", false),
                () => auth.ListApiKeys(Admin),
                () => auth.SetApiKeyActive(Admin, key.Record.Id, false),
                () => auth.CreateInitialUser("first", Role.Admin, "1234")
            };

            foreach (var call in calls)
            {
                var ex = Fails(call);
                Assert.That(ex.Kind, Is.EqualTo(ErrorKind.Unavailable));
                Assert.That(ex.Message, Is.EqualTo("Database unavailable"));
            }
        }

        [Test]
        public void Admin_check_comes_before_the_database()
        {
            users.Down = true;

            Assert.That(Fails(() => auth.ListUsers(Qa)).Kind, Is.EqualTo(ErrorKind.Forbidden));
        }

        // ---- construction ----

        [Test]
        public void Constructor_rejects_null_arguments()
        {
            Assert.Throws<ArgumentNullException>(() => new AuthService(null, sessions, clock));
            Assert.Throws<ArgumentNullException>(() => new AuthService(users, null, clock));
            Assert.Throws<ArgumentNullException>(() => new AuthService(users, sessions, null));
        }
    }
}
