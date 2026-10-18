using System;
using System.Linq;
using System.Net;
using System.Threading;
using Interlock.Core.Api;
using Interlock.Ui;
using NUnit.Framework;

namespace Interlock.Tests.Ui
{
    [TestFixture]
    public class FormSmokeTests
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

        [Test]
        public void Forms_can_be_constructed_without_being_shown()
        {
            OnSta(() =>
            {
                var client = new ApiClient("http://x/interlock/v1", new StubHandler(HttpStatusCode.OK, "[]"));
                using (var login = new LoginForm(client)) { Assert.That(login.IsHandleCreated, Is.False); }
                var session = Session.FromLogin("admin", new SessionDto { role = "ADMIN", token = "t" });
                using (var main = new MainForm(client, session))
                {
                    Assert.That(main.IsHandleCreated, Is.False);
                    var grid = main.Controls.OfType<System.Windows.Forms.DataGridView>().Single();
                    Assert.That(grid.Columns.Cast<System.Windows.Forms.DataGridViewColumn>().Select(c => c.HeaderText).ToArray(),
                        Is.EqualTo(new[] { "#", "Type", "Target", "Lanes", "Line", "Reason", "Source", "By", "Created (local)", "State", "Error" }));
                }
            });
        }
    }
}
