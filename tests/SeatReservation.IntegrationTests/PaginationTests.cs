using System.Net;
using System.Net.Http.Json;
using SeatReservation.Application.Contracts;
using SeatReservation.Domain.Entities;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests;

/// <summary>
/// The catalogue used to return every event in one response. With a handful seeded that is
/// invisible; with a season's worth it is a request anyone can send that reads the whole
/// table into memory and serialises it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PaginationTests : IAsyncLifetime
{
    private readonly PostgresApiFactory _factory;

    public PaginationTests(PostgresApiFactory factory) => _factory = factory;

    public async ValueTask InitializeAsync() => await _factory.ResetDatabaseAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Seeds events that all start at the same instant on purpose — that is the case where
    /// ordering by start time alone leaves the order undefined.
    /// </summary>
    private async Task<List<Guid>> SeedEventsAsync(int count, bool sameStart = true)
    {
        await using var db = _factory.CreateDbContext();

        var start = DateTimeOffset.UtcNow.AddDays(30);
        var ids = new List<Guid>();

        for (var i = 0; i < count; i++)
        {
            var @event = Event.Create($"Etkinlik {i:D2}", "Salon", sameStart ? start : start.AddHours(i));
            @event.AddSeatBlock("A", 1, 2, 100m);
            db.Events.Add(@event);
            ids.Add(@event.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<PagedResponse<EventSummaryResponse>> GetPageAsync(int page, int size)
        => (await _factory.CreateClient()
            .GetFromJsonAsync<PagedResponse<EventSummaryResponse>>($"/api/events?page={page}&size={size}"))!;

    [Fact]
    public async Task Sayfa_istenen_boyutta_doner()
    {
        await SeedEventsAsync(12);

        var result = await GetPageAsync(page: 1, size: 5);

        Assert.Equal(5, result.Items.Count);
        Assert.Equal(12, result.TotalCount);
        Assert.Equal(3, result.TotalPages);
        Assert.True(result.HasNextPage);
    }

    [Fact]
    public async Task Son_sayfada_sonraki_sayfa_yok()
    {
        await SeedEventsAsync(12);

        var result = await GetPageAsync(page: 3, size: 5);

        Assert.Equal(2, result.Items.Count);
        Assert.False(result.HasNextPage);
    }

    [Fact]
    public async Task Sayfalar_arasinda_kayit_tekrarlanmaz_ve_kaybolmaz()
    {
        // The invariant that makes paging trustworthy: walking every page returns each row
        // exactly once. All twelve events share a start time, so without a unique
        // tiebreaker the database is free to order them differently for each OFFSET, and a
        // row can land on two pages or on none.
        var seeded = await SeedEventsAsync(12);

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
            seen.AddRange((await GetPageAsync(page, size: 4)).Items.Select(e => e.Id));

        Assert.Equal(12, seen.Count);
        Assert.Equal(12, seen.Distinct().Count());
        Assert.Equal(seeded.OrderBy(id => id), seen.OrderBy(id => id));
    }

    [Fact]
    public async Task Bos_katalog_bos_sayfa_doner()
    {
        var result = await GetPageAsync(page: 1, size: 10);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.False(result.HasNextPage);
    }

    [Fact]
    public async Task Aralik_disindaki_sayfa_bos_doner_hata_degil()
    {
        await SeedEventsAsync(3);

        var result = await GetPageAsync(page: 99, size: 10);

        // Past the end is an empty page, not a 404: asking for page 99 of a shrinking list
        // is a race a client cannot avoid, and TotalCount tells it what happened.
        Assert.Empty(result.Items);
        Assert.Equal(3, result.TotalCount);
    }

    [Theory]
    [InlineData(0)]      // page below one
    [InlineData(-1)]
    public async Task Gecersiz_sayfa_numarasi_400_doner(int page)
    {
        var response = await _factory.CreateClient().GetAsync($"/api/events?page={page}&size=10");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PageRequest.MaxSize + 1)]
    [InlineData(1_000_000)]
    public async Task Tavan_ustundeki_boyut_400_doner(int size)
    {
        // The point of the ceiling: ?size=1000000 must not be a way to ask for the whole
        // table. Rejected rather than silently clamped, so a client is never handed a
        // hundred rows while believing it received a million.
        var response = await _factory.CreateClient().GetAsync($"/api/events?page=1&size={size}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Parametresiz_istek_varsayilan_sayfayi_doner()
    {
        // Older clients called this endpoint with no query string at all. They must keep
        // working, and they must not receive the whole table.
        await SeedEventsAsync(25, sameStart: false);

        var result = (await _factory.CreateClient()
            .GetFromJsonAsync<PagedResponse<EventSummaryResponse>>("/api/events"))!;

        Assert.Equal(PageRequest.DefaultSize, result.Size);
        Assert.Equal(PageRequest.DefaultSize, result.Items.Count);
        Assert.Equal(25, result.TotalCount);
    }

    [Fact]
    public async Task Sonuclar_baslangic_zamanina_gore_sirali()
    {
        await SeedEventsAsync(6, sameStart: false);

        var result = await GetPageAsync(page: 1, size: 6);

        var startTimes = result.Items.Select(e => e.StartsAt).ToList();
        Assert.Equal(startTimes.OrderBy(t => t), startTimes);
    }
}
