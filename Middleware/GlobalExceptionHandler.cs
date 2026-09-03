using System.Data.Common;
using GymApis.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GymApis.Middleware;

/// <summary>
/// The last line of defence. Every exception that escapes a controller lands
/// here and leaves as problem+json — never as an empty 500, never as a stack
/// trace on the wire.
///
/// Three things it is careful about:
///
///   * <b>Nothing internal escapes.</b> Only <see cref="AppException.PublicMessage"/>
///     is written, and for a 5xx that is a fixed sentence. Connection strings,
///     SQL text and upstream response bodies go to the log with the same
///     traceId the caller is handed, so support can join the two without the
///     caller ever seeing them. The real message is echoed to the response only
///     in Development, under a separate `exception` member.
///
///   * <b>Severity is not uniform.</b> A 404 logs at Information and a 401 at
///     Warning; only a genuine 5xx logs at Error. Logging every client mistake
///     as an error is how the one real incident gets buried.
///
///   * <b>A cancelled request is not an error.</b> When the client hangs up,
///     EF and HttpClient throw <see cref="OperationCanceledException"/>. There
///     is no socket left to answer on, so it is logged at Debug and nothing is
///     written.
///
/// Registered with <c>AddExceptionHandler</c> + <c>UseExceptionHandler</c>, so
/// it also covers exceptions thrown from middleware, not only from MVC.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    /// <summary>
    /// nginx's convention for "client closed the request". Never actually sent
    /// — the connection is already gone — but it keeps the access log honest.
    /// </summary>
    private const int ClientClosedRequest = 499;

    private readonly ILogger<GlobalExceptionHandler> _log;
    private readonly IHostEnvironment                _env;
    private readonly IProblemDetailsService          _problems;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> log,
        IHostEnvironment                env,
        IProblemDetailsService          problems)
    {
        _log      = log;
        _env      = env;
        _problems = problems;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext       context,
        Exception         exception,
        CancellationToken ct)
    {
        // The client hung up. Nothing to write to, and the surrounding
        // OperationCanceledException is expected, not a fault.
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            _log.LogDebug("Request {Method} {Path} was cancelled by the client.",
                context.Request.Method, context.Request.Path);
            context.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        var mapped = Map(exception);

        _log.Log(mapped.LogLevel, exception,
            "{Code} {Status} on {Method} {Path} (trace {TraceId}): {Message}",
            mapped.Code, mapped.Status, context.Request.Method, context.Request.Path,
            ApiProblem.TraceId(context), exception.Message);

        // Headers already flushed — a partially written response cannot be
        // turned into a problem document. Aborting is the only honest option:
        // it surfaces as a broken response rather than as valid-looking
        // truncated data.
        if (context.Response.HasStarted)
        {
            _log.LogWarning(
                "Response for {Method} {Path} had already started; aborting instead of writing a problem document.",
                context.Request.Method, context.Request.Path);
            context.Abort();
            return true;
        }

        context.Response.StatusCode = mapped.Status;

        var problem = ApiProblem.Create(
            context, mapped.Status, ApiProblem.TitleFor(mapped.Status),
            mapped.PublicMessage, mapped.Code, mapped.Errors);

        // Development only: the real message and type, so the Scalar UI and the
        // pytest suite show what actually broke. Never in any other environment
        // — this is exactly the internal detail the mapping above strips out.
        if (_env.IsDevelopment())
        {
            problem.Extensions["exception"] = exception.GetType().FullName;
            problem.Extensions["debugMessage"] = exception.Message;
        }

        return await _problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext      = context,
            ProblemDetails   = problem,
            Exception        = exception,
            AdditionalMetadata = context.Features.Get<IEndpointFeature>()?.Endpoint?.Metadata,
        });
    }

    private sealed record Mapping(
        int      Status,
        string   Code,
        string   PublicMessage,
        object?  Errors,
        LogLevel LogLevel);

    /// <summary>
    /// Framework and driver exceptions we know how to classify. Anything that
    /// falls through is a 500 — the safe default, because an unrecognised
    /// exception is by definition one whose message we have not vetted.
    /// </summary>
    private static Mapping Map(Exception exception) => exception switch
    {
        // Ours: the type already carries status, code and a vetted message.
        AppException app =>
            new(app.StatusCode, app.Code, app.PublicMessage, app.ErrorsPayload, app.LogLevel),

        // Unique violation that a service did not already turn into a friendly
        // message. 409, not 500 — the caller can act on it.
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
            new(StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                "That record already exists.", null, LogLevel.Warning),

        // A CHECK or FK the DTO validation failed to mirror. 400 rather than
        // 500: the request is genuinely bad, and the gap in validation is the
        // bug — which the Error-level log records.
        DbUpdateException { InnerException: PostgresException
            { SqlState: PostgresErrorCodes.CheckViolation
                     or PostgresErrorCodes.ForeignKeyViolation
                     or PostgresErrorCodes.NotNullViolation } } =>
            new(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed,
                "That request was rejected by the database. Check the values and try again.",
                null, LogLevel.Error),

        // Someone else changed or deleted the row mid-update.
        DbUpdateConcurrencyException =>
            new(StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                "That record was changed by someone else. Reload and try again.",
                null, LogLevel.Warning),

        // Postgres is down, refusing connections, or the pool is exhausted.
        // 503 tells a load balancer to take this instance out of rotation.
        NpgsqlException or DbException or TimeoutException =>
            new(StatusCodes.Status503ServiceUnavailable, ErrorCodes.DatabaseUnavailable,
                "The service is temporarily unavailable. Please try again shortly.",
                null, LogLevel.Error),

        // An outbound call (Brevo) timed out or could not connect.
        HttpRequestException =>
            new(StatusCodes.Status502BadGateway, ErrorCodes.UpstreamUnavailable,
                "A service we depend on is unavailable. Please try again shortly.",
                null, LogLevel.Error),

        TaskCanceledException =>
            new(StatusCodes.Status504GatewayTimeout, ErrorCodes.UpstreamTimeout,
                "That request took too long. Please try again.", null, LogLevel.Error),

        // Malformed body, or a body over [RequestSizeLimit] — Kestrel raises
        // this with the status already decided.
        BadHttpRequestException bad =>
            new(bad.StatusCode,
                bad.StatusCode == StatusCodes.Status413PayloadTooLarge
                    ? ErrorCodes.PayloadTooLarge
                    : ErrorCodes.ValidationFailed,
                bad.StatusCode == StatusCodes.Status413PayloadTooLarge
                    ? "That upload is too large."
                    : "The request body could not be read.",
                null, LogLevel.Warning),

        // Thrown by File IO on a permissions problem, not by our authorization
        // — which fails through the policy, never through an exception.
        UnauthorizedAccessException =>
            new(StatusCodes.Status500InternalServerError, ErrorCodes.StorageFailure,
                "Something went wrong on our side. Please try again.", null, LogLevel.Error),

        IOException =>
            new(StatusCodes.Status500InternalServerError, ErrorCodes.StorageFailure,
                "Something went wrong on our side. Please try again.", null, LogLevel.Error),

        _ => new(StatusCodes.Status500InternalServerError, ErrorCodes.Internal,
                 "Something went wrong on our side. Please try again.", null, LogLevel.Error),
    };
}
