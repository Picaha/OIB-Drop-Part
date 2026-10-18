using System.Drawing;
using Interlock.Ui;
using NUnit.Framework;

namespace Interlock.Tests.Ui
{
    [TestFixture]
    public class RowStyleTests
    {
        [TestCase("APPLIED", 220, 245, 220)]
        [TestCase("PENDING", 255, 245, 200)]
        [TestCase("FAILED", 255, 215, 215)]
        [TestCase("NOT_NEEDED", 235, 235, 235)]
        public void Row_colors(string s, int r, int g, int b)
        {
            Assert.That(RowStyle.BackColorFor(s), Is.EqualTo(Color.FromArgb(r, g, b)));
        }

        [TestCase("SOMETHING_ELSE")]
        [TestCase("")]
        [TestCase(null)]
        public void Unknown_state_is_white(string s)
        {
            Assert.That(RowStyle.BackColorFor(s), Is.EqualTo(Color.White));
        }
    }
}
