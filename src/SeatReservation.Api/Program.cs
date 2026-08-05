using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SeatReservation.Api;
using SeatReservation.Api.BackgroundServices;
using SeatReservation.Api.Common;
using SeatReservation.Api.Endpoints;
using SeatReservation.Application.Options;
using SeatReservation.Infrastructure;
using SeatReservation.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Structured logging from the first line, so a failure during startup is captured too.
builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddInfrastructure(builder.Configuration);

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                string.IsNullOrWhiteSpace(jwt.SigningKey) ? new string('0', 32) : jwt.SigningKey)),
            // Default is five minutes, which means an expired token keeps working for five
            // more. On a 15-minute access token that is a third of its lifetime.
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("seat-reservation-api"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();

        // Console exporter in development only. Left on everywhere it writes every trace
        // and every metric export to stdout, which drowns the application's own logs and
        // makes the container's output useless for diagnosing anything.
        if (builder.Environment.IsDevelopment())
            tracing.AddConsoleExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation();

        if (builder.Environment.IsDevelopment())
            metrics.AddConsoleExporter();
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Seat Reservation API",
        Version = "v1",
        Description =
            "Ticketing API demonstrating how a seat is sold exactly once under concurrent demand."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the access token returned by /api/auth/login."
    });

    // Swashbuckle 10 / Microsoft.OpenApi 2 takes a factory here so the requirement can
    // reference a scheme registered on the document being built.
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});

// The request contracts carry [Required], [EmailAddress], [MinLength] and friends, but
// minimal APIs do not act on them by themselves -- without this call every annotation is
// decorative and a three-character password reaches the domain unchallenged.
builder.Services.AddValidation();

var rateLimits = builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>()
                 ?? new RateLimitOptions();

// Guessing a password costs an attacker one request, and nothing above made that request
// any more expensive than a legitimate one -- so /login was a free oracle: unlimited
// attempts, against every account, for as long as anyone cared to keep going.
//
// This does not make guessing impossible. A botnet spreads across addresses and pays the
// limit once per address. It makes it slow, which against an online attack is most of the
// defence. Locking the account instead would trade a guessing problem for a denial-of-
// service one: anyone could lock anyone out by failing to log in as them.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.Credentials, httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            // Partitioned on the caller's address. Behind a proxy that is the proxy unless
            // forwarded headers are configured, and trusting `X-Forwarded-For` without
            // knowing the proxy would let a caller choose its own partition -- so it is
            // deliberately not read here.
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.PermitLimit,
                Window = rateLimits.Window,
                SegmentsPerWindow = rateLimits.SegmentsPerWindow,
                // Reject rather than queue: a caller past the limit should be told so now,
                // not held open on a connection until a slot frees.
                QueueLimit = 0
            }));

    options.OnRejected = async (context, ct) =>
    {
        // The sliding window limiter does not publish RetryAfter metadata, so the header is
        // computed here -- checking the lease first, in case a future limiter does supply
        // it. A permit returns when the oldest segment leaves the window, which is one
        // segment away at the earliest, so that is what a caller is told: a floor, not a
        // promise. Retrying on it and being refused again costs one rejected request; being
        // told nothing at all is what makes clients retry immediately and in a loop.
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var fromLease)
            ? fromLease
            : rateLimits.Window / Math.Max(1, rateLimits.SegmentsPerWindow);

        context.HttpContext.Response.Headers.RetryAfter =
            ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        // Written through the overload that takes a content type: WriteAsJsonAsync sets
        // application/json by itself and would overwrite anything assigned beforehand,
        // leaving a problem document that does not announce itself as one.
        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests.",
            Detail = "Too many attempts from this address. Wait and try again."
        }, options: null, contentType: "application/problem+json", cancellationToken: ct);
    };
});

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<ApplicationDbContext>("database");
builder.Services.AddHostedService<ExpiredHoldSweeper>();
builder.Services.AddHostedService<OutboxDispatcherService>();

var app = builder.Build();

// Before the pipeline is built and before any hosted service runs: the sweeper and the
// outbox dispatcher both query tables that have to exist first.
await DatabaseInitializer.InitializeAsync(app);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();

// Ahead of authentication: rejecting a flood should not first cost a token validation, and
// the endpoints being protected are the ones reached without a token in the first place.
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapEventEndpoints();
app.MapReservationEndpoints();

app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();

/// <summary>Exposed so the integration tests can host the real application through WebApplicationFactory.</summary>
public partial class Program { }
