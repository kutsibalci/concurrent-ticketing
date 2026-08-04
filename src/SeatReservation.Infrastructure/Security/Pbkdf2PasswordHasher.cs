using System.Security.Cryptography;
using SeatReservation.Application.Abstractions;

namespace SeatReservation.Infrastructure.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256.
///
/// Stored format: <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 subkey&gt;</c>.
/// The iteration count travels with the hash, so it can be raised later without
/// invalidating every existing password.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const int SaltSize = 16;
    private const int SubkeySize = 32;
    private const int DefaultIterations = 210_000; // OWASP guidance for PBKDF2-HMAC-SHA256

    private readonly int _iterations;

    public Pbkdf2PasswordHasher(int iterations = DefaultIterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1_000);
        _iterations = iterations;
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, _iterations, HashAlgorithmName.SHA256, SubkeySize);

        return $"{Prefix}${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(subkey)}";
    }

    public bool Verify(string password, string storedHash)
    {
        if (password is null || string.IsNullOrWhiteSpace(storedHash))
            return false;

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out var iterations) || iterations < 1)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            // A corrupt row must not take the login endpoint down with a 500.
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
            return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // Fixed-time: a short-circuiting comparison leaks how much of the hash matched,
        // which is enough to reconstruct it byte by byte.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
