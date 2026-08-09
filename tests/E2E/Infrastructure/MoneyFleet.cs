using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Dispatch;
using MageRide.Fare;
using MageRide.Fleet;
using MageRide.Reputation;
using MageRide.Ride;
using MageRide.Shared.Auth;
using MageRide.Shared.Caching;
using MageRide.Shared.Http;
using MageRide.Shared.Primitives;
using MageRide.Subscriptions;
using MageRide.TestKit;
using MageRide.Wallet;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using StackExchange.Redis;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// Every path money takes on this platform, running.
/// </summary>
/// <remarks>
/// <para>
/// Seven services on seven real sockets — wallet-svc, subscription-svc, fare-svc, ride-svc,
/// dispatch-svc, reputation-svc and fleet-svc — against a real Postgres, Redis and Redpanda, each
/// built through its own <c>XApplication.Build</c>, plus one thing that is not a MageRide component
/// at all: a real HTTP endpoint speaking OnePay's create-session shape and calling the webhook back
/// (<see cref="AcquirerGateway"/>). <b>Every background worker is on.</b> A scenario pays, and then
/// waits.
/// </para>
/// <para>
/// <b>Why each service has to be running rather than stood in for.</b>
/// </para>
/// <list type="bullet">
///   <item><b>wallet-svc</b> — the only writer of <c>billing.journal_postings</c> on this platform
///   (D-09). Every assertion this suite makes is ultimately about a row it wrote.</item>
///   <item><b>subscription-svc</b> — D-13's daily fee and Epic 23's subscription money. It holds the
///   Colombo-day rule and the first-trip waiver, and it moves nothing itself: the fee is a real HTTP
///   hop to wallet-svc's ledger seam, which is the seam most worth exercising for real.</item>
///   <item><b>fare-svc</b> — D-10's payment machine, AL-47's attestation pair and E-05's refund. It
///   is also the only caller of <c>POST /v1/internal/wallet/trip-payment</c>.</item>
///   <item><b>ride-svc</b> — there is no fare without a ride, and R-05's terminal is a hop back into
///   it. A payment scenario that invented a <c>fares.ride_payments</c> row would be asserting about
///   a payment for a journey nobody took.</item>
///   <item><b>dispatch-svc</b> — the daily fee counts <em>trips</em>, and a trip is an
///   <c>ACCEPTED</c> row in <c>dispatch.offers</c> written by the real offer loop. Both D-08's
///   pre-dispatch gate and D-13's charge read that number the same way, deliberately, so a suite
///   that wrote the rows by hand would be testing neither.</item>
///   <item><b>reputation-svc</b> — dispatch-svc's block gate is a gRPC call that fails <em>open</em>,
///   so a fleet without it reports every candidate as passing a gate that was never asked.</item>
///   <item><b>fleet-svc</b> — Epic 23's money is a pass-through to the fleet owner, and AL-49 makes
///   the destination a <c>verified</c> payout profile. Nothing else on the platform can produce one:
///   the owner submits it through <c>PUT …/payout-profile</c> and the org's approval verifies it, and
///   both are this service's.</item>
/// </list>
/// <para>
/// <b>What is deliberately absent.</b> notification-svc — every money push on this surface (US-9.9's
/// low-balance warning, AL-47's five-minute nudge, §11.14's "Refund processed") is C051/C052's, and
/// each producer here emits the event or logs the intent with the numbers on it, which is what a
/// scenario asserts. admin-bff — the Finance queue is <c>fares.refunds</c>'s partial index and the
/// refund route is fare-svc's own; the Verification Officer's screens are C062's and this suite calls
/// the <c>/v1/internal/**</c> plane they would call. payout-svc — AL-58's weekly sweep is C061's, and
/// the ledger seam it uses is asserted here as an absent caller rather than driven. ocr-svc, for
/// C121's reason. trip-state-svc, because R-01 draws the line.
/// </para>
/// <para>
/// <b>This fleet resets nothing</b>, which is C122's decision and holds for the same reason: the four
/// fleets in this assembly are never disposed, so a truncate run by whichever collection xUnit starts
/// second would pull the floor out from under services that are still running. Every scenario mints
/// fresh parties and takes its rides from the assembly-wide grid.
/// </para>
/// </remarks>
internal sealed class MoneyFleet : IAsyncDisposable
{
    public const string FareTokenKey = "mageride-c123-e2e-fare-estimate-key";
    public const string RideInternalKey = "mageride-c123-e2e-ride-internal-key";
    public const string DispatchInternalKey = "mageride-c123-e2e-dispatch-internal-key";
    public const string FareInternalKey = "mageride-c123-e2e-fare-internal-key";
    public const string ReputationInternalKey = "mageride-c123-e2e-reputation-key";
    public const string WalletInternalKey = "mageride-c123-e2e-wallet-internal-key";
    public const string SubscriptionInternalKey = "mageride-c123-e2e-subscription-internal-key";
    public const string FleetInternalKey = "mageride-c123-e2e-fleet-internal-key";

    public const string PhoneHashKey = "mageride-c123-e2e-rider-phone-hash-key";
    public const string OtpPepper = "mageride-c123-e2e-package-otp-pepper";

    /// <summary>
    /// The two webhook secrets, and there is no third.
    /// </summary>
    /// <remarks>
    /// AL-05 leaves exactly two rails by which real money reaches a MageRide wallet, and both are
    /// authenticated by an HMAC over the raw body. Unset, every callback on that rail is refused and
    /// a driver who paid at a gateway is never credited — which is why wallet-svc announces it as an
    /// error at start-up and why these are constants here rather than options a scenario could omit.
    /// </remarks>
    public const string OnepayWebhookSecret = "mageride-c123-e2e-onepay-webhook-secret";

    /// <inheritdoc cref="OnepayWebhookSecret"/>
    public const string LankaQrWebhookSecret = "mageride-c123-e2e-lankaqr-webhook-secret";

    /// <summary>Signs the Epic 23 pay-sheet's document links, so one process's link opens on another.</summary>
    public const string FileLinkSigningKey = "mageride-c123-e2e-mode-b-file-link-key";

    /// <summary>AL-15's bank-app deep link. <c>{orderId}</c> and <c>{amountMinor}</c> are the platform's.</summary>
    public const string LankaQrDeepLinkTemplate = "combank://pay?ref={orderId}&amount={amountMinor}";

    /// <summary>C120's widened window, held here for C120's reason.</summary>
    /// <remarks>
    /// D5' §3.5 gives 15 s. Every scenario in this suite has to reach <c>Accepted</c> through a real
    /// offer before it has anything to charge or to pay for, and on a loaded build host that race
    /// against the R-04 backstop is the one failure that is never about money. The value itself is
    /// pinned in dispatch-svc's own suite (C034 <c>OfferExpiryTests</c>).
    /// </remarks>
    public static readonly TimeSpan OfferTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The three-wheeler daily fee, D5' §2.1 verbatim: Rs 100.
    /// </summary>
    /// <remarks>
    /// Asserted rather than read from <c>billing.plans</c>. The rate table is seeded by migration 1901
    /// and is admin-editable, so a scenario that read it would agree with whatever it found — including
    /// with a rate an earlier suite's <c>PUT /v1/admin/fees/rates</c> had changed. D5' §2.1 prints the
    /// seven tiers and this is one of them; if the seed moves, this fails, which is the point.
    /// </remarks>
    public const long ThreeWheelerDailyFeeMinor = 10_000;

    private static readonly SemaphoreSlim SharedGate = new(1, 1);
    private static MoneyFleet? _shared;

    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    private readonly WebApplication[] _services;
    private readonly PostgresFixture _postgres;
    private readonly string _redisConnectionString;

    private MoneyFleet(
        WebApplication[] services,
        WebApplication ride,
        WebApplication dispatch,
        WebApplication fare,
        WebApplication wallet,
        WebApplication subscription,
        WebApplication fleet,
        AcquirerGateway acquirer,
        TestTokenIssuer tokens,
        PostgresFixture postgres,
        RedisFixture redis)
    {
        _services = services;
        _postgres = postgres;
        _redisConnectionString = redis.ConnectionString;

        Tokens = tokens;
        Acquirer = acquirer;
        Journal = new LedgerJournal(postgres);

        RideClient = NewClient(ride);
        DispatchClient = NewClient(dispatch);
        FareClient = NewClient(fare);
        WalletClient = NewClient(wallet);
        SubscriptionClient = NewClient(subscription);
        FleetClient = NewClient(fleet);

        WalletBaseUrl = BaseAddressOf(wallet);
        SubscriptionBaseUrl = BaseAddressOf(subscription);
    }

    /// <summary>ride-svc — the journey a fare is charged for.</summary>
    public HttpClient RideClient { get; }

    /// <summary>dispatch-svc — the offer loop whose <c>ACCEPTED</c> rows are D-13's trip count.</summary>
    public HttpClient DispatchClient { get; }

    /// <summary>fare-svc — D-10's machine, AL-47's attestation pair, E-05's refund.</summary>
    public HttpClient FareClient { get; }

    /// <summary>wallet-svc — the ledger, the two top-up rails, the vouchers, the transfers.</summary>
    public HttpClient WalletClient { get; }

    /// <summary>subscription-svc — D-13's daily fee and Epic 23's subscription money.</summary>
    public HttpClient SubscriptionClient { get; }

    /// <summary>fleet-svc — the organisation, its verified payout profile, its Mode B vehicles.</summary>
    public HttpClient FleetClient { get; }

    /// <summary>Where a signed callback is delivered — the acquirer needs an absolute address.</summary>
    public string WalletBaseUrl { get; }

    /// <inheritdoc cref="WalletBaseUrl"/>
    public string SubscriptionBaseUrl { get; }

    public TestTokenIssuer Tokens { get; }

    /// <summary>The party on the far side of the platform's egress, and the one that calls back.</summary>
    public AcquirerGateway Acquirer { get; }

    /// <summary>Prints every party's statement when an assertion fails.</summary>
    public LedgerJournal Journal { get; }

    // -----------------------------------------------------------------------------------------
    // Lifetime
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The fleet, started at most once per test run and never disposed.
    /// </summary>
    /// <remarks>
    /// Never disposed for C120's reason: the services live as long as the test host, and the
    /// containers are the TestKit's — the Testcontainers reaper removes them when the process ends.
    /// </remarks>
    public static async Task<MoneyFleet> SharedAsync(
        PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(redpanda);

        if (_shared is not null)
        {
            return _shared;
        }

        await SharedGate.WaitAsync();

        try
        {
            return _shared ??= await StartAsync(postgres, redis, redpanda);
        }
        finally
        {
            SharedGate.Release();
        }
    }

    private static async Task<MoneyFleet> StartAsync(
        PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    {
        postgres.RequireAvailable();
        redis.RequireAvailable();
        redpanda.RequireAvailable();

        await postgres.EnsureMigratedAsync();

        // The topics this plane uses. `wallet.events` is wallet-svc's outbox destination — the
        // `wallet.debited` event that invalidates D-08's cache — and it is created up front for the
        // reason the other four are: a producer that finds no topic retries through the first
        // scenario's timeout.
        foreach (var topic in new[]
                 {
                     "ride.events", "dispatch.events", "registry.events", "wallet.events",
                     "subscription.events", "audit.events",
                 })
        {
            await redpanda.CreateTopicAsync(topic);
        }

        var tokens = new TestTokenIssuer();
        var acquirer = await AcquirerGateway.StartAsync();
        var documentRoot = Path.Combine(Path.GetTempPath(), $"mageride-c123-{Guid.NewGuid():N}");

        Directory.CreateDirectory(documentRoot);

        // Start order is dependency order. wallet-svc first: subscription-svc and fare-svc are both
        // configured with its address, and a service that composed against an unset one would have
        // its money routes unmapped for the whole run.
        var wallet = BuildWallet(postgres, redis, redpanda, tokens, acquirer.BaseAddress);
        await wallet.StartAsync();

        var ride = BuildRide(postgres, redpanda, tokens);
        await ride.StartAsync();

        var reputation = BuildReputation(postgres, redis, redpanda, tokens);
        await reputation.StartAsync();

        var dispatch = BuildDispatch(
            postgres, redis, redpanda, tokens, BaseAddressOf(ride), GrpcAddressOf(reputation));
        await dispatch.StartAsync();

        var fare = BuildFare(
            postgres, tokens, BaseAddressOf(ride), BaseAddressOf(dispatch), BaseAddressOf(wallet));
        await fare.StartAsync();

        var subscription = BuildSubscription(
            postgres, redpanda, tokens, BaseAddressOf(wallet), documentRoot);
        await subscription.StartAsync();

        var fleet = BuildFleet(postgres, tokens, BaseAddressOf(subscription), documentRoot);
        await fleet.StartAsync();

        return new MoneyFleet(
            [fleet, subscription, fare, dispatch, reputation, ride, wallet],
            ride,
            dispatch,
            fare,
            wallet,
            subscription,
            fleet,
            acquirer,
            tokens,
            postgres,
            redis);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in new[]
                 { RideClient, DispatchClient, FareClient, WalletClient, SubscriptionClient, FleetClient })
        {
            client.Dispose();
        }

        // `_services` is already in reverse start order, so nothing is torn down under an in-flight
        // call from a service that is still running.
        foreach (var app in _services)
        {
            await app.StopAsync(TimeSpan.FromSeconds(10));
            await app.DisposeAsync();
        }

        await Acquirer.DisposeAsync();
    }

    // -----------------------------------------------------------------------------------------
    // The parties. iam-svc and registry-svc are not in this fleet, so their rows are seeded —
    // exactly as C120 and C122 seed theirs.
    // -----------------------------------------------------------------------------------------

    public async Task<Passenger> CreatePassengerAsync()
    {
        var id = Guid.NewGuid();
        var phone = NextPhone();

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, 'passenger');",
            new { Id = id, Phone = phone });

        return new Passenger(id, phone, Tokens.Passenger(id));
    }

    /// <summary>
    /// A driver with one APPROVED Mode C vehicle and an <b>empty</b> wallet.
    /// </summary>
    /// <remarks>
    /// <b>The one place this suite deliberately departs from C120's seeding</b>, and the reason is
    /// the whole component: C120 seeds Rs 5,000 so that the D-08 gate never refuses a driver over a
    /// balance rule it did not come to assert, whereas here the balance <em>is</em> the assertion.
    /// A driver starts with nothing and every rupee they hold arrives through a rail the platform
    /// actually has — an OnePay or LankaQR top-up settled by a signed callback, a bulk voucher, or
    /// another driver's transfer. Seeding a balance would make "the top-up credited Rs 2,000" true
    /// of a number nobody paid.
    /// </remarks>
    public async Task<Driver> CreateDriverAsync(string vehicleType = "three_wheeler")
    {
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var plate = NextPlate();
        var phone = NextPhone();

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role) VALUES (@DriverId, @Phone, 'driver');
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            VALUES (@VehicleId, @DriverId, @Plate, @VehicleType, 'C', 'APPROVED', 'E2E Driver');
            """,
            new
            {
                DriverId = driverId,
                VehicleId = vehicleId,
                Phone = phone,
                Plate = plate,
                VehicleType = vehicleType,
            });

        return new Driver(driverId, vehicleId, plate, phone, Tokens.Driver(driverId));
    }

    public async Task<Driver> CreateOnlineDriverAsync(GeoPoint at, string vehicleType = "three_wheeler")
    {
        var driver = await CreateDriverAsync(vehicleType);
        await GoOnlineAsync(driver, at);

        return driver;
    }

    /// <summary>
    /// A second APPROVED vehicle for a driver who already has one (US-9.6, D-03).
    /// </summary>
    /// <remarks>
    /// One live vehicle at a time, but a driver may own several and switch between them — which is
    /// the case D5' §2.2's per-<em>driver</em> trip count exists for: the waiver is one free trip per
    /// person per day, so counting per vehicle would hand a driver a fresh one every time they
    /// changed three-wheeler. Returns the same driver with the new vehicle selected, so a scenario
    /// goes on standby and charges against it exactly as the first.
    /// </remarks>
    public async Task<Driver> WithAnotherVehicleAsync(Driver driver, string vehicleType = "three_wheeler")
    {
        ArgumentNullException.ThrowIfNull(driver);

        var vehicleId = Guid.NewGuid();
        var plate = NextPlate();

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            VALUES (@VehicleId, @DriverId, @Plate, @VehicleType, 'C', 'APPROVED', 'E2E Driver');
            """,
            new
            {
                VehicleId = vehicleId,
                DriverId = driver.DriverId,
                Plate = plate,
                VehicleType = vehicleType,
            });

        return driver with { VehicleId = vehicleId, Plate = plate };
    }

    public async Task GoOnlineAsync(Driver driver, GeoPoint at)
    {
        ArgumentNullException.ThrowIfNull(driver);

        using var response = await PostAsync(
            DispatchClient,
            "/v1/standby/online",
            new
            {
                vehicleId = driver.VehicleId.ToString(),
                position = new { lat = at.Latitude, lng = at.Longitude },
            },
            driver.Bearer);

        await AssertOkAsync(response, $"driver {driver.DriverId} going on standby");
    }

    /// <summary>
    /// Puts an opening balance on a passenger's wallet. <b>This is the one write no route on this
    /// platform can make</b>, and it is a recorded gap rather than a convenience.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AL-57 is the change that created the passenger wallet: OnePay has one merchant account per
    /// merchant, so a card <em>fare</em> could only ever land in MageRide's own account, and card
    /// acceptance moved one step earlier — "a passenger tops up their MageRide wallet (OnePay,
    /// MageRide legitimately the payee) and pays the ride with the new method <c>wallet</c>".
    /// <b>The top-up half of that sentence does not exist.</b> Both rails are
    /// <c>.RequireMageRideRole(Driver, FleetOwner)</c>, and the internal credit seam resolves a
    /// <em>driver</em> account (<c>EnsureDriverAccountAsync</c>) — so a passenger id posted there
    /// opens a second, unrelated account of the wrong owner type. Nothing else on the platform
    /// touches an <c>owner_type='passenger'</c> row except the wallet fare that spends it.
    /// </para>
    /// <para>
    /// So the AL-57 rail's success path is unreachable, and this suite has two duties rather than
    /// one: <see cref="MageRide.E2E.Scenarios.RidePaymentScenario"/> asserts the gap <em>as</em> a
    /// gap — a passenger is refused <c>402</c> and every funding route refuses them — and this
    /// method opens the balance so the rest of the rail (the two-leg <c>trip_payment</c> entry,
    /// R-05's earning, E-05's refund) can be driven at all. C121's
    /// <c>MarkVehicleApprovedAsync</c> is the same shape and is there for the same reason.
    /// </para>
    /// <para>
    /// <b>It writes what a top-up callback would have written, and nothing else</b> — one balanced
    /// <c>topup</c> entry against the platform account, both account balances, the
    /// <c>billing.wallets</c> mirror and the history line, in one transaction. Every ledger
    /// invariant this suite asserts therefore stays true of it: Σ postings is zero, and each
    /// balance still equals the sum of its own legs. A bare
    /// <c>UPDATE billing.accounts SET balance_minor</c> would have been three lines and would have
    /// made the fence lie.
    /// </para>
    /// </remarks>
    public async Task OpenPassengerBalanceAsync(Passenger passenger, long amountMinor)
    {
        ArgumentNullException.ThrowIfNull(passenger);

        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO billing.accounts (owner_type, owner_id, currency)
            VALUES ('passenger', @PassengerId, 'LKR')
                ON CONFLICT (owner_type, owner_id, currency) WHERE owner_id IS NOT NULL DO NOTHING;

            WITH entry AS (
              INSERT INTO billing.journal_entries (kind, idempotency_key, description)
              VALUES ('topup', @Key, 'Opening passenger balance — no route on this platform can do this (AL-57 gap)')
              RETURNING id),
            party AS (
              SELECT id, 'passenger' AS side FROM billing.accounts
               WHERE owner_type = 'passenger' AND owner_id = @PassengerId AND currency = 'LKR'
               UNION ALL
              SELECT id, 'platform' FROM billing.accounts
               WHERE owner_type = 'platform' AND owner_id IS NULL AND currency = 'LKR'),
            legs AS (
              INSERT INTO billing.journal_postings (entry_id, account_id, amount_minor)
              SELECT entry.id, party.id,
                     CASE WHEN party.side = 'passenger' THEN @AmountMinor ELSE -@AmountMinor END
                FROM entry CROSS JOIN party
              RETURNING account_id, amount_minor, entry_id),
            balances AS (
              UPDATE billing.accounts a
                 SET balance_minor = a.balance_minor + legs.amount_minor
                FROM legs WHERE a.id = legs.account_id
              RETURNING a.id, a.balance_minor, legs.amount_minor, legs.entry_id),
            mirror AS (
              INSERT INTO billing.wallets (account_id, balance_minor)
              SELECT id, balance_minor FROM balances
               WHERE id = (SELECT id FROM billing.accounts
                            WHERE owner_type = 'passenger' AND owner_id = @PassengerId AND currency = 'LKR')
                  ON CONFLICT (account_id) DO UPDATE SET balance_minor = EXCLUDED.balance_minor
              RETURNING account_id)
            INSERT INTO billing.wallet_transactions
              (account_id, entry_id, kind, amount_minor, balance_after_minor, description)
            SELECT b.id, b.entry_id, 'topup', b.amount_minor, b.balance_minor, 'Opening passenger balance'
              FROM balances b WHERE b.id IN (SELECT account_id FROM mirror);
            """,
            new
            {
                PassengerId = passenger.Id,
                AmountMinor = amountMinor,
                Key = $"topup:e2e-passenger-opening-{Guid.NewGuid():N}",
            },
            transaction);

        await transaction.CommitAsync();
    }

    /// <summary>A Finance Officer — the only actor E-05's refund route admits (AL-02).</summary>
    public async Task<FinanceOfficer> CreateFinanceOfficerAsync()
    {
        var id = Guid.NewGuid();

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, 'finance_officer');",
            new { Id = id, Phone = NextPhone() });

        return new FinanceOfficer(id, Tokens.Issue(id, MageRideRoles.FinanceOfficer, MageRideApps.Admin));
    }

    /// <summary>
    /// An organisation with a verified payout profile and one Paid Mode B vehicle, built entirely
    /// through fleet-svc's own routes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four real calls in the order BR-31.1 forces, and the order is the interesting part.
    /// </para>
    /// <list type="number">
    ///   <item><c>POST /v1/fleets</c> — the org and its Owner's seat, PENDING.</item>
    ///   <item><c>PUT …/payout-profile</c> — the bank details, <c>pending_verification</c>. Allowed
    ///   while the org is PENDING <em>on purpose</em>: the payout documents are part of what the
    ///   officer reads before approving, so gating this would mean approving an organisation before
    ///   seeing the evidence you approve it on.</item>
    ///   <item><c>POST /v1/internal/fleets/{id}/approve</c> — the Verification Officer's decision,
    ///   made on the plane admin-bff would call it on. <b>This is the only thing on the platform
    ///   that verifies a payout profile</b>, and until it has happened a Paid vehicle collects
    ///   nothing.</item>
    ///   <item><c>POST …/vehicles</c> with <c>modeBBilling: paid</c> — which is refused with
    ///   <c>409 payout-profile-not-verified</c> if step 3 has not happened, and
    ///   <see cref="MageRide.E2E.Scenarios.ModeBSubscriptionPaymentScenario"/> asserts exactly
    ///   that.</item>
    /// </list>
    /// </remarks>
    public async Task<PaidFleetOrg> CreateFleetOrgAsync(long monthlyFareMinor = 250_000)
    {
        var pending = await CreateUnapprovedFleetOrgAsync();
        var (fleetId, ownerId, ownerBearer) = (pending.FleetId, pending.OwnerId, pending.OwnerBearer);

        // The bank-app LankaQR image, uploaded *before* the officer decides. BR-31.1 makes any edit
        // to a verified profile fork a new pending version — "replacing the bank statement behind a
        // verified profile is exactly the change an officer would want to see again" — so uploading
        // afterwards would leave the org collecting against a snapshot with no QR on it, which is
        // the pay sheet AL-49 says the passenger must be shown.
        await UploadPayoutDocumentAsync(fleetId, ownerBearer, "lankaqr_code", "owner-lankaqr.png");
        await UploadPayoutDocumentAsync(fleetId, ownerBearer, "bank_statement", "statement.pdf");

        await ApproveFleetAsync(fleetId);

        var profileId = await ReadVerifiedPayoutProfileIdAsync(fleetId);

        Guid vehicleId;
        string plate;

        using (var vehicle = await PostAsync(
            FleetClient,
            $"/v1/fleets/{fleetId}/vehicles",
            new
            {
                registrationNumber = plate = NextPlate(),
                vehicleType = "van",
                mode = "B",
                modeBBilling = "paid",
                defaultMonthlyFareMinor = monthlyFareMinor,
            },
            ownerBearer))
        {
            await AssertStatusAsync(vehicle, HttpStatusCode.Created, $"onboarding a Mode B vehicle for fleet {fleetId}");
            vehicleId = (await ReadJsonAsync(vehicle)).GetProperty("vehicleId").GetGuid();
        }

        return new PaidFleetOrg(fleetId, ownerId, ownerBearer, vehicleId, plate, profileId);
    }

    /// <summary>
    /// The organisation and its submitted-but-unverified bank details, and no further.
    /// </summary>
    /// <remarks>
    /// The state BR-31.1's gate is about: everything an owner can do on their own is done, and the
    /// one thing they cannot do for themselves — a Verification Officer approving the organisation,
    /// which is what verifies the payout profile — has not happened. A Paid vehicle is refused from
    /// here, and a Free one is not.
    /// </remarks>
    public async Task<PendingFleetOrg> CreateUnapprovedFleetOrgAsync(bool withPayoutProfile = true)
    {
        var ownerId = Guid.NewGuid();

        await using (var connection = await OpenAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, 'fleet_owner');",
                new { Id = ownerId, Phone = NextPhone() });
        }

        Guid fleetId;

        using (var registered = await PostAsync(
            FleetClient,
            "/v1/fleets",
            new
            {
                name = $"E2E Transport {ownerId:N}"[..28],
                registrationNo = $"PV{Random.Shared.Next(100_000, 999_999)}",
                contactPhone = NextPhone(),
                contactEmail = $"owner-{ownerId:N}@mageride.test",
                address = "1 Galle Road, Colombo 03",
            },
            Tokens.FleetOwner(ownerId)))
        {
            await AssertStatusAsync(registered, HttpStatusCode.Created, "registering a fleet organisation");
            fleetId = (await ReadJsonAsync(registered)).GetProperty("fleetId").GetGuid();
        }

        // From here the Owner's bearer carries the org claims iam-svc would have minted for it
        // (AL-03) — `FleetAccessFilter` reads `iam.fleet_members` for the org in the path anyway, and
        // the claim exists to get past deny-by-default authorization.
        var ownerBearer = Tokens.FleetMember(ownerId, fleetId, FleetRoles.Owner);

        // Allowed while the organisation is still PENDING, and deliberately: the payout documents
        // are part of what the Verification Officer reads *before* approving, so gating this would
        // mean approving an organisation before seeing the evidence you approve it on.
        if (withPayoutProfile)
        {
            using var profile = await PutAsync(
                FleetClient,
                $"/v1/fleets/{fleetId}/payout-profile",
                new
                {
                    bank = "Commercial Bank of Ceylon",
                    branch = "Kollupitiya",
                    accountNo = Random.Shared
                        .NextInt64(1_000_000_000, 9_999_999_999)
                        .ToString(CultureInfo.InvariantCulture),
                    accountHolderName = "E2E Transport (Pvt) Ltd",
                },
                ownerBearer);

            await AssertOkAsync(profile, $"submitting fleet {fleetId}'s payout profile");
        }

        return new PendingFleetOrg(fleetId, ownerId, ownerBearer);
    }

    /// <summary>
    /// The Verification Officer's decision, on the internal plane admin-bff would call it on.
    /// </summary>
    /// <remarks>
    /// Two things at once, and AL-49's whole point is the second: it approves the organisation
    /// (US-13.A7's gate on onboarding) <b>and</b> verifies whatever payout profile is pending. An
    /// organisation approved before its owner submitted any bank details is APPROVED with nothing
    /// verified — which is the ordinary way a Paid classification comes to be refused, and the state
    /// <see cref="MageRide.E2E.Scenarios.ModeBSubscriptionPaymentScenario"/> builds to reach it.
    /// </remarks>
    public async Task ApproveFleetAsync(Guid fleetId)
    {
        // A real officer, because `registry.fleet_payout_profiles.verified_by` is a foreign key into
        // `iam.users` — the decision has to name somebody who exists, which is what makes "who
        // approved these bank details" answerable a year later.
        var officerId = Guid.NewGuid();

        await using (var connection = await OpenAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, 'verification_officer');",
                new { Id = officerId, Phone = NextPhone() });
        }

        using var approved = await PostInternalAsync(
            FleetClient,
            $"/v1/internal/fleets/{fleetId}/approve",
            new { officerId = officerId.ToString() },
            FleetInternalKey);

        await AssertOkAsync(approved, $"approving fleet {fleetId}");
    }

    /// <summary>Another Mode B vehicle on an existing organisation's roster, Paid or Free (AL-24).</summary>
    public async Task<Guid> AddModeBVehicleAsync(
        PaidFleetOrg org, string modeBBilling, long monthlyFareMinor = 250_000)
    {
        ArgumentNullException.ThrowIfNull(org);

        var paid = string.Equals(modeBBilling, "paid", StringComparison.Ordinal);

        using var vehicle = await PostAsync(
            FleetClient,
            $"/v1/fleets/{org.FleetId}/vehicles",
            new
            {
                registrationNumber = NextPlate(),
                vehicleType = "van",
                mode = "B",
                modeBBilling,

                // A Free vehicle carries no fare at all — "Free, Rs 2,500" is not a state SCR-FP-004
                // can render, and a stale default is a number subscription-svc could pick up on a
                // switch back.
                defaultMonthlyFareMinor = paid ? monthlyFareMinor : (long?)null,
            },
            org.OwnerBearer);

        await AssertStatusAsync(
            vehicle, HttpStatusCode.Created, $"onboarding a {modeBBilling} Mode B vehicle for fleet {org.FleetId}");

        return (await ReadJsonAsync(vehicle)).GetProperty("vehicleId").GetGuid();
    }

    /// <summary>How many postings exist on the whole platform — the count a pass-through must not move.</summary>
    public async Task<int> CountPostingsAsync()
    {
        await using var connection = await OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM billing.journal_postings;");
    }

    /// <summary>A plate no other vehicle in this run holds.</summary>
    public static string NextPlate() =>
        "WP-M3-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>
    /// One AL-49 payout document, uploaded the way the Fleet Portal uploads it.
    /// </summary>
    /// <remarks>
    /// Multipart with <c>kind</c> and <c>file</c>, from a bearer-authenticated fetch rather than a
    /// browser form — which is why the route disables antiforgery. The bytes are not a real PNG and
    /// do not need to be: nothing on this path decodes them, and what a scenario asserts about is the
    /// <c>docs.uploads</c> pointer the pay sheet's signed link resolves to.
    /// </remarks>
    public async Task UploadPayoutDocumentAsync(Guid fleetId, string ownerBearer, string kind, string fileName)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(kind), "kind" },
        };

        var bytes = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. "e2e"u8]);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(bytes, "file", fileName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/fleets/{fleetId}/payout-profile/documents")
        {
            Content = form,
        };

        request.Headers.Add(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerBearer);

        using var response = await FleetClient.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertStatusAsync(response, HttpStatusCode.Created, $"uploading fleet {fleetId}'s {kind}");
    }

    /// <summary>
    /// A passenger's transfer slip, uploaded the way the Passenger App uploads it (US-23.4).
    /// </summary>
    public async Task<HttpResponseMessage> UploadTransferSlipAsync(Guid paymentId, Passenger passenger)
    {
        ArgumentNullException.ThrowIfNull(passenger);

        using var form = new MultipartFormDataContent();

        var bytes = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. "slip"u8]);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(bytes, "file", "transfer-slip.png");

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/mode-b/payments/{paymentId}/transfer-slip")
        {
            Content = form,
        };

        request.Headers.Add(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", passenger.Bearer);

        return await SubscriptionClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A passenger asking to track a Mode B vehicle, and the owner accepting (US-23.1, AL-23).
    /// </summary>
    /// <remarks>
    /// Per vehicle, never account-global: a driver with three vehicles works three queues, and
    /// <c>ux_grant_active</c> is keyed <c>(vehicle_id, passenger_id)</c>. Accepting grants tracking
    /// access <em>and</em> starts the subscription — one transaction, one <c>share.granted</c>.
    /// </remarks>
    public async Task<ModeBSubscription> SubscribeAsync(PaidFleetOrg org, Passenger passenger)
    {
        ArgumentNullException.ThrowIfNull(org);
        ArgumentNullException.ThrowIfNull(passenger);

        Guid requestId;

        using (var requested = await PostAsync(
            SubscriptionClient,
            $"/v1/mode-b/{org.VehicleId}/access-requests",
            new { note = "Morning school run" },
            passenger.Bearer))
        {
            await AssertStatusAsync(requested, HttpStatusCode.Created, "requesting Mode B access");
            requestId = (await ReadJsonAsync(requested)).GetProperty("requestId").GetGuid();
        }

        using var accepted = await PostAsync(
            SubscriptionClient, $"/v1/mode-b/access-requests/{requestId}/accept", new { }, org.OwnerBearer);

        await AssertOkAsync(accepted, $"the owner accepting request {requestId}");

        var body = await ReadJsonAsync(accepted);

        var subscription = new ModeBSubscription(
            body.GetProperty("subscriptionId").GetGuid(),
            body.GetProperty("grantId").GetGuid(),
            passenger,
            org,
            await ReadSubscriptionFareAsync(body.GetProperty("subscriptionId").GetGuid()));

        return subscription;
    }

    /// <summary>The monthly fare a subscription actually carries, after the vehicle's default.</summary>
    public async Task<long> ReadSubscriptionFareAsync(Guid subscriptionId)
    {
        await using var connection = await OpenAsync();

        return await connection.ExecuteScalarAsync<long>(
            "SELECT COALESCE(monthly_fare_minor, 0)::bigint FROM subscription.subscriptions WHERE id = @Id;",
            new { Id = subscriptionId });
    }

    /// <summary>The next-due date the billing cycle is standing on (US-23.8).</summary>
    public async Task<DateOnly?> ReadNextDueAsync(Guid subscriptionId)
    {
        await using var connection = await OpenAsync();

        return await connection.ExecuteScalarAsync<DateOnly?>(
            "SELECT next_due FROM subscription.subscriptions WHERE id = @Id;",
            new { Id = subscriptionId });
    }

    /// <summary>US-23.3 — the passenger opens a pay sheet for a month.</summary>
    public Task<HttpResponseMessage> PaySubscriptionAsync(
        ModeBSubscription subscription, string method)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        return PostAsync(
            SubscriptionClient,
            $"/v1/mode-b/subscriptions/{subscription.SubscriptionId}/pay",
            new { method },
            subscription.Passenger.Bearer);
    }

    /// <summary>Where a Mode B provider confirmation is delivered (R-19, D6' §7.1/§7.2).</summary>
    public string SubscriptionCallbackUrl(string method) =>
        SubscriptionBaseUrl.TrimEnd('/')
        + (method == "onepay" ? "/v1/mode-b/pay/onepay/webhook" : "/v1/mode-b/pay/lankaqr/confirm");

    /// <summary>The org's verified profile, which <c>ux_payout_profile_verified</c> makes singular.</summary>
    public async Task<Guid> ReadVerifiedPayoutProfileIdAsync(Guid fleetId)
    {
        await using var connection = await OpenAsync();

        var id = await connection.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM registry.fleet_payout_profiles WHERE fleet_id = @FleetId AND status = 'verified';",
            new { FleetId = fleetId });

        Assert.True(
            id is not null,
            $"Fleet {fleetId} has no verified payout profile, so nothing it operates can collect a "
            + "subscription (AL-49). Approving the organisation is what verifies it.");

        return id!.Value;
    }

    // -----------------------------------------------------------------------------------------
    // The ride a fare is charged for, driven through the surfaces an app has
    // -----------------------------------------------------------------------------------------

    /// <summary>A quote from the real fare-svc, signed with the key ride-svc verifies.</summary>
    public async Task<(long AmountMinor, string Token)> QuoteAsync(
        Passenger passenger, GeoPoint from, GeoPoint to, string vehicleType = "three_wheeler")
    {
        ArgumentNullException.ThrowIfNull(passenger);

        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"/v1/fare/estimate?fromLat={from.Latitude}&fromLng={from.Longitude}"
            + $"&toLat={to.Latitude}&toLng={to.Longitude}&vehicleType={vehicleType}");

        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", passenger.Bearer);

        using var response = await FareClient.SendAsync(request, TestContext.Current.CancellationToken);
        await AssertOkAsync(response, "fare estimate");

        var body = await ReadJsonAsync(response);

        return (body.GetProperty("amountMinor").GetInt64(), body.GetProperty("fareEstimateToken").GetString()!);
    }

    /// <summary>
    /// Books a ride and lets the real dispatch loop find it a driver, leaving it accepted.
    /// </summary>
    /// <remarks>
    /// Every state on the way is reached the way production reaches it: the driver goes on standby
    /// through dispatch-svc, the passenger books through ride-svc quoted by fare-svc,
    /// <c>ride.requested</c> crosses Redpanda, dispatch-svc builds candidates and calls ride-svc's
    /// internal plane, and the driver accepts through ride-svc's own route. The <c>ACCEPTED</c>
    /// <c>dispatch.offers</c> row this leaves behind is what D-13 counts as a trip.
    /// </remarks>
    public async Task<LiveRide> AcceptedRideAsync(
        Passenger passenger, Driver driver, string paymentMethod = "cash")
    {
        ArgumentNullException.ThrowIfNull(driver);

        var requested = await RequestRideAsync(passenger, driver, paymentMethod);
        var rideId = requested.RideId;
        long version;

        var offer = await WaitForOfferAsync(rideId, driver.DriverId);
        var offered = await ReadRideAsync(rideId);

        using (var accepted = await PostAsync(
            RideClient,
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.Id.ToString(), version = offered.Version },
            driver.Bearer))
        {
            await AssertOkAsync(accepted, $"driver {driver.DriverId} accepting ride {rideId}");
            version = (await ReadJsonAsync(accepted)).GetProperty("version").GetInt64();
        }

        // The offer does not become ACCEPTED when ride-svc answers 200. dispatch-svc marks it from
        // its own `ride.events` consumer, so between the accept and the row there is a commit, an
        // outbox dispatch, Redpanda and a consumer — and `dispatch.offers.status = 'ACCEPTED'` with
        // its `responded_at` is *the* number D-13 counts as a trip and D-08's gate predicts against.
        // A scenario that charged a fee here would find `tripsToday = 0` and be told the trip was
        // free, which is the right answer to the wrong question.
        await WaitForAcceptedOfferAsync(rideId, driver.DriverId);

        return new LiveRide(rideId, passenger, driver, version, requested.Pickup, requested.Dropoff);
    }

    /// <summary>
    /// Puts the driver on standby and books a ride at their pickup, and stops there.
    /// </summary>
    /// <remarks>
    /// The half <see cref="AcceptedRideAsync"/> shares with the scenarios that are about a ride
    /// <em>not</em> being offered. dispatch-svc's D-08 wallet gate withholds an offer from a driver
    /// who has already taken a trip this Colombo day and could not pay the fee for the next one, and
    /// the ride then rests in <c>Matching</c> until the US-6A.11 deadline — so "the platform refused
    /// to dispatch" is a state to wait for rather than a call to make.
    /// </remarks>
    public async Task<LiveRide> RequestRideAsync(
        Passenger passenger, Driver driver, string paymentMethod = "cash")
    {
        ArgumentNullException.ThrowIfNull(passenger);
        ArgumentNullException.ThrowIfNull(driver);

        var (pickup, dropoff) = ModeCFleet.NextPlaces();

        await GoOnlineAsync(driver, new GeoPoint(pickup.Latitude + 0.0006, pickup.Longitude));

        var (_, token) = await QuoteAsync(passenger, pickup, dropoff);

        using var booked = await PostAsync(
            RideClient,
            "/v1/rides/request",
            new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                kind = "passenger",
                pickup = new { lat = pickup.Latitude, lng = pickup.Longitude, address = "E2E pickup" },
                dropoff = new { lat = dropoff.Latitude, lng = dropoff.Longitude, address = "E2E dropoff" },
                vehicleType = "three_wheeler",
                fareEstimateToken = token,
                paymentMethod,
            },
            passenger.Bearer);

        await AssertStatusAsync(booked, HttpStatusCode.Accepted, "booking a ride");

        var body = await ReadJsonAsync(booked);

        return new LiveRide(
            body.GetProperty("rideId").GetGuid(),
            passenger,
            driver,
            body.GetProperty("version").GetInt64(),
            pickup,
            dropoff);
    }

    /// <summary>Waits for the ride to reach a state, and says what it was still in if it does not.</summary>
    public async Task WaitForRideStateAsync(Guid rideId, string state, TimeSpan? within = null)
    {
        var seen = string.Empty;

        await UntilAsync(
            async () => (seen = (await ReadRideAsync(rideId)).State) == state,
            $"ride {rideId} reaching {state} (it was {seen})",
            within);
    }

    /// <summary>Whether dispatch-svc has offered this ride to anybody at all.</summary>
    public async Task<bool> HasOfferAsync(Guid rideId)
    {
        await using var connection = await OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM dispatch.offers WHERE ride_id = @RideId;",
            new { RideId = rideId }) > 0;
    }

    /// <summary>Waits until dispatch-svc has recorded the accept — the trip D-13 counts.</summary>
    public async Task WaitForAcceptedOfferAsync(Guid rideId, Guid driverId)
    {
        await UntilAsync(
            async () =>
            {
                await using var connection = await OpenAsync();

                return await connection.ExecuteScalarAsync<int>(
                    """
                    SELECT count(*)::int FROM dispatch.offers
                     WHERE ride_id = @RideId AND driver_id = @DriverId
                       AND status = 'ACCEPTED' AND responded_at IS NOT NULL;
                    """,
                    new { RideId = rideId, DriverId = driverId }) == 1;
            },
            $"dispatch-svc recording driver {driverId}'s accept of ride {rideId} as an ACCEPTED offer");
    }

    /// <summary>
    /// The driver's trips on a Colombo day, counted the way both callers count them.
    /// </summary>
    /// <remarks>
    /// dispatch-svc's D-08 pre-dispatch gate and subscription-svc's D-13 charge read this number
    /// with the same predicate deliberately — "one number, read the same way in both services" — so
    /// a scenario that wants to say "this driver has had two trips today" has to ask it the same way
    /// or it is asserting about a third number nobody uses.
    /// </remarks>
    public async Task<int> TripsTodayAsync(Guid driverId, DateOnly feeDate)
    {
        await using var connection = await OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int FROM dispatch.offers
             WHERE driver_id = @DriverId AND status = 'ACCEPTED'
               AND (responded_at AT TIME ZONE 'Asia/Colombo')::date = @FeeDate;
            """,
            new { DriverId = driverId, FeeDate = feeDate });
    }

    /// <summary>Drives an accepted ride to <c>PaymentPending</c> and prices it.</summary>
    /// <remarks>
    /// <b><c>PriceAsync</c> is called by this suite because nothing in the platform calls it</b> —
    /// C120's finding, unchanged and now load-bearing here. ride-svc's <c>CompleteAsync</c> says at
    /// the line that fare-svc's <c>POST /v1/fare/calculate</c> "is C049/C050; until then this event
    /// is the entire hand-off", and nobody wired the caller — so every completed ride stops at
    /// <c>PaymentPending</c> with no <c>fares.ride_payments</c> row to pay. Standing in for the
    /// missing hop through the same internal route ride-svc would use is the only way to reach a
    /// payment at all; the gap is re-raised in the C123 handoff.
    /// </remarks>
    public async Task<(LiveRide Ride, FinalFare Fare)> CompleteAndPriceAsync(LiveRide ride)
    {
        ArgumentNullException.ThrowIfNull(ride);

        foreach (var command in new[] { "arrive", "start", "complete" })
        {
            using var response = await PostAsync(
                RideClient, $"/v1/rides/{ride.RideId}/{command}", new { version = ride.Version }, ride.Driver.Bearer);

            await AssertOkAsync(response, $"{command} on ride {ride.RideId}");
            ride = ride with { Version = (await ReadJsonAsync(response)).GetProperty("version").GetInt64() };
        }

        using var priced = await PostInternalAsync(
            FareClient, "/v1/fare/calculate", new { rideId = ride.RideId.ToString() }, FareInternalKey);

        await AssertOkAsync(priced, $"pricing ride {ride.RideId}");

        var body = await ReadJsonAsync(priced);

        return (ride, new FinalFare(
            body.GetProperty("paymentId").GetGuid(),
            body.GetProperty("amountMinor").GetInt64(),
            body.GetProperty("amountMinor").GetInt64()));
    }

    /// <summary>A ride taken from nothing to a priced, payable fare — the start of every payment scenario.</summary>
    public async Task<(LiveRide Ride, FinalFare Fare)> PayableRideAsync(
        Passenger passenger, Driver driver, string paymentMethod = "cash") =>
        await CompleteAndPriceAsync(await AcceptedRideAsync(passenger, driver, paymentMethod));

    /// <summary>
    /// One trip in a driver's working day: a fresh passenger, an accepted ride, nothing settled yet.
    /// </summary>
    /// <remarks>
    /// <b>A fresh passenger every time, because a passenger may hold one live ride</b> (ADD Appendix
    /// B.2 invariant 1) — so a driver's second trip of the day is somebody else's first. That is also
    /// what the invariant means in production: the same person does not book two cars at once.
    /// </remarks>
    public async Task<LiveRide> StartTripAsync(Driver driver, string paymentMethod = "cash") =>
        await AcceptedRideAsync(await CreatePassengerAsync(), driver, paymentMethod);

    /// <summary>
    /// Finishes a trip in cash and waits until the driver is free to take another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The wait is the interesting half, and where it stops is the whole of it.</b> ADD §11.12
    /// gives dispatch-svc three duties on a terminal event and <c>ReturnToPoolAsync</c> does them in
    /// order: release the accepted offer so it stops counting against R-10's one-live-offer rule,
    /// drop the <c>lock:driver-offer:{driverId}</c> reservation, and put the driver's presence row
    /// and GEO entry back where they stand. <b>Waiting on the first of the three is a race</b>:
    /// <c>released_at</c> is stamped before the reservation is dropped, so a scenario that went on
    /// standby the moment it appeared would have its fresh GEO entry removed by a release that was
    /// still catching up — and the next ride would find an empty candidate pool and rest in
    /// <c>Matching</c> until the US-6A.11 deadline. Found exactly that way. So the wait is on the
    /// <em>last</em> of the three, the presence row reaching <c>AVAILABLE</c>, which cannot be true
    /// until the other two have happened.
    /// </para>
    /// <para>
    /// Cash rather than wallet, because a cash fare touches no ledger at all — so the trips a fee
    /// scenario needs to manufacture cost it nothing it then has to account for.
    /// </para>
    /// </remarks>
    public async Task FinishTripAsync(LiveRide ride)
    {
        ArgumentNullException.ThrowIfNull(ride);

        await CompleteAndPriceAsync(ride);

        using var paid = await PayAsync(ride.RideId, ride.Passenger, "cash");
        await AssertOkAsync(paid, $"paying ride {ride.RideId} in cash");

        await UntilAsync(
            async () =>
            {
                await using var connection = await OpenAsync();

                return await connection.ExecuteScalarAsync<int>(
                    """
                    SELECT count(*)::int
                      FROM dispatch.offers o
                      JOIN dispatch.driver_presence p ON p.driver_id = o.driver_id
                     WHERE o.ride_id = @RideId AND o.driver_id = @DriverId
                       AND o.released_at IS NOT NULL AND p.state = 'AVAILABLE';
                    """,
                    new { RideId = ride.RideId, DriverId = ride.Driver.DriverId }) == 1;
            },
            $"dispatch-svc returning driver {ride.Driver.DriverId} to the pool after ride {ride.RideId} settled");
    }

    /// <summary>The passenger taps Pay (D-10). Raw, so a scenario can read the refusal itself.</summary>
    public Task<HttpResponseMessage> PayAsync(
        Guid rideId, Passenger passenger, string method = "cash", long tipMinor = 0)
    {
        ArgumentNullException.ThrowIfNull(passenger);

        return PostAsync(
            FareClient,
            "/v1/fare/pay",
            new { rideId = rideId.ToString(), method, tipMinor },
            passenger.Bearer);
    }

    // -----------------------------------------------------------------------------------------
    // Money, through the rails the platform has
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A real OnePay top-up: session, acquirer, signed callback, credit.
    /// </summary>
    /// <remarks>
    /// Four hops and not one of them is skipped. wallet-svc writes a <c>Pending</c>
    /// <c>billing.topups</c> row and calls the acquirer's create-session API; the acquirer answers
    /// with a redirect the driver would follow; the acquirer then calls the webhook back with a
    /// signed body; and only <em>that</em> credits the wallet. The order is the property worth
    /// testing — a balance that grew when the session opened would grow by abandoning a payment page.
    /// </remarks>
    public async Task<TopupSnapshot> TopUpAsync(
        Driver driver, long amountMinor, string providerTransactionId, string method = "onepay")
    {
        ArgumentNullException.ThrowIfNull(driver);

        var topup = await StartTopUpAsync(driver, amountMinor, method);

        using var callback = await ConfirmTopUpAsync(topup, providerTransactionId, method);

        await AssertOkAsync(callback, $"the acquirer confirming top-up {topup.TopupId}");
        await UntilAsync(
            async () => (await ReadTopupAsync(topup.TopupId)).State == "Succeeded",
            $"top-up {topup.TopupId} reaching Succeeded after a signed callback");

        return await ReadTopupAsync(topup.TopupId);
    }

    /// <summary>Opens a gateway session and stops there — the half that must move no money.</summary>
    public async Task<TopupSnapshot> StartTopUpAsync(Driver driver, long amountMinor, string method = "onepay")
    {
        ArgumentNullException.ThrowIfNull(driver);

        using var response = await PostAsync(
            WalletClient, $"/v1/wallet/topup/{method}", new { amountMinor }, driver.Bearer);

        await AssertOkAsync(response, $"opening a {method} top-up session for driver {driver.DriverId}");

        return await ReadTopupAsync((await ReadJsonAsync(response)).GetProperty("topupId").GetGuid());
    }

    /// <summary>The acquirer calls the webhook back, signed with the deployment's own secret.</summary>
    /// <param name="amountMinor">
    /// What the gateway says it collected. Omitted, the session's own amount — a callback that
    /// disagrees with its session is a settlement exception rather than something to credit, which is
    /// its own assertion.
    /// </param>
    public Task<HttpResponseMessage> ConfirmTopUpAsync(
        TopupSnapshot topup,
        string providerTransactionId,
        string method = "onepay",
        string status = "SUCCESS",
        long? amountMinor = null)
    {
        ArgumentNullException.ThrowIfNull(topup);

        return Acquirer.ConfirmAsync(
            TopupCallbackUrl(method),
            method == "onepay" ? OnepayWebhookSecret : LankaQrWebhookSecret,
            new
            {
                providerTransactionId,
                topupId = topup.TopupId.ToString(),
                orderId = topup.OrderId,
                status,
                amountMinor = amountMinor ?? topup.AmountMinor,
                currency = "LKR",
            });
    }

    /// <summary>
    /// Where a ride-side provider callback <em>would</em> be delivered, if one existed.
    /// </summary>
    /// <remarks>
    /// It does not. AL-57/AL-59 removed both, and <c>PaymentEndpoints</c> says so at the line under
    /// "REMOVED, do not re-add". The address is composed here so a scenario can knock on the door and
    /// find nobody home, which is the only way to assert that R-19's late-callback path has no
    /// entrance left.
    /// </remarks>
    public string FareCallbackUrl(string path) =>
        FareClient.BaseAddress!.ToString().TrimEnd('/') + "/v1/fare/pay/" + path;

    /// <summary>Where a top-up callback is delivered on each rail (D6' §7.1/§7.2).</summary>
    public string TopupCallbackUrl(string method) =>
        WalletBaseUrl.TrimEnd('/')
        + (method == "onepay" ? "/v1/wallet/topup/onepay/webhook" : "/v1/wallet/topup/lankaqr/confirm");

    /// <summary>
    /// The D-13 charge, on the plane D3' §325 has ride-svc call during offer acceptance.
    /// </summary>
    /// <remarks>
    /// <b>Called by this suite because nothing in the platform calls it.</b> subscription-svc's own
    /// route documentation says outright that "D3' §325 has ride-svc call it during offer acceptance,
    /// immediately after the conditional <c>UPDATE … AND version = :v</c> that wins the offer" —
    /// and ride-svc has no subscription client, no fee options and no such hop. So the platform's
    /// only revenue line is never collected, and the gap is a C123 finding rather than something this
    /// suite works around silently. Driven here through the same internal route ride-svc would use,
    /// with the ride excluded from the trip count exactly as that route requires.
    /// </remarks>
    public async Task<JsonElement> ChargeDailyFeeAsync(Driver driver, Guid? rideId = null)
    {
        ArgumentNullException.ThrowIfNull(driver);

        using var response = await PostInternalAsync(
            SubscriptionClient,
            $"/v1/internal/fees/{driver.DriverId}/charge-before-trip",
            new { vehicleId = driver.VehicleId.ToString(), rideId = rideId?.ToString() },
            SubscriptionInternalKey);

        await AssertOkAsync(response, $"charging driver {driver.DriverId}'s daily fee");

        return await ReadJsonAsync(response);
    }

    /// <summary>The same call, raw, so a scenario can read a <c>402</c> for itself (US-9.1).</summary>
    public Task<HttpResponseMessage> TryChargeDailyFeeAsync(Driver driver, Guid? rideId = null)
    {
        ArgumentNullException.ThrowIfNull(driver);

        return PostInternalAsync(
            SubscriptionClient,
            $"/v1/internal/fees/{driver.DriverId}/charge-before-trip",
            new { vehicleId = driver.VehicleId.ToString(), rideId = rideId?.ToString() },
            SubscriptionInternalKey);
    }

    /// <summary>
    /// The ledger seam itself — <c>POST /v1/internal/wallet/{driverId}/debit</c> · <c>/credit</c>.
    /// </summary>
    /// <remarks>
    /// The route five services call and the one place a caller's own idempotency key crosses into
    /// the ledger. A scenario uses it for the two things only a caller can express: replaying a key
    /// the platform has already spent (which must move nothing), and posting the key the platform
    /// <em>would</em> have composed for a different Colombo day — the only way to reach a second
    /// fee day without a clock this suite is not allowed to move. Both compose the key with the
    /// platform's own <c>DailyFeeRule.LedgerKey</c> rather than with a string of this suite's, so a
    /// change to the spelling breaks both sides together.
    /// </remarks>
    public Task<HttpResponseMessage> PostLedgerAsync(
        Guid driverId,
        string direction,
        long amountMinor,
        string kind,
        string idempotencyKey,
        string? description = null,
        string? reference = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);

        return PostInternalAsync(
            WalletClient,
            $"/v1/internal/wallet/{driverId}/{direction}",
            new { amountMinor, kind, idempotencyKey, description, reference },
            WalletInternalKey);
    }

    /// <summary>E-05 — Finance reverses a fare, in full or in part.</summary>
    public Task<HttpResponseMessage> RefundAsync(
        FinanceOfficer finance, Guid paymentId, string kind, long amountMinor, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(finance);

        return PostAsync(
            FareClient,
            "/v1/admin/fare/refund",
            new { paymentId = paymentId.ToString(), kind, amountMinor, reasonCode },
            finance.Bearer);
    }

    // -----------------------------------------------------------------------------------------
    // Reading the money back
    // -----------------------------------------------------------------------------------------

    /// <summary>One <c>billing.accounts</c> row with its <c>billing.wallets</c> mirror.</summary>
    /// <remarks>
    /// The owner type is part of the key and not a detail: since AL-57 one person can own a
    /// <c>driver</c> account and a <c>passenger</c> account, <c>ux_accounts_owner</c> is over
    /// <c>(owner_type, owner_id, currency)</c>, and a helper that looked an account up by id alone
    /// would silently read whichever of the two it found first.
    /// </remarks>
    public async Task<AccountSnapshot?> ReadAccountAsync(string ownerType, Guid ownerId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<AccountSnapshot>(
            """
            SELECT a.id AS AccountId, a.owner_type AS OwnerType, a.owner_id AS OwnerId,
                   a.balance_minor AS BalanceMinor, w.balance_minor AS MirrorMinor
              FROM billing.accounts a
              LEFT JOIN billing.wallets w ON w.account_id = a.id
             WHERE a.owner_type = @OwnerType AND a.owner_id = @OwnerId AND a.currency = 'LKR';
            """,
            new { OwnerType = ownerType, OwnerId = ownerId });
    }

    /// <summary>A driver's balance, or zero when they hold no account yet.</summary>
    /// <remarks>
    /// Zero rather than a failure, because "no account" and "an account at zero" are the same fact
    /// about what the driver can spend — <c>billing.accounts</c> rows are created lazily by the first
    /// posting, so a driver who has never been paid or charged has no row at all.
    /// </remarks>
    public async Task<long> BalanceOfAsync(Guid driverId) =>
        (await ReadAccountAsync("driver", driverId))?.BalanceMinor ?? 0;

    /// <inheritdoc cref="BalanceOfAsync"/>
    public async Task<long> PassengerBalanceOfAsync(Guid passengerId) =>
        (await ReadAccountAsync("passenger", passengerId))?.BalanceMinor ?? 0;

    /// <summary>The platform's own singleton account — the counterparty of every top-up and voucher.</summary>
    public async Task<AccountSnapshot> ReadPlatformAccountAsync()
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<AccountSnapshot>(
            """
            SELECT a.id AS AccountId, a.owner_type AS OwnerType, a.owner_id AS OwnerId,
                   a.balance_minor AS BalanceMinor, w.balance_minor AS MirrorMinor
              FROM billing.accounts a
              LEFT JOIN billing.wallets w ON w.account_id = a.id
             WHERE a.owner_type = 'platform' AND a.owner_id IS NULL AND a.currency = 'LKR';
            """);
    }

    /// <summary>One entry, by the business fact it was keyed on — or <see langword="null"/>.</summary>
    public async Task<LedgerEntrySnapshot?> ReadEntryAsync(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        await using var connection = await OpenAsync();

        var entry = await connection.QuerySingleOrDefaultAsync<(Guid Id, string Kind, string Key, string? Description, DateTimeOffset At)>(
            """
            SELECT id, kind, idempotency_key, description, ts
              FROM billing.journal_entries WHERE idempotency_key = @Key;
            """,
            new { Key = idempotencyKey });

        return entry.Id == Guid.Empty
            ? null
            : new LedgerEntrySnapshot(
                entry.Id, entry.Kind, entry.Key, entry.Description, entry.At, await ReadLegsAsync(connection, entry.Id));
    }

    /// <summary>Every entry that touched one owner's account, oldest first.</summary>
    public async Task<IReadOnlyList<LedgerEntrySnapshot>> ReadEntriesForAsync(
        string ownerType, Guid ownerId, string? kind = null)
    {
        await using var connection = await OpenAsync();

        var entries = await connection.QueryAsync<(Guid Id, string Kind, string Key, string? Description, DateTimeOffset At)>(
            """
            SELECT DISTINCT e.id, e.kind, e.idempotency_key, e.description, e.ts
              FROM billing.journal_entries e
              JOIN billing.journal_postings p ON p.entry_id = e.id
              JOIN billing.accounts a ON a.id = p.account_id
             WHERE a.owner_type = @OwnerType AND a.owner_id = @OwnerId
               AND (@Kind::text IS NULL OR e.kind = @Kind)
             ORDER BY e.ts, e.id;
            """,
            new { OwnerType = ownerType, OwnerId = ownerId, Kind = kind });

        var snapshots = new List<LedgerEntrySnapshot>();

        foreach (var (id, entryKind, key, description, at) in entries)
        {
            snapshots.Add(new LedgerEntrySnapshot(
                id, entryKind, key, description, at, await ReadLegsAsync(connection, id)));
        }

        return snapshots;
    }

    /// <summary>The driver's own statement — <c>billing.wallet_transactions</c>, the history screen.</summary>
    public async Task<IReadOnlyList<(string Kind, long AmountMinor, long BalanceAfterMinor)>> ReadStatementAsync(
        string ownerType, Guid ownerId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<(string, long, long)>(
            """
            SELECT t.kind, t.amount_minor, t.balance_after_minor
              FROM billing.wallet_transactions t
              JOIN billing.accounts a ON a.id = t.account_id
             WHERE a.owner_type = @OwnerType AND a.owner_id = @OwnerId
             ORDER BY t.id;
            """,
            new { OwnerType = ownerType, OwnerId = ownerId })];
    }

    public async Task<DailyFeeSnapshot?> ReadDailyFeeAsync(Guid driverId, Guid vehicleId, DateOnly feeDate)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<DailyFeeSnapshot>(
            """
            SELECT driver_id AS DriverId, vehicle_id AS VehicleId, fee_date AS FeeDate,
                   fee_date_tz_at AS FeeDateTzAt, amount_minor::bigint AS AmountMinor,
                   currency AS Currency, trips_that_day AS TripsThatDay, status AS Status
              FROM billing.daily_fee_charges
             WHERE driver_id = @DriverId AND vehicle_id = @VehicleId AND fee_date = @FeeDate;
            """,
            new { DriverId = driverId, VehicleId = vehicleId, FeeDate = feeDate });
    }

    /// <summary>How many top-up sessions a driver has ever opened, on any rail.</summary>
    public async Task<int> CountTopupsAsync(Guid driverId)
    {
        await using var connection = await OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM billing.topups WHERE driver_id = @DriverId;",
            new { DriverId = driverId });
    }

    /// <summary>The most recent session a driver opened — the row a client would be polling.</summary>
    public async Task<TopupSnapshot> ReadLatestTopupAsync(Guid driverId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<TopupSnapshot>(
            """
            SELECT id AS TopupId, driver_id AS DriverId, method AS Method,
                   amount_minor::bigint AS AmountMinor, state AS State,
                   provider_order_id AS OrderId,
                   provider_transaction_id AS ProviderTransactionId,
                   journal_entry_id AS EntryId
              FROM billing.topups WHERE driver_id = @DriverId ORDER BY created_at DESC, id LIMIT 1;
            """,
            new { DriverId = driverId });
    }

    /// <summary>
    /// D-08's cache key, as dispatch-svc's gate reads it — or <see langword="null"/> if it is not there.
    /// </summary>
    /// <remarks>
    /// <c>wallet:bal:{driverId}</c>, written through by wallet-svc <em>after</em> the posting commits
    /// and read by dispatch-svc's <c>WalletGate</c> before every candidate build. It is the one piece
    /// of this platform's money state that does not live in Postgres, and the one whose staleness
    /// would show up as a driver being offered a trip they can no longer pay for — so a scenario that
    /// says "the top-up landed" and never looks at this has not checked the half the dispatcher acts
    /// on.
    /// </remarks>
    public async Task<string?> ReadWalletCacheAsync(Guid driverId)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(_redisConnectionString);

        var value = await redis.GetDatabase().StringGetAsync(RedisKeys.WalletBalance(driverId));

        return value.IsNull ? null : value.ToString();
    }

    public async Task<TopupSnapshot> ReadTopupAsync(Guid topupId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<TopupSnapshot>(
            """
            SELECT id AS TopupId, driver_id AS DriverId, method AS Method,
                   amount_minor::bigint AS AmountMinor, state AS State,
                   provider_order_id AS OrderId,
                   provider_transaction_id AS ProviderTransactionId,
                   journal_entry_id AS EntryId
              FROM billing.topups WHERE id = @TopupId;
            """,
            new { TopupId = topupId });
    }

    public async Task<RidePaymentSnapshot> ReadRidePaymentAsync(Guid paymentId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<RidePaymentSnapshot>(
            """
            SELECT id AS PaymentId, ride_id AS RideId, state AS State, method AS Method,
                   amount_minor::bigint AS AmountMinor, surcharge_minor::bigint AS SurchargeMinor,
                   tip_amount_minor::bigint AS TipAmountMinor, payer_role AS PayerRole,
                   payer_user_id AS PayerUserId, attempt_no::int AS AttemptNo,
                   provider_transaction_id AS ProviderTransactionId
              FROM fares.ride_payments WHERE id = @PaymentId;
            """,
            new { PaymentId = paymentId });
    }

    /// <summary>
    /// The Finance queue, for one payment.
    /// </summary>
    /// <remarks>
    /// <c>fares.refunds</c> <b>is</b> the queue — <c>ix_refunds_open</c> (migration 1003) is a partial
    /// index over the unsettled statuses ordered by <c>requested_at</c>, and its own comment names
    /// SCR-AP-009. So a refund becoming visible to Finance is a row landing here, not an event and not
    /// a screen fare-svc owns.
    /// </remarks>
    public async Task<IReadOnlyList<RefundSnapshot>> ReadRefundsAsync(Guid paymentId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<RefundSnapshot>(
            """
            SELECT id AS RefundId, ride_payment_id AS PaymentId, kind AS Kind,
                   amount_minor::bigint AS AmountMinor, status AS Status, reason_code AS ReasonCode,
                   requested_by AS RequestedBy
              FROM fares.refunds WHERE ride_payment_id = @PaymentId ORDER BY requested_at, id;
            """,
            new { PaymentId = paymentId })];
    }

    public async Task<SupportTicketSnapshot?> ReadSupportTicketAsync(Guid ticketId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<SupportTicketSnapshot>(
            """
            SELECT id AS TicketId, user_id AS UserId, category AS Category, status AS Status,
                   description AS Description, ride_id AS RideId
              FROM support.tickets WHERE id = @TicketId;
            """,
            new { TicketId = ticketId });
    }

    public async Task<IReadOnlyList<SubscriptionPaymentSnapshot>> ReadSubscriptionPaymentsAsync(Guid subscriptionId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<SubscriptionPaymentSnapshot>(
            """
            SELECT id AS PaymentId, subscription_id AS SubscriptionId, vehicle_id AS VehicleId,
                   passenger_id AS PassengerId, period_month AS PeriodMonth, method AS Method,
                   amount_minor::bigint AS AmountMinor, status AS Status, slip_url AS SlipUrl,
                   gateway_ref AS GatewayRef, confirmed_by AS ConfirmedBy, paid_at AS PaidAt
              FROM subscription.payments WHERE subscription_id = @SubscriptionId
             ORDER BY created_at, id;
            """,
            new { SubscriptionId = subscriptionId })];
    }

    public async Task<RideSnapshot> ReadRideAsync(Guid rideId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<RideSnapshot>(
            """
            SELECT state AS State, version AS Version, current_offer_id AS CurrentOfferId,
                   offered_driver_id AS OfferedDriverId, accepted_driver_id AS AcceptedDriverId,
                   accepted_vehicle_id AS AcceptedVehicleId, offer_expires_at AS OfferExpiresAt,
                   terminal_at AS TerminalAt
              FROM rides.rides WHERE id = @RideId;
            """,
            new { RideId = rideId });
    }

    /// <summary>What the driver has earned — <c>fares.driver_earnings</c>, a per-Colombo-day rollup (D-38).</summary>
    public async Task<(int Trips, long GrossMinor)> ReadEarningsAsync(Guid driverId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<(int, long)>(
            """
            SELECT COALESCE(sum(trips), 0)::int, COALESCE(sum(gross_minor), 0)::bigint
              FROM fares.driver_earnings WHERE driver_id = @DriverId;
            """,
            new { DriverId = driverId });
    }

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    // -----------------------------------------------------------------------------------------
    // The fence
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The double-entry ledger balances to zero. <b>This assertion is not optional</b> (C123's fence).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three statements, because "balanced" means three different things and only one of them has a
    /// database trigger behind it.
    /// </para>
    /// <list type="number">
    ///   <item><b>Every entry sums to zero.</b> <c>trg_balanced</c> (migration 1101) enforces it at
    ///   COMMIT, so a violation here is not a caller's arithmetic bug — it is a row that reached the
    ///   table without the trigger firing, which is worth knowing about immediately.</item>
    ///   <item><b>Every posting on the platform sums to zero.</b> The same statement over the whole
    ///   table. It follows from the first and is asserted separately because it is the sentence D-09
    ///   actually makes, and because it catches an entry whose legs were written into a *different*
    ///   entry's id.</item>
    ///   <item><b>Every account's balance equals the sum of its own legs.</b> The one the trigger
    ///   cannot see. <c>billing.accounts.balance_minor</c> is a materialised figure that
    ///   <c>LedgerService</c> updates inside the posting transaction, and <c>billing.wallets</c>
    ///   mirrors it again for dispatch-svc's hot path — so a balance that stopped agreeing with the
    ///   postings is a wallet screen and a D-08 gate reading a number nobody posted. Both copies are
    ///   checked.</item>
    /// </list>
    /// <para>
    /// Called by <see cref="MoneyScenario"/> after every scenario body rather than by the scenarios
    /// themselves, which is what makes the fence structural: a new test in this suite is covered by
    /// it without its author remembering.
    /// </para>
    /// </remarks>
    public async Task AssertLedgerBalancedAsync()
    {
        await using var connection = await OpenAsync();

        var unbalanced = (await connection.QueryAsync<(Guid EntryId, string Kind, string Key, long Sum)>(
            """
            SELECT e.id, e.kind, e.idempotency_key, sum(p.amount_minor)::bigint
              FROM billing.journal_entries e
              JOIN billing.journal_postings p ON p.entry_id = e.id
             GROUP BY e.id, e.kind, e.idempotency_key
            HAVING sum(p.amount_minor) <> 0
             ORDER BY e.ts;
            """)).ToArray();

        Assert.True(
            unbalanced.Length == 0,
            "D-09: every journal entry's postings must sum to zero, and trg_balanced (1101) is supposed "
            + "to make that impossible. These reached the table anyway:\n"
            + string.Join('\n', unbalanced.Select(row =>
                $"    {row.Kind,-18} {row.Key} → Σ {row.Sum:N0}")));

        var total = await connection.ExecuteScalarAsync<long>(
            "SELECT COALESCE(sum(amount_minor), 0)::bigint FROM billing.journal_postings;");

        Assert.True(
            total == 0,
            $"D-09: Σ of every posting on the platform is {total:N0}, not zero. Money has been created "
            + "or destroyed by an entry whose legs do not belong to it.");

        var drifted = (await connection.QueryAsync<(string OwnerType, Guid? OwnerId, long Balance, long Posted, long? Mirror)>(
            """
            SELECT a.owner_type, a.owner_id, a.balance_minor,
                   COALESCE((SELECT sum(p.amount_minor) FROM billing.journal_postings p
                              WHERE p.account_id = a.id), 0)::bigint AS posted,
                   w.balance_minor
              FROM billing.accounts a
              LEFT JOIN billing.wallets w ON w.account_id = a.id
             WHERE a.balance_minor <> COALESCE(
                     (SELECT sum(p.amount_minor) FROM billing.journal_postings p
                       WHERE p.account_id = a.id), 0)
                OR (w.account_id IS NOT NULL AND w.balance_minor <> a.balance_minor)
             ORDER BY a.owner_type, a.created_at;
            """)).ToArray();

        Assert.True(
            drifted.Length == 0,
            "The materialised balances have drifted from the postings they mirror. billing.accounts is "
            + "what a wallet screen reads and billing.wallets is what dispatch-svc's D-08 gate falls "
            + "back to, so a driver is being shown — or gated on — a number nobody posted:\n"
            + string.Join('\n', drifted.Select(row =>
                $"    {row.OwnerType,-9} {row.OwnerId?.ToString() ?? "(singleton)"} "
                + $"balance {row.Balance:N0} · legs {row.Posted:N0} · mirror "
                + $"{(row.Mirror is null ? "none" : row.Mirror.Value.ToString("N0", CultureInfo.InvariantCulture))}")));
    }

    // -----------------------------------------------------------------------------------------
    // Waiting
    // -----------------------------------------------------------------------------------------

    /// <summary>Waits until <paramref name="condition"/> holds, or fails.</summary>
    public async Task UntilAsync(Func<Task<bool>> condition, string what, TimeSpan? within = null)
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

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail($"{what} did not happen within {timeout.TotalSeconds:F0}s.");
    }

    /// <summary>Waits for dispatch-svc to offer the ride to the driver this scenario put online.</summary>
    private async Task<OfferSnapshot> WaitForOfferAsync(Guid rideId, Guid driverId)
    {
        OfferSnapshot? offer = null;

        await UntilAsync(
            async () =>
            {
                await using var connection = await OpenAsync();

                offer = await connection.QuerySingleOrDefaultAsync<OfferSnapshot>(
                    """
                    SELECT id AS Id, driver_id AS DriverId, status AS Status,
                           sent_at AS SentAt, expires_at AS ExpiresAt
                      FROM dispatch.offers WHERE ride_id = @RideId ORDER BY sent_at DESC LIMIT 1;
                    """,
                    new { RideId = rideId });

                return offer is not null && (await ReadRideAsync(rideId)).State == "Offered";
            },
            $"ride {rideId} being offered to a driver");

        Assert.True(
            offer!.DriverId == driverId,
            $"Ride {rideId} was offered to driver {offer.DriverId} and not to {driverId}, which means two "
            + "rides shared a candidate pool. Every ride in this assembly must take its own square from "
            + "ModeCFleet.NextPlaces().");

        return offer;
    }

    // -----------------------------------------------------------------------------------------
    // HTTP plumbing
    // -----------------------------------------------------------------------------------------

    public static Task<HttpResponseMessage> PostAsync(
        HttpClient client, string path, object? body, string? bearer) =>
        SendAsync(client, HttpMethod.Post, path, body, bearer);

    public static Task<HttpResponseMessage> PutAsync(
        HttpClient client, string path, object? body, string? bearer) =>
        SendAsync(client, HttpMethod.Put, path, body, bearer);

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? bearer)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static Task<HttpResponseMessage> PostInternalAsync(
        HttpClient client, string path, object? body, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        request.Headers.Add(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        request.Headers.Add("X-MageRide-Internal-Key", apiKey);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return document.RootElement.Clone();
    }

    /// <summary>The <c>type</c> slug of an RFC 7807 problem, for the negative assertions.</summary>
    public static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        var problem = await ReadJsonAsync(response);
        var type = problem.TryGetProperty("type", out var value) ? value.GetString() ?? string.Empty : string.Empty;

        return type[(type.LastIndexOf('/') + 1)..];
    }

    public static Task AssertOkAsync(HttpResponseMessage response, string what) =>
        AssertStatusAsync(response, HttpStatusCode.OK, what);

    public static async Task AssertStatusAsync(
        HttpResponseMessage response, HttpStatusCode expected, string what)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode != expected)
        {
            Assert.Fail(
                $"{what} answered {(int)response.StatusCode} and not {(int)expected}: "
                + await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, object? body, string? bearer)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        // D3' §0 makes the header mandatory on every mutating request; omitting it by accident would
        // test the 400 path instead of the route.
        request.Headers.Add(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<LedgerPostingSnapshot>> ReadLegsAsync(
        NpgsqlConnection connection, Guid entryId) =>
        [.. await connection.QueryAsync<LedgerPostingSnapshot>(
            """
            SELECT p.id AS PostingId, p.account_id AS AccountId, a.owner_type AS OwnerType,
                   a.owner_id AS OwnerId, p.amount_minor AS AmountMinor
              FROM billing.journal_postings p
              JOIN billing.accounts a ON a.id = p.account_id
             WHERE p.entry_id = @EntryId ORDER BY p.id;
            """,
            new { EntryId = entryId })];

    // -----------------------------------------------------------------------------------------
    // Composition
    // -----------------------------------------------------------------------------------------

    private static WebApplication BuildWallet(
        PostgresFixture postgres,
        RedisFixture redis,
        RedpandaFixture redpanda,
        TestTokenIssuer tokens,
        string acquirerBaseUrl) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,

                // On. `wallet.debited` is what clears D-08's cache, and R-13's whole claim is that the
                // event exists because the transaction committed.
                ["Outbox:DispatcherEnabled"] = "true",

                ["Wallet:InternalApiKey"] = WalletInternalKey,

                // Both rails, configured. Unset, the card rail answers 503 before a session exists and
                // every callback on either is refused — the two failures wallet-svc announces as
                // errors at start-up, and the two that would make this suite assert nothing at all.
                ["Onepay:BaseUrl"] = acquirerBaseUrl,
                ["Onepay:ApiKey"] = "mageride-c123-e2e-onepay-api-key",
                ["Onepay:WebhookSecret"] = OnepayWebhookSecret,
                ["ComBankIpg:WebhookSecret"] = LankaQrWebhookSecret,
                ["LankaQr:MerchantId"] = "MR-C123-E2E",
                ["LankaQr:DeepLinkTemplate"] = LankaQrDeepLinkTemplate,

                // `LankaQr:PayloadTemplate` is deliberately unset. An EMVCo payload belongs to the
                // acquiring bank, so the deep link is the only route AL-15 gives — and a scenario
                // asserting `qrPayload` is absent is asserting the deployment's own decision.

                // On, at the D-08 value. dispatch-svc's gate reads `wallet:bal:{driverId}` and this
                // service is its only writer; off, a top-up would be invisible to the gate for the
                // TTL and a scenario would be waiting on a cache rather than on the ledger.
                ["Wallet:BalanceCacheEnabled"] = "true",
            },
            (options, configure) => WalletApplication.Build(options, configure));

    private static WebApplication BuildRide(
        PostgresFixture postgres, RedpandaFixture redpanda, TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Fare:EstimateTokenKey"] = FareTokenKey,
                ["Ride:InternalApiKey"] = RideInternalKey,
                ["Ride:PhoneHashKey"] = PhoneHashKey,
                ["Ride:OtpPepper"] = OtpPepper,
                ["Ride:OfferTtl"] = OfferTtl.ToString(),
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Outbox:DispatcherEnabled"] = "true",
                ["Ride:TimersEnabled"] = "true",

                // Off with EMQX: R-15's last-will plane is C120's subject and no money path touches
                // a broker, so this fleet has no reason to hold a broker connection open.
                ["Ride:VehicleStatusEnabled"] = "false",

                // Off: a dozen suites share one database and each would otherwise gauge the others'
                // rides. R-20's gauges are asserted in ride-svc's own suite.
                ["Ride:StuckStateMetricsEnabled"] = "false",
            },
            (options, configure) => RideApplication.Build(options, configure));

    private static WebApplication BuildReputation(
        PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Outbox:DispatcherEnabled"] = "true",
                ["Reputation:InternalApiKey"] = ReputationInternalKey,
                ["Reputation:GrpcListenPort"] = "0",
                ["Reputation:ConsumerEnabled"] = "false",
                ["Reputation:ExpiryWorkerEnabled"] = "false",
                ["Reputation:DetectorEnabled"] = "false",
                ["Reputation:BlockStatusCacheTtl"] = "00:00:00.100",
            },
            (options, configure) => ReputationApplication.Build(options, configure));

    private static WebApplication BuildDispatch(
        PostgresFixture postgres,
        RedisFixture redis,
        RedpandaFixture redpanda,
        TestTokenIssuer tokens,
        string rideBaseUrl,
        string reputationGrpcUrl) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Outbox:DispatcherEnabled"] = "true",
                ["Dispatch:RideServiceBaseUrl"] = rideBaseUrl,
                ["Dispatch:RideServiceInternalKey"] = RideInternalKey,
                ["Dispatch:ReputationGrpcAddress"] = reputationGrpcUrl,
                ["Dispatch:ReputationInternalKey"] = ReputationInternalKey,
                ["Dispatch:InternalApiKey"] = DispatchInternalKey,
                ["Dispatch:OfferTtl"] = OfferTtl.ToString(),
                ["Dispatch:ConsumerGroup"] = $"dispatch-svc-c123-{Guid.NewGuid():N}",
                ["Dispatch:ConsumerEnabled"] = "true",
                ["Dispatch:PositionConsumerEnabled"] = "true",
                ["Dispatch:ExpiryWorkerEnabled"] = "true",
                ["Dispatch:DispatchTimerWorkerEnabled"] = "true",
                ["Dispatch:KeyspaceNotificationsEnabled"] = "true",
                ["Dispatch:ReputationCacheTtl"] = "00:00:00",
                ["Dispatch:LastWillEnabled"] = "false",
                ["Dispatch:ScheduledWorkerEnabled"] = "false",
                ["Dispatch:LevelWorkerEnabled"] = "false",
            },
            (options, configure) => DispatchApplication.Build(options, configure));

    private static WebApplication BuildFare(
        PostgresFixture postgres,
        TestTokenIssuer tokens,
        string rideBaseUrl,
        string dispatchBaseUrl,
        string walletBaseUrl) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Fare:EstimateTokenKey"] = FareTokenKey,
                ["Fare:InternalApiKey"] = FareInternalKey,
                ["Fare:RideBaseUrl"] = rideBaseUrl,
                ["Fare:RideInternalApiKey"] = RideInternalKey,
                ["Fare:DispatchBaseUrl"] = dispatchBaseUrl,
                ["Fare:DispatchInternalApiKey"] = DispatchInternalKey,

                // Set, unlike C120's and C122's fleets, and this is the difference that makes C123
                // a different component: with it the `wallet` ride rail exists, the D-05 penalty
                // collected into a fare can be paid out, and the E-05 refund posts a real reversal.
                // fare-svc's own start-up calls out the one combination that is worse than either
                // extreme — dispatch set with wallet unset — and this fleet sets both.
                ["Fare:WalletBaseUrl"] = walletBaseUrl,
                ["Fare:WalletInternalApiKey"] = WalletInternalKey,

                // Off. The AL-47 +5-minute nudge identifies who should be pushed to and logs it,
                // because notification-svc is not in this fleet; a sweep running against every
                // unanswered claim in a shared database would be noise on every scenario.
                ["Fare:QrNudgeEnabled"] = "false",
            },
            (options, configure) => FareApplication.Build(options, configure));

    private static WebApplication BuildSubscription(
        PostgresFixture postgres,
        RedpandaFixture redpanda,
        TestTokenIssuer tokens,
        string walletBaseUrl,
        string documentRoot) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Outbox:DispatcherEnabled"] = "true",
                ["Subscription:InternalApiKey"] = SubscriptionInternalKey,

                // The seam D-13's fee actually moves over. Unset, every charge answers 503 and the
                // platform's only revenue line is silently never collected.
                ["Subscription:WalletBaseUrl"] = walletBaseUrl,
                ["Subscription:WalletInternalApiKey"] = WalletInternalKey,

                ["Subscription:ModeBSubscriptionsEnabled"] = "true",
                ["Subscription:OnepayWebhookSecret"] = OnepayWebhookSecret,
                ["Subscription:LankaQrWebhookSecret"] = LankaQrWebhookSecret,
                ["Subscription:FileLinkSigningKey"] = FileLinkSigningKey,
                ["Subscription:SlipRoot"] = Path.Combine(documentRoot, "transfer-slips"),

                // Off. The Mode B *platform* charge (billing.monthly_subscriptions) is a per-vehicle
                // DUE row this component does not assert, and its hourly runner sweeps every Mode B
                // vehicle in a database shared with C121's fleet scenarios — so it would raise rows
                // for vehicles no money scenario created. C060 is where those lines are consolidated.
                ["Subscription:ModeBBillingEnabled"] = "false",
            },
            (options, configure) => SubscriptionApplication.Build(options, configure));

    private static WebApplication BuildFleet(
        PostgresFixture postgres, TestTokenIssuer tokens, string subscriptionBaseUrl, string documentRoot) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Fleet:InternalApiKey"] = FleetInternalKey,
                ["Fleet:DocumentRoot"] = Path.Combine(documentRoot, "fleet-documents"),
                ["Fleet:SubscriptionBaseUrl"] = subscriptionBaseUrl,
                ["Fleet:ErrorReportSigningKey"] = "mageride-c123-e2e-fleet-error-report-key",

                // Off, with notification-svc absent: US-13.11's departure alarm has nothing to ring
                // into, and its sweep would fire against every schedule in a shared database.
                ["Fleet:ScheduleAlarmsEnabled"] = "false",

                // `Fleet:OcrBaseUrl` and `Fleet:ProvisioningBaseUrl` are deliberately unset, for
                // C121's reasons. Neither is on a money path: an unread document leaves an AL-50 slot
                // `pending`, which holds a vehicle out of APPROVED — and Epic 23 gates *collecting*
                // on the payout profile, never on the vehicle's approval, which is what
                // ModeBSubscriptionPaymentScenario relies on.
            },
            (options, configure) => FleetApplication.Build(options, configure));

    /// <summary>
    /// What every service in this fleet is configured with, and the one thing each has replaced:
    /// the bearer handler's signing key.
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
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(settings);
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });
    }

    private static HttpClient NewClient(WebApplication app) =>
        new() { BaseAddress = new Uri(BaseAddressOf(app)), Timeout = TimeSpan.FromSeconds(60) };

    private static string BaseAddressOf(WebApplication app) => AddressesOf(app)[0];

    /// <summary>
    /// reputation-svc's HTTP/2-only endpoint. <c>ReputationApplication</c> binds the HTTP/1.1 one
    /// first and the gRPC one second, and <c>IServerAddressesFeature</c> reports them in
    /// <c>Listen</c> order — the only way to tell them apart when both took port 0.
    /// </summary>
    private static string GrpcAddressOf(WebApplication app) => AddressesOf(app)[^1];

    private static IReadOnlyList<string> AddressesOf(WebApplication app) =>
        [.. app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses];

    private static string NextPhone() =>
        "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture);
}
