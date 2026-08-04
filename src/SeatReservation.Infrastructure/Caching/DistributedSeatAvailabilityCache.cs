using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using SeatReservation.Application.Abstractions;

namespace SeatReservation.Infrastructure.Caching;

/// <summary>
/// Seat-map cache over IDistributedCache (Redis in compose, in-memory in tests).
///
/// Every cache operation is wrapped: a cache is an optimisation, and an optimisation that
/// can take the API down when Redis is unavailable is a liability. A failed read is
/// treated as a miss and a failed write is dropped, so the request still completes from
/// the database.
/// </summary>
public sealed class DistributedSeatAvailabilityCache : ISeatAvailabilityCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDistributedCache _cache;
    private readonly ILogger<DistributedSeatAvailabilityCache> _logger;

    public DistributedSeatAvailabilityCache(
        IDistributedCache cache, ILogger<DistributedSeatAvailabilityCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            var payload = await _cache.GetStringAsync(key, cancellationToken);
            return payload is null ? null : JsonSerializer.Deserialize<T>(payload, SerializerOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Onbellek okunamadi ({Key}); veritabanina dusuluyor.", key);
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default)
        where T : class
    {
        try
        {
            await _cache.SetStringAsync(
                key,
                JsonSerializer.Serialize(value, SerializerOptions),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Onbellege yazilamadi ({Key}).", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged at warning, not swallowed silently: a failed eviction is how a seat
            // map goes stale and starts showing sold seats as free.
            _logger.LogWarning(ex, "Onbellek girdisi silinemedi ({Key}); TTL doluncaya kadar bayat kalabilir.", key);
        }
    }
}
