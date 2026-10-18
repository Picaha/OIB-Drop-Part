using Interlock.Core.Api;
using Interlock.Core.Model;
using Interlock.Ui;
using NUnit.Framework;

namespace Interlock.Tests.Ui
{
    [TestFixture]
    public class SessionTests
    {
        [TestCase("OPERATOR", Role.Operator, false, false)]
        [TestCase("QA", Role.Qa, true, false)]
        [TestCase("ENGINEER", Role.Engineer, true, false)]
        [TestCase("ADMIN", Role.Admin, true, true)]
        public void Derives_rights_from_role(string role, Role expected, bool canRelease, bool isAdmin)
        {
            var s = Session.FromLogin("bob", new SessionDto { role = role, token = "t" });
            Assert.That(s.Username, Is.EqualTo("bob"));
            Assert.That(s.Role, Is.EqualTo(expected));
            Assert.That(s.CanRelease, Is.EqualTo(canRelease));
            Assert.That(s.IsAdmin, Is.EqualTo(isAdmin));
        }

        [Test]
        public void Unknown_role_is_rejected()
        {
            Assert.Throws<System.InvalidOperationException>(() => Session.FromLogin("bob", new SessionDto { role = "GOD" }));
        }
    }
}
