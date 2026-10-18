using System;

namespace Interlock.Core.Errors
{
    /// <summary>The database cannot be reached. Callers carry on from the cache, the spool or the queue.</summary>
    public sealed class DbUnavailableException : Exception
    {
        public DbUnavailableException(string message) : base(message) { }

        public DbUnavailableException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>An ACTIVE lock with the same identity already exists (the unique index fired).</summary>
    public sealed class DuplicateActiveLockException : Exception
    {
        public DuplicateActiveLockException(string message) : base(message) { }

        public DuplicateActiveLockException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>A call to an OIB service (Setup Center, Line Control, Monitoring) failed.</summary>
    public sealed class OibCallException : Exception
    {
        public OibCallException(string message) : base(message) { }

        public OibCallException(string message, Exception inner) : base(message, inner) { }
    }
}
