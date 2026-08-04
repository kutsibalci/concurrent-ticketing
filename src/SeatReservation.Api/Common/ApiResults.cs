using System.Security.Claims;
using SeatReservation.Domain.Common;

namespace SeatReservation.Api.Common;

public static class ApiResults
{
    /// <summary>
    /// Maps a domain error to a status code.
    ///
    /// Kept in one place so the mapping is consistent and reviewable, rather than each
    /// endpoint inventing its own. The interesting one is 409 for a lost concurrency
    /// race: the request was valid and the client may reasonably retry with other seats.
    /// </summary>
    public static IResult Problem(Error error) => error.Code switch
    {
        "event.not_found" or "seat.not_found" or "reservation.not_found"
            => TypedResults.Problem(title: error.Message, statusCode: StatusCodes.Status404NotFound, type: error.Code),

        "reservation.not_owner"
            => TypedResults.Problem(title: error.Message, statusCode: StatusCodes.Status403Forbidden, type: error.Code),

        "user.invalid_credentials" or "user.invalid_refresh_token"
            => TypedResults.Problem(title: error.Message, statusCode: StatusCodes.Status401Unauthorized, type: error.Code),

        "user.email_already_used"
        or "seat.not_available"
        or "reservation.concurrency_conflict"
        or "reservation.already_confirmed"
            => TypedResults.Problem(title: error.Message, statusCode: StatusCodes.Status409Conflict, type: error.Code),

        _ => TypedResults.Problem(title: error.Message, statusCode: StatusCodes.Status400BadRequest, type: error.Code)
    };

    public static IResult Match<T>(this Result<T> result, Func<T, IResult> onSuccess)
        => result.IsSuccess ? onSuccess(result.Value) : Problem(result.Error);

    public static IResult Match(this Result result, Func<IResult> onSuccess)
        => result.IsSuccess ? onSuccess() : Problem(result.Error);

    /// <summary>
    /// The signed-in user's id, from the NameIdentifier claim.
    /// Reading identity from a claim rather than from anything the request body says is
    /// the difference between authentication and a suggestion.
    /// </summary>
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(raw, out var id)
            ? id
            : throw new InvalidOperationException("Authenticated principal has no usable NameIdentifier claim.");
    }
}
