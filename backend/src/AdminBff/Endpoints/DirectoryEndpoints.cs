using System.Collections.Frozen;
using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Authorization;
using MageRide.AdminBff.Directories;
using MageRide.AdminBff.Domain;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.AdminBff.Endpoints;

/// <summary>
/// SCR-AP-010…015 — the passenger, driver and vehicle directories (AL-40/41/42, US-24.9/10/11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Each directory is gated on the URD §2.3 row whose cells are exactly the role list D3' prints
/// for the route — with ◐ fenced.</b> That is what makes the two documents agree rather than one
/// overrule the other, and it is worth spelling out because the obvious row is the wrong one twice:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Passengers → Support · Read, platform-wide.</b> D3': <c>[support|admin|auditor]</c>. The row
/// gives DRV/PAX <c>raise</c> (which is not Read), FLT <c>◐ own org</c> and FIN <c>◐ financial</c>
/// (both fenced out), VER ➖, and leaves Admin, Super Admin, Support CSR and Auditor — the list,
/// exactly. It is also the row that describes the job: URD §2.4 gives the CSR "support ticket queue;
/// **trip/user read-only lookup**; investigate disputes", which is this screen and its Disputes tab.
/// <b>Not the Passenger row</b>, whose PAX cell is ✅ — gating an operator console on it would let
/// any passenger list every passenger on the platform.
/// </item>
/// <item>
/// <b>Drivers → Driver wallet &amp; credit transfers · Read, platform-wide.</b> D3':
/// <c>[support|admin|finance|auditor]</c>, and this is the only row that carries Finance with a
/// read — which the deliverable needs, because half of SCR-AP-013 is the wallet ledger, the daily
/// fee and the credit transfers. <b>Not the Driver-app row</b>, which is otherwise the natural fit
/// and gives Finance ➖: a Finance Officer refused the driver directory could not reconcile the
/// wallet they are told to reconcile (BR-28.8 names them in as many words). The DRV cell is
/// <c>◐ own</c> — "your own wallet in the Driver App" — and the fence turns that into the 403 it
/// has to be here.
/// </item>
/// <item>
/// <b>Vehicles → Fleet live map &amp; per-vehicle analytics · Read, platform-wide.</b> D3':
/// <c>[support|admin|finance|auditor]</c>, matched exactly, and per-vehicle analytics is what
/// SCR-AP-015's Trips / Earnings / Daily-fee tabs are. <b>Not the Fleet-operations row</b>
/// (onboarding, assignment, scheduling): that is the Fleet Portal's write surface and it gives
/// Finance ➖. The FLT cell is <c>◐ own org</c>, and a platform-wide registry is not their org.
/// </item>
/// </list>
/// <para>
/// <b>Every route is a GET and there is no other verb here.</b> BR-28.8: "All are read-only —
/// refunds route to Finance and wallet reversals stay Finance-only." The reversal button on
/// SCR-AP-013 posts to C065's <c>/v1/admin/drivers/wallet/{driverId}/reverse-fee</c>, which is
/// gated on the Finance row and audited as a mutation.
/// </para>
/// <para>
/// <b>The two detail reads declare an audit action, exactly as AL-39's viewer does.</b> A GET with
/// <c>.Audited(PII_READ, …)</c> is the shape <c>AuditEndpointExtensions</c> documents for a read
/// that is itself the auditable act: the handler records what was opened and whether the contact
/// details were revealed, and the interceptor writes the row once the response is known to be a
/// success.
/// </para>
/// </remarks>
internal static class DirectoryEndpoints
{
    public static IEndpointRouteBuilder MapDirectoryEndpoints(this IEndpointRouteBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        admin.MapGet("/passengers", SearchPassengersAsync)
            .WithName("searchPassengers")
            .WithSummary("Search the passenger directory by name, mobile, id or email (SCR-AP-010).")
            .RequirePlatformWideFeature(FeatureAreas.Support, PermissionGrant.Read);

        admin.MapGet("/passengers/{passengerId:guid}", GetPassengerAsync)
            .WithName("getPassengerDetail")
            .WithSummary("A passenger's profile and their Trips / Payments / Packages / Disputes (SCR-AP-011).")
            .RequirePlatformWideFeature(FeatureAreas.Support, PermissionGrant.Read)
            .Audited(AdminAuditActions.PiiRead, AdminAuditActions.PassengerEntity);

        admin.MapGet("/drivers", SearchDriversAsync)
            .WithName("searchDrivers")
            .WithSummary("Search the driver directory — verified by default (SCR-AP-012).")
            .RequirePlatformWideFeature(FeatureAreas.DriverWallet, PermissionGrant.Read);

        admin.MapGet("/drivers/{driverId:guid}", GetDriverAsync)
            .WithName("getDriverDetail")
            .WithSummary("A driver's profile, wallet, level, vehicles and five activity tabs (SCR-AP-013).")
            .RequirePlatformWideFeature(FeatureAreas.DriverWallet, PermissionGrant.Read)
            .Audited(AdminAuditActions.PiiRead, AdminAuditActions.DriverEntity);

        admin.MapGet("/vehicles", SearchVehiclesAsync)
            .WithName("searchVehicles")
            .WithSummary("Search the vehicle registry by plate, id, type, mode, owner or fleet (SCR-AP-014).")
            .RequirePlatformWideFeature(FeatureAreas.FleetMonitoring, PermissionGrant.Read);

        admin.MapGet("/vehicles/{vehicleId:guid}", GetVehicleAsync)
            .WithName("getVehicleDetail")
            .WithSummary("A vehicle's registration, documents and four activity tabs (SCR-AP-015).")
            .RequirePlatformWideFeature(FeatureAreas.FleetMonitoring, PermissionGrant.Read)
            .Audited(AdminAuditActions.PiiRead, AdminAuditActions.VehicleEntity);

        return admin;
    }

    // ---------------------------------------------------------------------------------------
    // Passengers (AL-40)
    // ---------------------------------------------------------------------------------------

    private static async Task<Ok<CursorPage<PassengerRowResponse>>> SearchPassengersAsync(
        string? name,
        string? mobile,
        string? id,
        string? email,
        string? cursor,
        int? limit,
        IDirectoryService directories,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directories);

        return TypedResults.Ok(await directories.SearchPassengersAsync(
            Criterion(name, nameof(name), 200),
            Criterion(mobile, nameof(mobile), 20),
            Identifier(id),
            Criterion(email, nameof(email), 200),
            PageRequest.Create(cursor, limit),
            cancellationToken));
    }

    private static async Task<Ok<PassengerDetailResponse>> GetPassengerAsync(
        Guid passengerId,
        HttpContext context,
        IDirectoryService directories,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(directories);

        return TypedResults.Ok(
            await directories.PassengerDetailAsync(passengerId, context, cancellationToken));
    }

    // ---------------------------------------------------------------------------------------
    // Drivers (AL-41)
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// <c>status</c> defaults to <c>verified</c> because US-24.10 does: the directory is for the
    /// people currently driving, and an operator who wants an applicant asks for one. <c>?level=1</c>
    /// is how ADD Appendix C's Level-1 list is obtained — the literal <c>/drivers/level-1</c> path it
    /// asked for is gone, because it would be ambiguous against <c>/drivers/{driverId}</c>.
    /// </remarks>
    private static async Task<Ok<CursorPage<DriverRowResponse>>> SearchDriversAsync(
        string? name,
        string? mobile,
        string? id,
        string? nic,
        string? regNo,
        int? level,
        string? status,
        string? cursor,
        int? limit,
        IDirectoryService directories,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directories);

        if (level is not (null or >= 1 and <= 3))
        {
            throw Invalid("level", "level is one of the three Driver Levels: 1, 2 or 3.");
        }

        var resolved = string.IsNullOrWhiteSpace(status)
            ? DriverDirectoryStatuses.Verified
            : status.Trim();

        if (!DriverDirectoryStatuses.IsKnown(resolved))
        {
            throw Invalid("status", "status is one of verified, pending, suspended or all.");
        }

        return TypedResults.Ok(await directories.SearchDriversAsync(
            Criterion(name, nameof(name), 200),
            Criterion(mobile, nameof(mobile), 20),
            Identifier(id),
            Criterion(nic, nameof(nic), 20),
            Criterion(regNo, nameof(regNo), 32),
            level,
            resolved,
            PageRequest.Create(cursor, limit),
            cancellationToken));
    }

    private static async Task<Ok<DriverDetailResponse>> GetDriverAsync(
        Guid driverId,
        HttpContext context,
        IDirectoryService directories,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(directories);

        return TypedResults.Ok(await directories.DriverDetailAsync(driverId, context, cancellationToken));
    }

    // ---------------------------------------------------------------------------------------
    // Vehicles (AL-42)
    // ---------------------------------------------------------------------------------------

    private static async Task<Ok<CursorPage<VehicleRowResponse>>> SearchVehiclesAsync(
        string? regNo,
        string? id,
        string? type,
        string? mode,
        string? ownerMobile,
        string? fleetOrg,
        string? status,
        string? cursor,
        int? limit,
        IDirectoryService directories,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var vehicleType = Criterion(type, nameof(type), 20);
        var operatingMode = Criterion(mode, nameof(mode), 1)?.ToUpperInvariant();
        var registrationStatus = Criterion(status, nameof(status), 20)?.ToUpperInvariant();

        // Exact-match criteria are validated rather than passed through: a typo'd enum would answer
        // 200 with an empty page, which reads as "no such vehicle" and is a different fact.
        if (vehicleType is not null && !VehicleTypes.Contains(vehicleType))
        {
            throw Invalid("type", $"type is one of: {string.Join(", ", VehicleTypes)}.");
        }

        if (operatingMode is not null && !OperatingModes.Contains(operatingMode))
        {
            throw Invalid("mode", "mode is A (scheduled public transport), B (shared) or C (on-demand).");
        }

        if (registrationStatus is not null && !RegistrationStatuses.Contains(registrationStatus))
        {
            throw Invalid("status", $"status is one of: {string.Join(", ", RegistrationStatuses)}.");
        }

        return TypedResults.Ok(await directories.SearchVehiclesAsync(
            Criterion(regNo, nameof(regNo), 32),
            Identifier(id),
            vehicleType,
            operatingMode,
            Criterion(ownerMobile, nameof(ownerMobile), 20),
            Criterion(fleetOrg, nameof(fleetOrg), 200),
            registrationStatus,
            PageRequest.Create(cursor, limit),
            cancellationToken));
    }

    private static async Task<Ok<AdminVehicleDetailResponse>> GetVehicleAsync(
        Guid vehicleId,
        HttpContext context,
        IDirectoryService directories,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(directories);

        return TypedResults.Ok(await directories.VehicleDetailAsync(vehicleId, context, cancellationToken));
    }

    // ---------------------------------------------------------------------------------------
    // Criteria
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The <c>registry.vehicles.vehicle_type</c> CHECK (0303) and
    /// <c>_shared.yaml#/components/schemas/VehicleType</c>, which are the same ten (AL-09).
    /// </summary>
    /// <remarks>
    /// Transcribed rather than referenced: registry-svc owns the enum and this project does not
    /// depend on that one — a BFF that referenced every service it reads from would be a build of
    /// the whole platform. The database is the backstop; this list is what produces a useful 400.
    /// </remarks>
    private static readonly FrozenSet<string> VehicleTypes = new[]
    {
        "motorbike", "three_wheeler", "flex", "sedan", "mini_van", "van", "truck", "mini_truck", "bus", "train",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> OperatingModes =
        new[] { "A", "B", "C" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> RegistrationStatuses =
        new[] { "PENDING", "APPROVED", "REJECTED", "DEACTIVATED" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// A free-text criterion, trimmed, length-checked and turned into null when it says nothing.
    /// </summary>
    /// <remarks>
    /// Blank is "no filter" rather than "match the empty string": a search box the operator cleared
    /// sends <c>?name=</c>, and treating that as a criterion would answer an empty page for a query
    /// they think they cancelled. The maxima are `admin-bff.yaml`'s own, enforced here so an
    /// oversized value is a named 400 rather than a pattern the database has to scan with.
    /// </remarks>
    private static string? Criterion(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength
            ? trimmed
            : throw Invalid(field, $"{field} must be at most {maxLength} characters.");
    }

    /// <summary>
    /// The <c>?id=</c> criterion.
    /// </summary>
    /// <remarks>
    /// Parsed here rather than bound as <c>Guid?</c>, because a framework binding failure is a 400
    /// with no error code and no field name — and D3' types this parameter as the platform's
    /// <c>Ulid</c> (a ULID *or* a UUID), so "not a valid id" is a message worth writing.
    /// </remarks>
    private static Guid? Identifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Guid.TryParse(value.Trim(), out var parsed)
            ? parsed
            : throw Invalid("id", "id must be a UUID.");
    }

    private static MageRideValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}
