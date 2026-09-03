namespace GymApis.Exceptions;

/// <summary>
/// Base for every failure this API raises deliberately.
///
/// The rule this codebase already follows (see <c>AuthResult</c> and
/// <c>ServiceResult&lt;T&gt;</c>) still stands: an <em>expected</em> outcome —
/// wrong password, duplicate plan code, unknown id on a lookup the caller is
/// allowed to probe — is returned as a result envelope, not thrown. Exceptions
/// are for the paths where returning is not an option:
///
///   * a helper deep in a call chain that has no envelope to return through,
///   * an invariant that should never have been reachable,
///   * an upstream (database, Brevo, disk) that failed on us.
///
/// Throwing one of these instead of a bare <see cref="Exception"/> buys three
/// things: the caller gets a documented status code rather than a blanket 500,
/// the response carries a stable <see cref="Code"/> the SPA can branch on, and
/// <c>GlobalExceptionHandler</c> logs it at the right severity — a 404 is not
/// an incident and must not page anyone.
/// </summary>
public abstract class AppException : Exception
{
    protected AppException(
        string     code,
        string     message,
        int        statusCode,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code       = code;
        StatusCode = statusCode;
    }

    /// <summary>Stable machine-readable code — see <see cref="ErrorCodes"/>.</summary>
    public string Code { get; }

    /// <summary>HTTP status this failure maps onto.</summary>
    public int StatusCode { get; }

    /// <summary>
    /// Whether <see cref="Exception.Message"/> may be shown to the caller.
    ///
    /// 4xx messages describe what the caller did wrong, so they are safe by
    /// definition. 5xx messages describe what went wrong inside — connection
    /// strings, host names, upstream response bodies — and are logged instead.
    /// A subclass that wraps an upstream failure overrides
    /// <see cref="PublicMessage"/> with something deliberately vague.
    /// </summary>
    public bool IsClientSafe => StatusCode < 500;

    /// <summary>What the caller is told. Never leaks internals for a 5xx.</summary>
    public virtual string PublicMessage =>
        IsClientSafe ? Message : "Something went wrong on our side. Please try again.";

    /// <summary>
    /// The `errors` member of the response. An array of strings by default so
    /// it matches what the SPA's `normalizeError` and the existing auth
    /// endpoints already emit; <see cref="ValidationException"/> overrides it
    /// with a field-keyed map, which that same helper also understands.
    /// </summary>
    public virtual object ErrorsPayload => new[] { PublicMessage };

    /// <summary>
    /// Severity to log at. Client mistakes are warnings at most — logging every
    /// 404 as an error is how a real incident gets lost in the noise.
    /// </summary>
    public virtual LogLevel LogLevel => StatusCode switch
    {
        >= 500 => LogLevel.Error,
        401 or 403 or 429 => LogLevel.Warning,
        _ => LogLevel.Information,
    };
}

// ---------------------------------------------------------------------------
// 4xx — the caller can fix these.
// ---------------------------------------------------------------------------

/// <summary>
/// 404. Prefer <c>ServiceResult.Missing()</c> where a service already returns an
/// envelope; this is for helpers with no envelope to return through.
/// </summary>
public sealed class NotFoundException : AppException
{
    public NotFoundException(string message)
        : base(ErrorCodes.NotFound, message, StatusCodes.Status404NotFound) { }

    /// <summary>"Membership plan 42 was not found."</summary>
    public NotFoundException(string resource, object key)
        : this($"{resource} {key} was not found.") { }
}

/// <summary>
/// 400 with per-field detail, for rules that <c>[ApiController]</c> model
/// validation cannot express — anything needing a database round trip, such as
/// a service id that does not exist or a shift that overlaps an existing one.
/// Attribute and <c>IValidatableObject</c> rules stay where they are; they
/// already produce this same shape.
/// </summary>
public sealed class ValidationException : AppException
{
    private static readonly IReadOnlyDictionary<string, string[]> None =
        new Dictionary<string, string[]>();

    public ValidationException(string message)
        : base(ErrorCodes.ValidationFailed, message, StatusCodes.Status400BadRequest)
        => FieldErrors = None;

    public ValidationException(IReadOnlyDictionary<string, string[]> fieldErrors)
        : base(ErrorCodes.ValidationFailed, "One or more fields are invalid.",
               StatusCodes.Status400BadRequest)
        => FieldErrors = fieldErrors;

    public ValidationException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] }) { }

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }

    public override object ErrorsPayload =>
        FieldErrors.Count > 0 ? FieldErrors : new[] { PublicMessage };
}

/// <summary>
/// 409. The request was valid but lost a race, or collides with existing state
/// — a unique constraint fired, or a row changed under an optimistic update.
/// Distinct from 400: retrying with the same body may well succeed.
/// </summary>
public sealed class ConflictException : AppException
{
    public ConflictException(string message, Exception? inner = null)
        : base(ErrorCodes.Conflict, message, StatusCodes.Status409Conflict, inner) { }
}

/// <summary>
/// 422. The request parsed and every field is individually legal, but it asks
/// for something the domain forbids — archiving a plan that still has active
/// members, assigning a trainer who is off that day.
/// </summary>
public sealed class BusinessRuleException : AppException
{
    public BusinessRuleException(string message)
        : base(ErrorCodes.BusinessRule, message, StatusCodes.Status422UnprocessableEntity) { }
}

/// <summary>
/// 401. Note the auth endpoints deliberately do <em>not</em> use this: login and
/// refresh return one uniform message through <c>AuthResult</c> so the response
/// cannot be used to tell "no such account" from "wrong password". Keep any
/// message thrown here equally uninformative.
/// </summary>
public sealed class UnauthorizedException : AppException
{
    public UnauthorizedException(string message = "Authentication is required.")
        : base(ErrorCodes.Unauthorized, message, StatusCodes.Status401Unauthorized) { }
}

/// <summary>
/// 403 — authenticated, but not allowed. Prefer a policy on the controller so
/// the check happens before any handler code runs; this is for row-level
/// decisions a policy cannot see.
/// </summary>
public sealed class ForbiddenException : AppException
{
    public ForbiddenException(string message = "You do not have access to that.")
        : base(ErrorCodes.Forbidden, message, StatusCodes.Status403Forbidden) { }
}

/// <summary>413 — body over the endpoint's <c>[RequestSizeLimit]</c>.</summary>
public sealed class PayloadTooLargeException : AppException
{
    public PayloadTooLargeException(string message = "That upload is too large.")
        : base(ErrorCodes.PayloadTooLarge, message, StatusCodes.Status413PayloadTooLarge) { }
}

/// <summary>
/// 415. Raised by content sniffing — <c>PhotoStorage</c> reads magic bytes and
/// rejects anything that is not a real JPEG/PNG/WebP, whatever the request
/// claimed.
/// </summary>
public sealed class UnsupportedMediaTypeException : AppException
{
    public UnsupportedMediaTypeException(string message)
        : base(ErrorCodes.UnsupportedMediaType, message,
               StatusCodes.Status415UnsupportedMediaType) { }
}

/// <summary>429 — for when throttling lands.</summary>
public sealed class TooManyRequestsException : AppException
{
    public TooManyRequestsException(string message = "Too many requests. Please slow down.")
        : base(ErrorCodes.TooManyRequests, message, StatusCodes.Status429TooManyRequests) { }
}

// ---------------------------------------------------------------------------
// 5xx — we can fix these. The message is for the log; the caller gets
// PublicMessage, which says nothing about our infrastructure.
// ---------------------------------------------------------------------------

/// <summary>
/// 502 — a third party we depend on failed or answered with an error. Carries
/// the service name so the log line identifies the culprit without the caller
/// learning we use it.
/// </summary>
public sealed class ExternalServiceException : AppException
{
    public ExternalServiceException(string service, string message, Exception? inner = null)
        : base(ErrorCodes.UpstreamUnavailable,
               $"{service} failed: {message}", StatusCodes.Status502BadGateway, inner)
        => Service = service;

    public string Service { get; }

    public override string PublicMessage =>
        "A service we depend on is unavailable. Please try again shortly.";
}

/// <summary>503 — the database is unreachable or refused the connection.</summary>
public sealed class DatabaseUnavailableException : AppException
{
    public DatabaseUnavailableException(string message, Exception? inner = null)
        : base(ErrorCodes.DatabaseUnavailable, message,
               StatusCodes.Status503ServiceUnavailable, inner) { }

    public override string PublicMessage =>
        "The service is temporarily unavailable. Please try again shortly.";
}

/// <summary>
/// 500 — writing or deleting an uploaded file failed. Separate from a generic
/// 500 because the fix is operational (disk full, permissions on wwwroot),
/// not a code change.
/// </summary>
public sealed class StorageException : AppException
{
    public StorageException(string message, Exception? inner = null)
        : base(ErrorCodes.StorageFailure, message,
               StatusCodes.Status500InternalServerError, inner) { }

    public override string PublicMessage =>
        "The file could not be saved. Please try again.";
}
