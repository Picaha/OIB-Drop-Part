using System.Windows.Forms;
using Interlock.Core.Api;

namespace Interlock.Ui
{
    /// <summary>The real implementations of the hooks of <see cref="MainForm"/>. Each one shows its dialog modally on the owner.</summary>
    public static class Dialogs
    {
        public static void NewLock(IWin32Window owner, ApiClient client, Session session)
        {
            using (var form = new NewLockForm(client))
                form.ShowDialog(owner);
        }

        public static void Release(IWin32Window owner, ApiClient client, LockDto selected)
        {
            if (selected == null) return;
            using (var form = new ReasonForm("Release lock #" + selected.id, reason => client.Release(selected.id, reason)))
                form.ShowDialog(owner);
        }

        /// <summary><paramref name="selected"/> may be null: the history then opens on the released locks.</summary>
        public static void History(IWin32Window owner, ApiClient client, LockDto selected)
        {
            using (var form = new HistoryForm(client, selected))
                form.ShowDialog(owner);
        }

        public static void Admin(IWin32Window owner, ApiClient client, Session session)
        {
            if (!AdminRules.CanOpen(session))
            {
                MessageBox.Show(owner, "Administration needs the ADMIN role.", "Interlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var form = new AdminForm(client, session))
                form.ShowDialog(owner);
        }
    }
}
