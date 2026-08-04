using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SeatReservation.Api.Common;
using SeatReservation.Application.Contracts;
using SeatReservation.Application.Services;

namespace SeatReservation.Api.Endpoints;

public static class ReservationEndpoints
{
    public static IEndpointRouteBuilder MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reservations")
            .WithTags("Reservations")
            // Applied to the group, not per endpoint: a new endpoint added here is
            // protected by default rather than when somebody remembers the attribute.
            .RequireAuthorization();

        group.MapPost("/", async (
                [FromBody] CreateReservationRequest request,
                ClaimsPrincipal user,
                ReservationService reservations,
                CancellationToken ct) =>
            {
                var result = await reservations.CreateAsync(user.GetUserId(), request, ct);
                return result.Match(created => TypedResults.Created($"/api/reservations/{created.Id}", created));
            })
            .WithSummary("Holds seats. Returns 409 if another request took one of them first.")
            .Produces<ReservationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{reservationId:guid}/confirm", async (
                Guid reservationId,
                ClaimsPrincipal user,
                ReservationService reservations,
                CancellationToken ct) =>
            {
                var result = await reservations.ConfirmAsync(reservationId, user.GetUserId(), ct);
                return result.Match(TypedResults.Ok);
            })
            .WithSummary("Turns a hold into a booking. Fails if the hold has already lapsed.")
            .Produces<ReservationResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapDelete("/{reservationId:guid}", async (
                Guid reservationId,
                ClaimsPrincipal user,
                ReservationService reservations,
                CancellationToken ct) =>
            {
                var result = await reservations.CancelAsync(reservationId, user.GetUserId(), ct);
                return result.Match(TypedResults.NoContent);
            })
            .WithSummary("Cancels a reservation and releases its seats.")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{reservationId:guid}", async (
                Guid reservationId,
                ClaimsPrincipal user,
                ReservationService reservations,
                CancellationToken ct) =>
            {
                var result = await reservations.GetAsync(reservationId, user.GetUserId(), ct);
                return result.Match(TypedResults.Ok);
            })
            .WithSummary("One reservation, including seconds left on the hold.")
            .Produces<ReservationResponse>();

        group.MapGet("/", async (
                ClaimsPrincipal user, ReservationService reservations, CancellationToken ct) =>
                TypedResults.Ok(await reservations.ListForUserAsync(user.GetUserId(), ct)))
            .WithSummary("The caller's own reservations.")
            .Produces<IReadOnlyList<ReservationResponse>>();

        return app;
    }
}
