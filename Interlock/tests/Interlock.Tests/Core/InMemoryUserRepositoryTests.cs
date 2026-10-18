using System;
using System.Collections.Generic;
using System.Linq;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Tests.Fakes;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class InMemoryUserRepositoryTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);

        private InMemoryUserRepository _users;

        [SetUp]
        public void SetUp()
        {
            _users = new InMemoryUserRepository();
        }

        private static AppUser User(string name, Role role = Role.Qa, string pinHash = "hash", bool active = true)
        {
            return new AppUser { Username = name, PinHash = pinHash, Role = role, Active = active, CreatedAtUtc = T0 };
        }

        [Test]
        public void FindUser_ignores_case_and_returns_null_for_unknown_users()
        {
            _users.SaveUser(User("qa1"));

            Assert.That(_users.FindUser("QA1").Username, Is.EqualTo("qa1"));
            Assert.That(_users.FindUser("nobody"), Is.Null);
            Assert.That(_users.FindUser(null), Is.Null);
        }

        [Test]
        public void SaveUser_adds_new_users_with_ids_and_updates_existing_ones_by_name()
        {
            _users.SaveUser(User("qa1", Role.Qa, "old"));
            _users.SaveUser(User("op1", Role.Operator));
            _users.SaveUser(User("QA1", Role.Engineer, "new", active: false));

            var qa = _users.FindUser("qa1");
            Assert.That(_users.ListUsers().Count, Is.EqualTo(2));
            Assert.That(qa.Id, Is.EqualTo(1));
            Assert.That(qa.Role, Is.EqualTo(Role.Engineer));
            Assert.That(qa.PinHash, Is.EqualTo("new"));
            Assert.That(qa.Active, Is.False);
            Assert.That(_users.FindUser("op1").Id, Is.EqualTo(2));
        }

        [Test]
        public void Stored_users_cannot_be_changed_through_returned_objects()
        {
            var input = User("qa1");
            _users.SaveUser(input);

            input.PinHash = "changed after save";
            _users.FindUser("qa1").PinHash = "changed after read";
            _users.ListUsers().Single().PinHash = "changed after list";

            Assert.That(_users.FindUser("qa1").PinHash, Is.EqualTo("hash"));
        }

        [Test]
        public void Api_keys_are_inserted_found_by_hash_and_can_be_deactivated()
        {
            var created = _users.InsertApiKey(new ApiKeyRecord { Name = "mes", KeyHash = "abc123", CanUnlock = true, Active = true, CreatedAtUtc = T0 });
            _users.InsertApiKey(new ApiKeyRecord { Name = "erp", KeyHash = "def456", Active = true, CreatedAtUtc = T0 });

            Assert.That(created.Id, Is.EqualTo(1));
            Assert.That(_users.FindApiKeyByHash("abc123").Name, Is.EqualTo("mes"));
            Assert.That(_users.FindApiKeyByHash("nope"), Is.Null);
            Assert.That(_users.ListApiKeys().Select(k => k.Name), Is.EqualTo(new[] { "mes", "erp" }));

            _users.SetApiKeyActive(created.Id, false);

            Assert.That(_users.FindApiKeyByHash("abc123").Active, Is.False, "an inactive key is still found; callers check Active");
            Assert.That(_users.ListApiKeys().Single(k => k.Name == "erp").Active, Is.True);
        }

        [Test]
        public void Down_makes_every_member_throw_db_unavailable()
        {
            var calls = new Dictionary<string, Action>
            {
                { "FindUser", () => _users.FindUser("a") },
                { "ListUsers", () => _users.ListUsers() },
                { "SaveUser", () => _users.SaveUser(User("a")) },
                { "FindApiKeyByHash", () => _users.FindApiKeyByHash("h") },
                { "ListApiKeys", () => _users.ListApiKeys() },
                { "InsertApiKey", () => _users.InsertApiKey(new ApiKeyRecord { Name = "k", KeyHash = "h" }) },
                { "SetApiKeyActive", () => _users.SetApiKeyActive(1, true) }
            };
            _users.Down = true;

            foreach (var call in calls)
                Assert.Throws<DbUnavailableException>(() => call.Value(), call.Key);

            _users.Down = false;
            Assert.That(_users.ListUsers(), Is.Empty);
            Assert.That(_users.ListApiKeys(), Is.Empty);
        }
    }
}
