using System.Security.Cryptography;
using System.Text;
using MageRide.Shared.Payments;

namespace MageRide.Shared.Tests.Payments;

/// <summary>
/// The <c>X-Signature</c> verification every payment-provider callback on this platform shares
/// (D6' §7.1/§7.2, <c>_shared.yaml</c>'s <c>hmacSignature</c>).
/// </summary>
/// <remarks>
/// In the kernel because there are six of these callbacks across four services (C046's two, C048's and
/// C049/C050's), and four copies of a signature check is four chances for one of them to compare with
/// <c>==</c>. Promoted here by C046, the first component to need one.
/// </remarks>
public sealed class WebhookSignatureTests
{
    private const string Secret = "provider-webhook-secret";

    private static readonly byte[] Body = Encoding.UTF8.GetBytes(
        """{"orderId":"mr-topup-1","providerTransactionId":"txn-1","status":"CHARGED","amountMinor":100000}""");

    [Fact]
    public void A_signature_this_class_computed_verifies() =>
        Assert.True(WebhookSignature.IsValid(Body, WebhookSignature.Compute(Body, Secret), Secret));

    /// <summary>
    /// The digest is HMAC-SHA256 over the raw bytes, keyed with the secret — asserted against an
    /// independently computed value rather than against this class's own output, so a change of algorithm
    /// cannot pass by agreeing with itself.
    /// </summary>
    [Fact]
    public void The_digest_is_hmac_sha256_over_the_raw_body()
    {
        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Body));

        Assert.Equal(expected, WebhookSignature.Compute(Body, Secret));
    }

    /// <summary>
    /// Both encodings are accepted: OnePay's documentation and the Commercial Bank IPG differ on hex
    /// versus base64, and a deployment that guessed wrong would reject every genuine callback — which
    /// looks exactly like a provider outage.
    /// </summary>
    [Fact]
    public void Hex_and_base64_are_both_accepted()
    {
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Body);

        Assert.True(WebhookSignature.IsValid(Body, Convert.ToHexStringLower(digest), Secret));
        Assert.True(WebhookSignature.IsValid(Body, Convert.ToHexString(digest), Secret));
        Assert.True(WebhookSignature.IsValid(Body, Convert.ToBase64String(digest), Secret));
    }

    /// <summary>Some providers prefix the header; the prefix is stripped rather than rejected.</summary>
    [Fact]
    public void A_sha256_prefix_is_tolerated()
    {
        var signature = WebhookSignature.Compute(Body, Secret);

        Assert.True(WebhookSignature.IsValid(Body, $"sha256={signature}", Secret));
        Assert.True(WebhookSignature.IsValid(Body, $"SHA256={signature}", Secret));
        Assert.True(WebhookSignature.IsValid(Body, $"  {signature}  ", Secret));
    }

    /// <summary>A body that changed by one byte does not verify. This is the whole point.</summary>
    [Fact]
    public void A_modified_body_does_not_verify()
    {
        var signature = WebhookSignature.Compute(Body, Secret);
        var tampered = Encoding.UTF8.GetBytes(
            """{"orderId":"mr-topup-1","providerTransactionId":"txn-1","status":"CHARGED","amountMinor":900000}""");

        Assert.False(WebhookSignature.IsValid(tampered, signature, Secret));
    }

    [Fact]
    public void Another_secret_does_not_verify() =>
        Assert.False(WebhookSignature.IsValid(Body, WebhookSignature.Compute(Body, "other-secret"), Secret));

    /// <summary>
    /// No secret configured means nothing verifies — there is deliberately no "accept anything" branch: a
    /// wallet-credit endpoint that trusts an unsigned body is a free-money endpoint.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_secret_verifies_nothing(string? secret) =>
        Assert.False(WebhookSignature.IsValid(Body, "anything", secret));

    /// <summary>A malformed header is an ordinary event on a public endpoint, not an exception.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex-at-all")]
    [InlineData("zzzz")]
    [InlineData("sha256=")]
    // 64 characters, so it passes the length test and fails the parse.
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    // A well-formed digest of the right shape but the wrong length.
    [InlineData("abcd")]
    public void A_malformed_header_is_false_rather_than_a_throw(string? presented) =>
        Assert.False(WebhookSignature.IsValid(Body, presented, Secret));

    /// <summary>An empty body is signable — a provider that posts nothing still has to prove it is them.</summary>
    [Fact]
    public void An_empty_body_is_signed_like_any_other()
    {
        var signature = WebhookSignature.Compute(ReadOnlySpan<byte>.Empty, Secret);

        Assert.True(WebhookSignature.IsValid(ReadOnlySpan<byte>.Empty, signature, Secret));
        Assert.False(WebhookSignature.IsValid(Body, signature, Secret));
    }

    [Fact]
    public void Computing_without_a_secret_is_a_programming_error() =>
        Assert.Throws<ArgumentException>(() => WebhookSignature.Compute(Body, string.Empty));

    /// <summary>The header name is the one the contract declares, in one place.</summary>
    [Fact]
    public void The_header_is_the_one_the_contract_declares() =>
        Assert.Equal("X-Signature", WebhookSignature.HeaderName);
}
