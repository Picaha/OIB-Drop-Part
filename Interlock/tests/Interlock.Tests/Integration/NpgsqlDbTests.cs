using System;
using System.IO;
using System.Net.Sockets;
using Interlock.Core.Errors;
using Interlock.Service.Data;
using Npgsql;
using NUnit.Framework;

namespace Interlock.Tests.Integration
{
    /// <summary>The parts of <see cref="NpgsqlDb"/> that need no database: error mapping, timeouts and the schema name check.</summary>
    [TestFixture]
    public class NpgsqlDbTests
    {
        private const string Unreachable = "Host=127.0.0.1;Port=1;Timeout=2;Username=x;Password=x;Database=x";

        [Test]
        public void Unreachable_server_maps_to_db_unavailable()
        {
            var bad = new NpgsqlLockRepository(new NpgsqlDb(Unreachable, "interlock_test"));

            Assert.Throws<DbUnavailableException>(() => bad.LoadActive());
        }

        [Test]
        public void Open_maps_an_unreachable_server_to_db_unavailable()
        {
            var ex = Assert.Throws<DbUnavailableException>(() => new NpgsqlDb(Unreachable, "interlock_test").Open());

            Assert.That(ex.InnerException, Is.Not.Null, "the cause is kept");
        }

        // ---- transient SQL states ----

        [TestCase("08000")]
        [TestCase("08001")]
        [TestCase("08006")]
        [TestCase("08P01")]
        [TestCase("53000")]
        [TestCase("53100")]
        [TestCase("53300")]
        [TestCase("57P01")]
        [TestCase("57P02")]
        [TestCase("57P03")]
        [TestCase("40001")]
        [TestCase("40P01")]
        public void Connection_resource_shutdown_and_retryable_states_are_transient(string sqlState)
        {
            Assert.That(NpgsqlDb.IsTransientSqlState(sqlState), Is.True);
        }

        [TestCase("23505")]
        [TestCase("23503")]
        [TestCase("23502")]
        [TestCase("23514")]
        [TestCase("42601")]
        [TestCase("42501")]
        [TestCase("42P01")]
        [TestCase("57014")]
        [TestCase("57P04")]
        [TestCase("28P01")]
        [TestCase("40002")]
        [TestCase("")]
        [TestCase(null)]
        public void Constraint_syntax_permission_and_other_states_are_not_transient(string sqlState)
        {
            Assert.That(NpgsqlDb.IsTransientSqlState(sqlState), Is.False);
        }

        // ---- exception mapping ----

        [Test]
        public void Connection_failures_map_to_db_unavailable_keeping_the_cause()
        {
            var causes = new Exception[]
            {
                new NpgsqlException("Exception while connecting", new SocketException(10061)),
                new SocketException(10061),
                new TimeoutException("timed out"),
                new IOException("stream closed"),
                new PostgresException("the database system is shutting down", "FATAL", "FATAL", "57P03"),
                new PostgresException("terminating connection due to administrator command", "FATAL", "FATAL", "57P01"),
                new PostgresException("could not serialize access", "ERROR", "ERROR", "40001")
            };

            foreach (var cause in causes)
            {
                var mapped = NpgsqlDb.Map(cause);

                Assert.That(mapped, Is.TypeOf<DbUnavailableException>(), cause.GetType().Name + " " + cause.Message);
                Assert.That(mapped.InnerException, Is.SameAs(cause));
            }
        }

        [Test]
        public void Other_failures_are_returned_unchanged()
        {
            var causes = new Exception[]
            {
                new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505"),
                new PostgresException("syntax error", "ERROR", "ERROR", "42601"),
                new PostgresException("permission denied", "ERROR", "ERROR", "42501"),
                new InvalidOperationException("bug"),
                new FormatException("Unknown lock type: 'X'."),
                new DuplicateActiveLockException("duplicate")
            };

            foreach (var cause in causes)
                Assert.That(NpgsqlDb.Map(cause), Is.SameAs(cause), cause.GetType().Name + " " + cause.Message);
        }

        [Test]
        public void A_db_unavailable_exception_is_not_wrapped_again()
        {
            var cause = new DbUnavailableException("down");

            Assert.That(NpgsqlDb.Map(cause), Is.SameAs(cause));
        }

        // ---- timeouts ----

        [Test]
        public void Default_timeouts_are_added_when_the_connection_string_has_none()
        {
            var builder = new NpgsqlConnectionStringBuilder(NpgsqlDb.WithDefaultTimeouts("Host=db1;Username=svc;Database=Drop_Production"));

            Assert.That(builder.Timeout, Is.EqualTo(5));
            Assert.That(builder.CommandTimeout, Is.EqualTo(10));
            Assert.That(builder.Host, Is.EqualTo("db1"));
            Assert.That(builder.Database, Is.EqualTo("Drop_Production"));
        }

        [TestCase("Host=db1;Timeout=15;Command Timeout=30", 15, 30)]
        [TestCase("Host=db1;timeout=2;commandtimeout=60", 2, 60)]
        [TestCase("Host=db1;Timeout=20", 20, 10)]
        [TestCase("Host=db1;CommandTimeout=0", 5, 0)]
        public void Timeouts_set_in_the_connection_string_are_kept(string connectionString, int timeout, int commandTimeout)
        {
            var builder = new NpgsqlConnectionStringBuilder(NpgsqlDb.WithDefaultTimeouts(connectionString));

            Assert.That(builder.Timeout, Is.EqualTo(timeout));
            Assert.That(builder.CommandTimeout, Is.EqualTo(commandTimeout));
        }

        // ---- construction ----

        [TestCase("interlock")]
        [TestCase("interlock_test")]
        [TestCase("_x1")]
        public void Valid_schema_names_are_accepted(string schema)
        {
            Assert.That(new NpgsqlDb("Host=db1", schema).Schema, Is.EqualTo(schema));
        }

        [TestCase("")]
        [TestCase("Interlock")]
        [TestCase("1interlock")]
        [TestCase("inter-lock")]
        [TestCase("interlock.lock")]
        [TestCase("interlock; DROP TABLE x")]
        [TestCase("\"interlock\"")]
        [TestCase("interlock\n")]
        [TestCase(" interlock")]
        public void Schema_names_outside_the_pattern_are_rejected(string schema)
        {
            Assert.Throws<ArgumentException>(() => new NpgsqlDb("Host=db1", schema));
        }

        [Test]
        public void Null_or_blank_arguments_are_rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new NpgsqlDb(null, "interlock"));
            Assert.Throws<ArgumentException>(() => new NpgsqlDb("  ", "interlock"));
            Assert.Throws<ArgumentNullException>(() => new NpgsqlDb("Host=db1", null));
            Assert.Throws<ArgumentNullException>(() => new NpgsqlLockRepository(null));
            Assert.Throws<ArgumentNullException>(() => new NpgsqlAuditRepository(null));
            Assert.Throws<ArgumentNullException>(() => new NpgsqlUserRepository(null));
        }
    }
}
