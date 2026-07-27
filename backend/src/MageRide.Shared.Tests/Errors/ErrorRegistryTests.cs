using System.Text.RegularExpressions;
using MageRide.Shared.Errors;

namespace MageRide.Shared.Tests.Errors;

public sealed class ErrorRegistryTests
{
    [Fact]
    public void Every_code_is_a_stable_kebab_key()
    {
        var kebab = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$");

        foreach (var error in MageRideErrors.All)
        {
            Assert.Matches(kebab, error.Code);
        }
    }

    [Fact]
    public void Every_code_is_unique()
    {
        var codes = MageRideErrors.All.Select(e => e.Code).ToArray();
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_code_maps_to_an_error_status()
    {
        foreach (var error in MageRideErrors.All)
        {
            Assert.InRange(error.Status, 400, 599);
        }
    }

    [Fact]
    public void Type_uri_follows_the_D3_convention()
    {
        Assert.Equal("https://mageride.lk/errors/offer-expired", MageRideErrors.OfferExpired.TypeUri);
    }

    /// <summary>
    /// Guards the D3' §0 error table and the per-endpoint <c>Errors:</c> lines against drift: if a
    /// code the contracts name disappears from the registry, clients branching on it break.
    /// </summary>
    [Theory]
    [InlineData("invalid-phone", 400)]
    [InlineData("otp-expired", 400)]
    [InlineData("invalid-otp", 401)]
    [InlineData("attestation-failed", 401)]
    [InlineData("insufficient-wallet", 402)]
    [InlineData("user-blocked", 403)]
    [InlineData("booking-disabled", 403)]
    [InlineData("vehicle-not-found", 404)]
    [InlineData("offer-already-accepted", 409)]
    [InlineData("version-conflict", 409)]
    [InlineData("directional-limit-reached", 409)]
    [InlineData("payout-profile-not-verified", 409)]
    [InlineData("offer-expired", 410)]
    [InlineData("token-expired-or-revoked", 410)]
    [InlineData("too-many-rows", 413)]
    [InlineData("route-unavailable", 422)]
    [InlineData("otp-locked", 423)]
    [InlineData("upgrade-required", 426)]
    [InlineData("otp-rate-limited", 429)]
    [InlineData("loc-request-rate-limited", 429)]
    public void Spec_named_codes_are_registered_with_their_spec_status(string code, int status)
    {
        Assert.True(MageRideErrors.TryGet(code, out var error), $"'{code}' is named by a spec but is not in the registry.");
        Assert.Equal(status, error!.Status);
    }

    [Fact]
    public void Registering_a_service_code_twice_with_the_same_shape_is_idempotent()
    {
        var first = MageRideErrors.Register("test-only-code", 400, "Test only");
        var second = MageRideErrors.Register("test-only-code", 400, "Test only");

        Assert.Same(first, second);
    }

    [Fact]
    public void Redefining_a_registered_code_throws()
    {
        MageRideErrors.Register("test-only-conflict", 409, "Test only");

        var ex = Assert.Throws<InvalidOperationException>(
            () => MageRideErrors.Register("test-only-conflict", 400, "Something else"));

        Assert.Contains("public contract", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_kebab_code_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => MageRideErrors.Register("Not_Kebab", 400, "x"));
        Assert.Throws<ArgumentException>(() => MageRideErrors.Register("trailing-", 400, "x"));
        Assert.Throws<ArgumentException>(() => MageRideErrors.Register("double--hyphen", 400, "x"));
    }

    [Fact]
    public void A_success_status_is_not_an_error_code()
    {
        Assert.Throws<ArgumentException>(() => MageRideErrors.Register("looks-fine", 200, "x"));
    }
}
