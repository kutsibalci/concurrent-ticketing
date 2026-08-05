using Microsoft.AspNetCore.Mvc;
using SeatReservation.Api.Common;
using SeatReservation.Application.Contracts;
using SeatReservation.Application.Services;
using SeatReservation.Domain.Entities;

namespace SeatReservation.Api.Endpoints;

public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events").WithTags("Events");

        // The catalogue and seat map are public: a customer must be able to look before
        // signing in. Everything that changes state below requires a token.
        group.MapGet("/", async (
                [AsParameters] PageRequest page, EventService events, CancellationToken ct) =>
                TypedResults.Ok(await events.ListAsync(page, ct)))
            .AllowAnonymous()
            .WithSummary(
                $"Lists events with a live count of available seats. Paged: ?page=1&size={PageRequest.DefaultSize}, "
                + $"at most {PageRequest.MaxSize} per request.")
            .Produces<PagedResponse<EventSummaryResponse>>();

        group.MapGet("/{eventId:guid}/seats", async (
                Guid eventId, EventService events, CancellationToken ct) =>
            {
                var result = await events.GetSeatMapAsync(eventId, ct);
                return result.Match(TypedResults.Ok);
            })
            .AllowAnonymous()
            .WithSummary("Seat map for one event. Cached briefly and evicted on every reservation.")
            .Produces<SeatMapResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", async (
                [FromBody] CreateEventRequest request, EventService events, CancellationToken ct) =>
            {
                var result = await events.CreateAsync(request, ct);
                return result.Match(created => TypedResults.Created($"/api/events/{created.Id}", created));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin))
            .WithSummary("Creates an event and its seat blocks. Administrators only.")
            .Produces<EventSummaryResponse>(StatusCodes.Status201Created);

        return app;
    }
}
