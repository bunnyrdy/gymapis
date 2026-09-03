namespace GymApis.Middleware;

/// <summary>
/// Ties a response to its log lines.
///
/// Every response — success or failure — leaves with an <c>X-Trace-Id</c>
/// header, and every log line written while handling that request carries the
/// same value plus the caller's user id. Without this, "the app showed me an
/// error at about 3pm" is unsearchable; with it, the id off the banner finds
/// the exact request and everything that was logged during it.
///
/// The user id is read from the validated JWT, never from a header, so it
/// cannot be spoofed into someone else's log trail. It is only present after
/// authentication has run — hence the placement of this middleware in
/// Program.cs, immediately after <c>UseAuthentication</c>.
/// </summary>
public sealed class RequestContextMiddleware
{
    private const string TraceHeader = "X-Trace-Id";

    private readonly RequestDelegate                   _next;
    private readonly ILogger<RequestContextMiddleware> _log;

    public RequestContextMiddleware(RequestDelegate next, ILogger<RequestContextMiddleware> log)
    {
        _next = next;
        _log  = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = ApiProblem.TraceId(context);

        // OnStarting, not a direct assignment: headers must be set before the
        // response begins, and a handler further down may start it at any point.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[TraceHeader] = traceId;
            return Task.CompletedTask;
        });

        var userId = context.User.FindFirst("sub")?.Value
                  ?? context.User.FindFirst(
                         "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

        using (_log.BeginScope(new Dictionary<string, object?>
        {
            ["TraceId"] = traceId,
            ["UserId"]  = userId,
        }))
        {
            await _next(context);
        }
    }
}
