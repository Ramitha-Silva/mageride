using System.Collections.Frozen;

namespace MageRide.Registry.Domain;

/// <summary>
/// The canonical AL-09 vehicle-type enumeration, and the subset the Driver App may onboard.
/// </summary>
/// <remarks>
/// AL-09 replaced the informal set ("car", "Tuk") with ten canonical values and maps
/// <c>car → sedan</c>. The mapping is a one-time data migration, not an input alias: a client
/// still sending <c>car</c> is a client that has not been updated, and silently rewriting it
/// would hide that until a fare tariff or a map marker disagreed. It is refused
/// <c>400 invalid-vehicle-type</c> instead, which is what this component's DoD requires.
/// <para>
/// The values mirror the <c>registry.vehicles.vehicle_type</c> CHECK (0303) exactly — the
/// database is the backstop, this list is the one that produces a useful error.
/// </para>
/// </remarks>
public static class VehicleTypes
{
    public const string Motorbike = "motorbike";
    public const string ThreeWheeler = "three_wheeler";
    public const string Flex = "flex";
    public const string Sedan = "sedan";
    public const string MiniVan = "mini_van";
    public const string Van = "van";
    public const string Truck = "truck";
    public const string MiniTruck = "mini_truck";
    public const string Bus = "bus";
    public const string Train = "train";

    /// <summary>All ten canonical types (ADD §1 AL-09, <c>registry.vehicles.vehicle_type</c>).</summary>
    public static readonly FrozenSet<string> All = new[]
    {
        Motorbike, ThreeWheeler, Flex, Sedan, MiniVan, Van, Truck, MiniTruck, Bus, Train,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The eight the Driver App onboards — <c>_shared.yaml#/components/schemas/RideVehicleType</c>.
    /// <c>bus</c> and <c>train</c> are Mode A and belong to the Fleet Portal and admin-bff.
    /// </summary>
    public static readonly FrozenSet<string> DriverApp = new[]
    {
        Motorbike, ThreeWheeler, Flex, Sedan, MiniVan, Van, Truck, MiniTruck,
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsCanonical(string? vehicleType) =>
        vehicleType is not null && All.Contains(vehicleType);

    public static bool IsDriverApp(string? vehicleType) =>
        vehicleType is not null && DriverApp.Contains(vehicleType);
}

/// <summary>The three operating modes (AL-03; <c>registry.vehicles.mode</c>).</summary>
public static class OperatingModes
{
    /// <summary>Scheduled public transport — bus and train. Onboarded by admin-bff.</summary>
    public const string A = "A";

    /// <summary>Shared private vehicle. Onboarded in the Fleet Portal (fleet-svc, SCR-FP-004).</summary>
    public const string B = "B";

    /// <summary>On-demand ride. The only mode the Driver App onboards.</summary>
    public const string C = "C";
}

/// <summary>The <c>registry.vehicles.status</c> lifecycle (0303).</summary>
public static class RegistrationStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Deactivated = "DEACTIVATED";
}

/// <summary>
/// The <c>registry.vehicles.dispatch_state</c> CHECK (0303) — E-03's document-expiry gate, which
/// is separate from <see cref="RegistrationStatuses"/> because an approved vehicle whose insurance
/// lapsed is not un-approved, it is off the road until the certificate is renewed.
/// </summary>
public static class DispatchStates
{
    public const string Active = "ACTIVE";
    public const string Suspended = "DISPATCH_SUSPENDED";
}

/// <summary>The AL-30 derived onboarding state (<c>registry.vehicles.onboarding_status</c>).</summary>
public static class OnboardingStatuses
{
    public const string Incomplete = "incomplete";
    public const string Approved = "approved";
}

/// <summary>
/// The per-step verdicts of the AL-30 onboarding machine
/// (<c>registry.yaml#/components/schemas/StepVerdict</c>).
/// </summary>
/// <remarks>
/// This slice never leaves <see cref="PendingInput"/>: no document is uploaded and no OCR runs,
/// so no step has been saved. C029 owns the machine that moves them.
/// </remarks>
public static class StepVerdicts
{
    public const string Verified = "VERIFIED";
    public const string PendingReview = "PENDING_REVIEW";
    public const string PendingInput = "PENDING_INPUT";
}
