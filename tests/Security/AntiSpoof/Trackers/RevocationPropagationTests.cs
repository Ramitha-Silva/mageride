using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MageRide.Provisioning.Domain;
using MageRide.Security.Tests.AntiSpoof.Mqtt;
using MageRide.Shared.Caching;
using MageRide.TestKit;
using StackExchange.Redis;

namespace MageRide.Security.Tests.AntiSpoof.Trackers;

/// <summary>
/// C128's fourth definition-of-done item: <b>revocation takes effect within 60 s on both MQTT and
/// TCP paths</b> (T-12, US-3.8, ADD §7.7.3).
///
/// <para>
/// The two paths are different mechanisms because the two transports are. tcp-adapter terminates
/// its own sockets and can be told directly, so revocation there is a Redis pub/sub message plus a
/// <c>validate</c> that starts answering no. A broker cannot be told, and X.509's answer to "this
/// certificate is no longer good" is a CRL — which provisioning-svc publishes and EMQX is supposed
/// to fetch on <c>crl_cache.refresh_interval</c>.
/// </para>
///
/// <para>
/// <b>The TCP path meets the budget. The MQTT path does not exist in any deployed configuration</b>,
/// and <see cref="A_revoked_tracker_certificate_still_completes_the_mutual_tls_handshake"/> is the
/// measurement that says so rather than the inference: <c>enable_crl_check</c> is commented out in
/// <c>infra/deploy/emqx/emqx.conf</c>, so the list is published and nothing reads it. Recorded as
/// C128-01 in <c>security/remediation-backlog.md</c>, with the reason it cannot simply be switched
/// on — every certificate minted so far carries no CRL distribution point, and a broker that cannot
/// locate a CRL refuses the handshake outright.
/// </para>
/// </summary>
[Collection<AntiSpoofCollection>]
[Trait("Category", "AntiSpoof")]
public sealed class RevocationPropagationTests(PostgresFixture postgres, RedisFixture redis, EmqxFixture emqx)
{
    /// <summary>T-12's budget, and the one the DoD names.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    /// <summary>ADD §7.7.3's tighter number for the socket the adapter already holds.</summary>
    private static readonly TimeSpan SocketCloseBudget = TimeSpan.FromSeconds(1);

    /// <summary>The DoD assertion, on the TCP path.</summary>
    [Fact]
    public async Task A_decommission_stops_the_tcp_path_authenticating_well_inside_the_budget()
    {
        RequireDatabase();

        await using var plane = await TrackerPlane.StartAsync(postgres, redis);

        var owner = await plane.CreateDriverAsync();
        var admin = await plane.CreateDriverAsync();
        var imei = TrackerPlane.NextImei();

        var bound = await plane.BindAsync(owner, imei, await plane.CreateVehicleAsync(owner));

        Assert.True((await plane.ValidateAsync(imei, bound.Credential.Serial)).Valid);

        var elapsed = Stopwatch.StartNew();

        await plane.DecommissionAsync(admin, imei);

        var after = await plane.ValidateAsync(imei, bound.Credential.Serial);

        elapsed.Stop();

        Assert.False(after.Valid);
        Assert.Equal(BindingStates.Revoked, after.State);

        // The cached fast path is gone too. Left behind, a reconnecting device would be resolved
        // from it for the cache's whole 24 h TTL without ever asking `validate` — which is how a
        // 60 s budget quietly becomes a day.
        Assert.Null(await plane.CachedVehicleAsync(imei));

        Assert.True(
            elapsed.Elapsed < Budget,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Revocation took {elapsed.Elapsed.TotalSeconds:F2} s to bite on the TCP path; T-12 budgets {Budget.TotalSeconds:F0} s."));
    }

    /// <summary>
    /// The fast half: the signal an open socket is closed by, on the channel the adapter watches.
    /// </summary>
    /// <remarks>
    /// <c>validate</c> answers the adapter's <i>next</i> question. What closes a socket that is
    /// already open and publishing is this message, and ADD §7.7.3 budgets one second for it — which
    /// is why it is a subscription rather than a consumer group's lag. The field names are asserted
    /// because the adapter deserialises into its own copy of the record
    /// (<c>TcpAdapter/Identity/RevocationWatcher.cs</c>): a rename on either side turns every field
    /// null and the socket simply never closes, with nothing logged on either end.
    /// </remarks>
    [Fact]
    public async Task A_revocation_reaches_the_channel_the_adapter_force_closes_on()
    {
        RequireDatabase();

        await using var plane = await TrackerPlane.StartAsync(postgres, redis);

        var owner = await plane.CreateDriverAsync();
        var admin = await plane.CreateDriverAsync();
        var vehicleId = await plane.CreateVehicleAsync(owner);
        var imei = TrackerPlane.NextImei();

        var bound = await plane.BindAsync(owner, imei, vehicleId);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = plane.Redis.GetSubscriber();
        var channel = RedisChannel.Literal(RedisKeys.TrackerCredentialChannel);

        await subscriber.SubscribeAsync(channel, (_, value) => received.TrySetResult(value.ToString()));

        try
        {
            var elapsed = Stopwatch.StartNew();

            await plane.DecommissionAsync(admin, imei);

            var completed = await Task.WhenAny(received.Task, Task.Delay(Budget));
            elapsed.Stop();

            Assert.True(completed == received.Task, $"Nothing arrived on {RedisKeys.TrackerCredentialChannel}.");

            using var signal = JsonDocument.Parse(await received.Task);
            var root = signal.RootElement;

            Assert.Equal("tracker.revoked", root.GetProperty("type").GetString());
            Assert.Equal(imei, root.GetProperty("imei").GetString());
            Assert.Equal(vehicleId, root.GetProperty("vehicleId").GetGuid());

            // The serials the message invalidates. A rotation overlap means a device may hold two,
            // and closing on the wrong one leaves the socket up.
            var serials = root.GetProperty("serials").EnumerateArray().Select(s => s.GetString()).ToList();
            Assert.Contains(bound.Credential.Serial, serials);

            Assert.True(
                elapsed.Elapsed < SocketCloseBudget + TimeSpan.FromSeconds(2),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The revocation signal took {elapsed.Elapsed.TotalMilliseconds:F0} ms to reach the channel; ADD §7.7.3 budgets {SocketCloseBudget.TotalSeconds:F0} s for the whole close."));
        }
        finally
        {
            await subscriber.UnsubscribeAsync(channel);
        }
    }

    /// <summary>
    /// The MQTT path's first half: the revoked serial is on the list, inside the budget.
    /// </summary>
    /// <remarks>
    /// This is everything provisioning-svc can do about a broker. It is necessary and — as the next
    /// test measures — not sufficient.
    /// </remarks>
    [Fact]
    public async Task A_revoked_certificate_is_on_the_published_revocation_list_inside_the_budget()
    {
        RequireDatabase();

        await using var plane = await TrackerPlane.StartAsync(postgres, redis);

        var owner = await plane.CreateDriverAsync();
        var admin = await plane.CreateDriverAsync();
        var imei = TrackerPlane.NextImei();

        var bound = await plane.BindAsync(owner, imei, await plane.CreateVehicleAsync(owner));
        var serial = bound.Credential.Serial;

        var before = await plane.Crl.GetAsync(CancellationToken.None);
        Assert.False(NamesSerial(before.Der, serial), "the serial is on the CRL before it was revoked");

        var elapsed = Stopwatch.StartNew();
        await plane.DecommissionAsync(admin, imei);

        // The service caches a built list for ten seconds, so the budget has to absorb one cache
        // generation — which is the point of it being far inside sixty.
        var listed = false;

        while (elapsed.Elapsed < Budget && !listed)
        {
            listed = NamesSerial((await plane.Crl.GetAsync(CancellationToken.None)).Der, serial);

            if (!listed)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        elapsed.Stop();

        Assert.True(listed, "the revoked serial never reached the CRL");
        Assert.True(
            elapsed.Elapsed < Budget,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The revoked serial took {elapsed.Elapsed.TotalSeconds:F2} s to reach the CRL; T-12 budgets {Budget.TotalSeconds:F0} s."));
    }

    /// <summary>
    /// C128-01. The MQTT half of T-12 is <b>not enforced in any deployed configuration</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The experiment is the whole argument: mint a device certificate from the CA the broker
    /// trusts, connect (it works), revoke it, connect again. If revocation took effect on the MQTT
    /// path, the second handshake would fail. It does not — and it cannot, because
    /// <c>enable_crl_check</c> is commented out in the <c>emqx.conf</c> this fixture and the replica
    /// both mount, so no broker fetches the list provisioning-svc publishes.
    /// </para>
    /// <para>
    /// <b>Asserted in the direction that makes the finding self-closing.</b> The day the control is
    /// turned on, this test fails and sends the reader to C128-01 to delete it. Recording a gap in
    /// a document alone is how a gap outlives the fix.
    /// </para>
    /// <para>
    /// Why it cannot simply be switched on: EMQX locates a CRL through the <b>CRL distribution
    /// point extension in the peer certificate</b>, and <c>EmbeddedStepCa</c> only writes that
    /// extension when <c>StepCa:CrlDistributionPoint</c> is configured — which no environment sets.
    /// So every certificate the platform has ever minted carries no distribution point, and a
    /// broker with <c>enable_crl_check = true</c> refuses a certificate whose CRL it cannot fetch.
    /// Turning the check on before re-minting the fleet does not tighten the tracker plane; it
    /// takes the whole of it off the air.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_revoked_tracker_certificate_still_completes_the_mutual_tls_handshake()
    {
        RequireDatabase();
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        // The plane mints from the CA this broker already trusts.
        await using var plane = await TrackerPlane.StartAsync(postgres, redis, caDirectory: emqx.DeviceCaDirectory);

        var owner = await plane.CreateDriverAsync();
        var admin = await plane.CreateDriverAsync();
        var vehicleId = await plane.CreateVehicleAsync(owner);
        var imei = TrackerPlane.NextImei();

        var bound = await plane.BindAsync(owner, imei, vehicleId);
        using var certificate = LoadCredential(bound.Credential.ClientCertPem);

        await using (var live = await MqttDevice.ConnectAsync(
            emqx, vehicleId, MqttPlane.TrackerMutualTls, certificate: certificate))
        {
            Assert.True(live.IsConnected, "a freshly bound tracker must be able to connect");
        }

        await plane.DecommissionAsync(admin, imei);

        // Every platform-side half of T-12 has now happened.
        Assert.False((await plane.ValidateAsync(imei, bound.Credential.Serial)).Valid);
        Assert.True(NamesSerial(
            (await plane.Crl.GetAsync(CancellationToken.None)).Der, bound.Credential.Serial));

        // Well past `crl_cache.refresh_interval`'s intended 60 s would be pointless to wait for:
        // the setting is commented out, so there is no cache to refresh. A second handshake now is
        // the measurement.
        await using var afterRevocation = await MqttDevice.ConnectAsync(
            emqx, vehicleId, MqttPlane.TrackerMutualTls, certificate: certificate);

        Assert.True(
            afterRevocation.IsConnected,
            "The broker refused a revoked certificate. That is the behaviour T-12 asks for — so if "
            + "enable_crl_check has been turned on and the fleet re-minted with a CRL distribution "
            + "point, close C128-01 in security/remediation-backlog.md, invert this assertion and "
            + "invert BrokerPolicyTests.The_broker_does_not_yet_check_the_revocation_list_and_that_is_recorded.");

        // And it can still publish, which is what the finding actually costs: a decommissioned or
        // stolen tracker keeps writing positions for its vehicle until its certificate expires.
        var published = await afterRevocation.PublishAsync(
            MageRide.Shared.Mqtt.MqttTopics.PositionLive(vehicleId), "{}"u8.ToArray());

        Assert.True(published.IsSuccess);
    }

    /// <summary>
    /// A rotation is not a revocation, and the distinction is what stops T-12 from bricking devices.
    /// </summary>
    /// <remarks>
    /// The replacement credential is minted fourteen days early and the outgoing one stays valid
    /// precisely so a tracker parked out of coverage can come back and collect it. A revocation
    /// path that treated the two alike would take every device offline on its rotation day —
    /// the failure mode is a fleet, not a device.
    /// </remarks>
    [Fact]
    public async Task A_rotation_leaves_the_outgoing_credential_authenticating()
    {
        RequireDatabase();

        await using var plane = await TrackerPlane.StartAsync(postgres, redis);

        var owner = await plane.CreateDriverAsync();
        var imei = TrackerPlane.NextImei();

        var bound = await plane.BindAsync(owner, imei, await plane.CreateVehicleAsync(owner));
        var replacement = await plane.TrackersAsync(
            trackers => trackers.RotateAsync(imei, CancellationToken.None));

        Assert.NotEqual(bound.Credential.Serial, replacement.Serial);

        Assert.True((await plane.ValidateAsync(imei, replacement.Serial)).Valid);
        Assert.True(
            (await plane.ValidateAsync(imei, bound.Credential.Serial)).Valid,
            "The outgoing credential stopped authenticating on rotation. A tracker out of coverage "
            + "on its rotation day would come back to a credential nobody accepts and no way to "
            + "collect the new one.");

        // And neither serial is on the CRL: nothing was revoked.
        var crl = (await plane.Crl.GetAsync(CancellationToken.None)).Der;
        Assert.False(NamesSerial(crl, bound.Credential.Serial));
        Assert.False(NamesSerial(crl, replacement.Serial));
    }

    /// <summary>
    /// Whether a DER-encoded CRL names a serial.
    /// </summary>
    /// <remarks>
    /// <b>.NET has no CRL reader</b>, so this looks for the serial's bytes in the encoding — which
    /// is sound because a revoked entry carries the serial as a DER INTEGER and the CA writes the
    /// same upper-case hex into <c>DeviceCredential.Serial</c>. `Provisioning.Api.Tests` reads the
    /// PEM the same way for the same reason; taking a dependency on a third-party ASN.1 parser to
    /// assert a security control is a dependency that can disagree with the verifier.
    /// </remarks>
    private static bool NamesSerial(byte[] der, string serial) =>
        Convert.ToHexString(der).Contains(serial, StringComparison.OrdinalIgnoreCase);

    private static X509Certificate2 LoadCredential(string? pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);

        using var leaf = X509Certificate2.CreateFromPem(pem, pem);

        // A PEM-loaded certificate holds an ephemeral key the Linux TLS stack refuses to use for
        // client authentication; the PKCS#12 round trip is what makes it usable.
        return X509CertificateLoader.LoadPkcs12(leaf.Export(X509ContentType.Pfx), password: null);
    }

    private void RequireDatabase()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
    }
}
