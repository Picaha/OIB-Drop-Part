using System;
using System.Configuration;
using System.IO;
using Interlock.Service;
using NUnit.Framework;

namespace Interlock.Tests.Service
{
    [TestFixture]
    public sealed class StartupErrorWriterTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "interlock-startup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_root, true);
        }

        private static Exception Thrown()
        {
            try
            {
                throw new ConfigurationErrorsException("The appSettings key 'Interlock.CoreHost' is required.");
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        [Test]
        public void Falls_back_to_the_next_folder_when_the_first_cannot_be_written()
        {
            // A file where the folder should be: the folder can neither be created nor written.
            var blocked = Path.Combine(_root, "blocked");
            File.WriteAllText(blocked, "x");
            var fallback = Path.Combine(_root, "fallback");

            var path = StartupErrorWriter.Write(Thrown(), new[] { blocked, fallback });

            Assert.That(path, Is.EqualTo(Path.Combine(fallback, "startup-error.txt")));
            var text = File.ReadAllText(path);
            StringAssert.Contains("System.Configuration.ConfigurationErrorsException", text);
            StringAssert.Contains("Interlock.CoreHost", text);
            StringAssert.Contains("at Interlock.Tests.Service.StartupErrorWriterTests.Thrown", text);
        }

        [Test]
        public void Returns_null_and_does_not_throw_when_no_folder_accepts_the_file()
        {
            var blocked = Path.Combine(_root, "blocked");
            File.WriteAllText(blocked, "x");

            Assert.That(StartupErrorWriter.Write(Thrown(), new[] { blocked }), Is.Null);
            Assert.That(StartupErrorWriter.Write(null, null), Is.Null);
        }
    }
}
