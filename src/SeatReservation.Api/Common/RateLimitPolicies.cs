namespace SeatReservation.Api.Common;

/// <summary>
/// Policy names shared between registration in <c>Program</c> and the endpoints that ask
/// for them. A constant rather than a string at each site: a typo in a policy name is not a
/// compile error, and a route that asks for a policy nobody registered throws at request
/// time rather than at startup.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>
    /// The endpoints that accept credentials or a refresh token. These are the ones worth
    /// guessing at, and the ones reachable without already holding a token.
    /// </summary>
    public const string Credentials = "credentials";
}
