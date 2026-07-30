using System.Text.Json;
using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Credentials;
using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Trackers;
using MageRide.Shared.Http;
using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Identity;
using MageRide.TcpAdapter.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Both sides of the seam declare this record — which is the point of the test below. Naming them
// apart is what lets one file hold both.
using AdapterSignal = MageRide.TcpAdapter.Identity.TrackerCredentialSignal;
using ProvisioningSignal = MageRide.Provisioning.Trackers.TrackerCredentialSignal;

namespace MageRide.TcpAdapter.Tests.Identity;

/// <summary>
/// The two formats this service implements a second time, asserted against provisioning-svc's own.
/// </summary>
/// <remarks>
/// <para>
/// The fence between C030 and C043 is that protocol decoding is the adapter's and credential minting is
/// provisioning-svc's; neither project references the other's implementation. That leaves two formats
/// written down twice — the signed PSK token (D6' §4.2) and the <c>prov:tracker</c> signal (T-12) — and
/// a divergence in either is <b>silent</b>: a renamed JSON field turns every value null and the socket
/// never closes; a changed HMAC payload makes every credential look forged.
/// </para>
/// <para>
/// So this file is the seam, and it is the reason <c>TcpAdapter.Tests</c> references
/// <c>Provisioning.Api</c> at all. The same shape as <c>HotPath.Tests</c> referencing
/// <c>TripState.Api</c> for one event name.
/// </para>
/// </remarks>
[Trait("Category", "Credential")]
public sealed class CredentialTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mageride-c043-ca-" + Guid.NewGuid().ToString("N")[..8]);

    private static readonly Guid Vehicle = Guid.Parse("00000000-0000-4000-8000-00000000c001");

    [Fact]
    public void A_PSK_token_provisioning_svc_minted_verifies_in_the_adapter()
    {
        using var ca = NewCertificateAuthority();

        var credential = ca.Issue(CredentialTypes.Psk, Vehicle, Captures.Imei, DateTimeOffset.UtcNow);

        Assert.NotNull(credential.PskToken);

        using var credentials = NewCredentials();

        Assert.True(credentials.CanVerify, "the adapter must find the signing key the CA wrote");

        Assert.True(
            credentials.TryRead(credential.PskToken, Captures.Imei, DateTimeOffset.UtcNow, out var serial),
            "a token minted by provisioning-svc must verify against the same signing key");

        Assert.Equal(credential.Serial, serial);
    }

    [Fact]
    public void A_token_minted_for_another_device_does_not_verify()
    {
        // The IMEI is inside the signature, which is the whole point of "signed PSK" rather than
        // "random secret": a token lifted off one tracker is useless on the next one.
        using var ca = NewCertificateAuthority();
        using var credentials = NewCredentials();

        var credential = ca.Issue(CredentialTypes.Psk, Vehicle, Captures.Imei, DateTimeOffset.UtcNow);

        Assert.False(
            credentials.TryRead(credential.PskToken, Captures.UnboundImei, DateTimeOffset.UtcNow, out var serial));

        // The serial is still reported: it is evidence for the anti-clone rule (T-08) rather than
        // authority, and provisioning-svc records the sighting whichever way the verdict went.
        Assert.Equal(credential.Serial, serial);
    }

    [Fact]
    public void An_expired_token_does_not_verify()
    {
        using var ca = NewCertificateAuthority();
        using var credentials = NewCredentials();

        var credential = ca.Issue(CredentialTypes.Psk, Vehicle, Captures.Imei, DateTimeOffset.UtcNow);

        // Cred:RotationDays defaults to 90; a year on, the token is long dead.
        Assert.False(
            credentials.TryRead(credential.PskToken, Captures.Imei, DateTimeOffset.UtcNow.AddYears(1), out _));
    }

    [Fact]
    public void A_forged_signature_does_not_verify()
    {
        using var ca = NewCertificateAuthority();
        using var credentials = NewCredentials();

        var credential = ca.Issue(CredentialTypes.Psk, Vehicle, Captures.Imei, DateTimeOffset.UtcNow);
        var parts = credential.PskToken!.Split('.');
        var forged = string.Join('.', parts[0], parts[1], parts[2], parts[3], "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        Assert.False(credentials.TryRead(forged, Captures.Imei, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void Only_a_token_shaped_like_one_is_treated_as_a_credential()
    {
        // JT/T 808's 0x0102 body is whatever the device was registered with, which for most firmware is
        // its own id echoed back. That is not a credential and must not be judged as a forged one.
        Assert.False(PskCredentials.LooksLikeToken("356938035643809"));
        Assert.False(PskCredentials.LooksLikeToken(null));
        Assert.False(PskCredentials.LooksLikeToken("mrp1.only.three.parts"));
        Assert.True(PskCredentials.LooksLikeToken("mrp1.serial.999.secret.signature"));
    }

    [Fact]
    public void An_unconfigured_signing_key_leaves_the_adapter_unable_to_verify_rather_than_refusing()
    {
        // A pod that starts before the CA volume is populated has to come up and serve the three
        // families whose protocols carry no credential at all.
        using var credentials = new PskCredentials(Options.Create(new AdapterOptions()));

        Assert.False(credentials.CanVerify);
        Assert.False(credentials.TryRead("mrp1.abc.9999999999.secret.signature", Captures.Imei, DateTimeOffset.UtcNow, out var serial));

        // The serial still comes out, so it can reach validate as anti-clone evidence.
        Assert.Equal("abc", serial);
    }

    /// <summary>
    /// The <c>prov:tracker</c> signal provisioning-svc publishes deserialises into the record this
    /// service reads it with.
    /// </summary>
    /// <remarks>
    /// Serialised with <see cref="MageRideJson.Options"/> because that is what
    /// <c>TrackerCache.PublishAsync</c> uses. If the two records' member names drift apart the payload
    /// still parses — every field simply comes out null — and the only symptom is a revoked tracker that
    /// keeps publishing until its socket's five-minute re-validation catches it.
    /// </remarks>
    [Fact]
    public void The_revocation_signal_provisioning_svc_publishes_is_the_one_the_watcher_reads()
    {
        var published = new ProvisioningSignal(
            TrackerEventTypes.TrackerRevoked,
            Captures.Imei,
            Vehicle,
            ["01:23:45", "67:89:AB"],
            "unbound",
            new DateTimeOffset(2026, 7, 30, 4, 15, 30, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(published, MageRideJson.Options);

        var received = JsonSerializer.Deserialize<AdapterSignal>(json, MageRideJson.Options);

        Assert.NotNull(received);
        Assert.Equal(published.Type, received!.Type);
        Assert.Equal(published.Imei, received.Imei);
        Assert.Equal(published.VehicleId, received.VehicleId);
        Assert.Equal(published.Serials, received.Serials);
        Assert.Equal(published.Reason, received.Reason);
        Assert.Equal(published.At, received.At);

        // And the two event names the watcher acts on are provisioning-svc's own.
        Assert.Equal(TrackerEventTypes.TrackerRevoked, RevocationWatcher.TrackerRevoked);
        Assert.Equal(TrackerEventTypes.TrackerBound, RevocationWatcher.TrackerBound);
    }

    [Fact]
    public void The_binding_states_the_adapter_maps_are_provisioning_svcs_own()
    {
        // TrackerDirectory spells REVOKED and QUARANTINED as literals because this project holds no
        // reference to C030's domain on the hot path. This is where that has to agree.
        Assert.Equal("REVOKED", BindingStates.Revoked);
        Assert.Equal("QUARANTINED", BindingStates.Quarantined);
        Assert.Equal("ACTIVE", BindingStates.Active);
    }

    [Fact]
    public void An_IMEI_is_fifteen_digits_and_the_Luhn_digit_is_not_enforced()
    {
        // Matching C030: D6' §4.1's grey-import units report IMEIs that fail Luhn, and refusing one
        // leaves a working tracker unprovisionable with no override.
        Assert.True(TrackerDirectory.IsImei(Captures.Imei));
        Assert.True(TrackerDirectory.IsImei("111111111111111"));
        Assert.False(TrackerDirectory.IsImei("12345678901234"));
        Assert.False(TrackerDirectory.IsImei("35693803564380X"));
        Assert.False(TrackerDirectory.IsImei(null));

        // And it agrees with provisioning-svc's own validator, which is the thing that decides whether
        // a binding could ever have existed.
        Assert.Equal(Imeis.IsValid(Captures.Imei), TrackerDirectory.IsImei(Captures.Imei));
        Assert.Equal(Imeis.IsValid("12345678901234"), TrackerDirectory.IsImei("12345678901234"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private EmbeddedStepCa NewCertificateAuthority() => new(
        Options.Create(new DevicePkiOptions { RootKeyPath = _root }),
        NullLogger<EmbeddedStepCa>.Instance);

    private PskCredentials NewCredentials() =>
        new(Options.Create(new AdapterOptions { PskKeyDirectory = _root }));
}
