using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;

namespace MageRide.Subscriptions.Endpoints;

// =============================================================================================
// The wire shapes of backend/contracts/subscription.yaml. The contract wins over this file: it is
// what C012/C013 generate the KMP client from and what C118 asserts the running service against.
//
// Public because the test suite deserialises them — the same reason wallet-svc's, query-svc's and
// content-svc's are.
// =============================================================================================

/// <summary>One rung of the seven-tier ladder (`GET /v1/fees/rates`).</summary>
/// <param name="DailyFeeMinor">Zero for Mode A — bus and train journeys carry no platform fee.</param>
public sealed record DailyFeeRateResponse(
    string VehicleType, long DailyFeeMinor, string Mode, string Currency);

/// <summary>`GET`/`PUT` of the rate ladder.</summary>
public sealed record DailyFeeRatesResponse(IReadOnlyList<DailyFeeRateResponse> Items);

/// <summary>`PUT /v1/admin/fees/rates` (US-14.4).</summary>
public sealed record UpdateDailyFeeRatesBody(IReadOnlyList<DailyFeeRateBody>? Items);

/// <summary>One submitted rate.</summary>
public sealed record DailyFeeRateBody(string? VehicleType, long? DailyFeeMinor, string? Mode);

/// <summary>`GET /v1/fees/{driverId}/today` (US-9.1, US-9.7).</summary>
/// <param name="FirstTripFree">
/// The platform's rule, not a fact about this driver's morning — D3's own example pairs it with
/// <c>tripsToday: 3</c>.
/// </param>
public sealed record TodaysFeeResponse(
    string VehicleType,
    Guid VehicleId,
    long DailyRateMinor,
    string Status,
    long DeductedMinor,
    int TripsToday,
    bool FirstTripFree,
    DateOnly FeeDate,
    DateTimeOffset FeeDateTzAt,
    string Currency);

/// <summary>One charged or waived Colombo day (`GET /v1/fees/{driverId}/history`, US-9A.6).</summary>
public sealed record DailyFeeChargeResponse(
    Guid DriverId,
    Guid VehicleId,
    DateOnly FeeDate,
    DateTimeOffset FeeDateTzAt,
    long AmountMinor,
    string Currency,
    int TripsThatDay,
    string Status,
    DateTimeOffset ChargedAt);

/// <summary>`POST /v1/internal/fees/{driverId}/charge-before-trip` (D-08/D-13).</summary>
public sealed record ChargeBeforeTripBody(string? VehicleId, string? RideId);

/// <summary>One rung of the bulk-voucher ladder (`PUT /v1/admin/voucher-discount-tiers`).</summary>
public sealed record VoucherDiscountTierResponse(
    long DenominationMinor, int DiscountBps, bool Active, DateTimeOffset UpdatedAt);

/// <summary>The ladder.</summary>
public sealed record VoucherDiscountTiersResponse(IReadOnlyList<VoucherDiscountTierResponse> Tiers);

/// <summary>`PUT /v1/admin/voucher-discount-tiers` (US-9A.15, AL-01).</summary>
public sealed record UpdateVoucherDiscountTiersBody(IReadOnlyList<VoucherDiscountTierBody>? Tiers);

/// <summary>One submitted rung.</summary>
public sealed record VoucherDiscountTierBody(long? DenominationMinor, int? DiscountBps, bool? Active);

/// <summary>One vehicle's Mode B platform charge for a month (the C060 hand-off).</summary>
public sealed record ModeBChargeResponse(
    Guid VehicleId,
    string RegistrationNumber,
    string VehicleType,
    Guid OwnerId,
    Guid? FleetId,
    DateOnly PeriodMonth,
    long AmountMinor,
    string Currency,
    string Status);

/// <summary>A month's charge lines with the consolidated per-fleet totals AL-03 asks for.</summary>
public sealed record ModeBChargesResponse(
    DateOnly PeriodMonth,
    long TotalMinor,
    string Currency,
    IReadOnlyList<ModeBFleetTotalResponse> Fleets,
    IReadOnlyList<ModeBChargeResponse> Items);

/// <summary>One fleet's consolidated total for the month.</summary>
/// <param name="FleetId">
/// <see langword="null"/> groups the individually-owned Mode B vehicles, which belong to no fleet and
/// therefore to no consolidated invoice.
/// </param>
public sealed record ModeBFleetTotalResponse(
    Guid? FleetId, int VehicleCount, long TotalMinor, string Currency);

/// <summary>What one run of the monthly charge raised.</summary>
public sealed record ModeBRunResponse(
    DateOnly PeriodMonth, int Raised, int FreeMonths, long TotalMinor, string Currency);

/// <summary>`POST /v1/fees/{driverId}/refund-requests` (US-9.23, US-14.11).</summary>
public sealed record FeeRefundRequestBody(string? FeeDate, string? Reason, string? RideId);

/// <summary>A raised fee-refund request.</summary>
/// <param name="AmountMinor">
/// The charge this request disputes, as this service recorded it — present on the create, where the
/// charge has just been read, and absent from the list, where <c>support.tickets</c> holds no such
/// column.
/// </param>
public sealed record FeeRefundRequestResponse(
    Guid RequestId,
    Guid DriverId,
    string Status,
    DateOnly? FeeDate,
    long? AmountMinor,
    string? Currency,
    DateTimeOffset CreatedAt);

/// <summary>The driver's own fee-refund requests.</summary>
public sealed record FeeRefundRequestsResponse(IReadOnlyList<FeeRefundRequestResponse> Items);

/// <summary>Parses the identifiers D3' types as <c>Ulid</c> ("ULID or UUID, rendered canonically").</summary>
/// <remarks>
/// The same twelve lines wallet-svc and reputation-svc carry. Per service rather than in the kernel
/// because each one names its own fields in the error, which is what makes a 400 actionable.
/// </remarks>
internal static class RequestIds
{
    public static Guid Require(string? value, string field) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [$"{field} is required and must be a ULID or a UUID."],
            });

    public static Guid? Optional(string? value) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;
}

/// <summary>Parses the <c>BusinessDate</c> <c>_shared.yaml</c> types as <c>yyyy-MM-dd</c>.</summary>
/// <remarks>
/// Every date on this surface is an <b>Asia/Colombo</b> business date (D-38), never an instant and
/// never a UTC date. Parsed exact and invariant: accepting <c>DateOnly.Parse</c>'s locale-sensitive
/// forms would let <c>03/07/2026</c> mean two different days on two different hosts, and the day is
/// what decides whether a driver is charged.
/// </remarks>
internal static class BusinessDates
{
    public const string Format = "yyyy-MM-dd";

    public static DateOnly? Optional(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(
            value.Trim(), Format, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [$"{field} must be an Asia/Colombo business date formatted {Format}."],
            });
    }

    public static DateOnly Require(string? value, string field) =>
        Optional(value, field)
        ?? throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [field] = [$"{field} is required and must be an Asia/Colombo business date formatted {Format}."],
        });
}
