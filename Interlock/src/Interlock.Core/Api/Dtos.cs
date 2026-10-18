using System.Runtime.Serialization;

namespace Interlock.Core.Api
{
    // The JSON shapes of the REST API. Members are camelCase on the wire, dates are ISO 8601 UTC strings and enums are the
    // database texts (see DtoMapper). Nothing here carries a PIN hash or an API key hash: those never leave the service.

    [DataContract]
    public sealed class LockDto
    {
        [DataMember(Name = "id")] public long id { get; set; }
        [DataMember(Name = "type")] public string type { get; set; }
        [DataMember(Name = "target")] public string target { get; set; }
        [DataMember(Name = "lanes")] public string lanes { get; set; }
        [DataMember(Name = "linePath")] public string linePath { get; set; }
        [DataMember(Name = "reason")] public string reason { get; set; }
        [DataMember(Name = "source")] public string source { get; set; }
        [DataMember(Name = "createdBy")] public string createdBy { get; set; }
        [DataMember(Name = "createdAt")] public string createdAt { get; set; }
        [DataMember(Name = "status")] public string status { get; set; }
        [DataMember(Name = "releasedBy")] public string releasedBy { get; set; }
        [DataMember(Name = "releasedAt")] public string releasedAt { get; set; }
        [DataMember(Name = "releaseReason")] public string releaseReason { get; set; }
        [DataMember(Name = "enforceState")] public string enforceState { get; set; }
        [DataMember(Name = "enforceError")] public string enforceError { get; set; }
    }

    [DataContract]
    public sealed class CreateLockDto
    {
        [DataMember(Name = "type")] public string type { get; set; }
        [DataMember(Name = "target")] public string target { get; set; }
        [DataMember(Name = "lanes")] public string lanes { get; set; }
        [DataMember(Name = "linePath")] public string linePath { get; set; }
        [DataMember(Name = "reason")] public string reason { get; set; }
    }

    [DataContract]
    public sealed class ReasonDto
    {
        [DataMember(Name = "reason")] public string reason { get; set; }
    }

    [DataContract]
    public sealed class SessionRequestDto
    {
        [DataMember(Name = "username")] public string username { get; set; }
        [DataMember(Name = "pin")] public string pin { get; set; }
    }

    [DataContract]
    public sealed class SessionDto
    {
        [DataMember(Name = "token")] public string token { get; set; }
        [DataMember(Name = "role")] public string role { get; set; }
        [DataMember(Name = "expiresAt")] public string expiresAt { get; set; }
    }

    [DataContract]
    public sealed class ActionDto
    {
        [DataMember(Name = "action")] public string action { get; set; }
        [DataMember(Name = "request")] public string request { get; set; }
        [DataMember(Name = "success")] public bool success { get; set; }
        [DataMember(Name = "error")] public string error { get; set; }
        [DataMember(Name = "attempt")] public int attempt { get; set; }
        [DataMember(Name = "createdAt")] public string createdAt { get; set; }
    }

    [DataContract]
    public sealed class LockDetailDto
    {
        [DataMember(Name = "lock")] public LockDto @lock { get; set; }
        [DataMember(Name = "actions")] public ActionDto[] actions { get; set; }
    }

    [DataContract]
    public sealed class DecisionDto
    {
        [DataMember(Name = "method")] public string method { get; set; }
        [DataMember(Name = "reasonCode")] public int? reasonCode { get; set; }
        [DataMember(Name = "uid")] public string uid { get; set; }
        [DataMember(Name = "pn")] public string pn { get; set; }
        [DataMember(Name = "mpn")] public string mpn { get; set; }
        [DataMember(Name = "supplier")] public string supplier { get; set; }
        [DataMember(Name = "linePath")] public string linePath { get; set; }
        [DataMember(Name = "machineId")] public string machineId { get; set; }
        [DataMember(Name = "feederId")] public string feederId { get; set; }
        [DataMember(Name = "result")] public string result { get; set; }
        [DataMember(Name = "message")] public string message { get; set; }
        [DataMember(Name = "matchedLockIds")] public string matchedLockIds { get; set; }
        [DataMember(Name = "createdAt")] public string createdAt { get; set; }
    }

    [DataContract]
    public sealed class StationDto
    {
        [DataMember(Name = "linePath")] public string linePath { get; set; }
        [DataMember(Name = "stationPath")] public string stationPath { get; set; }
    }

    [DataContract]
    public sealed class HealthDto
    {
        [DataMember(Name = "setupCenterConnected")] public bool setupCenterConnected { get; set; }

        /// <summary>Null when the license state is unknown (the call failed or has not run yet).</summary>
        [DataMember(Name = "setupCenterLicense")] public bool? setupCenterLicense { get; set; }

        [DataMember(Name = "lineControlLicense")] public bool? lineControlLicense { get; set; }
        [DataMember(Name = "monitoringOk")] public bool monitoringOk { get; set; }
        [DataMember(Name = "dbOk")] public bool dbOk { get; set; }
        [DataMember(Name = "queueLength")] public int queueLength { get; set; }
        [DataMember(Name = "cacheLoadedAt")] public string cacheLoadedAt { get; set; }
        [DataMember(Name = "lockCount")] public int lockCount { get; set; }
        [DataMember(Name = "failedLocks")] public int failedLocks { get; set; }
        [DataMember(Name = "cacheRefreshFailures")] public int cacheRefreshFailures { get; set; }
        [DataMember(Name = "cacheRefreshError")] public string cacheRefreshError { get; set; }
        [DataMember(Name = "alarms")] public string[] alarms { get; set; }
    }

    /// <summary>
    /// A user. <c>pin</c> is only ever read from a request, never written to a response. In a request a missing <c>active</c>
    /// means active for a new user and "keep the current value" for an existing one.
    /// </summary>
    [DataContract]
    public sealed class UserDto
    {
        [DataMember(Name = "username")] public string username { get; set; }
        [DataMember(Name = "role")] public string role { get; set; }
        [DataMember(Name = "active")] public bool? active { get; set; }
        [DataMember(Name = "pin", EmitDefaultValue = false)] public string pin { get; set; }
    }

    /// <summary>An API key. <c>key</c> is set once, in the response that creates the key.</summary>
    [DataContract]
    public sealed class ApiKeyDto
    {
        [DataMember(Name = "id")] public long id { get; set; }
        [DataMember(Name = "name")] public string name { get; set; }
        [DataMember(Name = "canUnlock")] public bool canUnlock { get; set; }
        [DataMember(Name = "active")] public bool active { get; set; }
        [DataMember(Name = "key", EmitDefaultValue = false)] public string key { get; set; }
    }

    [DataContract]
    public sealed class ErrorDto
    {
        [DataMember(Name = "error")] public string error { get; set; }
        [DataMember(Name = "message")] public string message { get; set; }
    }
}
