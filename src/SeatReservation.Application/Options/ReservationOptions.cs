namespace SeatReservation.Application.Options;

public sealed class ReservationOptions
{
    public const string SectionName = "Reservation";

    /// <summary>
    /// How long seats stay held before the sweeper reclaims them. Long enough to finish
    /// a checkout, short enough that an abandoned basket does not hold seats all evening.
    /// </summary>
    public TimeSpan HoldDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the background sweeper looks for lapsed holds.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan SeatMapCacheTtl { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "seat-reservation-api";
    public string Audience { get; set; } = "seat-reservation-clients";

    /// <summary>No default. A signing key with a fallback value is a signing key everyone knows.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);
}
