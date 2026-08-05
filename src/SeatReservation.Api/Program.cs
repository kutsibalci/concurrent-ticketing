using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SeatReservation.Api;
using SeatReservation.Api.BackgroundServices;
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
