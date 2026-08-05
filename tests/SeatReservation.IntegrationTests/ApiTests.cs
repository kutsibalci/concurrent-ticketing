using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SeatReservation.Application.Contracts;
using SeatReservation.Domain.Entities;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public sealed class ApiTests : IAsyncLifetime
{
    private readonly PostgresApiFactory _factory;

    public ApiTests(PostgresApiFactory factory) => _factory = factory;

    public async ValueTask InitializeAsync() => await _factory.ResetDatabaseAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(Guid EventId, List<Guid> SeatIds)> SeedEventAsync(int seatCount = 5)
    {
        await using var db = _factory.CreateDbContext();

        var @event = Event.Create("Test Etkinliği", "Salon", DateTimeOffset.UtcNow.AddDays(3));
        @event.AddSeatBlock("A", 1, seatCount, 120m);

        db.Events.Add(@event);
        await db.SaveChangesAsync();

        var ids = await db.Seats.Where(s => s.EventId == @event.Id)
            .OrderBy(s => s.Number).Select(s => s.Id).ToListAsync();

        return (@event.Id, ids);
    }

    // ------------------------------------------------------------------- auth

    [Fact]
    public async Task Kayit_token_cifti_doner()
    {
        var client = _factory.CreateClient();

        var auth = await client.RegisterAsync("yeni@ornek.test");

        Assert.NotEmpty(auth.AccessToken);
        Assert.NotEmpty(auth.RefreshToken);
        Assert.Equal(Roles.Customer, auth.Role);
    }

    [Fact]
    public async Task Ayni_email_ikinci_kez_kaydedilemez()
    {
        var client = _factory.CreateClient();
        await client.RegisterAsync("tekrar@ornek.test");

        var response = await client.PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest("tekrar@ornek.test", "GucluTestSifresi1!", "Tekrar"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Email_buyuk_kucuk_harf_ayrimi_yapmaz()
    {
        var client = _factory.CreateClient();
        await client.RegisterAsync("karisik@ornek.test");

        var response = await client.PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest("KARISIK@ornek.test", "GucluTestSifresi1!", "Karisik"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Yanlis_sifre_401_doner()
    {
        var client = _factory.CreateClient();
        await client.RegisterAsync("giris@ornek.test", "DogruSifre123!");

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("giris@ornek.test", "YanlisSifre"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Olmayan_kullanici_da_401_doner()
    {
        // Same status and body as a wrong password: otherwise the endpoint tells an
        // attacker which addresses are registered.
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("yok@ornek.test", "herhangi"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_token_rotasyonu_eskisini_gecersiz_kilar()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync("rotasyon@ornek.test");

        var first = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(auth.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The presented token is revoked as part of the exchange, so replaying it fails.
        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(auth.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Sifre_veritabaninda_acik_metin_tutulmaz()
    {
        const string password = "AcikMetinOlmamali1!";
        var client = _factory.CreateClient();
        await client.RegisterAsync("hash@ornek.test", password);

        await using var db = _factory.CreateDbContext();
        var user = await db.Users.SingleAsync(u => u.Email == "hash@ornek.test");

        Assert.DoesNotContain(password, user.PasswordHash);
        Assert.StartsWith("pbkdf2-sha256$", user.PasswordHash);
    }

    [Fact]
    public async Task Refresh_token_veritabaninda_ham_tutulmaz()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync("tokenhash@ornek.test");

        await using var db = _factory.CreateDbContext();
        var stored = await db.RefreshTokens.SingleAsync();

        Assert.NotEqual(auth.RefreshToken, stored.TokenHash);
    }

    // ----------------------------------------------------------- authorization

    [Fact]
    public async Task Rezervasyon_uclari_token_ister()
    {
        var (eventId, seatIds) = await SeedEventAsync();

        var response = await _factory.CreateClient().ReserveAsync(eventId, seatIds[0]);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Etkinlik_olusturmak_admin_rolu_ister()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync("musteri@ornek.test");
        client.WithToken(auth.AccessToken);

        var response = await client.PostAsJsonAsync("/api/events", new CreateEventRequest(
            "Yetkisiz", "Salon", DateTimeOffset.UtcNow.AddDays(1),
            [new SeatBlockRequest("A", 1, 5, 100m)]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Baskasinin_rezervasyonu_goruntulenemez()
    {
        var (eventId, seatIds) = await SeedEventAsync();

        var owner = _factory.CreateClient();
        owner.WithToken((await owner.RegisterAsync("sahip@ornek.test")).AccessToken);
        var created = await owner.ReserveAsync(eventId, seatIds[0]);
        var reservation = (await created.Content.ReadFromJsonAsync<ReservationResponse>())!;

        var stranger = _factory.CreateClient();
        stranger.WithToken((await stranger.RegisterAsync("yabanci@ornek.test")).AccessToken);

        // The id alone is not authorization.
        var response = await stranger.GetAsync($"/api/reservations/{reservation.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Baskasinin_rezervasyonu_iptal_edilemez()
    {
        var (eventId, seatIds) = await SeedEventAsync();

        var owner = _factory.CreateClient();
        owner.WithToken((await owner.RegisterAsync("sahip2@ornek.test")).AccessToken);
        var created = await owner.ReserveAsync(eventId, seatIds[0]);
        var reservation = (await created.Content.ReadFromJsonAsync<ReservationResponse>())!;

        var stranger = _factory.CreateClient();
        stranger.WithToken((await stranger.RegisterAsync("yabanci2@ornek.test")).AccessToken);

        var response = await stranger.DeleteAsync($"/api/reservations/{reservation.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(SeatStatus.Held, (await db.Seats.SingleAsync(s => s.Id == seatIds[0])).Status);
    }

    // ------------------------------------------------------- reservation flow

    [Fact]
    public async Task Rezervasyon_olustur_onayla_akisi()
    {
        var (eventId, seatIds) = await SeedEventAsync();
        var client = _factory.CreateClient();
        client.WithToken((await client.RegisterAsync("akis@ornek.test")).AccessToken);

        var created = await client.ReserveAsync(eventId, seatIds[0], seatIds[1]);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var reservation = (await created.Content.ReadFromJsonAsync<ReservationResponse>())!;
        Assert.Equal(nameof(ReservationStatus.Pending), reservation.Status);
        Assert.Equal(240m, reservation.TotalPrice);
        Assert.NotNull(reservation.SecondsUntilExpiry);

        var confirmed = await client.PostAsync($"/api/reservations/{reservation.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var body = (await confirmed.Content.ReadFromJsonAsync<ReservationResponse>())!;
        Assert.Equal(nameof(ReservationStatus.Confirmed), body.Status);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.Seats.CountAsync(s => s.Status == SeatStatus.Booked));
    }

    [Fact]
    public async Task Ust_sinirdan_fazla_koltuk_reddedilir()
    {
        var (eventId, seatIds) = await SeedEventAsync(seatCount: Reservation.MaxSeatsPerReservation + 2);
        var client = _factory.CreateClient();
        client.WithToken((await client.RegisterAsync("cokkoltuk@ornek.test")).AccessToken);

        var response = await client.ReserveAsync(eventId, seatIds.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Olmayan_etkinlik_404_doner()
    {
        var client = _factory.CreateClient();
        client.WithToken((await client.RegisterAsync("yoketkinlik@ornek.test")).AccessToken);

        var response = await client.ReserveAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --------------------------------------------------------- catalogue

    [Fact]
    public async Task Koltuk_haritasi_anonim_erisilebilir()
    {
        var (eventId, _) = await SeedEventAsync();

        var response = await _factory.CreateClient().GetAsync($"/api/events/{eventId}/seats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var map = (await response.Content.ReadFromJsonAsync<SeatMapResponse>())!;
        Assert.Equal(5, map.Seats.Count);
    }

    [Fact]
    public async Task Musait_koltuk_sayisi_rezervasyondan_sonra_azalir()
    {
        var (eventId, seatIds) = await SeedEventAsync();
        var client = _factory.CreateClient();
        client.WithToken((await client.RegisterAsync("sayac@ornek.test")).AccessToken);

        await client.ReserveAsync(eventId, seatIds[0], seatIds[1]);

        var events = (await _factory.CreateClient()
            .GetFromJsonAsync<PagedResponse<EventSummaryResponse>>("/api/events"))!;

        var summary = events.Items.Single(e => e.Id == eventId);
        Assert.Equal(5, summary.TotalSeats);
        Assert.Equal(3, summary.AvailableSeats);
    }

    [Fact]
    public async Task Koltuk_haritasi_rezervasyondan_sonra_bayat_kalmaz()
    {
        // The map is cached; every write path must evict it. A cache that serves a taken
        // seat as free is worse than no cache.
        var (eventId, seatIds) = await SeedEventAsync();
        var anonymous = _factory.CreateClient();

        await anonymous.GetAsync($"/api/events/{eventId}/seats"); // populate the cache

        var client = _factory.CreateClient();
        client.WithToken((await client.RegisterAsync("bayat@ornek.test")).AccessToken);
        await client.ReserveAsync(eventId, seatIds[0]);

        var map = (await anonymous.GetFromJsonAsync<SeatMapResponse>($"/api/events/{eventId}/seats"))!;
        var seat = map.Seats.Single(s => s.Id == seatIds[0]);

        Assert.Equal(nameof(SeatStatus.Held), seat.Status);
    }

    [Fact]
    public async Task Health_endpointi_veritabanina_dokunur()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
