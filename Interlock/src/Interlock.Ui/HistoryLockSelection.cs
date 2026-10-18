using System.Globalization;
using Interlock.Core.Api;

namespace Interlock.Ui
{
    /// <summary>Which lock number the "Lock #" box shows: only a selection made by the user may replace it.</summary>
    public static class HistoryLockSelection
    {
        public static string NextIdText(string current, LockDto picked, bool userDriven)
        {
            if (!userDriven || picked == null) return current;
            return picked.id.ToString(CultureInfo.InvariantCulture);
        }
    }
}
