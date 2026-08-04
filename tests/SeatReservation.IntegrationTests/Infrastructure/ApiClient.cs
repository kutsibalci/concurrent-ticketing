using System.Net.Http.Headers;
using System.Net.Http.Json;
using SeatReservation.Application.Contracts;

namespace SeatReservation.IntegrationTests.Infrastructure;

/// <summary>Small helpers so the tests read as scenarios rather than as HTTP plumbing.</summary>
public static class ApiClient
{
    public static async Task<AuthResponse> RegisterAsync(
        this HttpClient client, string email, string password = "GucluTestSifresi1!")
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest(email, password, email.Split('@')[0]));

        if (!response.IsSuccessStatusCode)
        {
            // Includes the body. EnsureSuccessStatusCode alone reports only the status,
            // which turns every server-side failure into the same uninformative message.
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Register failed ({(int)response.StatusCode}): {body}");
        }

        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    public static HttpClient WithToken(this HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public static Task<HttpResponseMessage> ReserveAsync(
        this HttpClient client, Guid eventId, params Guid[] seatIds)
        => client.PostAsJsonAsync("/api/reservations", new CreateReservationRequest(eventId, seatIds));
}
