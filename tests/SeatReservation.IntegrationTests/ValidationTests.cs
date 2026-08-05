using System.Net;
using System.Net.Http.Json;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests;

/// <summary>
/// The request contracts have carried [Required], [EmailAddress] and [MinLength] from the
/// start, and every one of them was decorative: minimal APIs do not evaluate data
/// annotations unless validation is registered, and it was not. Nothing here failed in a
/// way a test noticed, because the service tests build their own valid requests and never
/// cross the HTTP boundary where the annotations live.
///
/// Running the stack is what surfaced it -- a three-character password and the literal
/// string "bu-bir-email-degil" both opened accounts, and omitting displayName returned 500
/// rather than 400. These tests pin the boundary itself: the payload goes over HTTP as raw
/// JSON, so a field can genuinely be absent rather than merely empty.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ValidationTests : IAsyncLifetime
{
    private readonly PostgresApiFactory _factory;

    public ValidationTests(PostgresApiFactory factory) => _factory = factory;

    public async ValueTask InitializeAsync() => await _factory.ResetDatabaseAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static TheoryData<string, object> GecersizKayitlar() => new()
    {
        { "sifre cok kisa", new { email = "kisa@ornek.test", password = "abc", displayName = "Kisa" } },
        { "email formati bozuk", new { email = "bu-bir-email-degil", password = "GucluTestSifresi1!", displayName = "Bozuk" } },
        { "email bos", new { email = "", password = "GucluTestSifresi1!", displayName = "Bos" } },
        { "displayName yok", new { email = "eksik@ornek.test", password = "GucluTestSifresi1!" } },
        { "displayName bos", new { email = "bos@ornek.test", password = "GucluTestSifresi1!", displayName = "" } },
    };

    [Theory]
    [MemberData(nameof(GecersizKayitlar))]
    public async Task Gecersiz_kayit_400_doner(string durum, object payload)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", payload);

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{durum}: 400 bekleniyordu, {(int)response.StatusCode} geldi. " +
            "500 ise dogrulama isteği domaine kadar geçirmiş demektir.");
    }

    [Fact]
    public async Task Uzun_displayName_reddedilir()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "uzun@ornek.test",
            password = "GucluTestSifresi1!",
            displayName = new string('A', 200) // MaxLength(120)
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Dogrulama_hatasi_hangi_alan_oldugunu_soyler()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register", new { email = "bozuk", password = "abc", displayName = "" });

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();

        Assert.NotNull(problem);
        // A 400 that does not say which field is wrong makes the client guess.
        Assert.Contains("Email", problem.Errors.Keys);
        Assert.Contains("Password", problem.Errors.Keys);
        Assert.Contains("DisplayName", problem.Errors.Keys);
    }

    [Fact]
    public async Task Gecerli_kayit_hala_kabul_edilir()
    {
        // The other direction: validation that rejects everything would also pass the
        // tests above.
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "gecerli@ornek.test",
            password = "GucluTestSifresi1!",
            displayName = "Gecerli Kullanici"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Girilen_sifre_kisaysa_hesap_olusmaz()
    {
        // The status code is one thing; whether the row landed is the thing that matters.
        var client = _factory.CreateClient();

        await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "kayitolmamali@ornek.test",
            password = "abc",
            displayName = "Olmamali"
        });

        await using var db = _factory.CreateDbContext();
        Assert.DoesNotContain(db.Users, u => u.Email == "kayitolmamali@ornek.test");
    }
}

/// <summary>Minimal shape of the RFC 9110 problem document the validation filter returns.</summary>
public sealed class ValidationProblemDetails
{
    public string? Title { get; set; }
    public int Status { get; set; }
    public Dictionary<string, string[]> Errors { get; set; } = [];
}
