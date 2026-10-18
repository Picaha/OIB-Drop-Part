using System;
using System.Net;
using System.ServiceModel.Web;
using Interlock.Core.Api;
using Interlock.Core.Errors;
using Interlock.Core.Ports;

namespace Interlock.Service.Rest
{
    /// <summary>Maps exceptions to the JSON error responses: <c>{"error":"&lt;kind&gt;","message":"..."}</c>.</summary>
    public static class RestErrors
    {
        public const string InternalErrorMessage = "Internal error";

        public static HttpStatusCode StatusFor(ErrorKind k)
        {
            switch (k)
            {
                case ErrorKind.Validation: return HttpStatusCode.BadRequest;
                case ErrorKind.Unauthenticated: return HttpStatusCode.Unauthorized;
                case ErrorKind.Forbidden: return HttpStatusCode.Forbidden;
                case ErrorKind.NotFound: return HttpStatusCode.NotFound;
                case ErrorKind.InvalidState: return HttpStatusCode.Conflict;
                case ErrorKind.Unavailable: return HttpStatusCode.ServiceUnavailable;
                default: return HttpStatusCode.InternalServerError;
            }
        }

        /// <summary>
        /// The fault for this exception. An <see cref="InterlockException"/> keeps its message; anything else is a 500 with the
        /// fixed text "Internal error", and its details go to the log only.
        /// </summary>
        public static WebFaultException<ErrorDto> ToFault(Exception ex, ILog log)
        {
            var fault = ex as WebFaultException<ErrorDto>;
            if (fault != null)
                return fault;

            var known = ex as InterlockException;
            if (known != null)
            {
                return new WebFaultException<ErrorDto>(
                    new ErrorDto { error = known.Kind.ToString().ToLowerInvariant(), message = known.Message },
                    StatusFor(known.Kind));
            }

            if (log != null)
                log.Error("REST request failed: " + ex.GetType().Name + ": " + ex.Message, ex);
            return new WebFaultException<ErrorDto>(
                new ErrorDto { error = "internal", message = InternalErrorMessage },
                HttpStatusCode.InternalServerError);
        }
    }
}
