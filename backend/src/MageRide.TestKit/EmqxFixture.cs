using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace MageRide.TestKit;

/// <summary>
/// A throwaway EMQX 5.8 running <b>the repository's own broker policy</b> — the MQTT plane every
/// device and adapter publishes to (D6' §3).
/// </summary>
/// <remarks>
/// <para>
/// The image is <c>emqx/emqx:5.8</c> and the two configuration files are the ones
/// <c>infra/docker-compose.dev.slim.yml</c> mounts: <c>infra/deploy/emqx/emqx.conf</c> (JWT
/// authentication with <c>verify_claims = { vehicleId = "${username}" }</c>) and
/// <c>infra/deploy/emqx/acl.conf</c> (<c>no_match = deny</c>, devices bound to
/// <c>veh/${username}/*</c>). They are copied into the test output by
/// <c>MageRide.TestKit.csproj</c>, so a test asserts against the policy that is actually deployed
/// rather than a copy that can drift from it.
/// </para>
/// <para>
/// <b>There is no Testcontainers module for EMQX</b>, so this builds it from the generic
/// <see cref="ContainerBuilder"/>. The wait strategy is <c>emqx ctl status</c>, the same command
/// the compose file's health check runs — a broker whose TCP port is open is not necessarily one
/// that has finished loading its authentication chain, and connecting too early produces a
/// <c>NotAuthorized</c> that looks exactly like a wrong secret.
/// </para>
/// </remarks>
public sealed class EmqxFixture : ContainerFixture
{
    /// <summary>The image the dev stack and the replica run.</summary>
    public const string Image = "emqx/emqx:5.8";

    /// <summary>
    /// The HMAC secret the broker validates session tokens with.
    /// </summary>
    /// <remarks>
    /// Injected as <c>EMQX_AUTHENTICATION__1__SECRET</c>, exactly as the compose file does — EMQX's
    /// precedence puts <c>EMQX_*</c> above the mounted <c>emqx.conf</c>, which is what keeps the
    /// real secret out of git. A test mints its tokens with this same value through
    /// <c>MageRide.Shared.Mqtt.MqttSessionTokenIssuer</c>.
    /// </remarks>
    public const string SessionTokenSecret = "mageride-testkit-mqtt-jwt-secret-0123456789";

    private const int MqttPort = 1883;
    private const int MqttsPort = 8883;

    private IContainer? _container;

    protected override string Name => "EMQX 5.8";

    /// <summary>Host the broker is reachable on, e.g. <c>127.0.0.1</c>.</summary>
    public string Host => _container?.Hostname
        ?? throw new InvalidOperationException($"EMQX container is not running: {SkipReason ?? "not started"}");

    /// <summary>Host port the container's 1883 is published on.</summary>
    public int Port => _container?.GetMappedPublicPort(MqttPort)
        ?? throw new InvalidOperationException($"EMQX container is not running: {SkipReason ?? "not started"}");

    /// <summary>
    /// Host port the container's 8883 is published on — the hardware-tracker listener (T-02).
    /// </summary>
    /// <remarks>
    /// Mutual TLS, not JWT: a client here presents an X.509 certificate signed by
    /// <see cref="DeviceCaDirectory"/>'s chain, and <c>emqx.conf</c>'s
    /// <c>peer_cert_as_username = cn</c> turns its CN into the MQTT username the ACL is written
    /// against.
    /// </remarks>
    public int TlsPort => _container?.GetMappedPublicPort(MqttsPort)
        ?? throw new InvalidOperationException($"EMQX container is not running: {SkipReason ?? "not started"}");

    /// <summary>
    /// The device CA this broker trusts — <c>StepCa:RootKeyPath</c> for a provisioning-svc under
    /// test.
    /// </summary>
    /// <remarks>
    /// Generated before the container starts, because EMQX reads its <c>cacertfile</c> when the
    /// 8883 listener comes up and a service cannot create it in time. That is the same ordering
    /// the dev stack has, where <c>dev-up.sh</c> writes the CA and both containers mount it.
    /// </remarks>
    public string DeviceCaDirectory { get; } = Path.Combine(
        Path.GetTempPath(), "mageride-testkit-device-ca-" + Guid.NewGuid().ToString("N")[..12]);

    protected override async Task StartAsync()
    {
        var configuration = ConfigurationDirectory();

        DeviceCa.Create(DeviceCaDirectory);

        _container = new ContainerBuilder(Image)
            .WithPortBinding(MqttPort, assignRandomHostPort: true)
            .WithPortBinding(MqttsPort, assignRandomHostPort: true)
            .WithEnvironment("EMQX_AUTHENTICATION__1__SECRET", SessionTokenSecret)
            // A fixed node name keeps the Erlang cookie/nodename pair stable across the container's
            // own restarts; the compose file sets the same one.
            .WithEnvironment("EMQX_NODE__NAME", "emqx@127.0.0.1")
            .WithEnvironment("EMQX_NODE__COOKIE", "mageride-test-cookie")
            .WithBindMount(Path.Combine(configuration, "emqx.conf"), "/opt/emqx/etc/emqx.conf", AccessMode.ReadOnly)
            .WithBindMount(
                Path.Combine(configuration, "acl.conf"), "/opt/emqx/etc/mageride-acl.conf", AccessMode.ReadOnly)
            .WithBindMount(DeviceCaDirectory, "/opt/emqx/etc/device-ca", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(MqttPort)
                .UntilInternalTcpPortIsAvailable(MqttsPort)
                .UntilCommandIsCompleted("emqx", "ctl", "status"))
            .Build();

        await _container.StartAsync();
    }

    protected override async Task StopAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        // The device CA is key material in a temp directory; leaving it behind would accumulate a
        // root key per test run on the build host.
        if (Directory.Exists(DeviceCaDirectory))
        {
            Directory.Delete(DeviceCaDirectory, recursive: true);
        }
    }

    /// <summary>Broker logs, for a failure that needs to know why a CONNECT was refused.</summary>
    public async Task<string> ReadLogsAsync()
    {
        RequireAvailable();

        var (stdout, stderr) = await _container!.GetLogsAsync();
        return stdout + stderr;
    }

    /// <summary>
    /// Where the copied <c>emqx.conf</c> / <c>acl.conf</c> live in the test output.
    /// </summary>
    /// <remarks>
    /// Copied rather than resolved by walking up to the repository root: a test's working directory
    /// depends on how it was launched, and a fixture that silently found no configuration would
    /// start a broker with EMQX's shipped defaults — which allow every topic, and would turn the
    /// ACL test green for the worst possible reason.
    /// </remarks>
    private static string ConfigurationDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "emqx");

        if (!File.Exists(Path.Combine(directory, "emqx.conf"))
            || !File.Exists(Path.Combine(directory, "acl.conf")))
        {
            throw new InvalidOperationException(
                $"The EMQX configuration was not copied to '{directory}'. MageRide.TestKit.csproj copies " +
                "infra/deploy/emqx/*.conf into the output; without them the broker would run EMQX's " +
                "permissive defaults and every ACL assertion would pass for the wrong reason.");
        }

        return directory;
    }
}

/// <summary>Collection sharing one <see cref="EmqxFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class EmqxCollection : ICollectionFixture<EmqxFixture>
{
    public const string Name = "mageride-emqx";
}
