using System.Net;
using System.Text;
using System.Text.Json;
using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.AdminBff.Tests.Integration;

/// <summary>
/// C065's finance half: reconciliation and its exception queue, the refund queue and its decision,
/// the fee reversal, the transactions report and both exports, and the two review queues.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two of C065's four definition-of-done items are proved here</b> — "a reversal posts a balanced
/// journal entry and appears in the driver's ledger and the audit log" and "reconciliation flags a
/// gateway settlement mismatch into the exception queue". Both are claims about state on the far
/// side of a decision, so both are asserted against the database rather than against a response
/// body: the reversal test reads <c>billing.journal_postings</c>, <c>billing.wallet_transactions</c>
/// and <c>audit.events</c>, and the mismatch test seeds a ledger that disagrees with a gateway
/// session and looks for it in the queue. The other two are <c>PdpaTests</c>'.
/// </para>
/// <para>
/// <b>The wallet and fare stubs post and insert.</b> A canned <c>{"entryId": …}</c> would let the
/// reversal test pass while the ledger was left unbalanced, which is the one failure this component
/// cannot be allowed to have — see <c>StubInternalPlanes.MapInternalWallet</c>.
/// </para>
/// </remarks>
[Collection(AdminBffCollection.Name)]
[Trait("Category", "FinancePdpa")]
public sealed class FinanceTests(PostgresFixture postgres)
{
    // ---------------------------------------------------------------------------------------
    // Reconciliation (DoD 4)
    // ---------------------------------------------------------------------------------------

    /// <summary>DoD: "reconciliation flags a gateway settlement mismatch into the exception queue".</summary>
    [Fact]
    public async Task A_settlement_mismatch_reaches_the_exception_queue()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var fixture = await harness.Seed.SettlementFixtureAsync();
        var bearer = harness.Tokens.Internal(await harness.Seed.InternalUserAsync(MageRideRoles.FinanceOfficer),
            MageRideRoles.FinanceOfficer);

        using var response = await harness.GetAsync(
            "/v1/admin/finance/reconciliation/exceptions?limit=500", bearer);

        using var body = await harness.ReadJsonAsync(response);

        var rows = body.RootElement.EnumerateArray()
            .ToDictionary(row => row.GetProperty("topupId").GetGuid(), row => row);

        Assert.True(
            rows.ContainsKey(fixture.MismatchedTopupId),
            "A session the gateway settled for one amount and the ledger posted for another is the "
            + "reconciliation mismatch D6' §7.2 routes to Finance, and it is not in the queue.");

        var mismatch = rows[fixture.MismatchedTopupId];

        Assert.Equal("amount-mismatch", mismatch.GetProperty("kind").GetString());
        Assert.Equal(fixture.MismatchedMinor, mismatch.GetProperty("amountMinor").GetInt64());
        Assert.Equal(fixture.PostedForMismatchMinor, mismatch.GetProperty("postedMinor").GetInt64());

        // The other three classes, and the one row that is not an exception at all.
        Assert.Equal("settled-not-posted", rows[fixture.UnpostedTopupId].GetProperty("kind").GetString());
        Assert.Equal("unsettled", rows[fixture.StaleTopupId].GetProperty("kind").GetString());
        Assert.Equal("gateway-failed", rows[fixture.FailedTopupId].GetProperty("kind").GetString());

        Assert.False(
            rows.ContainsKey(fixture.MatchedTopupId),
            "A session that settled for what it was opened for and posted the same figure is reconciled; "
            + "a queue that showed it would bury the four that are not.");
    }

    /// <summary>The summary is the same two figures per rail per day, and their difference.</summary>
    [Fact]
    public async Task The_reconciliation_summary_states_the_variance_between_gateway_and_ledger()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var fixture = await harness.Seed.SettlementFixtureAsync();
        var bearer = harness.Tokens.Admin(await harness.Seed.InternalUserAsync(MageRideRoles.Admin));

        using var response = await harness.GetAsync("/v1/admin/finance/reconciliation?method=onepay", bearer);
        using var body = await harness.ReadJsonAsync(response);

        var settled = body.RootElement.GetProperty("settledMinor").GetInt64();
        var posted = body.RootElement.GetProperty("postedMinor").GetInt64();

        Assert.Equal(settled - posted, body.RootElement.GetProperty("varianceMinor").GetInt64());

        // The mismatch's shortfall is inside the window, so the two figures cannot be equal — which
        // is what makes "variance zero means reconciled" a claim worth printing on the screen.
        Assert.True(
            settled - posted >= fixture.MismatchedMinor - fixture.PostedForMismatchMinor,
            $"The window covers a {fixture.MismatchedMinor - fixture.PostedForMismatchMinor} shortfall and "
            + $"the summary reports a variance of {settled - posted}.");

        Assert.True(body.RootElement.GetProperty("exceptionCount").GetInt32() >= 4);
    }

    /// <summary>AL-05: there is no bank-transfer rail, and asking for one is a 400 rather than an empty page.</summary>
    [Fact]
    public async Task There_is_no_bank_transfer_rail_to_reconcile()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Admin(await harness.Seed.InternalUserAsync(MageRideRoles.Admin));

        using var response = await harness.GetAsync(
            "/v1/admin/finance/reconciliation?method=bank_transfer", bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // The fee reversal (DoD 1)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// DoD: "a reversal posts a balanced journal entry and appears in the driver's ledger and the
    /// audit log".
    /// </summary>
    [Fact]
    public async Task A_reversal_posts_a_balanced_entry_reaches_the_ledger_and_is_audited()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (driverId, vehicleId) = await harness.Seed.DriverWithVehicleAsync();
        var feeDate = await harness.Seed.DailyFeeChargeAsync(driverId, vehicleId, amountMinor: 150_00);
        var actorId = await harness.Seed.InternalUserAsync(MageRideRoles.FinanceOfficer);
        var bearer = harness.Tokens.Internal(actorId, MageRideRoles.FinanceOfficer);

        using var response = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/drivers/wallet/{driverId:D}/reverse-fee",
            bearer,
            new { feeDate, vehicleId, reason = "Charged while the driver was suspended" });

        using var body = await harness.ReadJsonAsync(response);

        var entryId = body.RootElement.GetProperty("entryId").GetGuid();

        Assert.Equal(150_00, body.RootElement.GetProperty("amountMinor").GetInt64());
        Assert.False(body.RootElement.GetProperty("replayed").GetBoolean());

        // Balanced: Σ of the entry's legs is zero, which is D-09's whole invariant.
        Assert.Equal(0, await harness.Seed.EntryBalanceAsync(entryId));

        // In the driver's ledger: the wallet-transactions line SCR-AP-013's wallet tab renders.
        var ledger = await harness.Seed.WalletLedgerAsync(driverId);

        Assert.Contains(ledger, line => line.Kind == "adjustment" && line.AmountMinor == 150_00);

        // In the audit log, under its own action and its own entity type.
        var audit = await harness.Seed.AuditRowsAsync(driverId);
        var row = Assert.Single(audit, entry => entry.Action == AdminAuditActions.FeeReversed);

        Assert.Equal(AdminAuditActions.WalletEntity, row.EntityType);
        Assert.Equal(actorId, row.ActorId);
        Assert.Contains("150", row.Before!, StringComparison.Ordinal);
        Assert.Contains("Charged while the driver was suspended", row.After!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ledger key is the business fact, so a second press replays rather than crediting twice.
    /// </summary>
    [Fact]
    public async Task A_second_reversal_of_one_charge_replays_instead_of_paying_twice()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (driverId, vehicleId) = await harness.Seed.DriverWithVehicleAsync();
        var feeDate = await harness.Seed.DailyFeeChargeAsync(driverId, vehicleId, amountMinor: 90_00);
        var bearer = harness.Tokens.SuperAdmin(await harness.Seed.InternalUserAsync(MageRideRoles.SuperAdmin));
        var request = new { feeDate, vehicleId, reason = "double click" };

        using var first = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/drivers/wallet/{driverId:D}/reverse-fee", bearer, request);
        using var second = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/drivers/wallet/{driverId:D}/reverse-fee", bearer, request);

        using var firstBody = await harness.ReadJsonAsync(first);
        using var secondBody = await harness.ReadJsonAsync(second);

        Assert.Equal(
            firstBody.RootElement.GetProperty("entryId").GetGuid(),
            secondBody.RootElement.GetProperty("entryId").GetGuid());

        Assert.True(secondBody.RootElement.GetProperty("replayed").GetBoolean());

        var credits = (await harness.Seed.WalletLedgerAsync(driverId))
            .Count(line => line.Kind == "adjustment");

        Assert.Equal(1, credits);

        // Audited both times regardless: what D-35 records is that an operator performed the action,
        // not that the ledger happened to be in a state where it had an effect.
        Assert.Equal(
            2,
            (await harness.Seed.AuditRowsAsync(driverId))
                .Count(entry => entry.Action == AdminAuditActions.FeeReversed));
    }

    /// <summary>A reversal compensates a charge that exists, and cannot exceed it.</summary>
    [Fact]
    public async Task A_reversal_is_refused_without_a_charge_or_beyond_one()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (driverId, vehicleId) = await harness.Seed.DriverWithVehicleAsync();
        var bearer = harness.Tokens.SuperAdmin(await harness.Seed.InternalUserAsync(MageRideRoles.SuperAdmin));

        using var noCharge = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/drivers/wallet/{driverId:D}/reverse-fee",
            bearer,
            new { feeDate = new DateOnly(2026, 1, 1), vehicleId, reason = "nothing was charged" });

        Assert.Equal(HttpStatusCode.NotFound, noCharge.StatusCode);

        var feeDate = await harness.Seed.DailyFeeChargeAsync(driverId, vehicleId, amountMinor: 100_00);

        using var tooMuch = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/drivers/wallet/{driverId:D}/reverse-fee",
            bearer,
            new { feeDate, vehicleId, amountMinor = 500_00, reason = "more than was taken" });

        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);

        // D-13's waived first trip moved no money, so there is nothing to give back.
        var (waivedDriver, waivedVehicle) = await harness.Seed.DriverWithVehicleAsync();
        var waivedDate = await harness.Seed.DailyFeeChargeAsync(
            waivedDriver, waivedVehicle, amountMinor: 0, status: "WAIVED_FIRST_TRIP");

        using var waived = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/drivers/wallet/{waivedDriver:D}/reverse-fee",
            bearer,
            new { feeDate = waivedDate, vehicleId = waivedVehicle, reason = "first trip was free" });

        Assert.Equal(HttpStatusCode.Conflict, waived.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // The refund queue (E-05, R-19)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// R-19: a payment that reached <c>Overpaid</c> with no refund raised is in the queue, and
    /// raising one moves it from the unraised half of the union to the raised half.
    /// </summary>
    [Fact]
    public async Task An_overpaid_payment_is_queued_until_a_refund_is_raised_through_fare_svc()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, paymentId, _, amountMinor) = await harness.Seed.OverpaidPaymentAsync();
        var actorId = await harness.Seed.InternalUserAsync(MageRideRoles.FinanceOfficer);
        var bearer = harness.Tokens.Internal(actorId, MageRideRoles.FinanceOfficer);

        using var before = await harness.GetAsync("/v1/admin/finance/refunds?limit=500", bearer);
        using var beforeBody = await harness.ReadJsonAsync(before);

        var queued = beforeBody.RootElement.EnumerateArray()
            .Single(row => row.GetProperty("paymentId").GetGuid() == paymentId);

        Assert.Equal("overpaid", queued.GetProperty("source").GetString());
        Assert.Equal("Overpaid", queued.GetProperty("paymentState").GetString());
        Assert.Equal(amountMinor, queued.GetProperty("paymentAmountMinor").GetInt64());

        using var issued = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/admin/finance/refunds",
            bearer,
            new { paymentId, kind = "overpaid_reversal", reasonCode = "late_callback" });

        using var issuedBody = await harness.ReadJsonAsync(issued);

        Assert.Equal("Submitted", issuedBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(amountMinor, issuedBody.RootElement.GetProperty("amountMinor").GetInt64());

        // fare-svc got the operator's own bearer, not the shared internal key: its refund route is
        // role-gated, and sending the key would bypass a check that exists.
        var forwarded = harness.Upstream.Last("/v1/admin/fare/refund");

        Assert.Null(forwarded.InternalKey);
        Assert.NotNull(forwarded.Authorization);

        using var after = await harness.GetAsync("/v1/admin/finance/refunds?limit=500", bearer);
        using var afterBody = await harness.ReadJsonAsync(after);

        var raised = afterBody.RootElement.EnumerateArray()
            .Single(row => row.GetProperty("paymentId").GetGuid() == paymentId);

        Assert.Equal("refund", raised.GetProperty("source").GetString());
        Assert.NotEqual(Guid.Empty, raised.GetProperty("refundId").GetGuid());

        var audit = await harness.Seed.AuditRowsAsync(paymentId);
        var row = Assert.Single(audit, entry => entry.Action == AdminAuditActions.RefundIssued);

        Assert.Equal(AdminAuditActions.PaymentEntity, row.EntityType);
        Assert.Equal(actorId, row.ActorId);
        Assert.Contains("late_callback", row.After!, StringComparison.Ordinal);
    }

    /// <summary>A refund cannot exceed what the payment collected, and a partial must say how much.</summary>
    [Fact]
    public async Task A_refund_is_bounded_by_what_was_collected()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, paymentId, _, amountMinor) = await harness.Seed.OverpaidPaymentAsync();
        var bearer = harness.Tokens.Internal(
            await harness.Seed.InternalUserAsync(MageRideRoles.FinanceOfficer), MageRideRoles.FinanceOfficer);

        using var tooMuch = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/admin/finance/refunds",
            bearer,
            new { paymentId, kind = "full", amountMinor = amountMinor + 1, reasonCode = "typo" });

        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);

        using var partialWithoutAmount = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/admin/finance/refunds",
            bearer,
            new { paymentId, kind = "partial", reasonCode = "goodwill" });

        Assert.Equal(HttpStatusCode.BadRequest, partialWithoutAmount.StatusCode);

        using var noSuchPayment = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/admin/finance/refunds",
            bearer,
            new { paymentId = Guid.CreateVersion7(), kind = "full", reasonCode = "x" });

        Assert.Equal(HttpStatusCode.NotFound, noSuchPayment.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // The transactions report and its two exports
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The four kinds the deliverable names, one row per money event rather than per account leg.
    /// </summary>
    [Fact]
    public async Task The_transactions_report_covers_the_four_kinds_once_each()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var fixture = await harness.Seed.TransactionsFixtureAsync();
        var bearer = harness.Tokens.Internal(
            await harness.Seed.InternalUserAsync(MageRideRoles.FinanceOfficer), MageRideRoles.FinanceOfficer);

        using var response = await harness.GetAsync(
            $"/v1/admin/finance/transactions?partyId={fixture.DriverId:D}&limit=500", bearer);

        using var body = await harness.ReadJsonAsync(response);

        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var kinds = items.Select(item => item.GetProperty("kind").GetString()).ToArray();

        Assert.Equal(1, kinds.Count(kind => kind == "topup"));
        Assert.Equal(1, kinds.Count(kind => kind == "daily_fee"));
        Assert.Equal(1, kinds.Count(kind => kind == "voucher_purchase"));

        // The transfer has two wallet legs and must still be ONE row — reading the projection
        // instead of the journal would double the platform's transfer volume.
        Assert.Equal(1, kinds.Count(kind => kind == "driver_transfer"));

        var transfer = items.Single(item => item.GetProperty("kind").GetString() == "driver_transfer");

        Assert.Equal(fixture.DriverId, transfer.GetProperty("fromPartyId").GetGuid());
        Assert.Equal(fixture.RecipientId, transfer.GetProperty("toPartyId").GetGuid());
        Assert.Equal(fixture.TransferMinor, transfer.GetProperty("amountMinor").GetInt64());

        // A top-up is platform → driver; the platform's singleton account has no owner by CHECK.
        var topup = items.Single(item => item.GetProperty("kind").GetString() == "topup");

        Assert.Equal("platform", topup.GetProperty("fromAccountType").GetString());
        Assert.Equal("driver", topup.GetProperty("toAccountType").GetString());
        Assert.Equal(fixture.DriverId, topup.GetProperty("toPartyId").GetGuid());
    }

    /// <summary>Both exports render the rows the JSON route returned for the same query.</summary>
    [Fact]
    public async Task Both_exports_render_the_same_rows_as_the_screen()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var fixture = await harness.Seed.TransactionsFixtureAsync();
        var bearer = harness.Tokens.Internal(
            await harness.Seed.InternalUserAsync(MageRideRoles.FinanceOfficer), MageRideRoles.FinanceOfficer);

        var query = $"?partyId={fixture.DriverId:D}&limit=500";

        using var csvResponse = await harness.GetAsync($"/v1/admin/finance/transactions.csv{query}", bearer);

        Assert.True(csvResponse.IsSuccessStatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);

        var csv = Encoding.UTF8.GetString(await csvResponse.Content.ReadAsByteArrayAsync());

        Assert.Contains("# money,integer minor units (LKR cents)", csv, StringComparison.Ordinal);
        Assert.Contains("ts,kind,amountMinor", csv, StringComparison.Ordinal);
        Assert.Contains(fixture.TransferMinor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            csv, StringComparison.Ordinal);

        // Four money events, four data rows — the same count the JSON route answers with.
        var dataRows = csv.Split("\r\n")
            .Where(line => line.StartsWith('"'))
            .ToArray();

        Assert.Equal(4, dataRows.Length);

        using var pdfResponse = await harness.GetAsync($"/v1/admin/finance/transactions.pdf{query}", bearer);

        Assert.True(pdfResponse.IsSuccessStatusCode);
        Assert.Equal("application/pdf", pdfResponse.Content.Headers.ContentType?.MediaType);

        var pdf = await pdfResponse.Content.ReadAsByteArrayAsync();
        var text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        Assert.Contains("driver_transfer", text, StringComparison.Ordinal);

        // The cross-reference offsets are the format's own integrity check: object 1 has to actually
        // begin where the xref says it does, or a strict reader refuses the file.
        var xrefIndex = text.LastIndexOf("startxref", StringComparison.Ordinal);
        var start = int.Parse(
            text[(xrefIndex + 9)..].Trim().Split('\n')[0],
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("xref", text.Substring(start, 4));

        // After `xref\n` come the subsection header (`0 N`), then object 0's free entry, then
        // object 1's — each entry exactly 20 bytes, which is what the format requires.
        var entries = text[(start + "xref\n".Length)..].Split('\n');
        var firstOffset = int.Parse(entries[2][..10], System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("1 0 obj", text.Substring(firstOffset, 7));
    }

    /// <summary>An inverted or over-wide window is a named 400, never a silently swapped one.</summary>
    [Fact]
    public async Task A_nonsense_reporting_window_is_refused()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Admin(await harness.Seed.InternalUserAsync(MageRideRoles.Admin));

        using var inverted = await harness.GetAsync(
            "/v1/admin/finance/transactions?from=2026-08-01&to=2026-07-01", bearer);

        Assert.Equal(HttpStatusCode.BadRequest, inverted.StatusCode);

        using var tooWide = await harness.GetAsync(
            "/v1/admin/finance/transactions?from=2020-01-01&to=2026-08-01", bearer);

        Assert.Equal(HttpStatusCode.BadRequest, tooWide.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // The two review queues
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// E-03: the queue holds the current document of each (subject, kind) and never the superseded
    /// copy a renewal left behind.
    /// </summary>
    [Fact]
    public async Task The_document_expiry_queue_shows_the_current_document_and_the_audited_links()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, _, currentDocId, supersededDocId) = await harness.Seed.ExpiringDocumentAsync(inDays: 10);
        var bearer = harness.Tokens.Internal(
            await harness.Seed.InternalUserAsync(MageRideRoles.VerificationOfficer),
            MageRideRoles.VerificationOfficer);

        using var response = await harness.GetAsync("/v1/admin/documents/expiring?limit=500", bearer);
        using var body = await harness.ReadJsonAsync(response);

        var rows = body.RootElement.EnumerateArray()
            .ToDictionary(row => row.GetProperty("docId").GetGuid(), row => row);

        Assert.True(rows.ContainsKey(currentDocId));
        Assert.False(
            rows.ContainsKey(supersededDocId),
            "A renewal supersedes rather than replaces, so the older row keeps a date in the past. "
            + "A queue that listed it would show a backlog that has already been dealt with.");

        var current = rows[currentDocId];

        Assert.InRange(current.GetProperty("daysRemaining").GetInt32(), 9, 11);
        Assert.Equal(30, current.GetProperty("lastNoticeDays").GetInt32());

        // AL-39: the links are the audited viewer's, never a bucket URL — one look is one DOC_VIEW row.
        Assert.StartsWith(
            $"/v1/admin/documents/{currentDocId:D}", current.GetProperty("thumbUrl").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// E-07: the fraud queue is reputation-svc's rows joined to the names it cannot supply, and it
    /// points the decision back at reputation-svc.
    /// </summary>
    [Fact]
    public async Task The_fraud_queue_names_its_subjects_and_defers_the_decision()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (flagId, subjectId, _, subjectName) = await harness.Seed.FraudFlagAsync();
        var resolved = await harness.Seed.FraudFlagAsync(status: "dismissed");
        var bearer = harness.Tokens.Admin(await harness.Seed.InternalUserAsync(MageRideRoles.Admin));

        using var response = await harness.GetAsync("/v1/admin/fraud/queue?limit=500", bearer);
        using var body = await harness.ReadJsonAsync(response);

        var ids = body.RootElement.EnumerateArray()
            .ToDictionary(row => row.GetProperty("flagId").GetGuid(), row => row);

        Assert.True(ids.ContainsKey(flagId));
        Assert.False(
            ids.ContainsKey(resolved.FlagId),
            "A review queue's job is what has not been reviewed; a dismissed flag is a filter away.");

        var flag = ids[flagId];

        Assert.Equal(subjectName, flag.GetProperty("subjectName").GetString());
        Assert.Equal(subjectId, flag.GetProperty("subjectId").GetGuid());
        Assert.Equal(11, flag.GetProperty("detail").GetProperty("rides").GetInt32());
        Assert.Equal(
            $"/v1/admin/reputation/flags/{flagId:D}/resolve", flag.GetProperty("resolveUrl").GetString());

        // No write reaches reputation-svc from here: this component reads the queue and nothing else.
        Assert.DoesNotContain(
            harness.Upstream.Calls, call => call.Path.Contains("/reputation/", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // The C065 fence, asserted against the route table
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The finance surface writes nothing itself: every mutation it offers is forwarded to the
    /// service that owns the rows.
    /// </summary>
    /// <remarks>
    /// Asserted against the running route table rather than in prose, in the same shape as C064's
    /// <c>No_directory_route_accepts_a_write</c>: a later component cannot hang a ledger write off a
    /// finance path without this failing.
    /// </remarks>
    [Fact]
    public async Task Only_two_finance_routes_mutate_and_both_forward()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var mutations = harness.Routes
            .Where(route => route.RoutePattern.RawText!.Contains("/finance/", StringComparison.Ordinal)
                            || route.RoutePattern.RawText!.Contains("/reverse-fee", StringComparison.Ordinal)
                            || route.RoutePattern.RawText!.Contains("/fraud/", StringComparison.Ordinal)
                            || route.RoutePattern.RawText!.Contains("/documents/expiring", StringComparison.Ordinal))
            .Where(AuditInterceptor.IsMutating)
            .Select(route => route.RoutePattern.RawText!)
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["/v1/admin/drivers/wallet/{driverId:guid}/reverse-fee", "/v1/admin/finance/refunds"],
            mutations);
    }

    /// <summary>
    /// The wallet reversal is Finance and Super Admin, exactly as URD §2.3's own row says — and an
    /// Admin, who holds ✅ on refunds, is 👁 here and refused.
    /// </summary>
    /// <remarks>
    /// <see cref="RbacMatrixTests"/> already drives every role against every route from the matrix.
    /// This asserts the surprising cell by name, for the same reason iam-svc asserts "an Admin is
    /// refused role management": C065's fence is written as "Finance/Super-Admin only" and it is
    /// worth proving that the row and the sentence agree rather than assuming it.
    /// </remarks>
    [Fact]
    public async Task Only_finance_and_super_admin_may_reverse_a_fee()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (driverId, vehicleId) = await harness.Seed.DriverWithVehicleAsync();
        var feeDate = await harness.Seed.DailyFeeChargeAsync(driverId, vehicleId, amountMinor: 50_00);
        var path = $"/v1/admin/drivers/wallet/{driverId:D}/reverse-fee";

        foreach (var role in MageRideRoles.Internal)
        {
            using var response = await harness.SendAsync(
                HttpMethod.Post,
                path,
                harness.Tokens.Internal(Guid.NewGuid(), role),
                new { feeDate, vehicleId, reason = "matrix probe" });

            var permitted = role is MageRideRoles.FinanceOfficer or MageRideRoles.SuperAdmin;

            Assert.True(
                permitted != (response.StatusCode == HttpStatusCode.Forbidden),
                $"{role} got {(int)response.StatusCode} on the fee reversal; URD §2.3's "
                + "'Driver wallet adjustments / reversals' row gives ✅ to Super Admin and Finance only.");
        }
    }

    /// <summary>A Support CSR may open the refund queue and may not execute one (`◐ raise/recommend`).</summary>
    [Fact]
    public async Task A_csr_sees_the_refund_queue_and_cannot_execute_one()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, paymentId, _, _) = await harness.Seed.OverpaidPaymentAsync();
        var bearer = harness.Tokens.Internal(
            await harness.Seed.InternalUserAsync(MageRideRoles.SupportCsr), MageRideRoles.SupportCsr);

        using var queue = await harness.GetAsync("/v1/admin/finance/refunds", bearer);

        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);

        using var issue = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/admin/finance/refunds",
            bearer,
            new { paymentId, kind = "full", reasonCode = "csr probe" });

        Assert.Equal(HttpStatusCode.Forbidden, issue.StatusCode);
    }

    /// <summary>An unconfigured upstream is a 503 on a route that is still mapped and still gated.</summary>
    [Fact]
    public async Task An_unconfigured_wallet_upstream_answers_503_rather_than_unmapping_the_route()
    {
        await using var harness = await AdminBffHarness.StartAsync(
            postgres, new Dictionary<string, string?> { ["AdminBff:Upstreams:Wallet:BaseUrl"] = string.Empty });

        var (driverId, vehicleId) = await harness.Seed.DriverWithVehicleAsync();
        var feeDate = await harness.Seed.DailyFeeChargeAsync(driverId, vehicleId, amountMinor: 60_00);

        using var response = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/drivers/wallet/{driverId:D}/reverse-fee",
            harness.Tokens.SuperAdmin(await harness.Seed.InternalUserAsync(MageRideRoles.SuperAdmin)),
            new { feeDate, vehicleId, reason = "no wallet-svc on this deployment" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Contains(
            "dependency-unavailable",
            problem.RootElement.GetProperty("type").GetString()!,
            StringComparison.Ordinal);
    }
}
