using System.ServiceModel;
using System.ServiceModel.Web;
using Interlock.Core.Api;

namespace Interlock.Service.Rest
{
    /// <summary>
    /// The REST API (JSON over HTTP). The templates are relative to the base address, <c>/interlock/v1</c>. Every operation
    /// except <c>session</c> and <c>health</c> needs <c>Authorization: Bearer &lt;token&gt;</c> or <c>X-Api-Key</c>. Ids, flags and
    /// times travel as text so a bad value becomes a JSON 400 of our own instead of a WCF help page.
    /// </summary>
    [ServiceContract]
    public interface IInterlockRestApi
    {
        [OperationContract]
        [WebInvoke(Method = "POST", UriTemplate = "session", RequestFormat = WebMessageFormat.Json, ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        SessionDto Login(SessionRequestDto request);

        /// <summary>201 when the lock was created, 200 when an identical ACTIVE lock already existed.</summary>
        [OperationContract]
        [WebInvoke(Method = "POST", UriTemplate = "locks", RequestFormat = WebMessageFormat.Json, ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        LockDto CreateLock(CreateLockDto request);

        [OperationContract]
        [WebGet(UriTemplate = "locks?status={status}&type={type}&linePath={linePath}&q={q}", ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        LockDto[] QueryLocks(string status, string type, string linePath, string q);

        [OperationContract]
        [WebGet(UriTemplate = "locks/{id}", ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        LockDetailDto GetLock(string id);

        [OperationContract]
        [WebInvoke(Method = "POST", UriTemplate = "locks/{id}/release", RequestFormat = WebMessageFormat.Json, ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        LockDto ReleaseLock(string id, ReasonDto request);

        [OperationContract]
        [WebInvoke(Method = "POST", UriTemplate = "locks/{id}/retry", RequestFormat = WebMessageFormat.Json, ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        LockDto RetryLock(string id);

        [OperationContract]
        [WebGet(UriTemplate = "decisions?uid={uid}&pn={pn}&from={from}&to={to}", ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        DecisionDto[] QueryDecisions(string uid, string pn, string from, string to);

        /// <summary>No authentication. Answers from the last health tick; never calls OIB.</summary>
        [OperationContract]
        [WebGet(UriTemplate = "health", ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        HealthDto GetHealth();

        [OperationContract]
        [WebGet(UriTemplate = "lines", ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        string[] GetLines();

        [OperationContract]
        [WebGet(UriTemplate = "stations", ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        StationDto[] GetStations();

        [OperationContract]
        [WebGet(UriTemplate = "admin/users", ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        UserDto[] ListUsers();

        [OperationContract]
        [WebInvoke(Method = "POST", UriTemplate = "admin/users", RequestFormat = WebMessageFormat.Json, ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        UserDto SaveUser(UserDto request);

        [OperationContract]
        [WebGet(UriTemplate = "admin/apikeys", ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        ApiKeyDto[] ListApiKeys();

        /// <summary>The response carries the plain key; it cannot be read again.</summary>
        [OperationContract]
        [WebInvoke(Method = "POST", UriTemplate = "admin/apikeys", RequestFormat = WebMessageFormat.Json, ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        ApiKeyDto CreateApiKey(ApiKeyDto request);

        [OperationContract]
        [WebInvoke(Method = "PUT", UriTemplate = "admin/apikeys/{id}?active={active}", ResponseFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare)]
        ApiKeyDto SetApiKeyActive(string id, string active);
    }
}
