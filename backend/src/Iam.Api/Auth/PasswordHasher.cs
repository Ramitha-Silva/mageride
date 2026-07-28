using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.Iam.Configuration;
using MageRide.Shared.Errors;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.Auth;

/// <summary>
/// Hashes and verifies portal passwords (AL-07).
/// </summary>
/// <remarks>
/// <para>
/// PBKDF2-HMAC-SHA256, 600 000 iterations by default, 128-bit salt, 256-bit output. Argon2id
/// would be the stronger primitive, but it is a third-party package and this is the only
/// consumer; PBKDF2 is in the BCL, is FIPS-approved, and at OWASP's iteration count is the
/// difference between "a leaked table is a password list" and "a leaked table is a bill".
/// </para>
/// <para>
/// The stored form is PHC-style — <c>$pbkdf2-sha256$i=600000$salt$hash</c> — so every row carries
/// the parameters it was made with. Raising
/// <see cref="AuthPolicyOptions.PasswordIterations"/> therefore needs no migration: existing rows
/// keep verifying at their own cost and are re-hashed the next time the password is set.
/// </para>
/// </remarks>
public sealed class PasswordHasher(IOptions<AuthPolicyOptions> options)
{
    private const string Algorithm = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly AuthPolicyOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// The encoded verifier for a new or changed password.
    /// </summary>
    /// <remarks>
    /// The length floor is enforced here rather than at sign-in, because this is the only way a
    /// password enters the system: rejecting a short one at sign-in would refuse a credential
    /// that already exists while telling an attacker what the policy is. The contract's
    /// <c>PasswordLogin.password</c> is <c>minLength: 12</c>, and whoever provisions the account
    /// (admin-bff, C062) is what calls this.
    /// </remarks>
    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        if (password.Length < _options.MinimumPasswordLength)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["password"] = [$"password must be at least {_options.MinimumPasswordLength} characters."],
            });
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var iterations = _options.PasswordIterations;
        var hash = Derive(password, salt, iterations);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"${Algorithm}$i={iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>
    /// Verifies a presented password against a stored verifier. Never throws on a malformed
    /// stored value — a corrupt row is a failed sign-in, not a 500 on somebody's login screen.
    /// </summary>
    public bool Verify(string? password, string? encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        // $pbkdf2-sha256$i=600000$<salt>$<hash> — the leading '$' makes the first segment empty.
        var parts = encoded.Split('$');
        if (parts.Length != 5 ||
            !string.Equals(parts[1], Algorithm, StringComparison.Ordinal) ||
            !parts[2].StartsWith("i=", StringComparison.Ordinal) ||
            !int.TryParse(parts[2].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) ||
            iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// A verifier-shaped value that matches nothing, for the "no such account" branch.
    /// </summary>
    /// <remarks>
    /// Sign-in runs a real derivation whether or not the email exists, so the response time does
    /// not tell an attacker which addresses are registered. The 600 000 iterations that make the
    /// hash expensive make that leak measurable if we skip it.
    /// </remarks>
    public string DummyVerifier { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"${Algorithm}$i={options?.Value.PasswordIterations ?? 600_000}${Convert.ToBase64String(new byte[SaltBytes])}${Convert.ToBase64String(new byte[HashBytes])}");

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
