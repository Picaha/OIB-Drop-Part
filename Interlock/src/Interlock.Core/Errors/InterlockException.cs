using System;

namespace Interlock.Core.Errors
{
    public enum ErrorKind { Validation, Unauthenticated, Forbidden, NotFound, InvalidState, Unavailable }

    /// <summary>A failure the API layer maps to a response; <see cref="Kind"/> picks the status.</summary>
    public sealed class InterlockException : Exception
    {
        public ErrorKind Kind { get; }

        public InterlockException(ErrorKind kind, string message) : base(message)
        {
            Kind = kind;
        }
    }
}
