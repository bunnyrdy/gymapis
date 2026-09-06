using System.Net;
using System.Text;
using GymApis;
using GymApis.Exceptions;
using GymApis.Filters;
using GymApis.Middleware;
using GymApis.Models.Auth;
using GymApis.Models.Jwt;
using GymApis.Repos;
using GymApis.Services.Attendance;
using GymApis.Services.Auth;
using GymApis.Services;
using GymApis.Services.Email;
using GymApis.Services.Dashboard;
using GymApis.Services.Members;
using GymApis.Services.Membership;
using GymApis.Services.Payments;
using GymApis.Services.Messaging;
using GymApis.Services.Site;
using GymApis.Services.Staff;
using GymApis.Services.Storage;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using Scalar.AspNetCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Data. EF maps onto the hand-written schema — there are no migrations.
// ---------------------------------------------------------------------------
builder.Services.AddDbContext<GymDbContext>(o => o
    .UseNpgsql(builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException(
            "Missing ConnectionStrings:Default — set it with dotnet user-secrets."))
    .UseSnakeCaseNamingConvention());

// ---------------------------------------------------------------------------
// Auth
// ---------------------------------------------------------------------------
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Missing the 'Jwt' configuration section.");
if (string.IsNullOrWhiteSpace(jwt.SigningKey))
    throw new InvalidOperationException(
        "Missing Jwt:SigningKey — set it with dotnet user-secrets, never in appsettings.json.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = jwt.Issuer,
            ValidAudience            = jwt.Audience,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew                = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization(options =>
{
    // Staff management is a privilege-escalation surface: whoever can create a
    // staff record could create a privileged one. Restricted to the managerial
    // roles, declared once in AuthPolicies.
    options.AddPolicy(AuthPolicies.ManageStaff, p =>
        p.RequireAuthenticatedUser().RequireRole(AuthPolicies.ManageStaffRoles));

    // Plans set what the gym charges. Same managerial roles, stated separately
    // so pricing and staffing rights can diverge later.
    options.AddPolicy(AuthPolicies.ManagePlans, p =>
        p.RequireAuthenticatedUser().RequireRole(AuthPolicies.ManagePlansRoles));

    // Anything without an explicit [AllowAnonymous] requires a valid token.
    // Fail closed: a new controller is protected before anyone remembers to
    // protect it.
    options.AddPolicy(AuthPolicies.EraseStaff, p =>
        p.RequireAuthenticatedUser().RequireRole(AuthPolicies.EraseStaffRoles));

    options.AddPolicy(AuthPolicies.ManageMembers, p =>
        p.RequireAuthenticatedUser().RequireRole(AuthPolicies.ManageMembersRoles));

    options.AddPolicy(AuthPolicies.EraseMembers, p =>
        p.RequireAuthenticatedUser().RequireRole(AuthPolicies.EraseMembersRoles));

    // Taking the register adds receptionist to the managerial roles — the desk
    // is who watches people arrive. Correcting a record that already exists is
    // not covered here; that endpoint carries ManageStaff instead.
    options.AddPolicy(AuthPolicies.MarkAttendance, p =>
        p.RequireAuthenticatedUser().RequireRole(AuthPolicies.MarkAttendanceRoles));

    // Revenue figures. This was declared expecting a future Payments or Reports
    // controller to carry it as an [Authorize] attribute. Payments arrived and
    // did not: it enforces the same role list inside PaymentService, the way the
    // dashboard does, because the restriction is three fields of a response
    // rather than the whole endpoint. Two reasons it went that way — the front
    // desk takes the payments and needs the page, and two of the five tabs read
    // /api/members, so an endpoint gate would produce a screen where three tabs
    // 403 and two work. Still declared, because a Reports controller is a
    // genuinely whole-endpoint case.
    options.AddPolicy(AuthPolicies.ViewRevenue, p =>
        p.RequireAuthenticatedUser().RequireRole(AuthPolicies.ViewRevenueRoles));

    options.AddPolicy(AuthPolicies.ManageSite, p => p
        .RequireAuthenticatedUser()
        .RequireRole(AuthPolicies.ManageSiteRoles));

    // Narrower than ManageSite on purpose: this is the switch that decides
    // whether the gym's mail leaves the building at all.
    options.AddPolicy(AuthPolicies.ManageSettings, p => p
        .RequireAuthenticatedUser()
        .RequireRole(AuthPolicies.ManageSettingsRoles));

    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// PasswordHasher without the ASP.NET Identity stores: the `users` table is our
// own (bigint PK, citext email, role CHECK), so we want the hashing algorithm
// and nothing else.
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<IReceptionistService, ReceptionistService>();
builder.Services.AddScoped<ITrainerService, TrainerService>();
builder.Services.AddScoped<IMembershipPlanService, MembershipPlanService>();
builder.Services.AddScoped<IMemberService, MemberService>();
builder.Services.AddScoped<IBranchClock, BranchClock>();
builder.Services.AddScoped<IAttendanceService, AttendanceService>();

// The payments console. Reads only, and composes MemberService.StatsAsync for
// its outstanding figures rather than recounting them — the Pending Payments tab
// reads /api/members, so a card counted here would disagree with the rows it
// opens. It also owns the monthly-collection sum the dashboard used to compute.
builder.Services.AddScoped<IPaymentService, PaymentService>();

// Reads only, and composes the services above rather than recounting what they
// already count — see DashboardService for why that matters.
builder.Services.AddScoped<IDashboardService, DashboardService>();

// The website CMS and its anonymous read model. Two interfaces over two
// classes rather than one: the public controller is handed a dependency with no
// write verbs on it, so the visibility and consent filters cannot be bypassed
// by reaching for the wrong method.
builder.Services.AddScoped<ISiteService, SiteService>();
builder.Services.AddScoped<IPublicSiteService, PublicSiteService>();

builder.Services.AddSingleton<IPhotoStorage, PhotoStorage>();

// Closes out unmarked days as Absent once each shift has ended. See
// AttendanceCloseoutWorker for why it is hourly rather than nightly, and
// 005_attendance_module.sql for what it runs.
builder.Services.AddHostedService<AttendanceCloseoutWorker>();

// ---------------------------------------------------------------------------
// Messaging
//
// Nothing sends mail from a request thread any more. A caller enqueues into the
// caller's own transaction; MessageDispatchWorker drains the queue against the
// day's remaining allowance. See 009_messaging.sql for why the reserve is a
// budget partition rather than a sort order, and why the quota day is UTC while
// every other "today" in this codebase is the branch's.
// ---------------------------------------------------------------------------
builder.Services.AddScoped<IMessagingPolicy, MessagingPolicy>();
builder.Services.AddScoped<IMessagingSettings, MessagingSettings>();
builder.Services.AddScoped<IMessageQueue, MessageQueue>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddHostedService<MessageDispatchWorker>();
builder.Services.AddHostedService<RenewalReminderWorker>();

// Brevo when a key is configured; otherwise log the message so the reset flow
// stays testable locally.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Brevo:ApiKey"]))
{
    // Fail at startup rather than on the first send. Without this the missing
    // key surfaces as an InvalidOperationException inside a background worker
    // the first time somebody asks for a reset link — hours later, in a log
    // nobody is reading. Same reasoning as the ConnectionStrings:Default and
    // Jwt:SigningKey checks above.
    if (string.IsNullOrWhiteSpace(builder.Configuration["Brevo:FromEmail"]))
        throw new InvalidOperationException(
            "Brevo:ApiKey is set but Brevo:FromEmail is not. Brevo rejects a send whose "
            + "'from' is not a sender verified in its dashboard — set both with dotnet user-secrets.");

    builder.Services.AddHttpClient<IEmailSender, EmailSender>()
        // The default is 100 seconds. One hung connection would then stall a
        // whole batch for over a minute and a half; the queue is the retry, so
        // the HTTP call should give up quickly and let it do its job.
        .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
}
else
{
    builder.Services.AddSingleton<IEmailSender, LogEmailSender>();
}

// ---------------------------------------------------------------------------
// Errors
//
// One shape for every failure, produced in one place. AddProblemDetails gives
// us the RFC 9457 writer and content negotiation; GlobalExceptionHandler turns
// thrown exceptions into it; InvalidModelStateResponseFactory below does the
// same for model validation; UseStatusCodePages does it for the bodiless
// statuses the framework produces on its own (a 401 from the fallback policy,
// a 404 from an unmatched route).
//
// Without all four, a client has to parse three different error formats
// depending on how far into the pipeline the request got.
// ---------------------------------------------------------------------------
builder.Services.AddProblemDetails(options =>
{
    // The backstop. A controller returning a bare NotFound() or BadRequest()
    // never passes through ApiProblem, so MVC writes a minimal problem document
    // with no `code` and no `errors` — and the SPA's normalizeError has nothing
    // to fold into a message. Rather than hunt down every such call site (and
    // re-hunt them for every module added after this one), fill in the missing
    // members here. Anything already set by ApiProblem is left alone.
    options.CustomizeProblemDetails = context =>
    {
        var problem = context.ProblemDetails;
        var status  = problem.Status ?? context.HttpContext.Response.StatusCode;

        problem.Status ??= status;
        problem.Title  ??= ApiProblem.TitleFor(status);
        problem.Detail ??= ApiProblem.TitleFor(status);
        problem.Instance ??=
            $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";

        problem.Extensions.TryAdd("code", ApiProblem.CodeFor(status));
        problem.Extensions.TryAdd("traceId", ApiProblem.TraceId(context.HttpContext));
        problem.Extensions.TryAdd("errors", new[] { problem.Detail });
    };
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// ---------------------------------------------------------------------------
// Web
// ---------------------------------------------------------------------------
builder.Services.AddControllers(options => options.Filters.Add<PayloadSizeGuard>());

// Liveness for the container runtime and the reverse proxy. It checks the
// database rather than just answering 200, because a process that is up with no
// connection string is not serving anything — and an orchestrator that restarts
// on a real failure is the point of having the endpoint at all.
builder.Services.AddHealthChecks().AddDbContextCheck<GymDbContext>();

// [ApiController]'s automatic 400 emits ValidationProblemDetails, which is
// close to our shape but carries no `code` and no `traceId`. Re-route it
// through ApiProblem so a validation failure is indistinguishable in shape
// from every other error, and `errors` stays the field-keyed map the SPA's
// normalizeError already flattens.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .ToDictionary(
                // The whole-body errors MVC files under "" would render as a
                // blank field name in the UI.
                e => string.IsNullOrEmpty(e.Key) ? "request" : e.Key,
                e => e.Value!.Errors
                    // Only messages our own attributes and IValidatableObject
                    // rules wrote are safe to echo, and those are always keyed
                    // by a field name. An error under the empty key, or one
                    // carrying an exception, was written by the framework while
                    // reading the body — its text names types, byte limits and
                    // file paths, none of which is the caller's business.
                    .Select(x => string.IsNullOrEmpty(e.Key)
                                 || x.Exception is not null
                                 || string.IsNullOrWhiteSpace(x.ErrorMessage)
                        ? "That value is not valid."
                        : x.ErrorMessage)
                    .ToArray());

        var problem = ApiProblem.Create(
            context.HttpContext,
            StatusCodes.Status400BadRequest,
            "Bad Request",
            "One or more fields are invalid.",
            ErrorCodes.ValidationFailed,
            errors);

        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" },
        };
    };
});

// The public website is the first surface an anonymous caller can reach in a
// loop. Fixed window, partitioned by IP, and a rejection is written as the same
// problem+json shape as every other failure rather than a bare 429 — the SPA
// switches on `code`, and TooManyRequests has been in ErrorCodes since the
// beginning waiting for exactly this.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.Public, http =>
        RateLimitPartition.GetFixedWindowLimiter(
            // The socket address, not a forwarded header: X-Forwarded-For is
            // caller-supplied and would let anyone reset their own bucket by
            // changing one string. Behind a reverse proxy, add
            // UseForwardedHeaders with a known-proxy allowlist first.
            partitionKey: http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = RateLimitPolicies.PublicPermitLimit,
                Window = TimeSpan.FromSeconds(RateLimitPolicies.PublicWindowSeconds),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = RateLimitPolicies.PublicQueueLimit,
            }));

    // Sign-in. Same partitioning as above and for the same reason — see the
    // comment on RateLimitPolicies.Auth for why this is applied per action
    // rather than to the whole AuthController.
    //
    // The ceiling is configurable, unlike the public one, for two reasons. A
    // gym behind one office NAT presents every receptionist to us as a single
    // address, so the right number is a property of the deployment rather than
    // of the code — and the fix for "the desk is being locked out" must not be
    // a redeploy. The other reason is the test suite: `tokens` in conftest.py
    // is function-scoped because the refresh-rotation and reuse-detection tests
    // each need their own token family, so a run signs in a few hundred times
    // from 127.0.0.1 and would spend a production-sized budget in seconds. See
    // appsettings.Development.json.
    var authLimit = builder.Configuration.GetSection("RateLimiting:Auth");
    options.AddPolicy(RateLimitPolicies.Auth, http =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authLimit.GetValue("PermitLimit", RateLimitPolicies.AuthPermitLimit),
                Window = TimeSpan.FromSeconds(
                    authLimit.GetValue("WindowSeconds", RateLimitPolicies.AuthWindowSeconds)),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = RateLimitPolicies.AuthQueueLimit,
            }));

    options.OnRejected = async (context, ct) =>
    {
        var problem = ApiProblem.Create(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            ApiProblem.TitleFor(StatusCodes.Status429TooManyRequests),
            "Too many requests. Please wait a moment and try again.",
            ErrorCodes.TooManyRequests);

        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(problem, ct);
    };
});

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type         = SecuritySchemeType.Http,
            Scheme       = "bearer",
            BearerFormat = "JWT",
            In           = ParameterLocation.Header,
            Description  = "Paste your JWT access token (no 'Bearer ' prefix)."
        };
        return Task.CompletedTask;
    });
}); // serves the spec at /openapi/v1.json in Development

const string FrontendCors = "frontend";
// One entry, from configuration. The hardcoded http://localhost:4173 that used
// to sit beside it was for `vite preview`, and it is not needed: the demo and
// production builds both set VITE_API_BASE_URL=/api, so the browser sees a
// single origin and never issues a cross-origin request at all. In production
// this whole policy is inert for the same reason — Caddy serves the SPA and the
// API under one hostname — but it stays wired so a direct-to-API frontend keeps
// working in development.
builder.Services.AddCors(o => o.AddPolicy(FrontendCors, p => p
    .WithOrigins(
        builder.Configuration["Frontend:BaseUrl"] ?? "http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));   // Bearer tokens, not cookies — no AllowCredentials needed

var app = builder.Build();

// First in the pipeline, so it wraps everything after it — including the
// static-file and CORS middleware, which can throw too. Registered before
// UseHttpsRedirection deliberately: an exception must not escape as a raw
// Kestrel 500 just because it happened during a redirect.
app.UseExceptionHandler();

// The framework short-circuits some requests with a status and no body: 401
// from the fallback policy, 403 from a policy, 404 for an unmatched route, 405
// for the wrong verb. Give those the same problem document as everything else
// so the SPA has exactly one error shape to handle.
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    if (response.ContentLength.HasValue || response.HasStarted) return;

    var status = response.StatusCode;
    var problem = ApiProblem.Create(
        context.HttpContext, status, ApiProblem.TitleFor(status),
        status switch
        {
            StatusCodes.Status401Unauthorized => "Authentication is required.",
            StatusCodes.Status403Forbidden    => "You do not have access to that.",
            StatusCodes.Status404NotFound     => "That endpoint does not exist.",
            StatusCodes.Status405MethodNotAllowed => "That method is not allowed on this endpoint.",
            _ => ApiProblem.TitleFor(status),
        },
        ApiProblem.CodeFor(status));

    await context.HttpContext.RequestServices
        .GetRequiredService<IProblemDetailsService>()
        .TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext    = context.HttpContext,
            ProblemDetails = problem,
        });
});

if (app.Environment.IsDevelopment())
{
    // The fallback policy would otherwise lock these behind a token, which is
    // useless for a docs UI. Development-only, so nothing is exposed in prod.
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

// Outside the Development block deliberately. This used to sit inside it,
// which meant a Production database was created with an empty `users` table
// and no route to a first account — the schema seeds a tenant and a branch but
// cannot seed a login, because PasswordHasher produces a versioned PBKDF2 blob
// that can't be hand-written into a .sql file. Nobody could ever sign in.
//
// Safe to run everywhere: it returns immediately when Seed:OwnerEmail and
// Seed:OwnerPassword are absent, and no-ops when that email already exists. In
// production both come from the environment, and Seed__OwnerPassword is
// deleted from the compose .env once the owner has changed it in the UI.
await SeedOwnerAsync(app);

// No UseHttpsRedirection. TLS is terminated at the reverse proxy (see
// deployment/Caddyfile); this process listens on plain HTTP and is never
// published. Left in, it logs "Failed to determine the https port for redirect"
// on every request, and once X-Forwarded-Proto is honoured below it can send a
// request that already arrived over HTTPS back round the loop. The proxy owns
// the redirect and the HSTS header.

// Uploaded photos. Two deliberate choices:
//   * nosniff — a file that is somehow both a valid JPEG and valid HTML can
//     never be re-interpreted as a document, which is what turns an image
//     upload into stored XSS.
//   * an explicit content-type provider with no mappings beyond the formats we
//     accept, so an unexpected extension is served as a download rather than
//     rendered.
//
// The two video types arrived with the public website's gallery. They are here
// and in PhotoStorage.AllowedVideo, and the two lists must stay in step: a
// container this provider does not map is downloaded instead of played, and one
// PhotoStorage does not sniff can never be written in the first place.
var uploadTypes = new FileExtensionContentTypeProvider
{
    Mappings =
    {
        [".jpg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
    }
};
uploadTypes.Mappings.Remove(".svg");

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = uploadTypes,
    ServeUnknownFileTypes = false,
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        // media-src was added for the gallery's videos. Without it a <video>
        // pointed at this origin is blocked with no visible error.
        ctx.Context.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; img-src 'self'; media-src 'self'";
    },
});

app.UseCors(FrontendCors);

// Behind the reverse proxy, every request arrives from the proxy's address on
// the container network. Without this the rate limiter below partitions every
// caller on earth into ONE bucket — its 60-per-minute ceiling becomes a global
// cap, and a single bot locks the marketing site for every real visitor. The
// limiter turns into the denial of service it exists to prevent.
//
// KnownNetworks is an allowlist and must stay one. X-Forwarded-For is a
// caller-supplied string; honoured from an arbitrary source it lets anyone
// reset their own bucket by changing a header. Only the proxy on the pinned
// compose subnet is trusted, and ForwardLimit = 1 means only the hop it added
// is read — a client that pre-populates the header cannot prepend to it.
//
// The subnet is pinned in deployment/docker-compose.yml. The two must agree:
// widen the compose network and this silently stops matching, which shows up
// as one shared bucket again rather than as an error.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit     = 1,
};
foreach (var network in builder.Configuration
             .GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
{
    forwardedHeaders.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
}
// The defaults are loopback only, which is right for a bare-metal run and
// wrong behind compose. Clearing them means an unconfigured deployment trusts
// nothing and partitions on the socket address — degraded, but never spoofable.
if (forwardedHeaders.KnownIPNetworks.Count > 0) forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

// After CORS so a preflight is never rate-limited, before authentication so a
// flood costs no token validation.
app.UseRateLimiter();

app.UseAuthentication();

// After UseAuthentication so the log scope can carry the caller's id, before
// UseAuthorization so a 403 is still traceable.
app.UseMiddleware<RequestContextMiddleware>();

app.UseAuthorization();
app.MapControllers();

// AllowAnonymous is required, not tidiness: the FallbackPolicy would 401 the
// probe, the container runtime would read that as unhealthy, and the API would
// restart-loop forever without a single line explaining why. It exposes nothing
// — a status word, no version, no connection string, no schema.
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();

// ---------------------------------------------------------------------------
// Dev-only seed. PasswordHasher produces a versioned PBKDF2 blob that can't be
// hand-written into a .sql file, so the first owner is created here instead of
// in the schema seed.
// ---------------------------------------------------------------------------
static async Task SeedOwnerAsync(WebApplication app)
{
    var email    = app.Configuration["Seed:OwnerEmail"];
    var password = app.Configuration["Seed:OwnerPassword"];
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        return;

    using var scope = app.Services.CreateScope();
    var db     = scope.ServiceProvider.GetRequiredService<GymDbContext>();
    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
    var log    = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // The first query against a database with no schema throws from deep inside
    // Npgsql — "the data type name 'citext' could not be found" — which is a
    // stack trace about type loading rather than the actual problem, and it
    // kills the process before a single request is served. In a container that
    // is a restart loop with the real cause fifteen frames down. Say it plainly
    // and rethrow: the app genuinely cannot run, but the operator should not
    // have to read Npgsql internals to learn why.
    try
    {
        if (await db.Users.AnyAsync(u => u.TenantId == Tenancy.TenantId && u.Email == email))
            return;
    }
    catch (Exception ex)
    {
        log.LogCritical(ex,
            "Could not read the users table. The schema has probably not been applied to this "
            + "database — run sqlfiles/schema_v1.sql and 002 through 010, in that order. "
            + "In Docker that is deployment/initdb.sh, which only runs on a FRESH volume: "
            + "a database created before it was wired up keeps its empty schema, and the fix "
            + "is to apply the files by hand or recreate the volume.");
        throw;
    }

    var user = new User { TenantId = Tenancy.TenantId, Email = email, Role = "owner" };
    user.PasswordHash = hasher.HashPassword(user, password);
    db.Users.Add(user);
    await db.SaveChangesAsync();

    log.LogInformation("Seeded owner account {Email}.", email);
}

// Exposed so the pytest suite and future integration tests can reference the host.
public partial class Program;
