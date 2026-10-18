using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using Interlock.Core.Api;
using Interlock.Ui;
using NUnit.Framework;

namespace Interlock.Tests.Ui
{
    [TestFixture]
    public class DialogSmokeTests
    {
        private static void OnSta(Action a)
        {
            Exception error = null;
            var t = new Thread(() => { try { a(); } catch (Exception e) { error = e; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.That(t.Join(TimeSpan.FromSeconds(30)), Is.True, "form construction hung");
            if (error != null) throw new Exception("form construction failed", error);
        }

        private static ApiClient Client() { return new ApiClient("http://x/interlock/v1", new StubHandler(HttpStatusCode.OK, "[]")); }
        private static Session As(string role) { return Session.FromLogin("u", new SessionDto { role = role, token = "t" }); }

        [Test]
        public void Dialogs_can_be_constructed_without_being_shown()
        {
            OnSta(() =>
            {
                var client = Client();
                using (var f = new NewLockForm(client)) { Assert.That(f.IsHandleCreated, Is.False); Assert.That(f.AcceptButton, Is.Not.Null); Assert.That(f.CancelButton, Is.Not.Null); Assert.That(f.ShowInTaskbar, Is.False); }
                using (var f = new ReasonForm("Release lock #1", null)) { Assert.That(f.IsHandleCreated, Is.False); Assert.That(f.Reason, Is.EqualTo("")); Assert.That(f.CancelButton, Is.Not.Null); }
                using (var f = new HistoryForm(client, null)) { Assert.That(f.IsHandleCreated, Is.False); Assert.That(f.Controls.OfType<TabControl>().Single().TabPages.Count, Is.EqualTo(3)); }
                using (var f = new HistoryForm(client, new LockDto { id = 4, status = "RELEASED" })) { Assert.That(f.IsHandleCreated, Is.False); }
                using (var f = new AdminForm(client, As("ADMIN"))) { Assert.That(f.IsHandleCreated, Is.False); Assert.That(f.Controls.OfType<TabControl>().Single().TabPages.Count, Is.EqualTo(2)); }
            });
        }

        [Test]
        public void Admin_form_refuses_a_non_admin()
        {
            OnSta(() =>
            {
                Assert.Throws<UnauthorizedAccessException>(() => new AdminForm(Client(), As("QA")));
            });
        }

        [Test]
        public void Main_form_uses_the_real_dialogs_not_the_placeholders()
        {
            OnSta(() =>
            {
                using (var main = new MainForm(Client(), As("ADMIN")))
                {
                    foreach (var d in new Delegate[] { main.NewLockDialog, main.ReleaseDialog, main.HistoryDialog, main.AdminDialog })
                        Assert.That(d.Method.DeclaringType, Is.EqualTo(typeof(Dialogs)));
                }
            });
        }
    }
}
