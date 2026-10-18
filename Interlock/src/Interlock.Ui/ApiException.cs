using System;

namespace Interlock.Ui
{
    /// <summary>A failed service call. <see cref="Status"/> is the HTTP status, or 0 when the service could not be reached.</summary>
    public sealed class ApiException : Exception
    {
        public ApiException(int status, string error, string message) : base(message)
        {
            Status = status;
            Error = error;
        }

        public int Status { get; }

        /// <summary>The machine-readable error code of the <c>{error, message}</c> body; may be null.</summary>
        public string Error { get; }
    }
}
