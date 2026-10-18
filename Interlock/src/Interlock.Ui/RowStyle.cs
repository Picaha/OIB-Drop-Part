using System.Drawing;

namespace Interlock.Ui
{
    public static class RowStyle
    {
        /// <summary>Row colour for an enforce state; an unknown state is white.</summary>
        public static Color BackColorFor(string enforceState)
        {
            switch (enforceState)
            {
                case "APPLIED": return Color.FromArgb(220, 245, 220);
                case "PENDING": return Color.FromArgb(255, 245, 200);
                case "FAILED": return Color.FromArgb(255, 215, 215);
                case "NOT_NEEDED": return Color.FromArgb(235, 235, 235);
                default: return Color.White;
            }
        }
    }
}
