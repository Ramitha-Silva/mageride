using System.Security.Cryptography;
using MageRide.Iam.Auth;
using MageRide.Iam.Configuration;
using MageRide.Shared.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Iam.Tests.Auth;

/// <summary>
/// The 90-day signing-key rotation (D7' §13, D-21) — one key signs, the outgoing one stays
/// published and accepted until every token it signed has expired.
/// </summary>
/// <remarks>
/// The failure this prevents is specific: promote a new key with no overlap and every session
/// issued in the previous thirty minutes 401s at once, across every service that validates
/// against the JWKS. It looks like an outage and it is caused by a deploy.
/// </remarks>
public sealed class KeyRotationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_jwks_publishes_the_active_key_first_and_then_the_overlap()
    {
        var outgoing = NewPrivateKeyPem();
        var incoming = NewPrivateKeyPem();

        var ring = Ring(incoming, outgoing);
        var published = JsonWebKeySetDocument.From(ring).Keys;

        Assert.Equal(2, published.Count);
        Assert.Equal(ring.KeyId, published[0].Kid);
        Assert.Equal(ring.KeyIds, published.Select(key => key.Kid));
    }

    [Fact]
    public void Only_the_active_key_signs()
    {
        var outgoing = NewPrivateKeyPem();
        var incoming = NewPrivateKeyPem();

        var ring = Ring(incoming, outgoing);
        var issued = Issuer(ring).Issue(Request());

        Assert.Equal(ring.KeyId, new JsonWebToken(issued.Value).Kid);
        Assert.Equal(Ring(incoming).KeyId, new JsonWebToken(issued.Value).Kid);
    }

    /// <summary>
    /// The whole point of the overlap: a token minted before the rotation is still good after it.
    /// </summary>
    [Fact]
    public async Task A_token_signed_by_the_retired_key_still_validates_after_the_rotation()
    {
        var outgoing = NewPrivateKeyPem();
        var incoming = NewPrivateKeyPem();

        // Yesterday's process: the outgoing key was the only key.
        var before = Issuer(Ring(outgoing)).Issue(Request());

        // Today's: the incoming key signs, the outgoing one is kept for the overlap.
        var after = Ring(incoming, outgoing);

        Assert.True(await ValidatesAsync(before.Value, after), "the retired key was dropped too early");
        Assert.Single(after.Resolve(new JsonWebToken(before.Value).Kid));
    }

    [Fact]
    public async Task A_token_signed_by_a_key_that_left_the_ring_no_longer_validates()
    {
        var dropped = NewPrivateKeyPem();
        var stale = Issuer(Ring(dropped)).Issue(Request());

        // The deploy after the overlap ends: the retired key is gone from configuration.
        var current = Ring(NewPrivateKeyPem());

        Assert.False(await ValidatesAsync(stale.Value, current));
        Assert.Empty(current.Resolve(new JsonWebToken(stale.Value).Kid));
    }

    [Fact]
    public void A_retired_entry_that_repeats_the_active_key_is_ignored_rather_than_published_twice()
    {
        var pem = NewPrivateKeyPem();

        // A half-finished rotation: the operator added the new key to both settings. Publishing
        // one kid twice makes a consumer's key set ambiguous.
        var ring = Ring(pem, pem);

        Assert.Single(ring.KeyIds);
        Assert.Single(JsonWebKeySetDocument.From(ring).Keys);
    }

    [Fact]
    public void Blank_entries_in_the_overlap_list_are_skipped()
    {
        // Environment-driven configuration produces these: Jwt__RetiredSigningKeyPems__0 set to
        // the empty string is how a deploy pipeline "removes" a key.
        var ring = Ring(NewPrivateKeyPem(), string.Empty, "   ");

        Assert.Single(ring.KeyIds);
    }

    /// <summary>
    /// A rotation must not log everybody out, which it would if the refresh HMAC were derived
    /// from the signing key and the signing key changed (<c>Jwt:RefreshTokenKey</c>, D7' §4.2).
    /// </summary>
    [Fact]
    public void A_configured_refresh_key_survives_a_signing_key_rotation()
    {
        var before = Ring(NewPrivateKeyPem());
        var after = Ring(NewPrivateKeyPem());

        Assert.Equal(
            before.DeriveRefreshTokenKey("a-configured-refresh-secret"),
            after.DeriveRefreshTokenKey("a-configured-refresh-secret"));

        // And the documented consequence of leaving it unset.
        Assert.NotEqual(before.DeriveRefreshTokenKey(null), after.DeriveRefreshTokenKey(null));
    }

    [Fact]
    public void An_unsigned_or_kid_less_token_is_offered_every_key_in_the_ring()
    {
        var ring = Ring(NewPrivateKeyPem(), NewPrivateKeyPem());

        // Mid-overlap, refusing a kid-less token would be refusing one this service itself signed.
        Assert.Equal(2, ring.Resolve(null).Count());
        Assert.Empty(ring.Resolve("some-other-services-kid"));
    }

    private static SigningKeyRing Ring(string activePem, params string[] retiredPems)
    {
        var options = new TokenOptions { SigningKeyPem = activePem, Issuer = "https://iam.mageride.lk" };

        foreach (var pem in retiredPems)
        {
            options.RetiredSigningKeyPems.Add(pem);
        }

        return new SigningKeyRing(
            Options.Create(options), TestEnvironment.Development, NullLogger<SigningKeyRing>.Instance);
    }

    private static AccessTokenIssuer Issuer(SigningKeyRing ring) => new(
        ring,
        Options.Create(new TokenOptions { Issuer = "https://iam.mageride.lk" }),
        new FakeTimeProvider(Now));

    private static AccessTokenRequest Request() => new(
        UserId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Roles: [MageRideRoles.Driver],
        DeviceKey: "device-abc",
        App: MageRideApps.Driver,
        SessionId: Guid.Parse("22222222-2222-2222-2222-222222222222"));

    /// <summary>Validates a token against exactly the bytes the JWKS endpoint would serve.</summary>
    private static async Task<bool> ValidatesAsync(string token, SigningKeyRing ring)
    {
        var published = JsonWebKeySet.Create(JsonWebKeySetDocument.From(ring).ToJson());

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = published.GetSigningKeys(),
            ValidIssuer = "https://iam.mageride.lk",
            ValidateAudience = false,
            ValidateLifetime = false,
        });

        return result.IsValid;
    }

    private static string NewPrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}
