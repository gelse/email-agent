using EmailAgent.Api.Audit;
using EmailAgent.Api.Auth;
using EmailAgent.Api.Configuration;
using EmailAgent.Api.Endpoints;
using EmailAgent.Api.Errors;
using EmailAgent.Api.HealthChecks;
using EmailAgent.Api.Options;
using EmailAgent.Api.Sources;
using EmailAgent.Core.Options;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Plugins;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using System.Runtime.InteropServices;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// §16 layered configuration: appsettings.json → appsettings.{Environment}.json
// → environment variables (both added by CreateBuilder) → DB overrides
// (M1T1 no-op stub — M1 has no DB-backed configuration overrides).
// ConfigurationManager implements IConfigurationBuilder.Add explicitly, so the
// source is added through the interface, not on the concrete type.
((Microsoft.Extensions.Configuration.IConfigurationBuilder)builder.Configuration)
    .Add(new DatabaseOverridesConfigurationSource());

// M1T2: EF Core data model — registers EmailAgentDbContext against
// ConnectionStrings:Postgres (ConnectionStrings__Postgres env var).
// M1T3: AddInfrastructure also calls AddEncryption — the Argon2id/AES-GCM
// IEncryptionService plus EncryptionOptions bound from the System section
// (env SYSTEM__KeyEncryptionKey, §16 / Q-E7). ValidateOnStart runs before
// the host starts, so a missing or < 32-byte key fails fast here with a
// clear OptionsValidationException instead of at the first wrap/unwrap.
//
// M1T4: AddInfrastructure also calls AddAuth — IRefreshTokenStore,
// IAuditLogger (AUDIT__HmacKey, §16 / SQ-2) and the Auth:AUDIT__HmacKey
// validation above.
builder.Services.AddInfrastructure(builder.Configuration);

// M1T4 step 6: ASP.NET Core Identity on `users` (IdentityUser<Guid>), roles
// `Admin`/`User` (SQ-10), and JWT bearer authentication for access tokens.
// Password policy per Q-A5 (2FA off) + M1T4 step 6: 12-char minimum, digit
// required. One AddIdentity call (it subsumes AddIdentityCore) registers the
// stores and validators. Cookie-based sign-in is deliberately unused: the SPA
// exchanges credentials for a JWT + the `ea_refresh` refresh cookie (§8).
builder.Services.AddIdentity<User, IdentityRole<Guid>>(options =>
{
    options.Password.RequiredLength = 12;
    options.Password.RequireDigit = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    // Q-A5: 2FA / lockout remain off in M1.
    options.SignIn.RequireConfirmedEmail = false;
})
.AddEntityFrameworkStores<EmailAgentDbContext>()
.AddDefaultTokenProviders();

var jwtSection = builder.Configuration.GetSection(AuthOptions.SectionName);
builder.Services.Configure<AuthOptions>(jwtSection);
builder.Services.Configure<OriginOptions>(options =>
{
    options.AllowedOrigins = builder.Configuration["Spa:AllowedOrigins"] ?? string.Empty;
    options.PublicBaseUrl = builder.Configuration["Api:PublicBaseUrl"] ?? string.Empty;
});

var jwtOptions = jwtSection.Get<AuthOptions>() ?? new AuthOptions();
// AddIdentity (above) configures DefaultChallengeScheme = Identity.Application
// (cookie) — a 302 would be wrong for a JWT-only API surface. These Configure
// actions run after AddIdentity's, so the bearer scheme wins for authenticate,
// challenge and the default scheme (§8: access tokens are the only credential).
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtOptions.SigningKey ?? string.Empty)),
        };

        // M1T6 AC (§13.12): the default challenge writes an empty 401/403 —
        // replace both outcomes with the RFC 7807 problem document so a
        // missing/insufficient token on /v1/account/* is a typed error too.
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await ProblemDetailsHelpers.Problem(
                        context.HttpContext,
                        StatusCodes.Status401Unauthorized,
                        "/errors/unauthorized",
                        "Unauthorized",
                        "A valid access token is required for this endpoint.")
                    .ExecuteAsync(context.HttpContext);
            },
            OnForbidden = async context =>
            {
                await ProblemDetailsHelpers.Problem(
                        context.HttpContext,
                        StatusCodes.Status403Forbidden,
                        "/errors/forbidden",
                        "Forbidden",
                        "You do not have access to this resource.")
                    .ExecuteAsync(context.HttpContext);
            },
        };
    });

builder.Services.AddAuthorization();
// M1T4 task step 5: IJwtTokenIssuer is an Api-layer service (it owns the
// AUTH__SigningKey binding above).
builder.Services.AddSingleton<IJwtTokenIssuer, JwtTokenIssuer>();
builder.Services.AddTransient<OriginValidator>();
builder.Services.AddRateLimitPolicies();

// M1T4 task step 4: optional startup chain verification. On a tampering
// signal the host logs a structured error — it never crashes the process
// (operators run `tools/AuditVerify` from M1T9 for the report).
builder.Services.AddHostedService<AuditChainStartupVerifier>();

// M2T3 (§9 / §4 / §16 / SQ-3 / SQ-6 / Q-O5): the plugin loader.
// - Plugin:SigningKey (env PLUGIN__SigningKey, ≥ 32 bytes) is required at
//   host start — same fail-fast posture as the System/Audit keys above.
// - Registry + gate services are singletons; PluginLoader is registered both
//   as itself (Program's SIGHUP handler resolves it) and as the hosted
//   service that runs one load cycle at startup (task step 8).
builder.Services.AddOptions<PluginOptions>()
    .Bind(builder.Configuration.GetSection(PluginOptions.SectionName))
    .Validate(
        options => PluginOptions.IsValidSigningKey(options.SigningKey),
        PluginOptions.InvalidKeyMessage)
    .ValidateOnStart();

builder.Services.AddSingleton<IPluginRegistry, PluginRegistry>();
builder.Services.AddSingleton<PluginSignatureVerifier>();
builder.Services.AddSingleton<PluginManifestValidator>();
builder.Services.AddSingleton<DefaultTrustAnchor>();
builder.Services.AddSingleton<PluginLoader>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PluginLoader>());

// M2T5 (§5 / §13.3 / SQ-7): the /v1/user/sources service pair. Scoped, not
// singleton, so SourceService, EncryptedSourceConfigStore and AuditLogger all
// resolve the SAME request DbContext — which is what lets source row, config
// blob and audit row commit in one transaction.
builder.Services.AddScoped<EncryptedSourceConfigStore>();
builder.Services.AddScoped<SourceService>();

builder.Host.UseSerilog((context, services, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithEnvironmentName());

// RFC 7807 Problem Details plumbing (exercised from M1T6 on).
builder.Services.AddProblemDetails();

// Swagger generation is on (Swashbuckle, M1T1 step 6); publication and CI
// gating of the spec ship in M6a.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        // M1T1 acceptance criterion: /metrics must expose process_runtime_*
        // series, which only the runtime instrumentation emits.
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

builder.Services.AddHealthChecks()
    // M1T2 step 14: the stable `db` check name + `ready` tag (Q-API40) now
    // back a DbContext CanConnect probe instead of M1T1's raw TCP ping.
    .AddDbContextCheck<EmailAgentDbContext>(name: "db", tags: new[] { "ready" })
    .AddCheck<BlobVolumeHealthCheck>("blob-volume", tags: new[] { "ready" });

var app = builder.Build();

// Q-A13 (§13.9 / §17): the API speaks plain HTTP internally only — TLS is
// terminated at the edge (nginx in dev, traefik in testing/prod), so the
// edge proxy's forwarded headers must be honoured before anything scheme-
// or IP-sensitive runs (Serilog request logging, the refresh-path Origin
// gate, Secure-cookie endpoints, per-IP rate limiting, audit client IPs):
//   X-Forwarded-Proto → Request.Scheme becomes https behind the edge
//   (the Origin gate's {scheme}://{host} comparison matches a browser at
//   https://localhost:8443 / the testing domain).
//   X-Forwarded-For    → Connection.RemoteIpAddress becomes the real client
//   IP (rate-limit partitions + audit rows) instead of the proxy hop.
// KnownProxies/KnownNetworks are cleared: every peer that can reach the api
// process IS a trusted edge hop by construction — the api service publishes
// no host port in dev (M1T8 same-origin topology) and sits behind traefik in
// testing. What such a hop forwards is trusted verbatim, so the edge contract
// is: it must APPEND the client address it itself observed as the rightmost
// X-Forwarded-For entry (nginx's $proxy_add_x_forwarded_for does; the traefik
// edge in testing must be configured to do the same). ForwardLimit stays at
// its default of 1, so only that rightmost, proxy-observed entry is consumed
// and any client-supplied values to its left are ignored — clients cannot
// spoof their IP (or rotate rate-limit/audit partitions) through the edge.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeaders.KnownProxies.Clear();
forwardedHeaders.KnownNetworks.Clear();
app.UseForwardedHeaders(forwardedHeaders);

app.UseSerilogRequestLogging();

// M1T6 AC (§13.12): unhandled exceptions surface as an RFC 7807 500
// (/errors/server-error) instead of an empty body. Registered inside any
// auto-added DeveloperExceptionPage (Development) so this writer wins, and
// the exception is logged rather than swallowed silently.
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var logger = context.RequestServices
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("EmailAgent.Api.ExceptionHandler");
    logger.LogError(exception, "Unhandled exception while serving {Path}.", context.Request.Path);

    await ProblemDetailsHelpers.Problem(
            context,
            StatusCodes.Status500InternalServerError,
            "/errors/server-error",
            "Internal Server Error",
            "An unexpected error occurred.")
        .ExecuteAsync(context);
}));

// M1T6: the LoginPerAccount rate-limit partition keys on the `email` form
// value, and the partition factory is synchronous — buffer the login forms
// here (async) before UseRateLimiter so Request.Form is served from the
// cache without synchronous body IO (Kestrel disables sync IO by default).
app.Use(async (context, next) =>
{
    if (context.Request.HasFormContentType
        && (context.Request.Path.StartsWithSegments("/v1/account/login")
            || context.Request.Path.StartsWithSegments("/v1/account/otp-login")))
    {
        _ = await context.Request.ReadFormAsync(context.RequestAborted);
    }

    await next();
});

// M1T4: authentication/authorization + rate limiting (Q-O7 / §13.14) are in
// the pipeline before any endpoint; the Origin check on the refresh path is
// a path-scoped inline gate per task step 8 (it only guards
// /v1/account/refresh until M1T6 maps the endpoint).
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// §13.14 / AC: X-RateLimit-Limit/-Remaining/-Reset on EVERY authenticated
// response (the built-in limiter exposes no success-path metadata, so the
// 60/min per-user budget + headers live in this middleware; named login
// policies above still own the 5/min 429 path).
app.UseMiddleware<AuthenticatedRateLimitMiddleware>();

// M1T4 task step 8: the Origin gate on the refresh path (§13.9 / §14) —
// runs for every /v1/account/refresh request before M1T6's endpoint sees it.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/v1/account/refresh"))
    {
        var originValidator = context.RequestServices.GetRequiredService<OriginValidator>();
        if (!originValidator.IsAllowed(context))
        {
            await OriginValidator.WriteForbiddenAsync(context, context.RequestAborted);
            return;
        }
    }

    await next();
});

// M1T6: the /v1/account/* namespace (login, otp-login, set-passphrase,
// change-password, refresh, logout) per §13.3 under the §13.7 /v1 prefix.
app.MapAccountEndpoints();

// M1T7: the §13.3 /admin/users namespace — list/get/create/reissue-OTP, all
// [Authorize(Roles = "Admin")], cursor pagination per §13.13.
app.MapAdminUserEndpoints();

// M2T5: the §13.3 /user/sources namespace (CRUD + /test-connection +
// /{id}/fetch-now), [Authorize(Roles = "User")], cursor pagination per §13.13,
// and the M2-only GET /v1/user/account/kdf the SPA derives X-User-Dek from.
// Mapped before the catch-all so the literal routes always outrank it.
app.MapSourcesEndpoints();
app.MapUserAccountEndpoints();

// §13.12 AC: unmatched routes answer with an RFC 7807 404 instead of an
// empty-bodied framework response. A `{**path}` catch-all (every method) is
// used rather than MapFallback's `{*path:nonfile}` pattern so even dotted
// unknown paths are covered; literal/specific routes always outrank it.
app.MapMethods(
    "{**path}",
    new[]
    {
        HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch,
        HttpMethods.Delete, HttpMethods.Head, HttpMethods.Options,
    },
    NotFoundAsProblem);

static async Task NotFoundAsProblem(HttpContext context)
{
    await ProblemDetailsHelpers.Problem(
            context,
            StatusCodes.Status404NotFound,
            "/errors/not-found",
            "Not Found",
            "The requested route does not exist.")
        .ExecuteAsync(context);
}

app.MapGet("/health/live", () => Results.Ok());

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = ReadyHealthCheckResponseWriter.WriteResponse,
});

app.MapPrometheusScrapingEndpoint("/metrics");

// M2T3 task step 8 (§4): reload plugins on SIGHUP — Linux only; Windows
// reloads on restart (M10 verifies), so no handler is installed there.
// DEVIATION (documented in the M2T3 task file): ctx.Cancel = true is set —
// the task's snippet omits it, which would leave SIGHUP's default action in
// place and TERMINATE the process instead of reloading. The registration is
// disposed when the host stops.
PosixSignalRegistration? pluginReloadSignal = null;
if (OperatingSystem.IsLinux())
{
    var pluginLoader = app.Services.GetRequiredService<PluginLoader>();
    pluginReloadSignal = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
    {
        context.Cancel = true;
        _ = pluginLoader.Reload();
    });
    app.Lifetime.ApplicationStopping.Register(() => pluginReloadSignal?.Dispose());
}

app.Run();

// Exposes the entry point to WebApplicationFactory<Program> in
// EmailAgent.IntegrationTests (M1T1 step 6 / test project).
public partial class Program { }
