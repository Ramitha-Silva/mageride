using System.Security.Claims;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using MageRide.Subscriptions.Configuration;
using MageRide.Subscriptions.Domain;
using MageRide.Subscriptions.Fees;
using MageRide.Subscriptions.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.Subscriptions.Endpoints;

/// <summary>
/// <c>/v1/fees</c> — the rate ladder, today's status, the deduction history and the US-9.23 refund
/// intake.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drivers never access the web portal</b> (URD §Epic 9's rationale), so every one of these is a
/// Driver-App API. A <c>{driverId}</c> in the path is checked against the token in one place
/// (<see cref="SubjectScope"/>) because the rule has to be identical on all four routes that carry one;
/// the six back-office roles pass, for the Admin Portal's finance tabs, and the D-35 audit for that
/// read is admin-bff's.
/// </para>
/// <para>
/// <b><c>GET /v1/fees/rates</c> is not scoped to a driver and is not admin-only.</b> The rate ladder is
/// what the Driver App draws on the plans screen before a driver has chosen a vehicle, so it answers
/// for any authenticated caller — it is a price list, and there is nothing in it that is not already
/// printed in the URD.
/// </para>
/// </remarks>
public static class FeeEndpoints
{
    public static IEndpointRouteBuilder MapFeeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var fees = endpoints.MapGroup("/v1/fees").WithTags("fees").RequireAuthorization();

        // Literal before template: `rates` is a fixed segment and `{driverId}` would otherwise match it
        // and fail as a malformed ULID rather than serving the ladder.
        fees.MapGet("/rates", ListRatesAsync).WithName("listDailyFeeRates");
        fees.MapGet("/{driverId}/today", GetTodayAsync).WithName("getTodaysDailyFee");
        fees.MapGet("/{driverId}/history", GetHistoryAsync).WithName("listDailyFeeHistory");

        fees.MapPost("/{driverId}/refund-requests", RequestRefundAsync).WithName("requestDailyFeeRefund");
        fees.MapGet("/{driverId}/refund-requests", ListRefundsAsync).WithName("listDailyFeeRefundRequests");

        return endpoints;
    }

    private static async Task<Ok<DailyFeeRatesResponse>> ListRatesAsync(
        IPlanRepository plans, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var rates = await plans.ListAsync(cancellationToken);

        return TypedResults.Ok(new DailyFeeRatesResponse(
            [.. rates.Select(rate => new DailyFeeRateResponse(
                rate.VehicleType, rate.DailyFeeMinor, rate.Mode, rate.Currency))]));
    }

    private static async Task<Ok<TodaysFeeResponse>> GetTodayAsync(
        string driverId,
        HttpContext context,
        DailyFeeService fees,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fees);

        var driver = SubjectScope.Require(context.User, driverId);
        var today = await fees.TodayAsync(driver, cancellationToken);

        return TypedResults.Ok(new TodaysFeeResponse(
            today.VehicleType,
            today.VehicleId,
            today.DailyRateMinor,
            today.Status,
            today.DeductedMinor,
            today.TripsToday,
            today.FirstTripFree,
            today.FeeDate,
            today.FeeDateTzAt,
            today.Currency));
    }

    /// <remarks>
    /// <c>?from=</c> and <c>?to=</c> are inclusive Colombo business dates (D-38), which is what a driver
    /// means by "June": one row per charged or waived day, and a single-day window returns that day.
    /// </remarks>
    private static async Task<Ok<CursorPage<DailyFeeChargeResponse>>> GetHistoryAsync(
        string driverId,
        string? from,
        string? to,
        HttpContext context,
        IDailyFeeRepository charges,
        IOptions<SubscriptionOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(charges);
        ArgumentNullException.ThrowIfNull(options);

        var driver = SubjectScope.Require(context.User, driverId);
        var page = PageRequest.FromQuery(context.Request);

        var window = (
            From: BusinessDates.Optional(from, "from"),
            To: BusinessDates.Optional(to, "to"));

        if (window is { From: { } start, To: { } end } && start > end)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["from"] = ["from must not be later than to."],
            });
        }

        var rows = await charges.HistoryAsync(
            driver,
            window.From,
            window.To,
            HistoryCursor.Decode(page.Cursor),
            Math.Min(page.OverfetchLimit, options.Value.MaxHistoryRows),
            cancellationToken);

        var result = CursorPage<DailyFeeCharge>.FromOverfetch(rows, page.Limit, HistoryCursor.Encode);

        return TypedResults.Ok(result.Select(charge => new DailyFeeChargeResponse(
            charge.DriverId,
            charge.VehicleId,
            charge.FeeDate,
            charge.FeeDateTzAt,
            charge.AmountMinor,
            charge.Currency,
            charge.TripsThatDay,
            charge.Status,
            charge.ChargedAt)));
    }

    /// <summary>
    /// US-9.23's "request a refund for a daily fee charged in error" — the intake, not the reversal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Validated against this service's own charge row, which is the reason the route lives here.</b>
    /// A driver disputing a day they were not charged for is a ticket Finance has to open, read and
    /// close by hand; a <c>404</c> with the reason costs nobody anything. The waived first trip is
    /// refused for the same reason — there is nothing to refund.
    /// </para>
    /// <para>
    /// <b>The reversal is admin-bff's</b> (US-14.11, C065): <c>POST
    /// /v1/admin/drivers/wallet/{id}/reverse-fee</c>, which is a wallet credit of kind
    /// <c>adjustment</c>. Nothing here moves money, and nothing here resolves the ticket.
    /// </para>
    /// </remarks>
    private static async Task<Created<FeeRefundRequestResponse>> RequestRefundAsync(
        string driverId,
        FeeRefundRequestBody? body,
        HttpContext context,
        IDailyFeeRepository charges,
        IRefundRequestRepository refunds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(charges);
        ArgumentNullException.ThrowIfNull(refunds);

        var driver = SubjectScope.RequireSelf(context.User, driverId);
        var feeDate = BusinessDates.Require(body?.FeeDate, "feeDate");
        var rideId = RequestIds.Optional(body?.RideId);

        var reason = body?.Reason?.Trim();

        if (string.IsNullOrEmpty(reason))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] = ["reason is required — the Support queue has to be able to triage the claim."],
            });
        }

        var charge = await charges.ReadForDayAsync(driver, feeDate, cancellationToken);

        if (charge is null || charge.Status != FeeStatuses.Paid || charge.AmountMinor <= 0)
        {
            throw new MageRideException(
                MageRideErrors.NotFound,
                $"No daily platform fee was deducted from this driver on {feeDate:yyyy-MM-dd}, so there is "
                + "nothing to refund. The first trip of each Asia/Colombo day is free.");
        }

        var ticket = await refunds.CreateAsync(
            driver,
            $"Daily platform fee refund requested for {feeDate:yyyy-MM-dd} "
            + $"({charge.AmountMinor} {charge.Currency} minor units, vehicle {charge.VehicleId}). "
            + $"Driver's reason: {reason}",
            rideId,
            cancellationToken);

        return TypedResults.Created(
            $"/v1/fees/{driver}/refund-requests",
            new FeeRefundRequestResponse(
                ticket.RequestId,
                driver,
                ticket.Status,
                feeDate,
                charge.AmountMinor,
                charge.Currency,
                ticket.CreatedAt));
    }

    /// <remarks>
    /// <c>feeDate</c>, <c>amountMinor</c> and <c>currency</c> are absent here, and that is not an
    /// oversight: <c>support.tickets</c> has no column for any of them, and re-deriving them from a
    /// description a CSR may have edited would report numbers the platform does not hold.
    /// </remarks>
    private static async Task<Ok<FeeRefundRequestsResponse>> ListRefundsAsync(
        string driverId,
        HttpContext context,
        IRefundRequestRepository refunds,
        IOptions<SubscriptionOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(refunds);
        ArgumentNullException.ThrowIfNull(options);

        var driver = SubjectScope.Require(context.User, driverId);
        var page = PageRequest.FromQuery(context.Request);

        var tickets = await refunds.ListAsync(driver, page.Limit, cancellationToken);

        return TypedResults.Ok(new FeeRefundRequestsResponse(
            [.. tickets.Select(ticket => new FeeRefundRequestResponse(
                ticket.RequestId, ticket.DriverId, ticket.Status, null, null, null, ticket.CreatedAt))]));
    }
}

/// <summary>The <c>{driverId}</c>-in-the-path rule, in one place.</summary>
internal static class SubjectScope
{
    /// <summary>The driver themselves, or any of the six back-office roles reading on their behalf.</summary>
    internal static Guid Require(ClaimsPrincipal? principal, string requestedDriverId)
    {
        var requested = Parse(requestedDriverId);

        if (requested == principal.RequireSubjectId())
        {
            return requested;
        }

        // AL-02/AL-06: Finance and Support answer fee disputes from the Admin Portal, which means
        // reading a driver's fee history for them. The D-35 PII_READ audit for that read is
        // admin-bff's, not this service's — but the read has to be possible.
        if (principal.Roles().Any(MageRideRoles.Internal.Contains))
        {
            return requested;
        }

        throw new MageRideException(MageRideErrors.Forbidden, "These fees are not yours.");
    }

    /// <summary>
    /// The driver themselves and nobody else.
    /// </summary>
    /// <remarks>
    /// Used for the refund intake: US-9.23 is a driver raising a ticket about their own money, and a
    /// back-office role that could raise one in a driver's name would put words in their mouth on a
    /// queue that ends in a wallet credit. An admin who wants to reverse a fee has
    /// <c>POST /v1/admin/drivers/wallet/{id}/reverse-fee</c> and does not need a ticket to do it.
    /// </remarks>
    internal static Guid RequireSelf(ClaimsPrincipal? principal, string requestedDriverId)
    {
        var requested = Parse(requestedDriverId);

        return requested == principal.RequireSubjectId()
            ? requested
            : throw new MageRideException(
                MageRideErrors.Forbidden, "A fee-refund request may only be raised by the driver it is about.");
    }

    /// <remarks>
    /// A malformed id is <c>403</c> rather than <c>400</c>: whatever it was, it was not the caller's,
    /// and answering "that is not a ULID" for someone else's identifier is a shape oracle.
    /// </remarks>
    private static Guid Parse(string requestedDriverId) =>
        Ulids.TryParse(requestedDriverId, out var requested) && requested != Guid.Empty
            ? requested
            : throw new MageRideException(MageRideErrors.Forbidden, "These fees are not yours.");
}

/// <summary>The <c>(feeDate, vehicleId)</c> position <c>GET /v1/fees/{driverId}/history</c> pages on.</summary>
/// <remarks>
/// A pair rather than a date, because a driver who used two vehicles in one Colombo day has two rows
/// sharing a date and a date-only cursor would drop whichever straddled a page boundary. Unsigned: the
/// value carries only an ordering position and the query is scoped by the caller's own id regardless of
/// what the cursor says.
/// </remarks>
internal static class HistoryCursor
{
    private sealed record Position(DateOnly FeeDate, Guid VehicleId);

    internal static string Encode(DailyFeeCharge charge) =>
        CursorCodec.Unsigned.Encode(new Position(charge.FeeDate, charge.VehicleId));

    internal static (DateOnly FeeDate, Guid VehicleId)? Decode(string? cursor) =>
        CursorCodec.Unsigned.TryDecode<Position>(cursor, out var position) && position is not null
            ? (position.FeeDate, position.VehicleId)
            : null;
}
