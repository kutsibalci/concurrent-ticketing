using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Contracts;
using SeatReservation.Application.Options;
using SeatReservation.Domain.Common;
using SeatReservation.Domain.Entities;

namespace SeatReservation.Application.Services;

public sealed class AuthService
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ITokenService _tokens;
    private readonly TimeProvider _clock;
    private readonly JwtOptions _jwt;

    public AuthService(
        IApplicationDbContext db,
        IPasswordHasher hasher,
        ITokenService tokens,
        TimeProvider clock,
        IOptions<JwtOptions> jwt)
    {
        _db = db;
        _hasher = hasher;
        _tokens = tokens;
        _clock = clock;
        _jwt = jwt.Value;
    }

    public async Task<Result<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            return Result.Failure<AuthResponse>(DomainErrors.User.EmailAlreadyUsed);

        var user = User.Create(email, _hasher.Hash(request.Password), request.DisplayName);
        _db.Users.Add(user);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two registrations for the same address can both pass the check above; the
            // unique index is what actually decides.
            return Result.Failure<AuthResponse>(DomainErrors.User.EmailAlreadyUsed);
        }

        return await IssueTokensAsync(user, ct);
    }

    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null)
        {
            // Verify against a throwaway hash anyway. Returning immediately makes
            // "no such account" measurably faster than "wrong password", which turns the
            // login endpoint into a user-enumeration oracle.
            _hasher.Verify(request.Password, DummyHash);
            return Result.Failure<AuthResponse>(DomainErrors.User.InvalidCredentials);
        }

        return _hasher.Verify(request.Password, user.PasswordHash)
            ? await IssueTokensAsync(user, ct)
            : Result.Failure<AuthResponse>(DomainErrors.User.InvalidCredentials);
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair, revoking the old one.
    ///
    /// Rotation means a stolen refresh token is usable at most once, and the revoked
    /// record leaves a trail linking it to its replacement.
    /// </summary>
    public async Task<Result<AuthResponse>> RefreshAsync(RefreshRequest request, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var hash = _tokens.HashRefreshToken(request.RefreshToken);

        var token = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token?.User is null || !token.IsActive(now))
            return Result.Failure<AuthResponse>(DomainErrors.User.InvalidRefreshToken);

        var user = token.User;
        var issued = await IssueTokensAsync(user, ct, save: false);

        token.Revoke(now, user.RefreshTokens.OrderByDescending(t => t.CreatedAt).First().Id);
        await _db.SaveChangesAsync(ct);

        return issued;
    }

    public async Task<Result> RevokeAsync(string rawRefreshToken, CancellationToken ct = default)
    {
        var hash = _tokens.HashRefreshToken(rawRefreshToken);
        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null)
            return Result.Failure(DomainErrors.User.InvalidRefreshToken);

        token.Revoke(_clock.GetUtcNow());
        await _db.SaveChangesAsync(ct);

        return Result.Success();
    }

    private async Task<Result<AuthResponse>> IssueTokensAsync(User user, CancellationToken ct, bool save = true)
    {
        var now = _clock.GetUtcNow();
        var (raw, hash) = _tokens.CreateRefreshToken();

        var token = user.IssueRefreshToken(hash, now.Add(_jwt.RefreshTokenLifetime), now);

        // Added explicitly rather than left to be discovered through the user's
        // collection. The domain assigns the key, and EF reads an entity that already has
        // one as an existing row unless it is told otherwise.
        _db.RefreshTokens.Add(token);

        if (save)
            await _db.SaveChangesAsync(ct);

        return new AuthResponse(
            user.Id,
            user.Email,
            user.DisplayName,
            user.Role,
            _tokens.CreateAccessToken(user),
            raw,
            now.Add(_jwt.AccessTokenLifetime));
    }

    private const string DummyHash =
        "pbkdf2-sha256$210000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
}
