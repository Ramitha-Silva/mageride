using System.Security.Cryptography;
using MageRide.Iam.Auth;
using MageRide.Iam.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.Tests.Auth;

/// <summary>
/// The opaque refresh token (D-29). Opaque to the client, self-describing to the server: the
/// session id plus an HMAC, so <c>iam.sessions</c> stays the single record of a session.
/// </summary>
public sealed class RefreshTokenCodecTests
{
    private static readonly string Pem = NewPrivateKeyPem();

    private static RefreshTokenCodec Create(string? refreshKey = null, string? pem = null)
    {
        var options = Options.Create(new TokenOptions { SigningKeyPem = pem ?? Pem, RefreshTokenKey = refreshKey });
        var keys = new SigningKeyRing(options, TestEnvironment.Development, NullLogger<SigningKeyRing>.Instance);

        return new RefreshTokenCodec(keys, options);
    }

    [Fact]
    public void A_token_round_trips_to_its_session_id()
    {
        var codec = Create();
        var sessionId = Guid.NewGuid();

        Assert.True(codec.TryRead(codec.Issue(sessionId), out var read));
        Assert.Equal(sessionId, read);
    }

    [Fact]
    public void It_reveals_nothing_and_is_url_safe()
    {
        var codec = Create();
        var sessionId = Guid.NewGuid();

        var token = codec.Issue(sessionId);

        Assert.DoesNotContain(sessionId.ToString(), token, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-a-token")]
    [InlineData("mr1.abc")]
    [InlineData("mr2.AAAAAAAAAAAAAAAAAAAAAA.AAAA")]
    public void Junk_never_reaches_the_database(string? token)
    {
        Assert.False(Create().TryRead(token, out var read));
        Assert.Equal(Guid.Empty, read);
    }

    [Fact]
    public void A_tampered_session_id_fails_the_signature()
    {
        var codec = Create();
        var token = codec.Issue(Guid.NewGuid());

        var parts = token.Split('.');
        var swapped = string.Join('.', parts[0], codec.Issue(Guid.NewGuid()).Split('.')[1], parts[2]);

        Assert.False(codec.TryRead(swapped, out _));
    }

    [Fact]
    public void A_token_from_another_deployment_is_not_ours()
    {
        var token = Create(refreshKey: "key-one").Issue(Guid.NewGuid());

        Assert.False(Create(refreshKey: "key-two").TryRead(token, out _));
    }

    [Fact]
    public void Without_a_configured_key_the_signing_key_derives_one_deterministically()
    {
        var token = Create().Issue(Guid.NewGuid());

        // Same signing key, new process: the token still verifies.
        Assert.True(Create().TryRead(token, out _));

        // Different signing key: it does not. This is why a deployment should set
        // Jwt:RefreshTokenKey — a 90-day signing rotation would otherwise log everybody out.
        Assert.False(Create(pem: NewPrivateKeyPem()).TryRead(token, out _));
    }

    [Fact]
    public void An_explicit_refresh_key_survives_a_signing_key_rotation()
    {
        var token = Create(refreshKey: "stable-across-rotations").Issue(Guid.NewGuid());

        Assert.True(Create(refreshKey: "stable-across-rotations", pem: NewPrivateKeyPem()).TryRead(token, out _));
    }

    private static string NewPrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}
