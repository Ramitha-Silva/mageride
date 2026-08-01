using System.Globalization;
using MageRide.FleetBilling.Authorization;
using MageRide.FleetBilling.Billing;
using MageRide.FleetBilling.Configuration;
using MageRide.FleetBilling.Domain;
using MageRide.FleetBilling.Export;
using MageRide.FleetBilling.Money;
using MageRide.FleetBilling.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.FleetBilling.Endpoints;

/// <summary>
/// <c>/v1/fleets/{fleetId}/billing/**</c> and <c>/v1/fleets/{fleetId}/wallet/**</c> — the Fleet
/// Portal's billing screen (SCR-FP-010).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two paths under <c>/v1/fleets</c> that fleet-svc does not own.</b> D3' lists
/// <c>GET /v1/fleets/{id}/billing</c> and <c>POST …/wallet/topup</c> in the fleet-svc route table
/// while ADD §6 gives the fleet wallet and the monthly invoicing to <b>fleet-billing-svc</b> — the
/// same split D3' makes for <c>POST /v1/fleets/{fleetId}/trackers/bulk</c> (provisioning-svc, C007)
/// and <c>GET /v1/fleets/{id}/health</c> (fleet-health-svc, C044). The gateway resolves a cluster
/// from the contract that declares the operation, so these live in
/// <c>backend/contracts/fleet-billing.yaml</c>; <c>fleet.yaml</c> drops both and says where they
/// went. C059's handoff left them deliberately unmapped for this component.
/// </para>
/// <para>
/// <b>Every route on one group, and the group carries the whole gate.</b>
/// <see cref="FleetBillingAccessFilter"/> is the only place the organisation, the sub-role and the
/// approval status are checked, so a route added here later is gated the moment it is mapped — and
/// <c>Every_billing_route_is_owner_only</c> walks the endpoint data source to keep that true for
/// whoever comes next. Unlike fleet-svc, the reads are gated too: US-13.A5 gives billing to the
/// Owner and takes it from the Manager in the same sentence.
/// </para>
/// </remarks>
public static class FleetBillingEndpoints
{
    /// <summary>The prefix every route here lives under.</summary>
    public const string FleetGroup = "/v1/fleets/{fleetId}";

    public static IEndpointRouteBuilder MapFleetBillingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(FleetGroup)
            .WithTags("fleet-billing")
            .RequireAuthorization()
            .AddEndpointFilter<FleetBillingAccessFilter>();

        group.MapGet("/billing", ListInvoicesAsync).WithName("getFleetBilling");
        group.MapGet("/billing/{invoiceId}", GetInvoiceAsync).WithName("getFleetInvoice");
        group.MapGet("/billing/{invoiceId}/export", ExportInvoiceAsync).WithName("exportFleetInvoice");
        group.MapGet("/billing/{invoiceId}/receipt", GetReceiptAsync).WithName("getFleetInvoiceReceipt");
        group.MapPost("/billing/{invoiceId}/pay", PayInvoiceAsync).WithName("payFleetInvoice");

        group.MapGet("/wallet", GetWalletAsync).WithName("getFleetWallet");
        group.MapPost("/wallet/topup", TopupAsync).WithName("topupFleetWallet");
        group.MapGet("/wallet/topup/{topupId}", GetTopupAsync).WithName("getFleetTopup");

        return endpoints;
    }

    // ---------------------------------------------------------------------------------------
    // Invoices
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// The cursor is the period month of the last row — safe as a bare keyset column here and
    /// nowhere else in this codebase, because <c>ux_fleet_invoices_fleet_period</c> makes (fleet,
    /// month) unique and there are therefore no ties to straddle a page boundary.
    /// </remarks>
    private static async Task<Ok<CursorPage<FleetInvoiceResponse>>> ListInvoicesAsync(
        HttpContext http,
        IFleetInvoiceRepository invoices,
        IOptions<FleetBillingOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(options);

        var fleet = http.RequireFleet();
        var page = PageRequest.FromQuery(http.Request);
        var limit = Math.Min(page.Limit, options.Value.MaxPageSize);

        DateOnly? before = null;

        if (page.Cursor is not null)
        {
            if (!CursorCodec.Unsigned.TryDecodeString(page.Cursor, out var raw)
                || !DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["cursor"] = ["cursor is not a cursor this endpoint issued."],
                });
            }

            before = parsed;
        }

        var rows = await invoices.ListAsync(fleet.FleetId, before, limit + 1, cancellationToken);

        return TypedResults.Ok(
            CursorPage<FleetInvoice>
                .FromOverfetch(rows, limit, static invoice =>
                    CursorCodec.Unsigned.EncodeString(invoice.PeriodMonth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .Select(Project));
    }

    private static async Task<Ok<FleetInvoiceDetailResponse>> GetInvoiceAsync(
        string invoiceId,
        HttpContext http,
        IFleetInvoiceRepository invoices,
        CancellationToken cancellationToken)
    {
        var detail = await RequireDetailAsync(invoiceId, http, invoices, cancellationToken);

        return TypedResults.Ok(new FleetInvoiceDetailResponse(
            Project(detail.Invoice),
            [.. detail.Lines.Select(ProjectLine)],
            detail.LineSumMinor));
    }

    /// <remarks>
    /// <para>
    /// One route for both formats rather than two, because the document is the same document: the
    /// CSV is what an accounts department reconciles and the PDF is what they file, and a client
    /// that offers a Download button with a format picker should not have to know two paths.
    /// </para>
    /// <para>
    /// <c>Content-Disposition</c> names the file after the organisation's month, so a folder of
    /// downloads is readable without opening them.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ExportInvoiceAsync(
        string invoiceId,
        string? format,
        HttpContext http,
        IFleetInvoiceRepository invoices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        var fleet = http.RequireFleet();
        var detail = await RequireDetailAsync(invoiceId, http, invoices, cancellationToken);
        var chosen = (format ?? "csv").Trim().ToLowerInvariant();

        var (bytes, contentType, extension) = chosen switch
        {
            "csv" => (InvoiceCsv.Render(detail, fleet.FleetName), "text/csv; charset=utf-8", "csv"),
            "pdf" => (InvoicePdf.Render(detail, fleet.FleetName), "application/pdf", "pdf"),
            _ => throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["format"] = [$"'{format}' is not an export format. This endpoint serves csv and pdf."],
            }),
        };

        var name = $"mageride-invoice-{detail.Invoice.PeriodMonth:yyyy-MM}-{detail.Invoice.Id}.{extension}";

        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";

        return Results.File(bytes, contentType);
    }

    private static async Task<Ok<FleetInvoiceReceiptResponse>> GetReceiptAsync(
        string invoiceId,
        HttpContext http,
        IFleetInvoiceRepository invoices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        var fleet = http.RequireFleet();
        var invoice = await RequireInvoiceAsync(invoiceId, http, invoices, cancellationToken);

        // A receipt is evidence that money moved. An unpaid invoice has none, and answering with an
        // empty one would let a client render a receipt for a bill nobody paid.
        if (invoice.Status != InvoiceStatuses.Paid
            || invoice.SettledAt is not { } settledAt
            || invoice.JournalEntryId is not { } entryId)
        {
            throw new MageRideException(
                MageRideErrors.NotFound,
                $"Invoice {invoice.Id} is {invoice.Status}; a receipt exists only for a settled invoice.");
        }

        return TypedResults.Ok(new FleetInvoiceReceiptResponse(
            invoice.Id,
            invoice.FleetId,
            fleet.FleetName,
            invoice.PeriodMonth,
            invoice.TotalMinor,
            invoice.Currency,
            invoice.VehicleCount,
            settledAt,
            entryId));
    }

    /// <remarks>
    /// The Pay button. Settlement also happens by itself on the runner's tick
    /// (<c>FleetBilling:AutoSettle</c>), so this exists for the operator who has just topped up and
    /// does not want to wait — which is why it answers the settled invoice rather than a 202.
    /// </remarks>
    private static async Task<Ok<FleetInvoiceResponse>> PayInvoiceAsync(
        string invoiceId,
        HttpContext http,
        IFleetInvoiceRepository invoices,
        IInvoiceSettlementService settlement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        var invoice = await RequireInvoiceAsync(invoiceId, http, invoices, cancellationToken);
        var outcome = await settlement.SettleAsync(invoice, cancellationToken);

        if (outcome.Insufficient)
        {
            // 402, carried through with the code the portal already branches on. Not reshaped into a
            // 503: this is a balance an operator can top up, not an outage they can wait out.
            throw new MageRideException(
                MageRideErrors.InsufficientWallet,
                $"The fleet wallet cannot cover {invoice.TotalMinor} minor units. Top up and try again.");
        }

        return TypedResults.Ok(Project(outcome.Invoice));
    }

    // ---------------------------------------------------------------------------------------
    // The wallet
    // ---------------------------------------------------------------------------------------

    private static async Task<Ok<FleetWalletResponse>> GetWalletAsync(
        HttpContext http,
        IFleetWalletRepository wallets,
        IOptions<FleetBillingOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(wallets);
        ArgumentNullException.ThrowIfNull(options);

        var fleet = http.RequireFleet();
        var page = PageRequest.FromQuery(http.Request);
        var limit = Math.Min(page.Limit, options.Value.MaxPageSize);

        var summary = await wallets.ReadSummaryAsync(fleet.FleetId, cancellationToken);
        var movements = await wallets.ReadMovementsAsync(fleet.FleetId, null, null, limit, cancellationToken);

        return TypedResults.Ok(new FleetWalletResponse(
            summary.BalanceMinor,
            summary.OutstandingMinor,
            summary.AvailableMinor,
            summary.Currency,
            summary.UpdatedAt,
            [
                .. movements.Select(movement => new FleetWalletMovementResponse(
                    movement.EntryId,
                    movement.Kind,
                    movement.AmountMinor,
                    movement.BalanceAfterMinor,
                    movement.Description,
                    movement.Ts)),
            ]));
    }

    private static async Task<Ok<FleetTopupResponse>> TopupAsync(
        FleetTopupBody? body,
        HttpContext http,
        IFleetTopupService topups,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(topups);

        var fleet = http.RequireFleet();

        if (body?.AmountMinor is not { } amountMinor || amountMinor <= 0)
        {
            throw new MageRideException(
                MageRideErrors.InvalidAmount, "amountMinor is required and must be positive.");
        }

        var method = body.Method?.Trim().ToLowerInvariant() ?? string.Empty;

        var (topup, session) = await topups.StartAsync(
            fleet.FleetId, fleet.UserId, method, amountMinor, body.ReturnUrl, cancellationToken);

        return TypedResults.Ok(new FleetTopupResponse(
            topup.Id,
            topup.State,
            topup.AmountMinor,
            topup.Currency,
            topup.Method,
            session.RedirectUrl,
            session.SessionToken,
            session.PaymentLink,
            session.QrPayload,
            topup.CreatedAt,
            Expired: false));
    }

    /// <remarks>
    /// The poll a client runs over D6' §7.1's 90-second window. The session artefacts are not
    /// returned here and never were stored: a redirect URL, a session token and a QR payload are
    /// single-use, seconds-lived instruments of one gateway session, and handing one back long after
    /// it stopped working would be worse than not having it.
    /// </remarks>
    private static async Task<Ok<FleetTopupResponse>> GetTopupAsync(
        string topupId,
        HttpContext http,
        IFleetTopupRepository topups,
        IFleetTopupService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(topups);
        ArgumentNullException.ThrowIfNull(service);

        var fleet = http.RequireFleet();
        var id = RequestIds.Require(topupId, "topupId");

        var topup = await topups.FindAsync(fleet.FleetId, id, cancellationToken)
                    ?? throw new MageRideException(MageRideErrors.NotFound, "No such top-up session.");

        return TypedResults.Ok(new FleetTopupResponse(
            topup.Id,
            topup.State,
            topup.AmountMinor,
            topup.Currency,
            topup.Method,
            RedirectUrl: null,
            SessionToken: null,
            PaymentLink: null,
            QrPayload: null,
            topup.CreatedAt,
            Expired: topup.State == TopupStates.Pending && !service.IsWithinPendingWindow(topup)));
    }

    // ---------------------------------------------------------------------------------------
    // Shared
    // ---------------------------------------------------------------------------------------

    private static async Task<FleetInvoice> RequireInvoiceAsync(
        string invoiceId,
        HttpContext http,
        IFleetInvoiceRepository invoices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(invoices);

        var fleet = http.RequireFleet();
        var id = RequestIds.Require(invoiceId, "invoiceId");

        // The organisation is in the query, not checked afterwards: another fleet's invoice is a 404
        // and never a row this service then decides to hide.
        return await invoices.FindAsync(fleet.FleetId, id, cancellationToken)
               ?? throw new MageRideException(MageRideErrors.NotFound, "No such invoice.");
    }

    private static async Task<FleetInvoiceDetail> RequireDetailAsync(
        string invoiceId,
        HttpContext http,
        IFleetInvoiceRepository invoices,
        CancellationToken cancellationToken)
    {
        var invoice = await RequireInvoiceAsync(invoiceId, http, invoices, cancellationToken);
        var lines = await invoices.ReadLinesAsync(invoice.Id, cancellationToken);

        return new FleetInvoiceDetail(invoice, lines);
    }

    private static FleetInvoiceResponse Project(FleetInvoice invoice) =>
        new(invoice.Id,
            invoice.PeriodMonth,
            invoice.PeriodMonthTzAt,
            invoice.VehicleCount,
            invoice.TotalMinor,
            invoice.Currency,
            invoice.Status,
            invoice.DueAt,
            invoice.OverdueAt,
            invoice.SettledAt,
            invoice.JournalEntryId);

    private static FleetInvoiceLineResponse ProjectLine(FleetInvoiceLine line) =>
        new(line.VehicleId,
            line.RegistrationNumber,
            line.VehicleType,
            line.AmountMinor,
            line.Currency,
            line.Status);
}
