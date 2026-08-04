using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Options;
using SeatReservation.Application.Services;
using SeatReservation.Infrastructure.Caching;
using SeatReservation.Infrastructure.Messaging;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.Infrastructure.Security;

namespace SeatReservation.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.SigningKey) && o.SigningKey.Length >= 32,
                "Jwt:SigningKey must be at least 32 characters.")
            // Validated at startup, not on first use: a misconfigured signing key should
            // stop deployment, not surface as a 500 on the first login attempt.
            .ValidateOnStart();

        services.AddOptions<ReservationOptions>()
            .Bind(configuration.GetSection(ReservationOptions.SectionName))
            .Validate(o => o.HoldDuration > TimeSpan.Zero, "Reservation:HoldDuration must be positive.")
            .ValidateOnStart();

        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .Validate(o => o.BatchSize > 0, "Outbox:BatchSize must be positive.")
            .Validate(o => o.MaxAttempts > 0, "Outbox:MaxAttempts must be positive.")
            .ValidateOnStart();

        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .ValidateOnStart();

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Postgres"),
                npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null)));

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        var redis = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redis))
        {
            // Keeps the API runnable with nothing but a database — useful in tests and
            // for anyone cloning the repository who does not want to start Redis.
            services.AddDistributedMemoryCache();
        }
        else
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redis;
                options.InstanceName = "seatres:";
            });
        }

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordHasher>(_ => new Pbkdf2PasswordHasher());
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<ISeatAvailabilityCache, DistributedSeatAvailabilityCache>();

        var rabbit = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        if (rabbit.Enabled && !string.IsNullOrWhiteSpace(rabbit.Host))
        {
            services.AddSingleton<RabbitMqConnection>();
            services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        }
        else
        {
            // The outbox still records everything; the rows simply wait for a broker.
            // Nothing is lost, and the API runs on a database alone.
            services.AddSingleton<IEventPublisher, NoOpEventPublisher>();
        }

        services.AddScoped<ReservationService>();
        services.AddScoped<AuthService>();
        services.AddScoped<EventService>();
        services.AddScoped<OutboxDispatcher>();

        return services;
    }
}
