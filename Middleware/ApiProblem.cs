using System.Diagnostics;
using GymApis.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace GymApis.Middleware;

/// <summary>
/// One place that builds the error body, so every failure — a thrown
/// <see cref="AppException"/>, a model-validation 400, a bare 401 from the
/// fallback policy, an unhandled crash — comes back in the same shape.
///
/// The shape is RFC 9457 problem+json plus three extensions:
///
/// <code>
/// {
///   "type":    "https://httpstatuses.io/404",
///   "title":   "Not Found",
///   "status":  404,
///   "detail":  "Membership plan 42 was not found.",
///   "instance":"GET /api/memberships/plans/42",
///   "code":    "not_found",              // stable, switch on this
///   "traceId": "00-4bf92f...-01",        // matches the log line
///   "errors":  ["Membership plan 42 was not found."]
/// }
/// </code>
///
/// `errors` is what keeps the SPA working without a change: its `normalizeError`
/// interceptor already folds either a string array or a field-keyed map down
/// into the single `message` the hooks render, and the auth endpoints have
/// always emitted the array form.
/// </summary>
public static class ApiProblem
{
    public static ProblemDetails Create(
        HttpContext context,
        int         status,
        string      title,
        string      detail,
        string      code,
        object?     errors = null)
    {
        var problem = new ProblemDetails
        {
            Status   = status,
            Title    = title,
            Detail   = detail,
            Type     = $"https://httpstatuses.io/{status}",
            Instance = $"{context.Request.Method} {context.Request.Path}",
        };

        problem.Extensions["code"]    = code;
        problem.Extensions["traceId"] = TraceId(context);
        problem.Extensions["errors"]  = errors ?? new[] { detail };

        return problem;
    }

    /// <summary>
    /// The W3C trace id when there is an active Activity, else the connection's
    /// own request id. Either way the value in the response is the value in the
    /// log line, which is the whole point of returning it: a user can quote the
    /// id off a red banner and it finds the exact request.
    /// </summary>
    public static string TraceId(HttpContext context) =>
        Activity.Current?.Id ?? context.TraceIdentifier;

    /// <summary>
    /// Fallback <see cref="ErrorCodes"/> value for a status the caller has not
    /// classified. Used by the status-code-pages handler, where all we have is
    /// the number — the alternative was labelling everything 4xx
    /// "validation_failed", which told a client that a 405 was a bad field.
    /// </summary>
    public static string CodeFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest           => ErrorCodes.ValidationFailed,
        StatusCodes.Status401Unauthorized         => ErrorCodes.Unauthorized,
        StatusCodes.Status403Forbidden            => ErrorCodes.Forbidden,
        StatusCodes.Status404NotFound             => ErrorCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed     => ErrorCodes.MethodNotAllowed,
        StatusCodes.Status406NotAcceptable        => ErrorCodes.NotAcceptable,
        StatusCodes.Status409Conflict             => ErrorCodes.Conflict,
        StatusCodes.Status413PayloadTooLarge      => ErrorCodes.PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => ErrorCodes.UnsupportedMediaType,
        StatusCodes.Status422UnprocessableEntity  => ErrorCodes.BusinessRule,
        StatusCodes.Status429TooManyRequests      => ErrorCodes.TooManyRequests,
        StatusCodes.Status502BadGateway           => ErrorCodes.UpstreamUnavailable,
        StatusCodes.Status503ServiceUnavailable   => ErrorCodes.DatabaseUnavailable,
        StatusCodes.Status504GatewayTimeout       => ErrorCodes.UpstreamTimeout,
        _ => status >= 500 ? ErrorCodes.Internal : ErrorCodes.ValidationFailed,
    };

    /// <summary>Reason phrase for a status code, for the problem `title`.</summary>
    public static string TitleFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest           => "Bad Request",
        StatusCodes.Status401Unauthorized         => "Unauthorized",
        StatusCodes.Status403Forbidden            => "Forbidden",
        StatusCodes.Status404NotFound             => "Not Found",
        StatusCodes.Status405MethodNotAllowed     => "Method Not Allowed",
        StatusCodes.Status406NotAcceptable        => "Not Acceptable",
        StatusCodes.Status409Conflict             => "Conflict",
        StatusCodes.Status413PayloadTooLarge      => "Payload Too Large",
        StatusCodes.Status415UnsupportedMediaType => "Unsupported Media Type",
        StatusCodes.Status422UnprocessableEntity  => "Unprocessable Entity",
        StatusCodes.Status429TooManyRequests      => "Too Many Requests",
        StatusCodes.Status500InternalServerError  => "Internal Server Error",
        StatusCodes.Status502BadGateway           => "Bad Gateway",
        StatusCodes.Status503ServiceUnavailable   => "Service Unavailable",
        StatusCodes.Status504GatewayTimeout       => "Gateway Timeout",
        _ => status >= 500 ? "Server Error" : "Request Error",
    };
}
