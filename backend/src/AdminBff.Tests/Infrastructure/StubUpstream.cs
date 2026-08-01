using System.Collections.Concurrent;
using Dapper;
using MageRide.Shared.Http;
using MageRide.TestKit;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MageRide.AdminBff.Tests.Infrastructure;

/// <summary>One request admin-bff forwarded, as the callee saw it.</summary>
internal sealed record ForwardedCall(
    string Method, string Path, string? InternalKey, string? Authorization, string? IdempotencyKey, string Body);

/// <summary>
/// A real HTTP server standing in for safety-svc, support-svc, content-svc and transit-svc.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real socket rather than a stubbed <c>HttpMessageHandler</c>.</b> The forwarding claims worth
/// asserting are about the wire — which credential admin-bff sends to which kind of callee, whether
/// the operator's <c>Idempotency-Key</c> reaches the service that owns the command log, and whether
/// an upstream's 404 arrives at the operator as a 404 rather than a 502. A handler substituted
/// inside the process tests the mapping code and not the decision.
/// </para>
/// <para>
/// It records every call, so a test can assert on what was sent as well as on what came back, and
/// it can be told to fail the next call with a given status — which is how the error-translation
/// path is exercised without arranging a real upstream failure.
/// </para>
/// </remarks>
internal sealed class StubUpstream : IAsyncDisposable
{
    /// <summary>The shared secret the two <c>/v1/internal/**</c> planes expect (C008).</summary>
    public const string InternalKey = "stub-internal-key";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<ForwardedCall> _calls = new();

    private (string Path, int Status, string Body)? _failure;

    private StubUpstream(WebApplication app)
    {
        _app = app;

        BaseUrl = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public string BaseUrl { get; }

    public IReadOnlyList<ForwardedCall> Calls => [.. _calls];

    /// <summary>The most recent call whose path contains <paramref name="fragment"/>.</summary>
    public ForwardedCall Last(string fragment) =>
        _calls.LastOrDefault(call => call.Path.Contains(fragment, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"No forwarded call matched '{fragment}'. Saw: {string.Join(", ", _calls.Select(c => c.Path))}");

    /// <summary>Makes the next call whose path contains <paramref name="pathFragment"/> fail.</summary>
    public void FailNext(string pathFragment, int status, string detail) =>
        _failure = (pathFragment, status,
            $$"""{"type":"https://mageride.lk/errors/not-found","title":"x","status":{{status}},"detail":"{{detail}}"}""");

    public static async Task<StubUpstream> StartAsync(PostgresFixture postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["urls"] = "http://127.0.0.1:0",
        });

        var app = builder.Build();
        var stub = new StubUpstreamState();

        app.Use(async (context, next) =>
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;

            stub.Record(new ForwardedCall(
                context.Request.Method,
                context.Request.Path + context.Request.QueryString,
                context.Request.Headers["X-MageRide-Internal-Key"].ToString() is { Length: > 0 } key ? key : null,
                context.Request.Headers.Authorization.ToString() is { Length: > 0 } auth ? auth : null,
                context.Request.Headers[MageRideHeaders.IdempotencyKey].ToString() is { Length: > 0 } idem
                    ? idem
                    : null,
                body));

            if (stub.TakeFailure(context.Request.Path) is { } failure)
            {
                context.Response.StatusCode = failure.Status;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync(failure.Body);
                return;
            }

            await next(context);
        });

        // safety-svc (C052) — the vehicle-report queue and the confirm/dismiss decision.
        app.MapGet("/v1/internal/safety/reports/queue", () => Results.Json(new
        {
            items = new[]
            {
                new
                {
                    reportId = SeedIds.Report,
                    vehicleId = SeedIds.ReportedVehicle,
                    reason = "Reckless driving",
                    tripId = (Guid?)null,
                    status = "PENDING",
                    createdAt = DateTimeOffset.UnixEpoch,
                },
            },
            cursor = (string?)null,
        }, MageRideJson.Options));

        app.MapPost("/v1/internal/safety/reports/{reportId:guid}/resolve", (Guid reportId) => Results.Json(new
        {
            reportId,
            status = "CONFIRMED",
            confirmedTotal = 3,
            delisted = true,
        }, MageRideJson.Options));

        // support-svc (C053) — the agent ticket queue.
        app.MapGet("/v1/internal/support/tickets", () => Results.Json(new
        {
            items = new[]
            {
                new
                {
                    ticketId = SeedIds.Ticket,
                    userId = SeedIds.TicketUser,
                    category = "payment",
                    status = "OPEN",
                    description = "Charged twice",
                    createdAt = DateTimeOffset.UnixEpoch,
                    resolvedAt = (DateTimeOffset?)null,
                },
            },
            cursor = (string?)null,
            hasMore = false,
        }, MageRideJson.Options));

        app.MapPost("/v1/internal/support/tickets/{ticketId:guid}/resolve", (Guid ticketId) => Results.Json(new
        {
            ticketId,
            userId = SeedIds.TicketUser,
            category = "payment",
            status = "RESOLVED",
            description = "Charged twice",
            response = "Refunded.",
            createdAt = DateTimeOffset.UnixEpoch,
            resolvedAt = DateTimeOffset.UnixEpoch.AddHours(1),
        }, MageRideJson.Options));

        // content-svc (C054) — content.broadcasts, which it owns.
        // A fresh id per call: two tests publishing an announcement must not end up asserting on
        // one another's audit row.
        app.MapPost("/v1/admin/content/broadcasts", () => Results.Json(new
        {
            broadcastId = Guid.CreateVersion7(),
        }, MageRideJson.Options, statusCode: StatusCodes.Status201Created));

        // transit-svc (C057) — the GTFS Dataset Manager the Configuration group proxies.
        app.Map("/v1/admin/transit/gtfs/{**path}", () => Results.Json(new
        {
            versions = Array.Empty<object>(),
        }, MageRideJson.Options));

        app.MapInternalRegistry(postgres);
        app.MapInternalFleet(postgres);
        app.MapInternalWallet(postgres);
        app.MapAdminFare(postgres);

        await app.StartAsync();

        var harness = new StubUpstream(app);
        stub.Attach(harness);

        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// The recording state, held apart so the middleware closure does not need the harness that
    /// does not exist until the server has an address.
    /// </summary>
    private sealed class StubUpstreamState
    {
        private StubUpstream? _harness;

        public void Attach(StubUpstream harness) => _harness = harness;

        public void Record(ForwardedCall call) => _harness?._calls.Enqueue(call);

        public (int Status, string Body)? TakeFailure(PathString path)
        {
            if (_harness?._failure is not { } failure ||
                !path.Value!.Contains(failure.Path, StringComparison.Ordinal))
            {
                return null;
            }

            _harness._failure = null;
            return (failure.Status, failure.Body);
        }
    }
}

/// <summary>
/// The two internal planes C063 forwards to, standing on the real database.
/// </summary>
/// <remarks>
/// <para>
/// <b>These two stubs write, where the other four only answer.</b> safety-svc's and support-svc's
/// decisions are somebody else's rows and a canned reply is enough to assert what admin-bff sent.
/// registry-svc's recompute and fleet-svc's approval are different: the C063 definition of done is
/// about state on the far side — "approving a fleet org's payout profile makes <c>payTo</c>
/// available to subscription-svc" is a claim about <c>registry.fleet_payout_profiles</c>, and
/// asserting it against a canned <c>{"status":"verified"}</c> would assert nothing. So each
/// performs the transaction its service performs, against the same Postgres.
/// </para>
/// <para>
/// <b>They are faithful, not complete.</b> The recompute applies AL-30's rule — a step is verified
/// when none of its fields is pending, and four verified steps approve the vehicle — and leaves out
/// AL-10's mandatory-document gate, which registry-svc's own suite proves and which admin-bff only
/// ever reads the *outcome* of. The fleet approval does AL-49's supersede-then-verify in the order
/// <c>ux_payout_profile_verified</c> demands.
/// </para>
/// </remarks>
internal static class StubInternalPlanes
{
    /// <summary>registry-svc's <c>POST /v1/internal/vehicles/{id}/onboarding/recompute</c> (C029).</summary>
    public static void MapInternalRegistry(this WebApplication app, PostgresFixture postgres)
    {
        // Δ C063 (AL-58). registry-svc's payout decision, standing in for the real service exactly
        // as the recompute below does. The ORDER is the part being stood in for — supersede, then
        // verify — because `ux_driver_payout_verified` admits one verified row per driver. The real
        // transition is proven against the real code in Registry.Api.Tests; what these routes let
        // AdminBff.Tests prove is the forwarding, the audit row and the queue.
        app.MapPost("/v1/internal/drivers/{driverId:guid}/payout-profile/approve", async (
            Guid driverId, PayoutDecisionRequest body) =>
        {
            await using var connection = await postgres.OpenAsync();

            await connection.ExecuteAsync(
                """
                UPDATE registry.driver_payout_profiles SET status = 'superseded'
                 WHERE driver_id = @Id AND status = 'verified'
                   AND EXISTS (SELECT 1 FROM registry.driver_payout_profiles p
                                WHERE p.driver_id = @Id AND p.status = 'pending_verification');
                """,
                new { Id = driverId });

            var row = await connection.QuerySingleOrDefaultAsync<PayoutDecisionRow>(
                """
                UPDATE registry.driver_payout_profiles
                   SET status = 'verified', verified_by = @OfficerId, verified_at = now(),
                       rejection_reason = NULL
                 WHERE driver_id = @Id AND status = 'pending_verification'
                RETURNING status AS Status, bank AS Bank, account_no AS AccountNo,
                          rejection_reason AS RejectionReason, verified_at AS VerifiedAt;
                """,
                new { Id = driverId, OfficerId = Guid.Parse(body.OfficerId) });

            return row is null ? Results.Conflict() : Results.Ok(row);
        });

        app.MapPost("/v1/internal/drivers/{driverId:guid}/payout-profile/reject", async (
            Guid driverId, PayoutDecisionRequest body) =>
        {
            await using var connection = await postgres.OpenAsync();

            // The incumbent is untouched — only the pending row is in scope.
            var row = await connection.QuerySingleOrDefaultAsync<PayoutDecisionRow>(
                """
                UPDATE registry.driver_payout_profiles
                   SET status = 'rejected', rejection_reason = @Reason, verified_by = @OfficerId
                 WHERE driver_id = @Id AND status = 'pending_verification'
                RETURNING status AS Status, bank AS Bank, account_no AS AccountNo,
                          rejection_reason AS RejectionReason, verified_at AS VerifiedAt;
                """,
                new { Id = driverId, OfficerId = Guid.Parse(body.OfficerId), body.Reason });

            return row is null ? Results.Conflict() : Results.Ok(row);
        });

        app.MapPost("/v1/internal/vehicles/{vehicleId:guid}/onboarding/recompute", async (Guid vehicleId) =>
        {
            await using var connection = await postgres.OpenAsync();

            // AL-30's derivation, in the order registry-svc's SettleAsync applies it: each saved
            // step is re-judged from its own fields, then the four verdicts decide the vehicle.
            await connection.ExecuteAsync(
                """
                UPDATE registry.onboarding_steps s
                   SET status = CASE WHEN EXISTS (
                                       SELECT 1
                                         FROM registry.documents d
                                         JOIN registry.document_fields f ON f.document_id = d.id
                                        WHERE d.vehicle_id = s.vehicle_id
                                          AND f.verify_status = 'pending')
                                     THEN 'pending_review' ELSE 'verified' END
                 WHERE s.vehicle_id = @Id
                   AND s.step <> 'details'
                   AND s.status <> 'pending_input';

                UPDATE registry.vehicles v
                   SET status = 'APPROVED', onboarding_status = 'approved'
                 WHERE v.id = @Id
                   AND v.status = 'PENDING'
                   AND NOT EXISTS (SELECT 1 FROM registry.onboarding_steps s
                                    WHERE s.vehicle_id = v.id AND s.status <> 'verified')
                   AND (SELECT count(*) FROM registry.onboarding_steps s WHERE s.vehicle_id = v.id) = 4;
                """,
                new { Id = vehicleId });

            var vehicle = await connection.QuerySingleOrDefaultAsync<(string Status, string OnboardingStatus)>(
                "SELECT status, onboarding_status FROM registry.vehicles WHERE id = @Id;", new { Id = vehicleId });

            if (vehicle.Status is null)
            {
                return Results.Json(
                    new { type = "https://mageride.lk/errors/vehicle-not-found", detail = "No such vehicle." },
                    MageRideJson.Options,
                    "application/problem+json",
                    StatusCodes.Status404NotFound);
            }

            var steps = (await connection.QueryAsync<(string Step, string Status)>(
                "SELECT step, status FROM registry.onboarding_steps WHERE vehicle_id = @Id;",
                new { Id = vehicleId })).ToDictionary(row => row.Step, row => row.Status, StringComparer.Ordinal);

            string Verdict(string step) => steps.TryGetValue(step, out var status)
                ? status switch
                {
                    "verified" => "VERIFIED",
                    "pending_review" => "PENDING_REVIEW",
                    _ => "PENDING_INPUT",
                }
                : "PENDING_INPUT";

            var order = new[] { "details", "insurance", "revenue", "photos" };

            return Results.Json(
                new
                {
                    status = vehicle.Status,
                    onboardingStatus = vehicle.OnboardingStatus,
                    nextStep = order.FirstOrDefault(step => Verdict(step) != "VERIFIED"),
                    steps = new
                    {
                        details = Verdict("details"),
                        insurance = Verdict("insurance"),
                        revenue = Verdict("revenue"),
                        photos = Verdict("photos"),
                    },
                    fields = Array.Empty<object>(),
                },
                MageRideJson.Options);
        });
    }

    /// <summary>fleet-svc's <c>/v1/internal/fleets/**</c> plane (C058, C059).</summary>
    public static void MapInternalFleet(this WebApplication app, PostgresFixture postgres)
    {
        app.MapGet("/v1/internal/fleets/queue", async (string? status, int? limit) =>
        {
            await using var connection = await postgres.OpenAsync();

            var rows = await connection.QueryAsync<FleetQueueDto>(
                """
                SELECT f.id           AS FleetId,
                       f.name         AS Name,
                       f.business_reg AS RegistrationNo,
                       f.contact_phone AS ContactPhone,
                       f.status       AS Status,
                       (SELECT p.status FROM registry.fleet_payout_profiles p
                         WHERE p.fleet_id = f.id AND p.status <> 'superseded'
                         ORDER BY p.created_at DESC LIMIT 1) AS PayoutProfileStatus,
                       0              AS DocumentCount,
                       f.created_at   AS CreatedAt
                  FROM registry.fleets f
                 WHERE f.status = @Status
                 ORDER BY f.created_at DESC
                 LIMIT @Limit;
                """,
                new { Status = status ?? "PENDING", Limit = limit ?? 50 });

            return Results.Json(
                new { items = rows.Select(row => row with { FleetId = row.FleetId }) }, MageRideJson.Options);
        });

        app.MapGet("/v1/internal/fleets/{fleetId:guid}", async (Guid fleetId) =>
        {
            await using var connection = await postgres.OpenAsync();

            var fleet = await connection.QuerySingleOrDefaultAsync<FleetKycDto>(
                """
                SELECT id AS FleetId, name AS Name, business_reg AS RegistrationNo,
                       contact_phone AS ContactPhone, contact_email AS ContactEmail, address AS Address,
                       status AS Status, rejection_reason AS RejectionReason, created_at AS CreatedAt
                  FROM registry.fleets WHERE id = @Id;
                """,
                new { Id = fleetId });

            if (fleet is null)
            {
                return Results.Json(
                    new { type = "https://mageride.lk/errors/fleet-not-found", detail = "No such fleet." },
                    MageRideJson.Options,
                    "application/problem+json",
                    StatusCodes.Status404NotFound);
            }

            var profile = await CurrentProfileAsync(connection, fleetId);

            return Results.Json(
                new
                {
                    kyc = fleet,
                    payoutProfileStatus = profile?.Status,
                    payoutProfile = profile,
                    documents = profile is null
                        ? Array.Empty<object>()
                        : await DocumentsAsync(connection, fleetId),
                },
                MageRideJson.Options);
        });

        app.MapPost("/v1/internal/fleets/{fleetId:guid}/approve", async (Guid fleetId, FleetDecisionRequest? body) =>
        {
            await using var connection = await postgres.OpenAsync();

            // Supersede before verify: ux_payout_profile_verified admits one verified row per org,
            // so the other order fails on the index (migration 0313's own comment).
            await connection.ExecuteAsync(
                """
                UPDATE registry.fleets SET status = 'APPROVED', rejection_reason = NULL WHERE id = @Id;

                UPDATE registry.fleet_payout_profiles
                   SET status = 'superseded'
                 WHERE fleet_id = @Id AND status = 'verified'
                   AND EXISTS (SELECT 1 FROM registry.fleet_payout_profiles p
                                WHERE p.fleet_id = @Id AND p.status = 'pending_verification');

                UPDATE registry.fleet_payout_profiles
                   SET status = 'verified', verified_by = @OfficerId, verified_at = now()
                 WHERE id = (SELECT id FROM registry.fleet_payout_profiles
                              WHERE fleet_id = @Id AND status = 'pending_verification'
                              ORDER BY created_at DESC LIMIT 1);
                """,
                new { Id = fleetId, OfficerId = Guid.TryParse(body?.OfficerId, out var id) ? id : (Guid?)null });

            return await DecisionAsync(connection, fleetId);
        });

        app.MapPost("/v1/internal/fleets/{fleetId:guid}/reject", async (Guid fleetId, FleetDecisionRequest? body) =>
        {
            await using var connection = await postgres.OpenAsync();

            // A rejection never disturbs the incumbent: refusing an edit is not a reason to stop an
            // organisation collecting against details somebody already approved (fleet-svc's rule).
            await connection.ExecuteAsync(
                """
                UPDATE registry.fleets SET status = 'REJECTED', rejection_reason = @Reason WHERE id = @Id;

                UPDATE registry.fleet_payout_profiles
                   SET status = 'rejected', rejection_reason = @Reason
                 WHERE fleet_id = @Id AND status = 'pending_verification';
                """,
                new { Id = fleetId, Reason = body?.Reason });

            return await DecisionAsync(connection, fleetId);
        });

        app.MapPost("/v1/internal/fleets/{fleetId:guid}/vehicles/{vehicleId:guid}/approve",
            async (Guid vehicleId) =>
            {
                await using var connection = await postgres.OpenAsync();

                await connection.ExecuteAsync(
                    "UPDATE registry.vehicles SET status = 'APPROVED' WHERE id = @Id AND status <> 'DEACTIVATED';",
                    new { Id = vehicleId });

                return Results.Json(new { docsStatus = "verified" }, MageRideJson.Options);
            });

        app.MapPost("/v1/internal/fleets/{fleetId:guid}/vehicles/{vehicleId:guid}/reject",
            async (Guid vehicleId, FleetDecisionRequest? body) =>
            {
                await using var connection = await postgres.OpenAsync();

                await connection.ExecuteAsync(
                    """
                    UPDATE registry.vehicles
                       SET status = 'REJECTED', rejection_reason = @Reason
                     WHERE id = @Id AND status <> 'DEACTIVATED';
                    """,
                    new { Id = vehicleId, Reason = body?.Reason });

                return Results.Json(new { docsStatus = "pending" }, MageRideJson.Options);
            });
    }

    private static async Task<IResult> DecisionAsync(Npgsql.NpgsqlConnection connection, Guid fleetId) =>
        Results.Json(
            new
            {
                fleet = await connection.QuerySingleAsync<FleetKycDto>(
                    """
                    SELECT id AS FleetId, name AS Name, business_reg AS RegistrationNo,
                           contact_phone AS ContactPhone, contact_email AS ContactEmail, address AS Address,
                           status AS Status, rejection_reason AS RejectionReason, created_at AS CreatedAt
                      FROM registry.fleets WHERE id = @Id;
                    """,
                    new { Id = fleetId }),
                payoutProfile = await CurrentProfileAsync(connection, fleetId),
            },
            MageRideJson.Options);

    private static Task<FleetPayoutDto?> CurrentProfileAsync(Npgsql.NpgsqlConnection connection, Guid fleetId) =>
        connection.QuerySingleOrDefaultAsync<FleetPayoutDto>(
            """
            SELECT bank AS Bank, branch AS Branch, account_no AS AccountNo,
                   account_holder_name AS AccountHolderName, status AS Status,
                   rejection_reason AS RejectionReason, verified_at AS VerifiedAt
              FROM registry.fleet_payout_profiles
             WHERE fleet_id = @Id AND status <> 'superseded'
             ORDER BY created_at DESC LIMIT 1;
            """,
            new { Id = fleetId });

    private static async Task<object[]> DocumentsAsync(Npgsql.NpgsqlConnection connection, Guid fleetId)
    {
        var rows = await connection.QueryAsync<(Guid DocId, string? Kind, DateTimeOffset CreatedAt)>(
            """
            SELECT u.id, u.kind, u.created_at
              FROM docs.uploads u
              JOIN registry.fleet_payout_profiles p
                ON u.id IN (p.proof_upload_id, p.lankaqr_upload_id)
             WHERE p.fleet_id = @Id AND p.status <> 'superseded';
            """,
            new { Id = fleetId });

        return [.. rows.Select(row => (object)new { docId = row.DocId, kind = row.Kind ?? "", createdAt = row.CreatedAt })];
    }

    private sealed record FleetQueueDto(
        Guid FleetId,
        string Name,
        string? RegistrationNo,
        string? ContactPhone,
        string Status,
        string? PayoutProfileStatus,
        int DocumentCount,
        DateTimeOffset CreatedAt);

    private sealed record FleetKycDto(
        Guid FleetId,
        string Name,
        string? RegistrationNo,
        string? ContactPhone,
        string? ContactEmail,
        string? Address,
        string Status,
        string? RejectionReason,
        DateTimeOffset CreatedAt);

    private sealed record FleetPayoutDto(
        string Bank,
        string Branch,
        string AccountNo,
        string AccountHolderName,
        string Status,
        string? RejectionReason,
        DateTimeOffset? VerifiedAt);

    internal sealed record FleetDecisionRequest(string? OfficerId, string? Reason);

    // ---------------------------------------------------------------------------------------
    // C065 — the two upstreams a finance decision reaches
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// wallet-svc's <c>POST /v1/internal/wallet/{driverId}/credit</c> (C046).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This stub posts, because C065's definition of done is about the ledger.</b> "A reversal
    /// posts a balanced journal entry and appears in the driver's ledger and the audit log" is a
    /// claim about <c>billing.journal_postings</c> summing to zero and about a
    /// <c>billing.wallet_transactions</c> line existing — asserting it against a canned
    /// <c>{"entryId": …}</c> would assert nothing at all. So it does what
    /// <c>LedgerService.PostAsync</c> does, against the same Postgres.
    /// </para>
    /// <para>
    /// <b>Faithful, not complete.</b> The idempotency claim, the balanced pair, the two balance
    /// mirrors and the history line are here because admin-bff's behaviour depends on them; the
    /// account lock ordering, the non-negativity refusal and the D-08 Redis write-through are
    /// wallet-svc's own suite's business and admin-bff never observes them.
    /// </para>
    /// </remarks>
    public static void MapInternalWallet(this WebApplication app, PostgresFixture postgres)
    {
        app.MapPost("/v1/internal/wallet/{driverId:guid}/credit", async (
            Guid driverId, LedgerPostingRequest body) =>
        {
            await using var connection = await postgres.OpenAsync();

            var accountId = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO billing.accounts (owner_type, owner_id, currency, balance_minor)
                VALUES ('driver', @DriverId, 'LKR', 0)
                ON CONFLICT (owner_type, owner_id, currency) WHERE owner_id IS NOT NULL DO NOTHING;

                SELECT id FROM billing.accounts
                 WHERE owner_type = 'driver' AND owner_id = @DriverId AND currency = 'LKR';
                """,
                new { DriverId = driverId });

            // The whole idempotency mechanism, in wallet-svc's own spelling: the loser of a race
            // reads what the winner wrote and reports `replayed`.
            var entryId = await connection.ExecuteScalarAsync<Guid?>(
                """
                INSERT INTO billing.journal_entries (kind, idempotency_key, description)
                VALUES (@Kind, @Key, @Description)
                ON CONFLICT (idempotency_key) DO NOTHING
                RETURNING id;
                """,
                new { body.Kind, Key = body.IdempotencyKey, body.Description });

            if (entryId is null)
            {
                var existing = await connection.QuerySingleAsync<(Guid EntryId, long BalanceAfter)>(
                    """
                    SELECT e.id,
                           (SELECT balance_minor FROM billing.accounts WHERE id = @AccountId)
                      FROM billing.journal_entries e WHERE e.idempotency_key = @Key;
                    """,
                    new { Key = body.IdempotencyKey, AccountId = accountId });

                return Results.Json(
                    new
                    {
                        entryId = existing.EntryId,
                        accountId,
                        amountMinor = body.AmountMinor,
                        balanceAfterMinor = existing.BalanceAfter,
                        replayed = true,
                    },
                    MageRideJson.Options);
            }

            // Both legs and both mirrors in one statement, so trg_balanced (DEFERRABLE INITIALLY
            // DEFERRED) sees a balanced entry at COMMIT rather than a half-written one.
            var balance = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO billing.journal_postings (entry_id, account_id, amount_minor)
                VALUES (@EntryId, @AccountId, @AmountMinor);

                INSERT INTO billing.journal_postings (entry_id, account_id, amount_minor)
                SELECT @EntryId, a.id, -@AmountMinor
                  FROM billing.accounts a
                 WHERE a.owner_type = 'platform' AND a.owner_id IS NULL AND a.currency = 'LKR';

                UPDATE billing.accounts SET balance_minor = balance_minor + @AmountMinor
                 WHERE id = @AccountId;

                UPDATE billing.accounts SET balance_minor = balance_minor - @AmountMinor
                 WHERE owner_type = 'platform' AND owner_id IS NULL AND currency = 'LKR';

                INSERT INTO billing.wallets (account_id, balance_minor)
                SELECT @AccountId, a.balance_minor FROM billing.accounts a WHERE a.id = @AccountId
                ON CONFLICT (account_id) DO UPDATE SET balance_minor = EXCLUDED.balance_minor;

                INSERT INTO billing.wallet_transactions
                    (account_id, entry_id, kind, amount_minor, balance_after_minor, description)
                SELECT @AccountId, @EntryId, @Kind, @AmountMinor, a.balance_minor, @Description
                  FROM billing.accounts a WHERE a.id = @AccountId
                ON CONFLICT (account_id, entry_id) DO NOTHING;

                SELECT balance_minor FROM billing.accounts WHERE id = @AccountId;
                """,
                new
                {
                    EntryId = entryId.Value,
                    AccountId = accountId,
                    body.AmountMinor,
                    body.Kind,
                    body.Description,
                });

            return Results.Json(
                new
                {
                    entryId = entryId.Value,
                    accountId,
                    amountMinor = body.AmountMinor,
                    balanceAfterMinor = balance,
                    replayed = false,
                },
                MageRideJson.Options);
        });
    }

    /// <summary>
    /// fare-svc's <c>POST /v1/admin/fare/refund</c> (C050) — a role-gated route, so the caller's own
    /// bearer arrives rather than the shared key, which is one of the things a test asserts.
    /// </summary>
    /// <remarks>
    /// Writes the <c>fares.refunds</c> row because the refund queue then has to stop showing the
    /// payment as an unraised <c>Overpaid</c> — the transition from one population of the union to
    /// the other is a C065 behaviour and a canned reply could not exercise it. The gateway reverse
    /// call and the balanced ledger entry are fare-svc's own suite's.
    /// </remarks>
    public static void MapAdminFare(this WebApplication app, PostgresFixture postgres)
    {
        app.MapPost("/v1/admin/fare/refund", async (FareRefundRequest body) =>
        {
            await using var connection = await postgres.OpenAsync();

            var row = await connection.QuerySingleOrDefaultAsync<(Guid RefundId, string Status, long AmountMinor, string Currency)>(
                """
                INSERT INTO fares.refunds
                    (ride_payment_id, kind, amount_minor, currency, status, reason_code)
                SELECT rp.id, @Kind, @AmountMinor, @Currency, 'Submitted', @ReasonCode
                  FROM fares.ride_payments rp WHERE rp.id = @PaymentId
                RETURNING id, status, amount_minor, currency;
                """,
                new { body.PaymentId, body.Kind, body.AmountMinor, Currency = body.Currency ?? "LKR", body.ReasonCode });

            return row.RefundId == Guid.Empty
                ? Results.Json(
                    new { type = "https://mageride.lk/errors/not-found", detail = "No such payment." },
                    MageRideJson.Options,
                    "application/problem+json",
                    StatusCodes.Status404NotFound)
                : Results.Json(
                    new
                    {
                        refundId = row.RefundId,
                        status = row.Status,
                        amountMinor = row.AmountMinor,
                        currency = row.Currency,
                    },
                    MageRideJson.Options,
                    statusCode: StatusCodes.Status201Created);
        });
    }

    /// <summary>wallet.yaml's <c>LedgerPostingRequest</c>, as the stub receives it.</summary>
    internal sealed record LedgerPostingRequest(
        long AmountMinor, string Kind, string IdempotencyKey, string? Description, string? Reference);

    /// <summary>fare.yaml's refund body.</summary>
    internal sealed record FareRefundRequest(
        Guid PaymentId, string Kind, long AmountMinor, string? Currency, string ReasonCode);
}

/// <summary>
/// Ids the stub answers with, so a test can assert on the row a forward produced.
/// </summary>
/// <remarks>
/// Only the two <em>queue</em> rows are fixed — those are read, not written, so sharing them across
/// tests costs nothing. Anything a test then audits (a resolved report, a published broadcast) gets
/// a fresh id per call, because <c>audit.events</c> is append-only and shared: two tests keyed on
/// one entity would see each other's rows.
/// </remarks>
internal static class SeedIds
{
    public static readonly Guid Report = Guid.Parse("01930000-0000-7000-8000-000000000001");
    public static readonly Guid ReportedVehicle = Guid.Parse("01930000-0000-7000-8000-000000000002");
    public static readonly Guid Ticket = Guid.Parse("01930000-0000-7000-8000-000000000003");
    public static readonly Guid TicketUser = Guid.Parse("01930000-0000-7000-8000-000000000004");
}

/// <summary>What admin-bff sends registry-svc on a payout decision.</summary>
internal sealed record PayoutDecisionRequest(string OfficerId, string? Reason);

/// <summary>What it sends back.</summary>
internal sealed record PayoutDecisionRow(
    string Status, string Bank, string AccountNo, string? RejectionReason, DateTimeOffset? VerifiedAt);
