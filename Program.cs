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
using GymApis.Services.Messaging;
using GymApis.Services.Site;
using GymApis.Services.Staff;
using GymApis.Services.Storage;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

    // Revenue figures. Declared here so a future Payments or Reports controller
    // can carry it as an [Authorize] attribute; the dashboard enforces the same
    // role list inside the service, because there the restriction is two fields
    // of a response rather than the whole endpoint.
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

// Reads only, and composes the two services above rather than recounting what
// they already count — see DashboardService for why that matters.
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
builder.Services.AddCors(o => o.AddPolicy(FrontendCors, p => p
    .WithOrigins(
        builder.Configuration["Frontend:BaseUrl"] ?? "http://localhost:5173",
        "http://localhost:4173")
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
    await SeedOwnerAsync(app);
}

app.UseHttpsRedirection();

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

// After CORS so a preflight is never rate-limited, before authentication so a
// flood costs no token validation.
app.UseRateLimiter();

app.UseAuthentication();

// After UseAuthentication so the log scope can carry the caller's id, before
// UseAuthorization so a 403 is still traceable.
app.UseMiddleware<RequestContextMiddleware>();

app.UseAuthorization();
app.MapControllers();

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

    if (await db.Users.AnyAsync(u => u.TenantId == Tenancy.TenantId && u.Email == email))
        return;

    var user = new User { TenantId = Tenancy.TenantId, Email = email, Role = "owner" };
    user.PasswordHash = hasher.HashPassword(user, password);
    db.Users.Add(user);
    await db.SaveChangesAsync();

    log.LogInformation("Seeded owner account {Email}.", email);
}

// Exposed so the pytest suite and future integration tests can reference the host.
public partial class Program;
