using System.Security.Cryptography.X509Certificates;
using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Credentials;
using MageRide.Provisioning.Domain;
using MageRide.Shared.Mqtt;
using MageRide.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MageRide.Security.Tests.AntiSpoof.Mqtt;

/// <summary>
/// C128's second definition-of-done item: <b>a cross-vehicle publish attempt is refused by EMQX in
/// every tested configuration</b> (D6' §3.1, ADD §7.2/§7.7, T-02, E-02).
///
/// <para>
/// "Every configuration" means every listener, and there are three of them. They share the ACL file
/// and nothing else — the transport differs, and so does the mechanism that decides who the
/// principal is: a verified <c>vehicleId</c> JWT claim on 1883 and 8084, a certificate CN on 8883.
/// A refusal proved on one says nothing about the other two, which is why this is a matrix.
/// </para>
///
/// <para>
/// <b>8084 had never been driven by any suite before C128</b>, and it is the one a driver's handset
/// actually connects to — 1883 is documented as in-cluster only and never published past the docker
/// network. The listener with the most coverage was the one with the least exposure.
/// </para>
/// </summary>
[Collection<AntiSpoofCollection>]
[Trait("Category", "AntiSpoof")]
public sealed class CrossVehiclePublishTests(EmqxFixture emqx) : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly List<X509Certificate2> _issued = [];

    /// <summary>The three planes, as theory data — so a plane that stopped being covered is visible.</summary>
    public static TheoryData<MqttPlane> Planes() => new(Enum.GetValues<MqttPlane>());

    /// <summary>The DoD assertion, on every plane.</summary>
    [Theory]
    [MemberData(nameof(Planes))]
    public async Task A_device_publishing_under_another_vehicles_topic_is_refused(MqttPlane plane)
    {
        RequireBroker();

        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        await using var device = await ConnectAsync(mine, plane);

        try
        {
            await device.PublishAsync(MqttTopics.PositionLive(theirs), "{}"u8.ToArray());
        }
        catch (Exception)
        {
            // `deny_action = disconnect` may drop the socket before the PUBACK, which is the same
            // refusal arriving sooner.
        }

        await AssertDisconnectedAsync(
            device,
            $"On the {plane} plane, EMQX admitted a publish to another vehicle's topic. "
            + "acl.conf grants publish only under veh/${username}/*, and the username is bound to "
            + "the credential — so this is either a rule that stopped being principal-scoped or a "
            + "listener whose principal is not what the ACL thinks it is.");
    }

    /// <summary>
    /// The same device, publishing where it is entitled to. Without this the test above would pass
    /// on a broker that refused everything.
    /// </summary>
    [Theory]
    [MemberData(nameof(Planes))]
    public async Task A_device_may_publish_to_its_own_live_topic(MqttPlane plane)
    {
        RequireBroker();

        var vehicleId = Guid.NewGuid();
        await using var device = await ConnectAsync(vehicleId, plane);

        var result = await device.PublishAsync(MqttTopics.PositionLive(vehicleId), "{}"u8.ToArray());

        Assert.True(result.IsSuccess, $"{plane}: a device could not publish its own position.");
        Assert.True(device.IsConnected);
    }

    /// <summary>
    /// Nothing outside the vehicle tree, on any plane — <c>no_match = deny</c> plus the final
    /// <c>{deny, all}</c>.
    /// </summary>
    /// <remarks>
    /// Without both, a topic nobody wrote a rule for would be <i>allowed</i> — EMQX's shipped
    /// default — and every rule in <c>acl.conf</c> would be advisory rather than exhaustive.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Planes))]
    public async Task A_device_may_not_publish_outside_the_vehicle_tree_at_all(MqttPlane plane)
    {
        RequireBroker();

        await using var device = await ConnectAsync(Guid.NewGuid(), plane);

        try
        {
            await device.PublishAsync("telemetry/anything", "x"u8.ToArray());
        }
        catch (Exception)
        {
            // As above.
        }

        await AssertDisconnectedAsync(
            device, $"{plane}: an unlisted topic must be denied by the fallthrough, not allowed.");
    }

    /// <summary>
    /// The E-08 shared subscription belongs to <c>svc-</c> principals, on every plane.
    /// </summary>
    /// <remarks>
    /// A device that could join <c>posGroup</c> would not merely read every vehicle's position — it
    /// would <b>take</b> messages out of the shared subscription and mqtt-bridge-svc would silently
    /// stop seeing them. Silent, because a shared subscription losing a member is not an error.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Planes))]
    public async Task A_device_may_not_hold_the_platforms_shared_subscription(MqttPlane plane)
    {
        RequireBroker();

        await using var device = await ConnectAsync(Guid.NewGuid(), plane);

        try
        {
            await device.SubscribeAsync(MqttTopics.SharedPositionLive());
        }
        catch (Exception)
        {
            // A refused subscribe with deny_action = disconnect drops the socket.
        }

        await AssertDisconnectedAsync(
            device, $"{plane}: only svc-* principals may hold the E-08 shared subscription.");
    }

    /// <summary>
    /// A stolen credential does not become another vehicle's credential by being presented as one.
    /// </summary>
    /// <remarks>
    /// The JWT planes are refused at CONNECT by <c>verify_claims</c>. The tracker plane cannot be
    /// tested the same way — a certificate's CN is not a field the client chooses — so the
    /// equivalent there is <see cref="A_tracker_is_confined_by_its_certificate_subject_and_not_by_what_it_claims"/>.
    /// </remarks>
    [Theory]
    [InlineData(MqttPlane.InClusterTcp)]
    [InlineData(MqttPlane.MobileWebSocket)]
    public async Task A_device_presenting_another_vehicles_token_is_refused_at_connect(MqttPlane plane)
    {
        RequireBroker();

        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        var refused = await Assert.ThrowsAsync<MqttPlaneRefusedException>(
            () => MqttDevice.ConnectAsync(emqx, mine, plane, credential: MqttDevice.TokenFor(theirs)));

        Assert.Equal(plane, refused.Plane);
    }

    /// <summary>
    /// On the tracker plane the username is the certificate's CN, so the confinement travels with
    /// the credential rather than with anything the device sends.
    /// </summary>
    [Fact]
    public async Task A_tracker_is_confined_by_its_certificate_subject_and_not_by_what_it_claims()
    {
        RequireBroker();

        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        // Connected under a client id naming the OTHER vehicle. `peer_cert_as_username = cn` means
        // the broker never reads it: the principal is the subject of a certificate signed by a CA
        // the broker trusts, which a device cannot alter without the issuing key.
        await using var device = await MqttDevice.ConnectAsync(
            emqx, theirs, MqttPlane.TrackerMutualTls, certificate: IssueCertificate(mine));

        try
        {
            await device.PublishAsync(MqttTopics.PositionLive(theirs), "{}"u8.ToArray());
        }
        catch (Exception)
        {
            // As above.
        }

        await AssertDisconnectedAsync(
            device,
            "A tracker holding vehicle A's certificate published under vehicle B's topic and was "
            + "not refused. The CN is what confines a hardware device to its own topics (T-02) — if "
            + "the broker stopped deriving the username from it, every tracker would be free of the "
            + "ACL while every mobile client stayed inside it.");
    }

    /// <summary>
    /// A device may subscribe to its own downlink, on every plane.
    /// </summary>
    /// <remarks>
    /// The negative assertions above would all pass on a broker that denied everything; this is one
    /// of the two that would not. §7.7.5's commands arrive here — R-07's cadence hint among them —
    /// so denying it would silently pin every device to its default publish rate.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Planes))]
    public async Task A_device_may_subscribe_to_its_own_command_topic(MqttPlane plane)
    {
        RequireBroker();

        var vehicleId = Guid.NewGuid();
        await using var device = await ConnectAsync(vehicleId, plane);

        var result = await device.SubscribeAsync(MqttTopics.Command(vehicleId));

        Assert.All(result.Items, item => Assert.Equal(
            MQTTnet.MqttClientSubscribeResultCode.GrantedQoS1, item.ResultCode));
    }

    /// <summary>
    /// And not another vehicle's downlink — which would leak the ride assignments and geofences
    /// §7.7.5 sends over it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Planes))]
    public async Task A_device_may_not_subscribe_to_another_vehicles_command_topic(MqttPlane plane)
    {
        RequireBroker();

        await using var device = await ConnectAsync(Guid.NewGuid(), plane);

        try
        {
            await device.SubscribeAsync(MqttTopics.Command(Guid.NewGuid()));
        }
        catch (Exception)
        {
            // As above.
        }

        await AssertDisconnectedAsync(
            device, $"{plane}: a device subscribed to another vehicle's downlink command topic.");
    }

    public void Dispose()
    {
        foreach (var certificate in _issued)
        {
            certificate.Dispose();
        }
    }

    private void RequireBroker() => Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

    private Task<MqttDevice> ConnectAsync(Guid vehicleId, MqttPlane plane) => MqttDevice.ConnectAsync(
        emqx,
        vehicleId,
        plane,
        certificate: plane is MqttPlane.TrackerMutualTls ? IssueCertificate(vehicleId) : null);

    private static async Task AssertDisconnectedAsync(MqttDevice device, string because)
    {
        var completed = await Task.WhenAny(device.Disconnected, Task.Delay(Timeout));

        Assert.True(completed == device.Disconnected, because);
    }

    /// <summary>
    /// Mints a device certificate from the CA the fixture's broker already trusts.
    /// </summary>
    /// <remarks>
    /// <c>EmbeddedStepCa</c>, provisioning-svc's own issuer, pointed at
    /// <c>EmqxFixture.DeviceCaDirectory</c> — the same arrangement the dev stack has, where
    /// <c>dev-up.sh</c> writes the CA before the broker starts and the service loads it rather than
    /// creating it. A hand-rolled certificate would prove the test's own signing code.
    /// </remarks>
    private X509Certificate2 IssueCertificate(Guid vehicleId)
    {
        using var authority = new EmbeddedStepCa(
            Options.Create(new DevicePkiOptions { RootKeyPath = emqx.DeviceCaDirectory }),
            NullLogger<EmbeddedStepCa>.Instance);

        var credential = authority.Issue(
            CredentialTypes.X509, vehicleId, Imeis.Require("350123456789017"), DateTimeOffset.UtcNow);

        var pem = credential.ClientCertPem
            ?? throw new InvalidOperationException("The issuer returned no X.509 bundle.");

        using var leaf = X509Certificate2.CreateFromPem(pem, pem);

        // Round-tripped through PKCS#12: on Linux a certificate created from PEM holds an ephemeral
        // key the TLS stack refuses to use for client authentication.
        var loaded = X509CertificateLoader.LoadPkcs12(leaf.Export(X509ContentType.Pfx), password: null);
        _issued.Add(loaded);

        return loaded;
    }
}
