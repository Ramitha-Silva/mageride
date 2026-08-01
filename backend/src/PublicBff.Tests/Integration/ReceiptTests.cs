using MageRide.PublicBff.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.PublicBff.Tests.Integration;

/// <summary>
/// SCR-WT-005 (US-25.6): the four outcomes, and the refusal before the journey ends.
/// </summary>
[Collection<PublicBffCollection>]
public sealed class ReceiptTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_receipt_asked_for_mid_journey_is_a_409()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 2);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        var (status, code, _) = await PublicBffHarness.ProblemAsync(
            await harness.GetAsync($"/public/track/{token}/receipt"));

        Assert.Equal(409, status);
        Assert.Equal("receipt-not-ready", code);
    }

    [Fact]
    public async Task A_delivery_signed_for_by_code_reports_otp_verified()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(
            state: "Paid", kind: 2, paymentMethod: "onepay",
            terminalAt: harness.Now.AddMinutes(-4));

        await harness.Seed.PaymentAsync(ride.RideId, "Succeeded", "onepay", 52_500);

        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(1));

        var body = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}/receipt"), "the receipt");

        Assert.Equal("package", body.GetProperty("kind").GetString());
        Assert.Equal("otp_verified", body.GetProperty("proof").GetString());
        Assert.Equal(52_500, body.GetProperty("totalMinor").GetInt64());
        Assert.Equal("LKR", body.GetProperty("currency").GetString());
        Assert.Equal(harness.Now.AddMinutes(-4), body.GetProperty("completedAt").GetDateTimeOffset());

        // AL-48's tel: link exists so a recipient can reach a driver who is on the way to them.
        // Once the parcel is delivered there is nothing to call about, and a receipt gets forwarded.
        Assert.False(body.GetProperty("driver").TryGetProperty("phone", out _));
    }

    [Fact]
    public async Task A_delivery_the_recipient_missed_reports_photo_proof()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(
            state: "Paid", kind: 2, paymentMethod: "onepay",
            terminalAt: harness.Now.AddMinutes(-9));

        await harness.Seed.PaymentAsync(ride.RideId, "Succeeded", "onepay", 40_000);
        await harness.Seed.ProofPhotoAsync(ride.RideId);

        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(1));

        var body = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}/receipt"), "the receipt");

        Assert.Equal("photo_proof", body.GetProperty("proof").GetString());

        // The stored pointer is an `s3://` key and must never reach a browser. With no bucket
        // configured nothing can be presigned, and the field is absent rather than leaking the key.
        Assert.True(
            !body.TryGetProperty("proofPhotoUrl", out var proofUrl)
            || !proofUrl.GetString()!.StartsWith("s3://", StringComparison.Ordinal),
            "a receipt must never hand out the stored object pointer");
    }

    [Fact]
    public async Task A_cash_on_delivery_parcel_reports_cod_collected_even_when_it_was_photographed()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(
            state: "CashOnDeliveryCollected", kind: 2, paymentMethod: "cod",
            terminalAt: harness.Now.AddMinutes(-2));

        await harness.Seed.PaymentAsync(ride.RideId, "CashOnDeliveryCollected", "cod", 45_000);
        await harness.Seed.ProofPhotoAsync(ride.RideId);

        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(1));

        var body = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}/receipt"), "the receipt");

        // Money outranks evidence: a receipt is opened to answer "was this paid for".
        Assert.Equal("cod_collected", body.GetProperty("proof").GetString());
    }

    [Fact]
    public async Task An_uncollected_parcel_reports_disputed()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(
            state: "Disputed", kind: 2, paymentMethod: "cod",
            terminalAt: harness.Now.AddHours(-25));

        await harness.Seed.ProofPhotoAsync(ride.RideId);

        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(1));

        var body = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}/receipt"), "the receipt");

        // P-14, and it outranks everything: a receipt claiming a successful handover on a disputed
        // delivery would be the platform contradicting its own ledger.
        Assert.Equal("disputed", body.GetProperty("proof").GetString());
        Assert.Equal("Disputed", body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_cash_settled_proxy_ride_reports_cod_collected()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(
            state: "CashSettled", kind: 1, paymentMethod: "cash",
            terminalAt: harness.Now.AddMinutes(-1));

        await harness.Seed.PaymentAsync(ride.RideId, "Succeeded", "cash", 45_000);

        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(1));

        var body = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}/receipt"), "the receipt");

        Assert.Equal("ride", body.GetProperty("kind").GetString());
        Assert.Equal("cod_collected", body.GetProperty("proof").GetString());
    }

    [Fact]
    public async Task A_cancelled_ride_has_no_receipt()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(
            state: "CancelledByDriver", kind: 1, paymentMethod: "cash",
            terminalAt: harness.Now.AddMinutes(-1));

        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(1));

        var (status, code, _) = await PublicBffHarness.ProblemAsync(
            await harness.GetAsync($"/public/track/{token}/receipt"));

        // A journey that did not happen has no receipt, and answering one with `otp_verified` would
        // be a record of a handoff that never took place.
        Assert.Equal(409, status);
        Assert.Equal("receipt-not-ready", code);
    }

    [Fact]
    public async Task A_pickup_confirm_link_has_no_journey_to_receipt()
    {
        await using var harness = await StartAsync();

        var (token, _, _, _) = await harness.Seed.PickupRequestAsync(
            issuedAt: harness.Now.AddSeconds(-30));

        var (status, code, _) = await PublicBffHarness.ProblemAsync(
            await harness.GetAsync($"/public/track/{token}/receipt"));

        Assert.Equal(409, status);
        Assert.Equal("receipt-not-ready", code);
    }

    private Task<PublicBffHarness> StartAsync() => PublicBffHarness.StartAsync(postgres, redis);
}
