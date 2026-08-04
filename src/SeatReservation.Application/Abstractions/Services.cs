using SeatReservation.Domain.Entities;

namespace SeatReservation.Application.Abstractions;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string storedHash);
}

public sealed record TokenPair(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt);

public interface ITokenService
{
    string CreateAccessToken(User user);

    /// <summary>Returns the raw token to hand to the client and the hash to persist. The raw value is never stored.</summary>
    (string Raw, string Hash) CreateRefreshToken();

    string HashRefreshToken(string rawToken);
}

/// <summary>
/// Cache over the per-event seat map.
///
/// The seat map is read on every page load and changes only when somebody reserves, so it
/// is the one query worth caching. Every write path invalidates the event's entry — a
/// stale seat map showing a taken seat as free is worse than no cache at all.
/// </summary>
public interface ISeatAvailabilityCache
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class;
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    static string SeatMapKey(Guid eventId) => $"event:{eventId}:seatmap";
}
