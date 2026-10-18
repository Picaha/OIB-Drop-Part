using Interlock.Core.Text;
using NUnit.Framework;

namespace Interlock.Tests.Core
{
    [TestFixture]
    public class NormalizerTests
    {
        [TestCase(" abc123\r\n", "ABC123")]
        [TestCase("\tMpn-1 ", "MPN-1")]
        [TestCase("x", "X")]
        public void Norm_trims_and_uppercases(string input, string expected)
        {
            Assert.That(Normalizer.Norm(input), Is.EqualTo(expected));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  \r\n")]
        public void Norm_blank_is_null(string input)
        {
            Assert.That(Normalizer.Norm(input), Is.Null);
        }
    }
}
