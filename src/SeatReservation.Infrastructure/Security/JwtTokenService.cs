using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Options;
using SeatReservation.Domain.Entities;

namespace SeatReservation.Infrastructure.Security;

public sealed class JwtTokenService : ITokenService
{
    private readonly JwtOptions _options;
    private readonly TimeProvider _clock;
    private readonly SigningCredentials _credentials;

    public JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;

        if (string.IsNullOrWhiteSpace(_options.SigningKey) || _options.SigningKey.Length < 32)
        {
            // Refused rather than defaulted. A signing key with a fallback value is a
            // signing key that anyone who has read the source can forge tokens with.
            throw new InvalidOperationException(
                "Jwt:SigningKey is missing or shorter than 32 characters. Provide it through " +
                "configuration (user-secrets in development, the environment in production).");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    public string CreateAccessToken(User user)
    {
        var now = _clock.GetUtcNow();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            // A fresh jti per token, so an individual token can be identified in logs.
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role)
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(_options.AccessTokenLifetime).UtcDateTime,
            signingCredentials: _credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// A 256-bit random value. Only its hash is persisted: the raw token is a bearer
    /// credential, so a read of the token table should not be enough to impersonate anyone.
    /// </summary>
    public (string Raw, string Hash) CreateRefreshToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (raw, HashRefreshToken(raw));
    }

    /// <summary>
    /// Plain SHA-256, deliberately. Unlike a password this value is already 256 bits of
    /// entropy, so there is nothing for a slow KDF to protect against — and refresh runs
    /// on every token exchange, where 210 000 iterations would be a real cost.
    /// </summary>
    public string HashRefreshToken(string rawToken)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
