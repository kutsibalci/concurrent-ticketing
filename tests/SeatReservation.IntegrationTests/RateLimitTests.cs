using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests;

/// <summary>
/// Guessing a password used to cost exactly one request, answered as fast as a correct one,
/// with no limit on how many followed. These tests hold that door shut.
///
/// The shared factory raises the limit out of the way so twenty deliberate logins elsewhere
/// in the suite are not mistaken for an attack. Here the host is rebuilt with a low one, so
/// what is asserted is the configured limit doing its job rather than a number that happens
/// to be small.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RateLimitTests : IAsyncLifetime
{
    private const int Limit = 4;

    private readonly PostgresApiFactory _factory;

    public RateLimitTests(PostgresApiFactory factory) => _factory = factory;

    public async ValueTask InitializeAsync() => await _factory.ResetDatabaseAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// A separate host with a tight limit. The window is long enough that it cannot lapse
    /// midway through a test and turn a real failure into a passing one.
    /// </summary>
    private WebApplicationFactory<Program> LimitedHost() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:PermitLimit", Limit.ToString());
            builder.UseSetting("RateLimiting:Window", "00:05:00");
        });

    private static HttpContent Credentials(string email, string password) =>
        JsonContent.Create(new { email, password });

    [Fact]
    public async Task Limit_asilinca_429_doner()
    {
        using var host = LimitedHost();
        var client = host.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < Limit + 2; i++)
        {
            var response = await client.PostAsync(
                "/api/auth/login", Credentials("yok@ornek.test", "YanlisSifre123!"));
            statuses.Add(response.StatusCode);
        }

        // The first `Limit` are answered on their merits -- 401, because the account does
        // not exist. What matters is that the ones after are refused without being tried.
        Assert.All(statuses.Take(Limit), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.All(statuses.Skip(Limit), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));
    }

    [Fact]
    public async Task Reddedilen_istek_ne_zaman_tekrar_denenecegini_soyler()
    {
        using var host = LimitedHost();
        var client = host.CreateClient();

        HttpResponseMessage? rejected = null;
        for (var i = 0; i < Limit + 1; i++)
            rejected = await client.PostAsync(
                "/api/auth/login", Credentials("yok@ornek.test", "YanlisSifre123!"));

        Assert.NotNull(rejected);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // Without Retry-After a well-behaved client has nothing to go on but guesswork, and
        // guessing usually means retrying immediately. The sliding window limiter publishes
        // no RetryAfter metadata, so this header only appears because it is computed --
        // which is exactly why it is asserted rather than assumed.
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.True(
            rejected.Headers.RetryAfter!.Delta > TimeSpan.Zero,
            "Retry-After should tell the caller how long to wait, not zero.");

        // A problem document that announces itself as application/json is one a client has
        // to sniff. This assertion exists because the first implementation did exactly
        // that: WriteAsJsonAsync overwrote the content type set before it.
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Limit_dogru_sifreyi_de_durdurur()
    {
        // The limit counts requests, not failures. An attacker who found the password on
        // attempt three would otherwise be waved through on every attempt after it.
        using var host = LimitedHost();
        var client = host.CreateClient();

        const string email = "kurban@ornek.test";
        const string password = "GucluTestSifresi1!";
        await client.PostAsJsonAsync("/api/auth/register", new { email, password, displayName = "Kurban" });

        for (var i = 0; i < Limit; i++)
            await client.PostAsync("/api/auth/login", Credentials(email, "YanlisSifre123!"));

        var withCorrectPassword = await client.PostAsync("/api/auth/login", Credentials(email, password));

        Assert.Equal(HttpStatusCode.TooManyRequests, withCorrectPassword.StatusCode);
    }

    [Fact]
    public async Task Katalog_sinirlanmaz()
    {
        // Only the credential endpoints are limited. Browsing what is on sale is not an
        // attack, and a customer refreshing the page should never meet a 429.
        using var host = LimitedHost();
        var client = host.CreateClient();

        for (var i = 0; i < Limit * 3; i++)
        {
            var response = await client.GetAsync("/api/events");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Normal_kullanim_limite_takilmaz()
    {
        // The other direction: a limiter that rejected everything would satisfy the tests
        // above. One person signing in successfully must not be treated as a flood.
        using var host = LimitedHost();
        var client = host.CreateClient();

        const string email = "duzgun@ornek.test";
        const string password = "GucluTestSifresi1!";
        await client.PostAsJsonAsync("/api/auth/register", new { email, password, displayName = "Duzgun" });

        var response = await client.PostAsync("/api/auth/login", Credentials(email, password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
