namespace SeatReservation.Domain.Entities;

public static class Roles
{
    public const string Customer = "Customer";
    public const string Admin = "Admin";
}

public sealed class User
{
    private readonly List<RefreshToken> _refreshTokens = [];

    private User() { } // EF Core

    private User(Guid id, string email, string passwordHash, string displayName, string role)
    {
        Id = id;
        Email = email;
        PasswordHash = passwordHash;
        DisplayName = displayName;
        Role = role;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Stored lower-cased; the unique index is on this value, so casing cannot create a second account.</summary>
    public string Email { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string Role { get; private set; } = Roles.Customer;
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyCollection<RefreshToken> RefreshTokens => _refreshTokens.AsReadOnly();

    public static User Create(string email, string passwordHash, string displayName, string role = Roles.Customer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        // Guarded like the other two: this used to be an unchecked displayName.Trim(), so a
        // request that simply omitted the field became a NullReferenceException and a 500.
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        return new User(Guid.NewGuid(), email.Trim().ToLowerInvariant(), passwordHash, displayName.Trim(), role);
    }

    public RefreshToken IssueRefreshToken(string tokenHash, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var token = RefreshToken.Create(Id, tokenHash, expiresAt, now);
        _refreshTokens.Add(token);
        return token;
    }
}

/// <summary>
/// A refresh token, stored as a hash.
///
/// The raw token is a bearer credential: anyone holding it can mint access tokens. Storing
/// only its hash means a read of this table is not enough to impersonate anyone — the same
/// reasoning that applies to passwords.
/// </summary>
public sealed class RefreshToken
{
    private RefreshToken() { } // EF Core

    private RefreshToken(Guid id, Guid userId, string tokenHash, DateTimeOffset expiresAt, DateTimeOffset createdAt)
    {
        Id = id;
        UserId = userId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Set when this token is exchanged, linking it to its successor so a whole chain can be traced.</summary>
    public Guid? ReplacedByTokenId { get; private set; }

    public User? User { get; private set; }

    internal static RefreshToken Create(Guid userId, string tokenHash, DateTimeOffset expiresAt, DateTimeOffset createdAt)
        => new(Guid.NewGuid(), userId, tokenHash, expiresAt, createdAt);

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public void Revoke(DateTimeOffset now, Guid? replacedByTokenId = null)
    {
        RevokedAt ??= now;
        ReplacedByTokenId ??= replacedByTokenId;
    }
}
