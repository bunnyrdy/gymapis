namespace GymApis.Exceptions;

/// <summary>
/// Machine-readable error codes. These travel to the client as the `code`
/// member of the problem document and are part of the API contract: the SPA
/// switches on them, so renaming one is a breaking change. Human-facing wording
/// lives in the exception message and may be reworded freely.
/// </summary>
public static class ErrorCodes
{
    // 4xx — the caller can fix these.
    public const string ValidationFailed     = "validation_failed";
    public const string NotFound             = "not_found";
    public const string Conflict             = "conflict";
    public const string BusinessRule         = "business_rule_violated";
    public const string Unauthorized         = "unauthorized";
    public const string Forbidden            = "forbidden";
    public const string PayloadTooLarge      = "payload_too_large";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string MethodNotAllowed     = "method_not_allowed";
    public const string NotAcceptable        = "not_acceptable";
    public const string TooManyRequests      = "too_many_requests";
    public const string RequestCancelled     = "request_cancelled";

    // 5xx — we can fix these.
    public const string Internal             = "internal_error";
    public const string DatabaseUnavailable  = "database_unavailable";
    public const string UpstreamUnavailable  = "upstream_unavailable";
    public const string UpstreamTimeout      = "upstream_timeout";
    public const string StorageFailure       = "storage_failure";
}
