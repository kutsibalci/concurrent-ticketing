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

/// <summary>
/// Limits on the credential endpoints. Configurable rather than hard-coded because the
/// right number depends on where the API sits: behind a NAT one address is a whole office,
/// and in a test suite twenty deliberate logins in a second are the point of the test.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Requests allowed per window, per client address.</summary>
    public int PermitLimit { get; set; } = 10;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Slices the window so the limit slides instead of resetting on a boundary. With a
    /// fixed window a caller can spend the whole allowance at the end of one window and the
    /// whole allowance at the start of the next, landing twice the limit back to back.
    /// </summary>
    public int SegmentsPerWindow { get; set; } = 6;
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
