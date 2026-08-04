using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SeatReservation.Application.Contracts;
using SeatReservation.Domain.Entities;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests;

/// <summary>
/// The reason this project exists: a seat must be sold exactly once, whatever the timing.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ConcurrencyTests : IAsyncLifetime
{
    private readonly PostgresApiFactory _factory;

    public ConcurrencyTests(PostgresApiFactory factory) => _factory = factory;

    public async ValueTask InitializeAsync() => await _factory.ResetDatabaseAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(Guid EventId, List<Guid> SeatIds)> SeedEventAsync(int seatCount)
    {
        await using var db = _factory.CreateDbContext();

        var @event = Event.Create("Final Maçı", "Arena", DateTimeOffset.UtcNow.AddDays(7));
        @event.AddSeatBlock("A", 1, seatCount, 500m);

        db.Events.Add(@event);
        await db.SaveChangesAsync();

        var seatIds = await db.Seats
            .Where(s => s.EventId == @event.Id)
            .OrderBy(s => s.Number)
            .Select(s => s.Id)
            .ToListAsync();

        return (@event.Id, seatIds);
    }

    /// <summary>
    /// Twenty customers, one seat, one winner.
    ///
    /// This is the case a read-then-write implementation gets wrong: every request reads
    /// the seat as Available before any of them writes, so all of them proceed. Here the
    /// UPDATE carries the xmin the row had when it was read, so nineteen of them match
    /// zero rows and come back 409.
    /// </summary>
    [Fact]
    public async Task Yirmi_es_zamanli_istek_ayni_koltugu_yalnizca_bir_kisiye_satar()
    {
        const int contenders = 20;
        var (eventId, seatIds) = await SeedEventAsync(seatCount: 1);
        var contestedSeat = seatIds[0];

        var clients = new List<HttpClient>();
        for (var i = 0; i < contenders; i++)
        {
            var client = _factory.CreateClient();
            var auth = await client.RegisterAsync($"yarisan{i}@ornek.test");
            clients.Add(client.WithToken(auth.AccessToken));
        }

        // Released together so the requests actually overlap rather than queueing.
        using var gate = new SemaphoreSlim(0, contenders);
        var attempts = clients.Select(async client =>
        {
            await gate.WaitAsync();
            return await client.ReserveAsync(eventId, contestedSeat);
        }).ToList();

        gate.Release(contenders);
        var responses = await Task.WhenAll(attempts);

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var conflicts = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, created);
        Assert.Equal(contenders - 1, conflicts);

        // And the database agrees: exactly one held seat, exactly one reservation.
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Seats.CountAsync(s => s.Id == contestedSeat && s.Status == SeatStatus.Held));
        Assert.Equal(1, await db.Reservations.CountAsync(r => r.EventId == eventId));
    }

    [Fact]
    public async Task Es_zamanli_istekler_farkli_koltuklari_alabilir()
    {
        // The lock must be per seat, not per event: ten people buying ten different seats
        // is the normal case and none of them should conflict.
        const int count = 10;
        var (eventId, seatIds) = await SeedEventAsync(seatCount: count);

        var clients = new List<(HttpClient Client, Guid SeatId)>();
        for (var i = 0; i < count; i++)
        {
            var client = _factory.CreateClient();
            var auth = await client.RegisterAsync($"farkli{i}@ornek.test");
            clients.Add((client.WithToken(auth.AccessToken), seatIds[i]));
        }

        using var gate = new SemaphoreSlim(0, count);
        var attempts = clients.Select(async pair =>
        {
            await gate.WaitAsync();
            return await pair.Client.ReserveAsync(eventId, pair.SeatId);
        }).ToList();

        gate.Release(count);
        var responses = await Task.WhenAll(attempts);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        await using var db = _factory.CreateDbContext();
        Assert.Equal(count, await db.Seats.CountAsync(s => s.EventId == eventId && s.Status == SeatStatus.Held));
    }

    [Fact]
    public async Task Kismi_cakisma_tamamen_reddedilir()
    {
        // Two requests overlapping on one seat out of three. A reservation is all or
        // nothing: the loser must not end up holding the two seats it did manage to get.
        var (eventId, seatIds) = await SeedEventAsync(seatCount: 4);

        var first = _factory.CreateClient();
        first.WithToken((await first.RegisterAsync("ilk@ornek.test")).AccessToken);

        var second = _factory.CreateClient();
        second.WithToken((await second.RegisterAsync("ikinci@ornek.test")).AccessToken);

        var firstResponse = await first.ReserveAsync(eventId, seatIds[0], seatIds[1]);
        var secondResponse = await second.ReserveAsync(eventId, seatIds[1], seatIds[2], seatIds[3]);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);

        await using var db = _factory.CreateDbContext();
        var held = await db.Seats.CountAsync(s => s.EventId == eventId && s.Status == SeatStatus.Held);
        Assert.Equal(2, held); // only the first reservation's two seats
        Assert.Equal(1, await db.Reservations.CountAsync());
    }

    [Fact]
    public async Task Iptal_edilen_koltuk_yeniden_satilabilir()
    {
        var (eventId, seatIds) = await SeedEventAsync(seatCount: 1);

        var first = _factory.CreateClient();
        first.WithToken((await first.RegisterAsync("iptal-eden@ornek.test")).AccessToken);

        var created = await first.ReserveAsync(eventId, seatIds[0]);
        var reservation = (await created.Content.ReadFromJsonAsync<ReservationResponse>())!;

        var second = _factory.CreateClient();
        second.WithToken((await second.RegisterAsync("bekleyen@ornek.test")).AccessToken);

        Assert.Equal(HttpStatusCode.Conflict, (await second.ReserveAsync(eventId, seatIds[0])).StatusCode);

        var cancelled = await first.DeleteAsync($"/api/reservations/{reservation.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await second.ReserveAsync(eventId, seatIds[0])).StatusCode);
    }
}
