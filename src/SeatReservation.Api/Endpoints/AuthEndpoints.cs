using Microsoft.AspNetCore.Mvc;
using SeatReservation.Api.Common;
using SeatReservation.Application.Contracts;
using SeatReservation.Application.Services;

namespace SeatReservation.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (
                [FromBody] RegisterRequest request, AuthService auth, CancellationToken ct) =>
            {
                var result = await auth.RegisterAsync(request, ct);
                return result.Match(TypedResults.Ok);
            })
            .WithSummary("Creates an account and returns a token pair.")
            .Produces<AuthResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/login", async (
                [FromBody] LoginRequest request, AuthService auth, CancellationToken ct) =>
            {
                var result = await auth.LoginAsync(request, ct);
                return result.Match(TypedResults.Ok);
            })
            .WithSummary("Exchanges credentials for a token pair.")
            .Produces<AuthResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/refresh", async (
                [FromBody] RefreshRequest request, AuthService auth, CancellationToken ct) =>
            {
                var result = await auth.RefreshAsync(request, ct);
                return result.Match(TypedResults.Ok);
            })
            .WithSummary("Rotates a refresh token. The presented token is revoked, so it works exactly once.")
            .Produces<AuthResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/revoke", async (
                [FromBody] RefreshRequest request, AuthService auth, CancellationToken ct) =>
            {
                var result = await auth.RevokeAsync(request.RefreshToken, ct);
                return result.Match(TypedResults.NoContent);
            })
            .RequireAuthorization()
            .WithSummary("Revokes a refresh token.");

        return app;
    }
}
