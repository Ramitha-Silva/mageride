using MageRide.Iam.Auth;
using MageRide.Iam.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.Tests.Auth;

/// <summary>
/// The portal password verifier (AL-07). Nothing here touches a database — the point of a
/// PHC-style encoded hash is that the string is self-contained.
/// </summary>
public sealed class PasswordHasherTests
{
    private const string Password = "correct horse battery staple";

    private static PasswordHasher Hasher(int iterations = 100_000) =>
        new(Options.Create(new AuthPolicyOptions { PasswordIterations = iterations }));

    [Fact]
    public void A_password_verifies_against_its_own_hash()
    {
        var hasher = Hasher();

        Assert.True(hasher.Verify(Password, hasher.Hash(Password)));
    }

    [Fact]
    public void A_wrong_password_does_not()
    {
        var hasher = Hasher();

        Assert.False(hasher.Verify("Correct horse battery staple", hasher.Hash(Password)));
    }

    /// <summary>
    /// Two accounts with the same password must not share a hash, or a leaked table tells an
    /// attacker which accounts to attack once.
    /// </summary>
    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        var hasher = Hasher();

        var first = hasher.Hash(Password);
        var second = hasher.Hash(Password);

        Assert.NotEqual(first, second);
        Assert.True(hasher.Verify(Password, first));
        Assert.True(hasher.Verify(Password, second));
    }

    /// <summary>
    /// The reason the parameters live in the string: raising the work factor must not invalidate
    /// rows written at the old one.
    /// </summary>
    [Fact]
    public void A_hash_made_at_a_lower_work_factor_still_verifies_after_it_is_raised()
    {
        var old = Hasher(100_000).Hash(Password);

        Assert.True(Hasher(600_000).Verify(Password, old));
    }

    [Fact]
    public void The_encoded_form_names_its_algorithm_and_iterations()
    {
        var encoded = Hasher(123_456).Hash(Password);

        Assert.StartsWith("$pbkdf2-sha256$i=123456$", encoded, StringComparison.Ordinal);
    }

    /// <summary>
    /// A corrupt or truncated row is a failed sign-in, not a 500 on somebody's login screen.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("$pbkdf2-sha256$i=600000$onlythreeparts")]
    [InlineData("$argon2id$i=3$c2FsdA$aGFzaA")]
    [InlineData("$pbkdf2-sha256$i=zero$c2FsdA$aGFzaA")]
    [InlineData("$pbkdf2-sha256$i=600000$not base64!$aGFzaA")]
    public void A_malformed_verifier_fails_closed(string encoded)
    {
        Assert.False(Hasher().Verify(Password, encoded));
    }

    /// <summary>
    /// The contract's <c>PasswordLogin.password</c> is <c>minLength: 12</c>, enforced where a
    /// password enters the system rather than where one is presented — refusing a short password
    /// at sign-in would reject a credential that already exists and announce the policy while
    /// doing it.
    /// </summary>
    [Fact]
    public void A_password_below_the_floor_cannot_be_stored()
    {
        var exception = Assert.Throws<MageRide.Shared.Errors.MageRideValidationException>(
            () => Hasher().Hash("short"));

        Assert.Contains("password", exception.Errors.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public void An_empty_password_never_verifies()
    {
        var hasher = Hasher();

        Assert.False(hasher.Verify(null, hasher.Hash(Password)));
        Assert.False(hasher.Verify(string.Empty, hasher.Hash(Password)));
    }

    /// <summary>
    /// The "no such account" branch runs a real derivation against this, so it has to be a
    /// well-formed verifier that nothing matches.
    /// </summary>
    [Fact]
    public void The_dummy_verifier_is_well_formed_and_matches_nothing()
    {
        var hasher = Hasher();

        Assert.StartsWith("$pbkdf2-sha256$", hasher.DummyVerifier, StringComparison.Ordinal);
        Assert.False(hasher.Verify(Password, hasher.DummyVerifier));
        Assert.False(hasher.Verify(string.Empty, hasher.DummyVerifier));
    }
}
