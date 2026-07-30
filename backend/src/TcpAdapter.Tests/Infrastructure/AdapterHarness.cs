using Dapper;
using MageRide.Shared.Caching;
using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Identity;
using MageRide.TcpAdapter.Ingest;
using MageRide.TcpAdapter.Modes;
using MageRide.TcpAdapter.Protocols;
using MageRide.TcpAdapter.Publishing;
using MageRide.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using StackExchange.Redis;

// `System.Net.Sockets` is in scope for the device helpers; the spec's word wins by name.
using ProtocolFamily = MageRide.TcpAdapter.Protocols.ProtocolFamily;

namespace MageRide.TcpAdapter.Tests.Infrastructure;

/// <summary>
/// The real tcp-adapter host, on ephemeral ports, against the collection's containers.
/// </summary>
/// <remarks>
/// <para>
/// Built through <see cref="TcpAdapterApplication.Build"/> — the same composition
/// <c>Program.cs</c> runs — because every claim in this half of the suite is about a seam, and a
/// hand-wired subset of the graph is precisely where a seam can be missing. The only things overridden
/// are addresses and the two clocks a test cannot wait out.
/// </para>
/// <para>
/// <b>Ports are ephemeral.</b> <c>Adapter:Ports=0,0,0,0</c> lets the OS choose, so the suite runs beside
/// a dev stack already holding 5023-5026 — and <see cref="PortFor"/> waits for the bind rather than
/// reading it straight after <c>StartAsync</c>, because a <c>BackgroundService</c>'s <c>ExecuteAsync</c>
/// is started and not awaited by the host.
/// </para>
/// </remarks>
internal sealed class AdapterHarness : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly ProvisioningStub _provisioning;
    private readonly IConnectionMultiplexer _redis;
    private readonly string _postgres;

    private AdapterHarness(
        IHost host, ProvisioningStub provisioning, IConnectionMultiplexer redis, string postgres)
    {
        _host = host;
        _provisioning = provisioning;
        _redis = redis;
        _postgres = postgres;
    }

    /// <summary>The stub the adapter authenticates devices against.</summary>
    public ProvisioningStub Provisioning => _provisioning;

    /// <summary>Live sessions on this pod — the T-08 and downlink map.</summary>
    public SessionRegistry Sessions => _host.Services.GetRequiredService<SessionRegistry>();

    /// <summary>The adapter's broker connection.</summary>
    public EmqxLink Link => _host.Services.GetRequiredService<EmqxLink>();

    /// <summary>The downlink router, so a test can drive one command without a broker round trip.</summary>
    public DownlinkRouter Downlink => _host.Services.GetRequiredService<DownlinkRouter>();

    /// <summary>The T-12 watcher, so a test can hand it a signal directly as well as through Redis.</summary>
    public RevocationWatcher Revocations => _host.Services.GetRequiredService<RevocationWatcher>();

    /// <summary>The T-11 gate.</summary>
    public IModeGate Gate => _host.Services.GetRequiredService<IModeGate>();

    /// <summary>The per-pod socket budget.</summary>
    public SocketBudget Budget => _host.Services.GetRequiredService<SocketBudget>();

    /// <summary>Redis, for the keys the two other planes write.</summary>
    public IDatabase Cache => _redis.GetDatabase();

    /// <summary>
    /// Starts the adapter.
    /// </summary>
    /// <param name="emqx">The broker fixture.</param>
    /// <param name="redis">The cache fixture.</param>
    /// <param name="postgres">The database fixture, already migrated.</param>
    /// <param name="settings">Extra configuration, overriding the defaults below.</param>
    public static async Task<AdapterHarness> StartAsync(
        EmqxFixture emqx,
        RedisFixture redis,
        PostgresFixture postgres,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(emqx);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(postgres);

        var provisioning = await ProvisioningStub.StartAsync();

        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,

            ["Mqtt:Host"] = emqx.Host,
            ["Mqtt:Port"] = emqx.Port.ToString(),
            ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,

            ["Adapter:BindAddress"] = "127.0.0.1",
            ["Adapter:Ports"] = "0,0,0,0",
            ["Adapter:ProvisioningBaseUrl"] = provisioning.BaseUrl,
            ["Adapter:ProvisioningInternalApiKey"] = ProvisioningStub.ApiKey,

            // Short enough that a test can wait on it, long enough that it is the deployed behaviour
            // rather than a different one: the deployed value is 5 s.
            ["Adapter:OfflineWindow"] = "00:00:05",

            // The deployed idle timeout is fifteen minutes; nothing here waits that long, and a test
            // that hangs should fail rather than idle.
            ["Adapter:IdleTimeout"] = "00:00:30",

            // No OTLP endpoint: `Otel:Endpoint` unset is what keeps the exporter from retrying against
            // a collector that is not there for the whole run.
            ["Otel:Endpoint"] = null,
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                configuration[key] = value;
            }
        }

        var host = TcpAdapterApplication.Build(
            [], builder => builder.Configuration.AddInMemoryCollection(configuration));

        await host.StartAsync();

        var harness = new AdapterHarness(
            host,
            provisioning,
            host.Services.GetRequiredService<IConnectionMultiplexer>(),
            postgres.ConnectionString);

        // The broker connection is what publishing depends on, and the T-04 assertion is a deadline —
        // so a test must not start measuring one before the link is up.
        if (host.Services.GetRequiredService<AdapterOptions>().BrokerEnabled)
        {
            Assert.True(
                await harness.Link.WaitForConnectionAsync(TimeSpan.FromSeconds(30), CancellationToken.None),
                "the adapter did not connect to EMQX");
        }

        return harness;
    }

    /// <summary>The port a family's listener bound, once it has bound.</summary>
    public async Task<int> PortFor(ProtocolFamily family)
    {
        var listeners = _host.Services.GetRequiredService<AdapterListeners>();
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (listeners.PortFor(family) is { } port)
            {
                return port;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException($"{ProtocolFamilies.Name(family)} never bound a port.");
    }

    /// <summary>Primes <c>imei:{imei}</c> — the T-03 cache provisioning-svc writes on bind.</summary>
    public Task PrimeImeiCacheAsync(string imei, Guid vehicleId) =>
        Cache.StringSetAsync(RedisKeys.Imei(imei), vehicleId.ToString(), TimeSpan.FromMinutes(10));

    /// <summary>Deletes it, as a revoke does.</summary>
    public Task InvalidateImeiCacheAsync(string imei) => Cache.KeyDeleteAsync(RedisKeys.Imei(imei));

    /// <summary>
    /// Writes <c>veh:driver:{vehicleId}</c> — dispatch-svc's standby binding, which is what T-11 reads
    /// as "the driver has gone online in the app".
    /// </summary>
    public Task BringDriverOnlineAsync(Guid vehicleId, Guid driverId) =>
        Cache.StringSetAsync(RedisKeys.VehicleDriver(vehicleId), driverId.ToString(), TimeSpan.FromMinutes(10));

    /// <summary>Deletes it, as going off duty does.</summary>
    public Task TakeDriverOfflineAsync(Guid vehicleId) => Cache.KeyDeleteAsync(RedisKeys.VehicleDriver(vehicleId));

    /// <summary>Publishes a credential signal on <c>prov:tracker</c>, as provisioning-svc does (T-12).</summary>
    public Task<long> PublishRevocationAsync(string json) =>
        _redis.GetSubscriber().PublishAsync(RedisChannel.Literal(RedisKeys.TrackerCredentialChannel), json);

    /// <summary>
    /// Seeds a vehicle in <c>registry.vehicles</c> — the column T-11's mode comes from.
    /// </summary>
    /// <remarks>
    /// Inserted directly rather than through registry-svc: this suite is about what the adapter does
    /// with the answer, and going through that service's API would need its own host, its own auth and
    /// the four-step onboarding machine to reach an APPROVED row.
    /// </remarks>
    public async Task SeedVehicleAsync(Guid vehicleId, string mode, string vehicleType = "bus")
    {
        await using var connection = new NpgsqlConnection(_postgres);
        await connection.OpenAsync();

        var ownerId = Guid.NewGuid();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name, language)
            VALUES (@OwnerId, @Phone, 'driver', 'C043', 'en')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            VALUES
              (@VehicleId, @OwnerId, @Plate, @VehicleType, @Mode, 'APPROVED', 'C043 Driver')
            ON CONFLICT (id) DO NOTHING;
            """,
            new
            {
                OwnerId = ownerId,
                // Unique per seeded vehicle: iam.users.phone and ux_vehicles_regno_active are both
                // unique over the live set, so a fixed value would collide on the second call.
                Phone = "+9477" + ownerId.ToString("N")[..7],
                VehicleId = vehicleId,
                Plate = "WP-C043-" + vehicleId.ToString("N")[..6].ToUpperInvariant(),
                VehicleType = vehicleType,
                Mode = mode,
            });
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception)
        {
            // A listener that never bound, or a session mid-teardown. The host is going away regardless.
        }

        _host.Dispose();
        await _provisioning.DisposeAsync();
    }
}
