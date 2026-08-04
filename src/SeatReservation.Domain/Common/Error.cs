namespace SeatReservation.Domain.Common;

/// <summary>
/// A named failure. Domain rules return these instead of throwing: a seat already being
/// taken is an expected outcome of a race, not an exceptional condition, and using
/// exceptions for it makes the normal path indistinguishable from a genuine fault.
/// </summary>
public sealed record Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    public override string ToString() => $"{Code}: {Message}";
}
