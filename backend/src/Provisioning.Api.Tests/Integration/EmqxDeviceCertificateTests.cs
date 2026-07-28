using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MageRide.Provisioning.Tests.Infrastructure;
using MageRide.Shared.Mqtt;
using MageRide.TestKit;
using MQTTnet;

namespace MageRide.Provisioning.Tests.Integration;

/// <summary>
/// The DoD's first line: a bound tracker authenticates to EMQX with the certificate this service
/// minted, and is ACL-scoped to its own vehicle's topics (T-02, ADD §7.7.3).
/// </summary>
/// <remarks>
/// <para>
/// Asserted against the <b>deployed</b> broker policy — <c>EmqxFixture</c> bind-mounts
/// <c>infra/deploy/emqx/emqx.conf</c> and <c>acl.conf</c>, the same two files
/// <c>infra/docker-compose.dev.slim.yml</c> mounts. The chain under test is the whole one:
/// provisioning-svc mints from the CA the broker trusts, the broker turns the certificate's CN
/// into the MQTT username, and <c>acl.conf</c>'s <c>veh/${username}/*</c> rules do the rest. A
/// test against a hand-written config would prove the copy right and say nothing about the stack.
/// </para>
/// <para>
/// <b>No new ACL rule was needed to make this work</b>, and that is the point of putting the
/// vehicleId in the CN: the rules mobile clients are confined by are the rules a tracker is
/// confined by.
/// </para>
/// </remarks>
[Collection<ProvisioningCollection>]
public sealed class EmqxDeviceCertificateTests(PostgresFixture postgres, RedisFixture redis, EmqxFixture emqx)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The DoD assertion.</summary>
    [Fact]
    public async Task A_bound_tracker_connects_with_its_minted_certificate_and_publishes_its_own_positions()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        // The CA the broker's cacertfile already trusts. provisioning-svc loads it rather than
        // generating one, which is what a deployment does with the dev-up.sh volume.
        await using var harness = await ProvisioningHarness.StartAsync(
            postgres, redis, caDirectory: emqx.DeviceCaDirectory);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        var bound = await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId);
        using var certificate = ReadCredential(bound);

        using var client = new MqttClientFactory().CreateMqttClient();

        MqttClientConnectResult connected;
        try
        {
            connected = await client.ConnectAsync(OptionsFor(certificate));
        }
        catch (Exception exception)
        {
            // A refused mutual-TLS handshake surfaces client-side as a bare EOF; the broker is the
            // only party that knows which check failed.
            Assert.Fail($"{exception.Message}\n\n--- EMQX ---\n{await emqx.ReadLogsAsync()}");
            throw;
        }

        Assert.Equal(MqttClientConnectResultCode.Success, connected.ResultCode);

        var published = await client.PublishStringAsync(
            MqttTopics.PositionLive(vehicleId), "{}", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);

        Assert.True(published.IsSuccess);
        Assert.True(client.IsConnected);

        await client.DisconnectAsync();
    }

    /// <summary>
    /// The ACL half. The certificate's CN is the username, so a tracker publishing under another
    /// vehicle's topic is refused by the same rule that refuses a mobile client — and
    /// <c>deny_action = disconnect</c> drops the socket rather than silently binning the publish.
    /// </summary>
    [Fact]
    public async Task A_tracker_publishing_under_another_vehicles_topic_is_disconnected()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(
            postgres, redis, caDirectory: emqx.DeviceCaDirectory);

        var driverId = await harness.CreateUserAsync();
        var mine = await harness.CreateVehicleAsync(driverId);
        var theirs = await harness.CreateVehicleAsync(await harness.CreateUserAsync());

        var bound = await harness.BindAsync(
            harness.Tokens.Driver(driverId), ProvisioningHarness.NextImei(), mine);

        using var certificate = ReadCredential(bound);
        using var client = new MqttClientFactory().CreateMqttClient();

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DisconnectedAsync += _ =>
        {
            disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        await client.ConnectAsync(OptionsFor(certificate));

        try
        {
            await client.PublishStringAsync(
                MqttTopics.PositionLive(theirs), "{}", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);
        }
        catch (Exception)
        {
            // The broker may drop the socket before the PUBACK, which is the same rejection.
        }

        var completed = await Task.WhenAny(disconnected.Task, Task.Delay(Timeout));

        Assert.True(
            completed == disconnected.Task,
            "EMQX should have disconnected a tracker that published under another vehicle's topic.");
    }

    /// <summary>
    /// <c>fail_if_no_peer_cert = true</c>: the tracker listener is mutual TLS, so a client with no
    /// certificate never reaches the ACL at all.
    /// </summary>
    [Fact]
    public async Task A_client_with_no_certificate_cannot_reach_the_tracker_listener()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        using var client = new MqttClientFactory().CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(emqx.Host, emqx.TlsPort)
            .WithTlsOptions(tls => tls
                .UseTls()
                .WithTargetHost(FreshTargetHost())
                .WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12
                                  | System.Security.Authentication.SslProtocols.Tls13)
                .WithCertificateValidationHandler(_ => true))
            .WithClientId("no-certificate")
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(options));
    }

    /// <summary>
    /// A certificate from a CA the broker does not trust is refused at the handshake, whatever CN
    /// it claims. Without this the CN would be a self-asserted string rather than a verified one,
    /// and the ACL would be writing rules against something anybody could type.
    /// </summary>
    [Fact]
    public async Task A_certificate_from_another_authority_is_refused()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={vehicleId}"), key, HashAlgorithmName.SHA256);

        using var selfSigned = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));

        using var forged = X509CertificateLoader.LoadPkcs12(
            selfSigned.Export(X509ContentType.Pfx), password: null);

        using var client = new MqttClientFactory().CreateMqttClient();

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(OptionsFor(forged)));
    }

    /// <summary>
    /// Rebuilds the device's key pair from the PEM bundle the bind returned, exactly as a tracker
    /// writing it to flash would.
    /// </summary>
    private static X509Certificate2 ReadCredential(System.Text.Json.JsonElement bound)
    {
        var pem = bound.GetProperty("credential").GetProperty("clientCertPem").GetString()!;

        // The leaf is the first certificate in the bundle; the intermediate follows it. Only the
        // leaf carries the key, and it is the one the handshake presents.
        using var leaf = X509Certificate2.CreateFromPem(pem, pem);
        Assert.True(leaf.HasPrivateKey, "the bundle must carry the device's key or it cannot authenticate");

        // Round-tripped through PKCS#12: on Linux a certificate created from PEM holds an
        // ephemeral key that the TLS stack refuses to use for client authentication.
        var loaded = X509CertificateLoader.LoadPkcs12(leaf.Export(X509ContentType.Pfx), password: null);
        Assert.True(loaded.HasPrivateKey, "the PKCS#12 round trip dropped the private key");

        return loaded;
    }

    private MqttClientOptions OptionsFor(X509Certificate2 certificate) =>
        new MqttClientOptionsBuilder()
            .WithTcpServer(emqx.Host, emqx.TlsPort)
            .WithTlsOptions(tls => tls
                .UseTls()
                .WithTargetHost(FreshTargetHost())
                .WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12
                                  | System.Security.Authentication.SslProtocols.Tls13)
                .WithClientCertificates([certificate])
                // The broker still presents the image's self-signed server certificate; C125
                // replaces it with the platform one. What is under test is the *client* half.
                .WithCertificateValidationHandler(_ => true))
            .WithClientId(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false))
            .WithCleanSession()
            .Build();

    /// <summary>
    /// A TLS target host no other connection in this process has used.
    /// </summary>
    /// <remarks>
    /// <b>Not cosmetic.</b> .NET caches TLS sessions per target host and resumes them, and this
    /// class deliberately makes connections both with and without a client certificate to the same
    /// broker. Resuming a ticket from a certificate-less handshake on a listener with
    /// <c>fail_if_no_peer_cert</c> is refused, so without a fresh host per connection these tests
    /// poison each other in whatever order they happen to run. Real devices do not share a process
    /// or a session cache, so there is nothing to fix in the broker or the credential — only in
    /// how a single test process talks to it. Server-name verification is off here anyway.
    /// </remarks>
    private static string FreshTargetHost() => "tracker-" + Guid.NewGuid().ToString("N")[..12] + ".mageride.test";
}
