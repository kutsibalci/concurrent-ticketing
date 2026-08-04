using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Options;
using SeatReservation.Application.Services;
using SeatReservation.Infrastructure.Caching;
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

        services.AddScoped<ReservationService>();
        services.AddScoped<AuthService>();
        services.AddScoped<EventService>();

        return services;
    }
}
