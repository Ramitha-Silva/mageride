using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Fanout;
using MageRide.Fleet;
using MageRide.HotPath.MqttBridge;
using MageRide.HotPath.PersistenceWriter;
using MageRide.HotPath.PositionProcessor;
using MageRide.Provisioning;
using MageRide.Shared.Auth;
using MageRide.Shared.Http;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Primitives;
using MageRide.Subscriptions;
using MageRide.TcpAdapter;
using MageRide.TcpAdapter.Ingest;
using MageRide.TcpAdapter.Protocols;
using MageRide.TestKit;
using MageRide.TripState;
using MageRide.TripState.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// The Mode A/B platform, running: nine services, four containers, and a socket a tracker can dial.
/// </summary>
/// <remarks>
/// <para>
/// trip-state-svc, tcp-adapter, provisioning-svc, fleet-svc, subscription-svc, fanout-svc,
/// mqtt-bridge-svc, position-processor-svc and persistence-writer-svc, each built through its own
/// composition root, on real sockets, against a real Postgres, Redis, Redpanda and EMQX.
/// <b>Every background worker is on</b> — trip-state's sweep and its <c>telemetry.normalized</c>
/// consumer, the outbox dispatchers, the bridge's two shared subscriptions, the processor's
/// pipeline, the writer's COPY batches and its trip summariser, fanout's pumps, consumers and
/// control plane, and the adapter's four listeners. A scenario writes bytes to a socket and then
/// <b>waits</b>: nothing in this assembly calls <c>ISessionService</c>, <c>IModeBAccessService</c>
/// or any other service type to move state along.
/// </para>
/// <para>
/// <b>The two fences C121 is built around.</b> R-01: no ride-svc, no dispatch-svc and no
/// <c>rides.*</c> row appears anywhere in these scenarios — a Mode A/B journey is a
/// <em>session</em>, and trip-state-svc is its only writer. And the tracker plane is driven by real
/// protocol frames on real sockets (<see cref="TrackerDevice"/>), never by publishing a synthetic
/// sample to EMQX: a scenario that did that would prove position-processor can parse a payload the
/// scenario wrote.
/// </para>
/// <para>
/// <b>What is deliberately not here.</b> ride-svc and dispatch-svc, by the fence above. iam-svc, for
/// the reason <see cref="TestTokenIssuer"/> records. ocr-svc, whose absence is <em>load bearing</em>
/// rather than incidental — see <see cref="MarkVehicleApprovedAsync"/>. notification-svc, so
/// <c>Fleet:ScheduleAlarmsEnabled</c> is off and US-13.11's alarm is C059's own suite's; wallet-svc,
/// so <c>Subscription:WalletBaseUrl</c> is unset and the C047 daily fee cannot be charged — neither
/// is on any Mode A/B path. admin-bff, whose internal callers this suite stands in for on the two
/// <c>/v1/internal/**</c> planes fleet-svc exposes.
/// </para>
/// </remarks>
internal sealed class ModeAbFleet : IAsyncDisposable
{
    /// <summary>Guards trip-state-svc's <c>/v1/internal/sessions/**</c> — the tracker's ignition route.</summary>
    public const string TripStateInternalKey = "mageride-c121-e2e-trip-state-internal-key";

    /// <summary>Guards provisioning-svc's <c>/v1/internal/trackers/**</c> — the adapter's `validate`.</summary>
    public const string ProvisioningInternalKey = "mageride-c121-e2e-provisioning-internal-key";

    /// <summary>Guards fleet-svc's <c>/v1/internal/fleets/**</c> — the Verification Officer's plane.</summary>
    public const string FleetInternalKey = "mageride-c121-e2e-fleet-internal-key";

    /// <summary>
    /// A real login role that is a member of <c>mageride_fleet_reader</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes the cross-org assertion mean anything (C121 DoD 2).</b> The container's
    /// own <c>mageride</c> user is a superuser and a superuser bypasses RLS entirely, so a read made
    /// through it would prove only that fleet-svc's <c>WHERE</c> clause works. This role reaches the
    /// database the way migrations 1806/1807 intend and with no fleet-svc code in the path.
    /// </remarks>
    public const string FleetReaderLogin = "c121_fleet_reader";

    private const string FleetReaderPassword = "c121-not-a-secret";

    /// <summary>
    /// How often trip-state-svc's durable-timer sweep looks, and it is not the deployed minute.
    /// </summary>
    /// <remarks>
    /// The cadence, not a window. Every window a scenario turns on is asserted at the value its
    /// specification gives — US-5.3's 30-minute idle, US-5.4's 100 metres, US-5.10's 5-minute grace,
    /// R-15/T-04's offline grace — and read off the running service's own options before anything is
    /// moved (<see cref="AssertWindowsAsync"/>). This only decides how long a scenario waits for the
    /// worker that was going to fire anyway.
    /// </remarks>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(2);

    private static readonly SemaphoreSlim SharedGate = new(1, 1);
    private static ModeAbFleet? _shared;

    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;
    private static int _imeiCounter = Random.Shared.Next(100_000, 899_999);
    private static int _placeCounter;

    private readonly WebApplication _provisioning;
    private readonly WebApplication _tripState;
    private readonly WebApplication _subscription;
    private readonly WebApplication _fleet;
    private readonly WebApplication _fanout;
    private readonly WebApplication _bridge;
    private readonly WebApplication _processor;
    private readonly WebApplication _writer;
    private readonly IHost _adapter;
    private readonly PostgresFixture _postgres;
    private readonly string _documentRoot;

    private ModeAbFleet(
        WebApplication provisioning,
        WebApplication tripState,
        WebApplication subscription,
        WebApplication fleet,
        WebApplication fanout,
        WebApplication bridge,
        WebApplication processor,
        WebApplication writer,
        IHost adapter,
        TestTokenIssuer tokens,
        PostgresFixture postgres,
        EmqxFixture emqx,
        string documentRoot)
    {
        _provisioning = provisioning;
        _tripState = tripState;
        _subscription = subscription;
        _fleet = fleet;
        _fanout = fanout;
        _bridge = bridge;
        _processor = processor;
        _writer = writer;
        _adapter = adapter;
        _postgres = postgres;
        _documentRoot = documentRoot;

        Tokens = tokens;
        Broker = emqx;

        TripStateClient = NewClient(tripState);
        FleetClient = NewClient(fleet);
        SubscriptionClient = NewClient(subscription);
        ProvisioningClient = NewClient(provisioning);
        FanoutBaseAddress = new Uri(BaseAddressOf(fanout));
        // The Redis half is a lambda, not the IDatabase: the journal is built here in the
        // constructor, and `Cache` resolves through _fanout's provider, which is not safe to touch
        // until every host has started. Deferring the resolution to the moment a failure is being
        // described also means a torn-down fleet degrades to "unreadable" rather than throwing
        // inside a diagnostic.
        Journal = new SessionJournal(postgres, () => Cache);
    }

    /// <summary>Talks to trip-state-svc — Start/End/Restart Journey and the internal ignition route.</summary>
    public HttpClient TripStateClient { get; }

    /// <summary>Talks to fleet-svc — the org, the roster, assignments, the map, the analytics.</summary>
    public HttpClient FleetClient { get; }

    /// <summary>Talks to subscription-svc — Epic 23's access requests, roster and unsubscribe.</summary>
    public HttpClient SubscriptionClient { get; }

    /// <summary>Talks to provisioning-svc — the tracker binding a frame is resolved through.</summary>
    public HttpClient ProvisioningClient { get; }

    /// <summary>Where fanout-svc's <c>/hubs/live</c> is listening.</summary>
    public Uri FanoutBaseAddress { get; }

    public TestTokenIssuer Tokens { get; }

    /// <summary>The broker, for the retained presence pair T-04 leaves behind.</summary>
    public EmqxFixture Broker { get; }

    /// <summary>Prints a vehicle's whole session history when an assertion fails.</summary>
    public SessionJournal Journal { get; }

    /// <summary>trip-state-svc's container, for the windows a scenario asserts before moving a clock.</summary>
    public IServiceProvider TripStateServices => _tripState.Services;

    // -----------------------------------------------------------------------------------------
    // Lifetime
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The fleet, started at most once per test run.
    /// </summary>
    /// <remarks>
    /// Never disposed on purpose: the nine services live as long as the test host. Tearing them down
    /// between scenario classes would restart four Kafka consumer groups and two MQTT shared
    /// subscriptions per class, and a rebalance mid-suite is exactly the window in which a
    /// <c>telemetry.normalized</c> record read from the latest offset is the record a scenario was
    /// waiting for.
    /// </remarks>
    public static async Task<ModeAbFleet> SharedAsync(
        PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(redpanda);
        ArgumentNullException.ThrowIfNull(emqx);

        if (_shared is not null)
        {
            return _shared;
        }

        await SharedGate.WaitAsync();

        try
        {
            return _shared ??= await StartAsync(postgres, redis, redpanda, emqx);
        }
        finally
        {
            SharedGate.Release();
        }
    }

    private static async Task<ModeAbFleet> StartAsync(
        PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    {
        postgres.RequireAvailable();
        redis.RequireAvailable();
        redpanda.RequireAvailable();
        emqx.RequireAvailable();

        await postgres.EnsureMigratedAsync();
        await EnsureFleetReaderLoginAsync(postgres);

        // Created up front rather than left to the broker's auto-creation, so a consumer that
        // subscribes before its topic's first message finds a topic rather than a metadata error it
        // will retry through the first scenario's timeout.
        foreach (var topic in new[]
                 {
                     "telemetry.raw", "telemetry.normalized", "trip.events", "registry.events",
                     "provisioning.events", "audit.events",
                 })
        {
            await redpanda.CreateTopicAsync(topic);
        }

        var tokens = new TestTokenIssuer();
        var documentRoot = Path.Combine(Path.GetTempPath(), "mageride-c121", Guid.NewGuid().ToString("N"));
        var certificateAuthority = Path.Combine(documentRoot, "step-ca");

        // Start order is the dependency order: provisioning-svc holds the binding the adapter
        // resolves through, trip-state-svc holds the session the adapter reports ignition to,
        // subscription-svc answers the Epic 23 proxies fleet-svc forwards, and the three hot-path
        // services and the adapter only need the containers.
        var provisioning = BuildProvisioning(postgres, redis, redpanda, tokens, certificateAuthority);
        await provisioning.StartAsync();

        var tripState = BuildTripState(postgres, redis, redpanda, emqx, tokens);
        await tripState.StartAsync();

        var subscription = BuildSubscription(postgres, redpanda, tokens, documentRoot);
        await subscription.StartAsync();

        var fleet = BuildFleet(
            postgres, tokens, documentRoot, BaseAddressOf(provisioning), BaseAddressOf(subscription));
        await fleet.StartAsync();

        var fanout = BuildFanout(redis, redpanda, emqx, tokens);
        await fanout.StartAsync();

        var bridge = BuildBridge(redis, redpanda, emqx, tokens);
        await bridge.StartAsync();

        var processor = BuildProcessor(redis, redpanda, tokens);
        await processor.StartAsync();

        var writer = BuildWriter(postgres, redpanda, tokens);
        await writer.StartAsync();

        var adapter = BuildAdapter(
            postgres, redis, emqx, BaseAddressOf(provisioning), BaseAddressOf(tripState));
        await adapter.StartAsync();

        return new ModeAbFleet(
            provisioning, tripState, subscription, fleet, fanout, bridge, processor, writer, adapter,
            tokens, postgres, emqx, documentRoot);
    }

    public async ValueTask DisposeAsync()
    {
        TripStateClient.Dispose();
        FleetClient.Dispose();
        SubscriptionClient.Dispose();
        ProvisioningClient.Dispose();

        await _adapter.StopAsync(TimeSpan.FromSeconds(20));
        _adapter.Dispose();

        // Reverse start order, so nothing is torn down under an in-flight call from a service that
        // is still running.
        foreach (var app in new[] { _writer, _processor, _bridge, _fanout, _fleet, _subscription, _tripState, _provisioning })
        {
            await app.StopAsync(TimeSpan.FromSeconds(10));
            await app.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(_documentRoot))
            {
                Directory.Delete(_documentRoot, recursive: true);
            }
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"warning: could not remove {_documentRoot}: {exception.Message}");
        }
    }

    // -----------------------------------------------------------------------------------------
    // The organisation, its people and its vehicles — through fleet-svc's own routes
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Registers an organisation through <c>POST /v1/fleets</c>. It is PENDING (US-13.A7).
    /// </summary>
    /// <remarks>
    /// Through the API rather than by INSERT, because the owner's <c>iam.fleet_members</c> seat is
    /// written by the same transaction as the <c>registry.fleets</c> row — an organisation whose
    /// registrant has no seat is one nobody can open, including the person who just created it, and
    /// a fixture that wrote only one of the two would be testing a state the service cannot produce.
    /// </remarks>
    public async Task<FleetOrg> CreateOrgAsync(string? name = null)
    {
        var ownerId = await CreateUserAsync("fleet_owner");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var businessReg = $"PV-{suffix}";

        using var response = await PostAsync(
            FleetClient,
            "/v1/fleets",
            new
            {
                name = name ?? $"C121 Transit {suffix}",
                registrationNo = businessReg,
                contactPhone = NextPhone(),
                contactEmail = $"ops-{suffix}@example.lk",
                address = "42 Galle Road, Colombo 03",
            },
            Tokens.FleetOwner(ownerId));

        await AssertSuccessAsync(response, "registering an organisation");

        var fleetId = (await ReadJsonAsync(response)).GetProperty("fleetId").GetGuid();

        return new FleetOrg(fleetId, ownerId, Tokens.FleetMember(ownerId, fleetId, "owner"), businessReg);
    }

    /// <summary>An organisation a Verification Officer has approved — the state onboarding needs.</summary>
    public async Task<FleetOrg> CreateApprovedOrgAsync(string? name = null)
    {
        var org = await CreateOrgAsync(name);
        await ApproveOrgAsync(org.FleetId);

        return org;
    }

    /// <summary>
    /// Approves an organisation through <c>POST /v1/internal/fleets/{id}/approve</c> — the plane
    /// admin-bff calls (AL-39).
    /// </summary>
    public async Task ApproveOrgAsync(Guid fleetId)
    {
        var officerId = await CreateUserAsync("verification_officer");

        using var response = await PostInternalAsync(
            FleetClient,
            $"/v1/internal/fleets/{fleetId}/approve",
            new { officerId = officerId.ToString() },
            FleetInternalKey);

        await AssertSuccessAsync(response, $"approving organisation {fleetId}");
    }

    /// <summary>A driver account with the canonical role an assignment demands (US-13.2).</summary>
    public async Task<ModeAbDriver> CreateDriverAsync()
    {
        var phone = NextPhone();
        var driverId = await CreateUserAsync("driver", phone);

        return new ModeAbDriver(driverId, phone, Tokens.Driver(driverId));
    }

    public async Task<ModeAbPassenger> CreatePassengerAsync()
    {
        var phone = NextPhone();
        var id = await CreateUserAsync("passenger", phone);

        return new ModeAbPassenger(id, phone, Tokens.Passenger(id));
    }

    /// <summary>
    /// Onboards a Mode A or Mode B vehicle onto an organisation's roster and assigns it a driver,
    /// both through fleet-svc's own routes (US-13.1, US-13.2).
    /// </summary>
    /// <param name="approve">
    /// Whether to leave the vehicle operable. See <see cref="MarkVehicleApprovedAsync"/> — the
    /// Verification Officer's decision it stands in for is not on any service in this fleet.
    /// </param>
    public async Task<FleetVehicle> OnboardVehicleAsync(
        FleetOrg org,
        ModeAbDriver driver,
        string mode = "A",
        string vehicleType = "bus",
        bool approve = true)
    {
        ArgumentNullException.ThrowIfNull(org);
        ArgumentNullException.ThrowIfNull(driver);

        var plate = NextPlate();

        using var added = await PostAsync(
            FleetClient,
            $"/v1/fleets/{org.FleetId}/vehicles",
            new
            {
                registrationNumber = plate,
                vehicleType,
                mode,
                modeBBilling = mode == "B" ? "free" : null,
            },
            org.OwnerBearer);

        await AssertSuccessAsync(added, $"onboarding a Mode {mode} {vehicleType}");

        var vehicleId = (await ReadJsonAsync(added)).GetProperty("vehicleId").GetGuid();

        await AssignDriverAsync(org, vehicleId, driver);

        if (approve)
        {
            await MarkVehicleApprovedAsync(vehicleId);
        }

        return new FleetVehicle(vehicleId, org.FleetId, mode, vehicleType, plate, driver);
    }

    /// <summary>Assigns a driver to one of the organisation's vehicles (US-13.2, US-13.9).</summary>
    public async Task<Guid> AssignDriverAsync(
        FleetOrg org, Guid vehicleId, ModeAbDriver driver, DateTimeOffset? validFrom = null,
        DateTimeOffset? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(org);
        ArgumentNullException.ThrowIfNull(driver);

        using var response = await PostAsync(
            FleetClient,
            $"/v1/fleets/{org.FleetId}/assignments",
            new
            {
                vehicleId = vehicleId.ToString(),
                driverId = driver.DriverId.ToString(),

                // `from`, not `validFrom`: US-13.9 makes an assignment time-bounded and the contract
                // spells the window's ends `from` and `to`. `valid_from` is the column, and it is
                // not `assigned_at` renamed — a relief driver booked on Monday for Thursday's shift
                // must not be able to take the bus out on Monday.
                from = validFrom ?? DateTimeOffset.UtcNow.AddMinutes(-1),
                to = expiresAt,
            },
            org.OwnerBearer);

        await AssertSuccessAsync(response, $"assigning driver {driver.DriverId} to vehicle {vehicleId}");

        return Guid.Parse((await ReadJsonAsync(response)).GetProperty("assignmentId").GetString()!);
    }

    /// <summary>
    /// Leaves a fleet vehicle APPROVED, which <b>nothing in this fleet can do through a route</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called because the platform has no reachable path to it, and that is a finding rather than
    /// a convenience.</b> AL-50's gate (<c>POST /v1/internal/fleets/{id}/vehicles/{vid}/approve</c>)
    /// requires every required document slot to be <c>verified</c>; a slot is verified only when its
    /// document has fields and none of them is <c>pending</c>; and fleet-svc writes a field
    /// <c>auto_verified</c> only when ocr-svc returns it above <c>Fleet:OcrConfidenceThreshold</c>.
    /// ocr-svc caps its on-prem Tesseract path <em>below</em> that threshold by construction, so
    /// without a reachable Gemini every field is <c>pending</c> — and the only surface that can
    /// confirm one is admin-bff's <c>PUT /v1/admin/verification/{subjectId}/fields/{fieldKey}</c>,
    /// which is C062's and is not in this fleet.
    /// </para>
    /// <para>
    /// So this writes the one column the officer's decision would have written, and
    /// <c>FleetOperationsScenario</c> drives the gate itself and asserts that it <em>refuses</em> —
    /// which is the half of AL-50 that is reachable, and the half worth pinning. The gap is recorded
    /// in the C121 handoff and in <c>FleetOperationsScenario.Unreachable</c>.
    /// </para>
    /// </remarks>
    public async Task MarkVehicleApprovedAsync(Guid vehicleId)
    {
        await using var connection = await OpenAsync();

        var updated = await connection.ExecuteAsync(
            "UPDATE registry.vehicles SET status = 'APPROVED' WHERE id = @VehicleId AND status <> 'APPROVED';",
            new { VehicleId = vehicleId });

        Assert.True(updated == 1, $"Vehicle {vehicleId} was not waiting on a Verification Officer's decision.");
    }

    /// <summary>
    /// Binds a hardware tracker to a vehicle through the Fleet Portal (US-13.12), which forwards the
    /// operator's own bearer to provisioning-svc.
    /// </summary>
    /// <remarks>
    /// The IMEI is what every inbound frame is resolved through (T-03), so binding it through the
    /// real route is what makes <see cref="TrackerDevice"/> authenticate against the platform rather
    /// than against a cache entry this suite wrote.
    /// </remarks>
    public async Task<string> BindTrackerAsync(FleetOrg org, FleetVehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(org);
        ArgumentNullException.ThrowIfNull(vehicle);

        var imei = NextImei();

        using var response = await PostAsync(
            FleetClient,
            $"/v1/fleets/{org.FleetId}/trackers/bind",
            new { imei, vehicleId = vehicle.VehicleId.ToString(), autoStartSession = true },
            org.OwnerBearer);

        await AssertSuccessAsync(response, $"binding tracker {imei} to vehicle {vehicle.VehicleId}");

        return imei;
    }

    /// <summary>A vehicle with a tracker bound to it, ready to dial the adapter.</summary>
    public async Task<FleetVehicle> OnboardTrackedVehicleAsync(
        FleetOrg org, ModeAbDriver driver, string mode = "A", string vehicleType = "bus")
    {
        var vehicle = await OnboardVehicleAsync(org, driver, mode, vehicleType);
        var imei = await BindTrackerAsync(org, vehicle);

        return vehicle with { Imei = imei };
    }

    // -----------------------------------------------------------------------------------------
    // The journey, through the surfaces a driver app has
    // -----------------------------------------------------------------------------------------

    /// <summary>Start Journey — <c>POST /v1/sessions/start</c> (US-5.1, D-03).</summary>
    public async Task<StartedSession> StartJourneyAsync(
        FleetVehicle vehicle, bool autoEndAtDestination = false, Guid? routeId = null)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        using var response = await PostAsync(
            TripStateClient,
            "/v1/sessions/start",
            new
            {
                vehicleId = vehicle.VehicleId.ToString(),
                mode = vehicle.Mode,
                routeId = routeId?.ToString(),
                autoEndAtDestination,
            },
            vehicle.Driver.Bearer);

        if (response.StatusCode != HttpStatusCode.Created)
        {
            Assert.Fail(
                $"Start Journey on vehicle {vehicle.VehicleId} answered {(int)response.StatusCode}: "
                + await response.Content.ReadAsStringAsync());
        }

        var body = await ReadJsonAsync(response);

        return new StartedSession(
            body.GetProperty("sessionId").GetGuid(),
            body.GetProperty("vehicleId").GetGuid(),
            body.GetProperty("mode").GetString()!,
            body.GetProperty("state").GetString()!);
    }

    /// <summary>End Journey — the dashboard action that overrides the device (US-5.2, AL-32).</summary>
    public Task<HttpResponseMessage> EndJourneyAsync(FleetVehicle vehicle, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        return EndJourneyAsync(vehicle.Driver.Bearer, sessionId);
    }

    /// <summary>
    /// End Journey as whoever holds the session.
    /// </summary>
    /// <remarks>
    /// An ignition-started session belongs to the vehicle's <em>owner</em> and not to the driver
    /// assigned to it — a tracker knows its vehicle and nothing else (US-3.22), so that is the only
    /// person it can be attributed to. Ending one is therefore the owner's dashboard action, and
    /// <c>/v1/sessions/{id}/end</c> admits the <c>fleet_owner</c> role for exactly that reason.
    /// </remarks>
    public Task<HttpResponseMessage> EndJourneyAsync(string bearer, Guid sessionId) =>
        PostAsync(TripStateClient, $"/v1/sessions/{sessionId}/end", new { }, bearer);

    /// <summary>Resume an auto-ended journey inside the grace window (US-5.10).</summary>
    public Task<HttpResponseMessage> RestartJourneyAsync(FleetVehicle vehicle, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        return PostAsync(TripStateClient, $"/v1/sessions/{sessionId}/restart", new { }, vehicle.Driver.Bearer);
    }

    /// <summary>The driver app's cold-start read — <c>GET /v1/sessions/{vehicleId}/active</c>.</summary>
    public Task<HttpResponseMessage> ReadActiveSessionAsync(FleetVehicle vehicle) =>
        GetAsync(TripStateClient, $"/v1/sessions/{vehicle?.VehicleId}/active", vehicle!.Driver.Bearer);

    // -----------------------------------------------------------------------------------------
    // Reading what the platform did
    // -----------------------------------------------------------------------------------------

    public async Task<SessionRow> ReadSessionAsync(Guid sessionId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<SessionRow>(SessionSelect + " WHERE id = @SessionId;",
            new { SessionId = sessionId });
    }

    /// <summary>The vehicle's live session, or <see langword="null"/>.</summary>
    public async Task<SessionRow?> ActiveSessionAsync(Guid vehicleId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<SessionRow>(
            SessionSelect + " WHERE vehicle_id = @VehicleId AND state = 'ACTIVE';",
            new { VehicleId = vehicleId });
    }

    /// <summary>Every session on a vehicle, newest first.</summary>
    public async Task<IReadOnlyList<SessionRow>> SessionsOfAsync(Guid vehicleId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<SessionRow>(
            SessionSelect + " WHERE vehicle_id = @VehicleId ORDER BY started_at DESC, id DESC;",
            new { VehicleId = vehicleId })];
    }

    /// <summary>The domain log a support engineer reads six weeks later (<c>trips.events</c>, 0502).</summary>
    public async Task<IReadOnlyList<string>> TripEventKindsAsync(Guid sessionId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<string>(
            "SELECT kind FROM trips.events WHERE session_id = @SessionId ORDER BY ts, id;",
            new { SessionId = sessionId })];
    }

    /// <summary>The event types a vehicle put on <c>trips.outbox</c> for <c>trip.events</c>.</summary>
    public async Task<IReadOnlyList<string>> TripOutboxAsync(Guid vehicleId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<string>(
            "SELECT event_type FROM trips.outbox WHERE aggregate_id = @VehicleId ORDER BY id;",
            new { VehicleId = vehicleId })];
    }

    /// <summary>Rows persistence-writer-svc has landed in the Timescale hypertable (T-06).</summary>
    public async Task<int> TelemetryRowCountAsync(Guid vehicleId)
    {
        await using var connection = await OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM telemetry.positions WHERE vehicle_id = @VehicleId;",
            new { VehicleId = vehicleId });
    }

    /// <summary>
    /// The newest sample the whole plane produced for a vehicle, as Timescale holds it.
    /// </summary>
    /// <remarks>
    /// <c>source</c> is D6' §4.1's family code, stamped by whichever adapter decoded the frame — so
    /// asserting on it is asserting that the <em>right</em> decoder produced these numbers, which a
    /// coordinate on its own cannot say. <c>fleet_id</c> is denormalised at write time by
    /// persistence-writer-svc (<c>mqtt-topics.md</c> §6) and is what the fleet's own map and
    /// analytics are scoped by.
    /// </remarks>
    public async Task<TelemetryRow?> NewestTelemetryAsync(Guid vehicleId)
    {
        await using var connection = await OpenAsync();

        // Every column is cast rather than mapped by name. `telemetry.positions` is D4' §17's
        // hypertable and stores `lat`/`lng`/`speed_mps` as `real` and `sample_ts` as `timestamptz`;
        // Dapper's constructor binding matches parameter types *exactly*, so a `real` column against
        // a `double` parameter does not fail to convert — it fails to materialise the record at all,
        // with a message about a missing constructor. The same trap C047 records for its money
        // columns.
        var row = await connection.QuerySingleOrDefaultAsync<(double, double, double?, short, Guid?, DateTimeOffset)?>(
            """
            SELECT lat::double precision, lng::double precision, speed_mps::double precision,
                   source::smallint, fleet_id, sample_ts
              FROM telemetry.positions WHERE vehicle_id = @VehicleId
             ORDER BY sample_ts DESC, seq DESC LIMIT 1;
            """,
            new { VehicleId = vehicleId });

        return row is null
            ? null
            : new TelemetryRow(row.Value.Item1, row.Value.Item2, row.Value.Item3, row.Value.Item4,
                row.Value.Item5, row.Value.Item6);
    }

    /// <summary>The tracker binding provisioning-svc holds for an IMEI (T-03).</summary>
    public async Task<(Guid VehicleId, string State)?> TrackerBindingAsync(string imei) =>
        (await TrackerBindingsAsync(imei)).OrderByDescending(binding => binding.State == "ACTIVE")
            .Select(binding => ((Guid, string)?)(binding.VehicleId, binding.State))
            .FirstOrDefault();

    /// <summary>
    /// Every binding an IMEI has, oldest first.
    /// </summary>
    /// <remarks>
    /// Plural because T-08 makes it plural: two devices claiming one IMEI inside the anti-clone
    /// window leave <b>two</b> rows, both QUARANTINED, because closing one would destroy the
    /// evidence and might well leave the clone publishing. An operator resolves it; provisioning-svc
    /// holds both until they do.
    /// </remarks>
    public async Task<IReadOnlyList<(Guid VehicleId, string State)>> TrackerBindingsAsync(string imei)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<(Guid, string)>(
            "SELECT vehicle_id, state FROM prov.tracker_bindings WHERE imei = @Imei ORDER BY created_at;",
            new { Imei = imei })];
    }

    /// <summary>
    /// A back-office bearer, for the two routes on this plane that belong to a person rather than to
    /// an operator.
    /// </summary>
    /// <remarks>
    /// Decommissioning a tracker (T-12) is <c>admin</c>/<c>super_admin</c> on provisioning-svc and
    /// not the fleet operator's to do — an operator can bind a device to their own vehicle, but
    /// revoking a credential takes it off the air permanently and is an Admin Portal action.
    /// </remarks>
    public async Task<string> AdminBearerAsync()
    {
        var adminId = await CreateUserAsync(MageRideRoles.SuperAdmin);

        return Tokens.Issue(adminId, MageRideRoles.SuperAdmin, MageRideApps.Admin);
    }

    /// <summary>
    /// The retained value on <c>veh/{vehicleId}/status</c> — what a subscriber joining afterwards
    /// reads (T-04).
    /// </summary>
    /// <remarks>
    /// Retention is the whole mechanism: a consumer that connects a minute after a device went away
    /// has to learn the vehicle is dark without anything republishing it, which is what makes the
    /// pair guarded by <c>SessionRegistry.IsCurrent</c> in the adapter — an <c>offline</c> from a
    /// session that has already been replaced would overwrite the replacement's <c>online</c> and
    /// the vehicle would read dark until its next reconnect.
    /// </remarks>
    public async Task<string?> RetainedStatusAsync(Guid vehicleId)
    {
        var tokens = new MqttSessionTokenIssuer(
            Options.Create(new MqttOptions
            {
                Host = Broker.Host,
                Port = Broker.Port,
                SessionTokenSecret = EmqxFixture.SessionTokenSecret,
            }),
            TimeProvider.System);

        var credential = tokens.IssueForService("e2e-status-reader");

        using var client = new MQTTnet.MqttClientFactory().CreateMqttClient();

        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        client.ApplicationMessageReceivedAsync += message =>
        {
            seen.TrySetResult(System.Text.Encoding.UTF8.GetString(
                System.Buffers.BuffersExtensions.ToArray(message.ApplicationMessage.Payload)).Trim());
            return Task.CompletedTask;
        };

        await client.ConnectAsync(
            new MQTTnet.MqttClientOptionsBuilder()
                .WithTcpServer(Broker.Host, Broker.Port)
                .WithClientId($"mageride-e2e-status-{Guid.NewGuid():N}")
                .WithCredentials(credential.Username, credential.Jwt)
                .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
                .WithCleanStart(true)
                .Build(),
            TestContext.Current.CancellationToken);

        await client.SubscribeAsync(
            new MQTTnet.MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(MqttTopics.Status(vehicleId), MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(),
            TestContext.Current.CancellationToken);

        var arrived = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        await client.DisconnectAsync(new MQTTnet.MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);

        return arrived == seen.Task ? await seen.Task : null;
    }

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>
    /// A connection as a role that is not a superuser and holds only migrations 1806/1807's grants.
    /// </summary>
    /// <remarks>
    /// The only way this suite can ask the database a question the way a fleet reader would. Every
    /// read through it is subject to the RESTRICTIVE policies; every read through
    /// <see cref="OpenAsync"/> is not, and that distinction is the whole of DoD 2.
    /// </remarks>
    public async Task<NpgsqlConnection> OpenAsFleetReaderAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
        {
            Username = FleetReaderLogin,
            Password = FleetReaderPassword,
        };

        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        return connection;
    }

    /// <summary>Redis, for the D-23 entitlement SET and the live indexes the map is drawn from.</summary>
    public IDatabase Cache => _fanout.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    /// <summary>The port a protocol family's listener bound, once it has bound.</summary>
    /// <remarks>
    /// <c>Adapter:Ports=0,0,0,0</c> lets the OS choose, so the suite runs beside a dev stack already
    /// holding 5023-5026 — and this waits for the bind rather than reading it straight after
    /// <c>StartAsync</c>, because a <c>BackgroundService</c>'s <c>ExecuteAsync</c> is started and not
    /// awaited by the host.
    /// </remarks>
    public async Task<int> TrackerPortAsync(ProtocolFamily family)
    {
        var listeners = _adapter.Services.GetRequiredService<AdapterListeners>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (listeners.PortFor(family) is { } port)
            {
                return port;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"tcp-adapter's {ProtocolFamilies.Name(family)} listener never bound a port.");
        return 0;
    }

    // -----------------------------------------------------------------------------------------
    // Waiting, and the one clock a scenario is allowed to move
    // -----------------------------------------------------------------------------------------

    /// <summary>Waits until the vehicle's newest session satisfies <paramref name="condition"/>.</summary>
    public async Task<SessionRow> WaitForSessionAsync(
        Guid vehicleId, Func<SessionRow, bool> condition, string what, TimeSpan? within = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var timeout = within ?? TimeSpan.FromSeconds(60);
        var deadline = DateTimeOffset.UtcNow + timeout;

        SessionRow? newest = null;

        do
        {
            newest = (await SessionsOfAsync(vehicleId)).FirstOrDefault();

            if (newest is not null && condition(newest))
            {
                return newest;
            }

            await Task.Delay(150, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail(
            $"Vehicle {vehicleId} never reached '{what}' within {timeout.TotalSeconds:F0}s."
            + await Journal.DescribeAsync(vehicleId));

        return newest!;
    }

    /// <summary>Waits until one session satisfies <paramref name="condition"/>.</summary>
    public async Task<SessionRow> WaitForSessionByIdAsync(
        Guid vehicleId, Guid sessionId, Func<SessionRow, bool> condition, string what, TimeSpan? within = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var timeout = within ?? TimeSpan.FromSeconds(60);
        var deadline = DateTimeOffset.UtcNow + timeout;

        SessionRow session;

        do
        {
            session = await ReadSessionAsync(sessionId);

            if (condition(session))
            {
                return session;
            }

            await Task.Delay(150, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail(
            $"Session {sessionId} never reached '{what}' within {timeout.TotalSeconds:F0}s."
            + await Journal.DescribeAsync(vehicleId));

        return session;
    }

    /// <summary>
    /// Waits until a session has seen the fix a device sent — the suite's synchronisation point.
    /// </summary>
    /// <remarks>
    /// <b>Nothing that depends on where a vehicle is may run before this.</b> A frame crosses EMQX,
    /// mqtt-bridge-svc, <c>telemetry.raw</c>, position-processor-svc, <c>telemetry.normalized</c>
    /// and trip-state-svc's consumer before it becomes <c>last_position_at</c>, and a scenario that
    /// carried on as soon as <em>some</em> fix had landed leaves the rest in flight — where they are
    /// applied to whatever session is live when they arrive. That is a real ordering hazard rather
    /// than a theoretical one: it is how an inbound journey was found to have ended at its
    /// destination one second after it started, on its predecessor's last four fixes.
    /// </remarks>
    public Task<SessionRow> WaitForFixAsync(
        Guid vehicleId, Guid sessionId, ReportedFix fix, TimeSpan? within = null)
    {
        ArgumentNullException.ThrowIfNull(fix);

        return WaitForSessionByIdAsync(
            vehicleId,
            sessionId,
            session => session.LastPositionAt >= fix.CapturedAt,
            $"the fix captured at {fix.CapturedAt:HH:mm:ss} reaching the session",
            within);
    }

    /// <summary>
    /// Waits until a fix a device sent has landed in Timescale, for the stretches where no session
    /// is live to observe it through.
    /// </summary>
    /// <remarks>
    /// A tracker publishes whether or not the driver's app holds a session — that is US-3.22's whole
    /// point, and it is how a bus gets from where one journey ended to where the next one starts.
    /// Those fixes are dropped by trip-state-svc (no session to apply them to) and are visible only
    /// where every accepted sample lands: persistence-writer-svc's hypertable.
    /// </remarks>
    public Task WaitForTelemetryAsync(Guid vehicleId, ReportedFix fix, TimeSpan? within = null)
    {
        ArgumentNullException.ThrowIfNull(fix);

        return UntilAsync(
            vehicleId,
            async () =>
            {
                await using var connection = await OpenAsync();

                return await connection.ExecuteScalarAsync<bool>(
                    """
                    SELECT EXISTS (SELECT 1 FROM telemetry.positions
                                    WHERE vehicle_id = @VehicleId AND sample_ts >= @CapturedAt);
                    """,
                    new { VehicleId = vehicleId, fix.CapturedAt });
            },
            $"the fix captured at {fix.CapturedAt:HH:mm:ss} reaching telemetry.positions",
            within);
    }

    /// <summary>Waits until <paramref name="condition"/> holds, printing the vehicle if it never does.</summary>
    public async Task UntilAsync(
        Guid vehicleId, Func<Task<bool>> condition, string what, TimeSpan? within = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var timeout = within ?? TimeSpan.FromSeconds(60);
        var deadline = DateTimeOffset.UtcNow + timeout;

        do
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(150, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail(
            $"{what} did not happen within {timeout.TotalSeconds:F0}s." + await Journal.DescribeAsync(vehicleId));
    }

    /// <summary>
    /// The four windows this suite turns on, read off the running service rather than from a spec
    /// quoted in a comment.
    /// </summary>
    /// <remarks>
    /// Called by every scenario that moves a clock, <b>before</b> it moves one. That is what keeps
    /// <see cref="AgeIdleClockAsync"/> and its two siblings honest: they move the platform's own
    /// record of <em>when</em> something happened, never what happened, and the arithmetic they
    /// short-circuit is asserted at the value the URD gives it first.
    /// </remarks>
    public TripStateOptions Windows =>
        TripStateServices.GetRequiredService<IOptions<TripStateOptions>>().Value;

    /// <summary>
    /// Moves a session's idle clock past US-5.3's window so the sweep that was going to fire, fires.
    /// </summary>
    /// <remarks>
    /// <b>A clock, not a state fix</b> — the distinction <c>e2e/CLAUDE.md</c> draws and the reason
    /// this is allowed at all. Nothing about the session, its reason or its actor is touched: the
    /// column moved is <c>last_movement_at</c>, the platform's own record of when the vehicle last
    /// moved, and it was written by a real fix arriving from a real tracker through the whole hot
    /// path. What fires is the real sweep and what it does is <c>AutoEndAsync</c>, the same code the
    /// deployed timer runs. The alternative is a scenario that takes half an hour.
    /// </remarks>
    public async Task AgeIdleClockAsync(Guid sessionId)
    {
        var window = Windows.IdleTimeout;

        Assert.True(
            window == TimeSpan.FromMinutes(30),
            $"US-5.3's idle window is thirty minutes and this service is running {window}.");

        var session = await ReadSessionAsync(sessionId);

        Assert.True(
            session.LastMovementAt is not null,
            $"Session {sessionId} has no idle clock to move — no fix has reached it."
            + await Journal.DescribeAsync(session.VehicleId));

        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            """
            UPDATE trips.sessions
               SET last_movement_at = last_movement_at - @By, started_at = started_at - @By
             WHERE id = @SessionId;
            """,
            new { SessionId = sessionId, By = window + TimeSpan.FromMinutes(1) });
    }

    /// <summary>Moves an auto-ended session's clock past US-5.10's 5-minute restart grace.</summary>
    /// <inheritdoc cref="AgeIdleClockAsync"/>
    public async Task AgeRestartGraceAsync(Guid sessionId)
    {
        var window = Windows.RestartGrace;

        Assert.True(
            window == TimeSpan.FromMinutes(5),
            $"US-5.10's restart grace is five minutes and this service is running {window}.");

        await using var connection = await OpenAsync();

        var moved = await connection.ExecuteAsync(
            "UPDATE trips.sessions SET ended_at = ended_at - @By WHERE id = @SessionId AND ended_at IS NOT NULL;",
            new { SessionId = sessionId, By = window + TimeSpan.FromMinutes(1) });

        Assert.True(moved == 1, $"Session {sessionId} has not ended, so it has no grace window to close.");
    }

    /// <summary>Moves a vehicle's last-will clock past R-15/T-04's offline grace.</summary>
    /// <inheritdoc cref="AgeIdleClockAsync"/>
    public async Task AgeOfflineGraceAsync(Guid sessionId)
    {
        var window = Windows.OfflineGrace;

        await using var connection = await OpenAsync();

        var moved = await connection.ExecuteAsync(
            """
            UPDATE trips.sessions SET offline_since = offline_since - @By
             WHERE id = @SessionId AND offline_since IS NOT NULL;
            """,
            new { SessionId = sessionId, By = window + TimeSpan.FromMinutes(1) });

        Assert.True(
            moved == 1,
            $"Session {sessionId} has not been marked offline, so there is no last-will clock to move.");
    }

    /// <summary>
    /// A square of Sri Lanka no other vehicle in this run has used.
    /// </summary>
    /// <remarks>
    /// Two reasons, both about isolation of an index rather than of a row. The live map is fanned
    /// out per H3 res-7 cell, so two scenarios in one cell would see each other's vehicles in the
    /// same <c>VehiclePositions</c> batch; and US-5.4's arrival fence is a radius around a vehicle's
    /// <em>previous</em> journey's end, so a scenario has to be sure the only thing near its
    /// destination is its own bus. The grid steps 0.12° (~13 km), inside the box the platform prices
    /// and maps, and <b>throws when exhausted</b> rather than wrapping.
    /// </remarks>
    public static GeoPoint NextPlace()
    {
        const double Step = 0.12;
        const int Rows = 32;
        const int Columns = 19;

        var index = Interlocked.Increment(ref _placeCounter) - 1;

        if (index >= Rows * Columns)
        {
            throw new InvalidOperationException(
                $"This run has asked for {index + 1} places and the grid holds {Rows * Columns}. Reusing one "
                + "would put two vehicles in each other's map cell and each other's arrival fence.");
        }

        return new GeoPoint(6.0 + (Step * (index / Columns)), 79.6 + (Step * (index % Columns)));
    }

    /// <summary>A point <paramref name="metres"/> due north of <paramref name="from"/>.</summary>
    /// <remarks>
    /// Northward, so the arithmetic is one constant: a degree of latitude is 111,320 m everywhere,
    /// while a degree of longitude is not. Scenarios use it to walk a vehicle towards a destination
    /// at a plausible speed — ADD §12.6 refuses a bus that covers ground faster than 120 km/h, and a
    /// refused sample never reaches the session's clocks at all.
    /// </remarks>
    public static GeoPoint MetresNorth(GeoPoint from, double metres) =>
        new(from.Latitude + (metres / 111_320.0), from.Longitude);

    /// <summary>Great-circle metres between two points, so a drive can pace itself.</summary>
    /// <remarks>
    /// The same spherical formula trip-state-svc's position consumer uses to decide whether a
    /// vehicle moved, for the same reason it uses one: this is arithmetic about a few hundred
    /// metres, and PostGIS would be a round trip per step.
    /// </remarks>
    public static double DistanceM(GeoPoint from, GeoPoint to)
    {
        const double EarthRadiusM = 6_371_008.8;

        var lat1 = double.DegreesToRadians(from.Latitude);
        var lat2 = double.DegreesToRadians(to.Latitude);
        var deltaLat = lat2 - lat1;
        var deltaLng = double.DegreesToRadians(to.Longitude - from.Longitude);

        var h = (Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2))
                + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLng / 2) * Math.Sin(deltaLng / 2));

        return 2 * EarthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    // -----------------------------------------------------------------------------------------
    // HTTP plumbing
    // -----------------------------------------------------------------------------------------

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body, string? bearer)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }, options: MageRideJson.Options),
        };

        // D3' §0 makes the header mandatory on every POST; omitting it by accident would exercise
        // the 400 path instead of the route.
        request.Headers.Add(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostInternalAsync(
        HttpClient client, string path, object? body, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }, options: MageRideJson.Options),
        };

        request.Headers.Add(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        request.Headers.Add("X-MageRide-Internal-Key", apiKey);

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? bearer)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string path, string? bearer)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new HttpRequestMessage(HttpMethod.Delete, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client.SendAsync(request);
    }

    /// <summary>Uploads a document into one of AL-50's named slots, as SCR-FP-004 does.</summary>
    public async Task<HttpResponseMessage> UploadVehicleDocumentAsync(
        FleetOrg org, Guid vehicleId, string kind, DateOnly? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(org);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/fleets/{org.FleetId}/vehicles/{vehicleId}/documents");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", org.OwnerBearer);
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        var content = new MultipartFormDataContent { { new StringContent(kind), "kind" } };

        if (expiresAt is { } day)
        {
            content.Add(new StringContent(day.ToString("O", CultureInfo.InvariantCulture)), "expiresAt");
        }

        // A one-pixel PNG. The bytes are never read by anything in this fleet — ocr-svc is not here
        // — but they have to be bytes: the upload is streamed and counted rather than measured by
        // Content-Length, and an empty body is refused before the slot is ever written.
        var file = new ByteArrayContent(
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        ]);

        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", $"{kind}.png");
        request.Content = content;

        return await FleetClient.SendAsync(request);
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>The <c>type</c> slug of an RFC 7807 problem, for the negative assertions.</summary>
    public static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        var problem = await ReadJsonAsync(response);
        var type = problem.TryGetProperty("type", out var value) ? value.GetString() ?? string.Empty : string.Empty;

        return type[(type.LastIndexOf('/') + 1)..];
    }

    public static async Task AssertSuccessAsync(HttpResponseMessage response, string what)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail($"{what} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Composition
    // -----------------------------------------------------------------------------------------

    private static WebApplication BuildTripState(
        PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx,
        TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["ConnectionStrings:Redis"] = redis.ConnectionString,

                // The container is plain Postgres, not PgBouncer — so the pooled DSN also serves the
                // LISTEN the outbox dispatcher registers (E-09).
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,

                // On. R-13's whole claim is that `session.ended` exists because a transaction
                // committed, and the LISTEN/NOTIFY dispatcher is what puts it on `trip.events`
                // afterwards — where persistence-writer-svc reads it to close the trip summary.
                ["Outbox:DispatcherEnabled"] = "true",
                ["TripState:InternalApiKey"] = TripStateInternalKey,

                // The durable timers, on. US-5.3's idle window, US-5.4's arrival fence and
                // R-15/T-04's offline grace are only reachable through this worker; a suite that
                // swept by hand would be asserting its own fixture.
                ["TripState:SweepEnabled"] = "true",
                ["TripState:SweepInterval"] = SweepInterval.ToString(),

                // The input those timers have. Without it US-5.3 and US-5.4 are both unreachable —
                // the sweep runs and simply never finds a session that has stopped moving.
                ["TripState:PositionConsumerEnabled"] = "true",
                ["TripState:PositionConsumerGroup"] = $"trip-state-positions-e2e-{Guid.NewGuid():N}",

                // R-15's `veh/+/status` subscription and D5' §5.2's cadence hint, on a real broker
                // running the deployed ACL.
                ["TripState:VehicleStatusEnabled"] = "true",
                ["TripState:PublishCadenceHints"] = "true",
                ["Mqtt:Host"] = emqx.Host,
                ["Mqtt:Port"] = emqx.Port.ToString(CultureInfo.InvariantCulture),
                ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,
            },
            (options, configure) => TripStateApplication.Build(options, configure));

    private static WebApplication BuildProvisioning(
        PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, TestTokenIssuer tokens,
        string certificateAuthority) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Outbox:DispatcherEnabled"] = "true",
                ["Provisioning:InternalApiKey"] = ProvisioningInternalKey,

                // The device CA lives in this run's own directory: a credential minted from a root
                // another run created would validate against a different chain than the one this
                // suite's broker trusts.
                ["StepCa:RootKeyPath"] = certificateAuthority,

                // The 90-day rotation cron and the bulk minter are C030's subject and neither is on
                // a Mode A/B path. Left off rather than left ticking under an assertion about a
                // binding nobody rotated.
                ["Provisioning:RotationEnabled"] = "false",
                ["Provisioning:BulkMintEnabled"] = "false",
            },
            (options, configure) => ProvisioningApplication.Build(options, configure));

    private static WebApplication BuildFleet(
        PostgresFixture postgres,
        TestTokenIssuer tokens,
        string documentRoot,
        string provisioningBaseUrl,
        string subscriptionBaseUrl) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Fleet:InternalApiKey"] = FleetInternalKey,
                ["Fleet:DocumentRoot"] = Path.Combine(documentRoot, "fleet-documents"),

                // Both hops are real services on real sockets beside this one: US-13.12's tracker
                // bind and Epic 23's four proxies, each forwarding the operator's own bearer.
                ["Fleet:ProvisioningBaseUrl"] = provisioningBaseUrl,
                ["Fleet:SubscriptionBaseUrl"] = subscriptionBaseUrl,

                // `Fleet:OcrBaseUrl` is deliberately unset. ocr-svc needs Tesseract, an OpenCV
                // native and a reachable Gemini, and its absence is what this suite asserts *about*
                // AL-50 rather than around: every uploaded slot stays `pending` and the approval
                // gate refuses. See MarkVehicleApprovedAsync.
                //
                // `Fleet:NotificationBaseUrl` likewise: notification-svc is not in this fleet, so
                // the US-13.11 not-started alarm is switched off rather than left ringing into a
                // socket nobody is listening on. Scheduling is C059's own suite's.
                ["Fleet:ScheduleAlarmsEnabled"] = "false",

                // A key rather than a per-process one, so a signed bulk-error link is stable across
                // the run.
                ["Fleet:ErrorReportSigningKey"] = "mageride-c121-e2e-fleet-error-report-key",
            },
            (options, configure) => FleetApplication.Build(options, configure));

    private static WebApplication BuildSubscription(
        PostgresFixture postgres, RedpandaFixture redpanda, TestTokenIssuer tokens, string documentRoot) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,

                // D-22 lives or dies here: the grant, the cancellation and the `share.revoked` row
                // commit together and the dispatcher puts it on `registry.events` afterwards. A
                // revocation published before the commit revokes somebody whose unsubscribe then
                // rolls back; one published after it leaves a passenger watching a vehicle they left.
                ["Outbox:DispatcherEnabled"] = "true",
                ["Subscription:ModeBSubscriptionsEnabled"] = "true",
                ["Subscription:FileLinkSigningKey"] = "mageride-c121-e2e-subscription-file-link-key",
                ["Subscription:SlipRoot"] = Path.Combine(documentRoot, "transfer-slips"),

                // `Subscription:WalletBaseUrl` is deliberately unset: wallet-svc is not in this
                // fleet and no Mode A/B path moves the platform's own money. The C047 daily fee is
                // therefore uncollectable here, which the service says at start-up — and every
                // scenario in this suite is Epic 23's pass-through, which never touches a ledger.
                ["Subscription:ModeBBillingEnabled"] = "false",
            },
            (options, configure) => SubscriptionApplication.Build(options, configure));

    private static WebApplication BuildFanout(
        RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx, TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Fanout:ConsumerGroup"] = $"fanout-svc-e2e-{Guid.NewGuid():N}",

                // All four halves on. The consumers build `share:{userId}` from `registry.events`
                // (D-23); the control plane carries the directed `ShareRevoked` (D-22); the presence
                // subscription is US-7.17's immediate half; and the pumps are what actually put a
                // Mode B van on a passenger's map — unlike C120's fleet, this one has
                // position-processor-svc writing the cell streams they read.
                ["Fanout:EventsEnabled"] = "true",
                ["Fanout:PresenceEnabled"] = "true",
                ["Fanout:ControlPlaneEnabled"] = "true",
                ["Fanout:PumpEnabled"] = "true",
                ["Mqtt:Host"] = emqx.Host,
                ["Mqtt:Port"] = emqx.Port.ToString(CultureInfo.InvariantCulture),
                ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,
            },
            (options, configure) => FanoutApplication.Build(options, configure));

    private static WebApplication BuildBridge(
        RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx, TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Mqtt:Host"] = emqx.Host,
                ["Mqtt:Port"] = emqx.Port.ToString(CultureInfo.InvariantCulture),
                ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,

                // A client id per run: EMQX evicts an existing session holding the same id, so two
                // runs against one broker would take turns disconnecting each other.
                ["MqttBridge:ClientIdPrefix"] = $"mageride-bridge-e2e-{Guid.NewGuid():N}"[..24],
            },
            (options, configure) => MqttBridgeApplication.Build(options, configure));

    private static WebApplication BuildProcessor(
        RedisFixture redis, RedpandaFixture redpanda, TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["PositionProcessor:ConsumerGroup"] = $"position-processor-e2e-{Guid.NewGuid():N}",

                // Every gate on, at its own default. A fix that this suite's device sends and the
                // platform then refuses is a fix that would have been refused in production, and
                // switching D-18 off to make a scenario pass would remove the one thing standing
                // between a scenario and an assertion about a position nobody could have reported.
            },
            (options, configure) => PositionProcessorApplication.Build(options, configure));

    private static WebApplication BuildWriter(
        PostgresFixture postgres, RedpandaFixture redpanda, TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["PersistenceWriter:ConsumerGroup"] = $"persistence-writer-e2e-{Guid.NewGuid():N}",
                ["PersistenceWriter:TripConsumerGroup"] = $"persistence-writer-trips-e2e-{Guid.NewGuid():N}",

                // The COPY batch flushes twice a second rather than at 1,000 rows: a scenario sends
                // a handful of fixes, and the fleet map reads `telemetry.positions`.
                ["PersistenceWriter:BatchRows"] = "16",
            },
            (options, configure) => PersistenceWriterApplication.Build(options, configure));

    /// <summary>
    /// tcp-adapter, the one worker host in the fleet.
    /// </summary>
    /// <remarks>
    /// A <c>Microsoft.NET.Sdk.Worker</c> project with no Kestrel and no HTTP surface at all
    /// (<c>mqtt-topics.md</c> §7), so it is built through <c>Host.CreateApplicationBuilder</c> and
    /// not through <see cref="Build"/> — there is no bearer handler to repoint, because there is no
    /// listener for a request to arrive on.
    /// </remarks>
    private static IHost BuildAdapter(
        PostgresFixture postgres,
        RedisFixture redis,
        EmqxFixture emqx,
        string provisioningBaseUrl,
        string tripStateBaseUrl)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Mqtt:Host"] = emqx.Host,
            ["Mqtt:Port"] = emqx.Port.ToString(CultureInfo.InvariantCulture),
            ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,

            ["Adapter:BindAddress"] = "127.0.0.1",

            // Ephemeral, so the suite runs beside a dev stack already holding 5023-5026. Positional:
            // GT06, JT/T 808, H02, generic NMEA over UDP, in D6' §4.1's order.
            ["Adapter:Ports"] = "0,0,0,0",

            // The real credential plane, on a real socket. Unset, every device whose cache entry is
            // absent is refused — which is the safe direction and completely silent from the
            // device's side.
            ["Adapter:ProvisioningBaseUrl"] = provisioningBaseUrl,
            ["Adapter:ProvisioningInternalApiKey"] = ProvisioningInternalKey,

            // AL-32's caller. Unset, ACC transitions are decoded and never reported, and a
            // tracker-equipped bus never starts a journey.
            ["Adapter:TripStateBaseUrl"] = tripStateBaseUrl,
            ["Adapter:TripStateInternalApiKey"] = TripStateInternalKey,

            // The deployed value is five seconds; T-04's assertion is a deadline, so the window is
            // kept at what is deployed rather than shortened.
            ["Adapter:OfflineWindow"] = "00:00:05",

            // The deployed idle timeout is fifteen minutes. Nothing here waits that long, and a
            // socket this suite forgets should be reaped rather than held to the end of the run.
            ["Adapter:IdleTimeout"] = "00:01:00",
            ["Otel:PrometheusEnabled"] = "false",
            ["Otel:Endpoint"] = null,
        };

        return TcpAdapterApplication.Build(
            [],
            builder =>
            {
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(settings);
            });
    }

    /// <summary>
    /// The four things every HTTP service in this fleet is configured with, and the one thing every
    /// one of them has replaced: the bearer handler's signing key.
    /// </summary>
    private static WebApplication Build(
        TestTokenIssuer tokens,
        Dictionary<string, string?> settings,
        Func<WebApplicationOptions, Action<WebApplicationBuilder>, WebApplication> build)
    {
        settings["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json";
        settings["Jwt:Issuer"] = tokens.IssuerName;
        settings["Jwt:RequireHttpsMetadata"] = "false";
        settings["Otel:PrometheusEnabled"] = "false";

        return build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                // MAGERIDE_TEST_LOGS=1 keeps every service's console provider. On a suite with nine
                // of them it is usually the fastest way to see which worker did what.
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(settings);
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                // PostConfigure so this runs after each service's own AddMageRideAuth. Everything
                // else about validation — RS256 only, lifetime, issuer, and fanout-svc's
                // `access_token` hook — is left exactly as the service configured it.
                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });
    }

    // -----------------------------------------------------------------------------------------
    // Rows only iam-svc would write, and the login the RLS assertion needs
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// An <c>iam.users</c> row.
    /// </summary>
    /// <remarks>
    /// iam-svc is not in this fleet (see <see cref="TestTokenIssuer"/>), so the account rows every
    /// foreign key in this suite lands on are written the way that service writes them. Nothing else
    /// about a person is seeded: the organisation seat, the roster row and the assignment all come
    /// from fleet-svc's own routes.
    /// </remarks>
    private async Task<Guid> CreateUserAsync(string role, string? phone = null)
    {
        var id = Guid.CreateVersion7();

        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, @Role);",
            new { Id = id, Phone = phone ?? NextPhone(), Role = role });

        return id;
    }

    /// <summary>
    /// Creates the non-superuser login the cross-org assertion connects as.
    /// </summary>
    /// <remarks>
    /// A role, not a fixture row, so it is created once per container and left in place — roles are
    /// cluster-scoped and <c>CREATE ROLE IF NOT EXISTS</c> does not exist, hence the guard. The same
    /// shape <c>infra/scripts/migrate-verify.sh</c> uses for its own <c>verify_fleet</c>.
    /// </remarks>
    private static async Task EnsureFleetReaderLoginAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            $"""
             DO $$
             BEGIN
               IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{FleetReaderLogin}') THEN
                 CREATE ROLE {FleetReaderLogin} LOGIN PASSWORD '{FleetReaderPassword}';
               END IF;
             END $$;
             GRANT mageride_fleet_reader TO {FleetReaderLogin};
             GRANT CONNECT ON DATABASE {connection.Database} TO {FleetReaderLogin};
             """);
    }

    private const string SessionSelect =
        """
        SELECT id AS Id, vehicle_id AS VehicleId, driver_id AS DriverId, mode AS Mode, state AS State,
               end_reason AS EndReason, started_by AS StartedBy, ended_by AS EndedBy,
               auto_end_at_destination AS AutoEndAtDestination,
               (destination_geo IS NOT NULL) AS DestinationArmed, started_at AS StartedAt,
               ended_at AS EndedAt, last_movement_at AS LastMovementAt, last_position_at AS LastPositionAt,
               offline_since AS OfflineSince
          FROM trips.sessions
        """;

    private static HttpClient NewClient(WebApplication app) =>
        new() { BaseAddress = new Uri(BaseAddressOf(app)), Timeout = TimeSpan.FromSeconds(60) };

    private static string BaseAddressOf(WebApplication app) =>
        app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private static string NextPlate() =>
        "WP-C1-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    private static string NextPhone() =>
        "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// An IMEI no other device in this run presents.
    /// </summary>
    /// <remarks>
    /// Fifteen digits, which is <c>provisioning.yaml</c>'s <c>^\d{15}$</c> — and unique because
    /// <c>ux_tracker_imei_active</c> treats a second live claim on one IMEI as T-08's anti-clone
    /// signal and quarantines <em>both</em> bindings. A counter rather than a random number for
    /// exactly that reason. The Luhn check digit is not enforced by the platform (D6' §4.1's grey
    /// imports fail it), so these do not carry one either.
    /// </remarks>
    private static string NextImei() =>
        "35693" + (Interlocked.Increment(ref _imeiCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture)
        + Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);
}
