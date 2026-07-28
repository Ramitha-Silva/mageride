using System.Text.Json;
using MageRide.Registry.Domain;
using MageRide.Registry.Persistence;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;

namespace MageRide.Registry.Onboarding;

/// <summary>
/// The onboarding and document-expiry envelopes registry-svc writes into <c>registry.outbox</c>
/// (migration 0309, topic <c>registry.events</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every one of these is a micro-change-set.</b> E-03 names <c>document.expiring</c> and
/// <c>document.expired</c> and stops there; D6' §2.2 gives schemas for <c>ride.events</c>,
/// <c>dispatch.events</c>, <c>telemetry.normalized</c> and <c>audit.events</c> and none for these.
/// The shapes below are chosen for the consumers the specs do name: notification-svc renders
/// US-2.14's REGISTRATION_RESULT and the E-03 driver warnings from them, and admin-bff's
/// Verification-Officer queue (SCR-AP-003) is fed by <see cref="ReviewRequired"/>.
/// </para>
/// <para>
/// The aggregate id is the <b>vehicle</b> throughout, matching <c>registry.events</c>'s partition
/// key — so <c>document.expired</c> cannot overtake the <c>vehicle.approved</c> that preceded it.
/// A driver-level document with no vehicle has nothing to key on, which is why
/// <see cref="DocumentExpiry"/> emits one event per covered vehicle rather than one per document.
/// </para>
/// </remarks>
public static class OnboardingEvents
{
    /// <summary>D3' <c>POST /v1/vehicles</c>: "Side Effects: emits `vehicle.registered`".</summary>
    public static OutboxRecord VehicleRegistered(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        return Record(
            OnboardingEventTypes.VehicleRegistered,
            vehicle.Id,
            new
            {
                vehicleId = vehicle.Id,
                ownerId = vehicle.OwnerId,
                registrationNumber = vehicle.RegistrationNumber,
                vehicleType = vehicle.VehicleType,
                mode = vehicle.Mode,
                status = vehicle.Status,
                registeredAt = vehicle.CreatedAt,
            });
    }

    /// <summary>
    /// AL-27's auto-approval, with no Verification Officer in the loop. Carries the fact that
    /// makes it auditable — that all four steps verified — because "approved by nobody" is a claim
    /// a reader of the topic should be able to check.
    /// </summary>
    public static OutboxRecord VehicleApproved(Vehicle vehicle, DateTimeOffset approvedAt)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        return Record(
            OnboardingEventTypes.VehicleApproved,
            vehicle.Id,
            new
            {
                vehicleId = vehicle.Id,
                ownerId = vehicle.OwnerId,
                registrationNumber = vehicle.RegistrationNumber,
                status = vehicle.Status,
                onboardingStatus = vehicle.OnboardingStatus,
                approvedBy = "auto",
                approvedAt,
            });
    }

    /// <summary>
    /// AL-29/AL-30: a step is holding a field nobody has verified, so it goes to SCR-AP-003.
    /// </summary>
    /// <param name="vehicleId">
    /// <see langword="null"/> for Profile Setup, whose pending fields are a driver's identity and
    /// belong to no vehicle (AL-27, US-2.4a). That is also the one case where the aggregate id is
    /// the driver: <c>registry.events</c> keys by vehicle, and an event about a person has no
    /// vehicle to key by — ordering per driver is the right guarantee for it.
    /// </param>
    /// <param name="pendingFieldKeys">
    /// Which fields, so the officer queue can be sorted and counted without joining back to
    /// <c>registry.document_fields</c> on every render.
    /// </param>
    public static OutboxRecord ReviewRequired(
        Guid? vehicleId, Guid driverId, string step, IReadOnlyCollection<string> pendingFieldKeys) =>
        Record(
            OnboardingEventTypes.DocumentReviewRequired,
            vehicleId ?? driverId,
            new
            {
                vehicleId,
                driverId,
                step,
                pendingFieldKeys,
                queue = "verification-officer",
            });

    /// <summary>The step name <see cref="ReviewRequired"/> carries for Profile Setup.</summary>
    public const string ProfileStep = "profile";

    /// <summary>
    /// E-03, for one vehicle the document covers. The event type differs between the reminders and
    /// expiry itself, so a consumer can subscribe to the suspension without filtering on a field.
    /// </summary>
    /// <param name="vehicleId">
    /// <see langword="null"/> when the document covers no vehicle — a driver whose licence is
    /// expiring before they have onboarded one still has to be told (US-2.14's channel). The event
    /// is then keyed by the driver, for the same reason <see cref="ReviewRequired"/> is.
    /// </param>
    public static OutboxRecord DocumentExpiry(DueDocumentNotice notice, Guid? vehicleId)
    {
        ArgumentNullException.ThrowIfNull(notice);

        return Record(
            notice.IsExpired ? OnboardingEventTypes.DocumentExpired : OnboardingEventTypes.DocumentExpiring,
            vehicleId ?? notice.DriverId ?? notice.DocumentId,
            new
            {
                documentId = notice.DocumentId,
                vehicleId,
                driverId = notice.DriverId,
                kind = notice.Kind,
                expiresAt = notice.ExpiresAt,
                // Absent from the expired event's meaning but kept for both, so a consumer
                // rendering "your insurance expires in N days" has N without recomputing it
                // against a clock that may differ from the server's (D-38).
                daysRemaining = notice.ThresholdDays,
                dispatchState = notice.IsExpired ? DispatchStates.Suspended : DispatchStates.Active,
            });
    }

    /// <summary>
    /// The release. E-03 suspends "until re-uploaded and re-approved" and never says how the
    /// downstream planes learn it happened; without this, a vehicle that renewed its insurance is
    /// live in Postgres and still absent from every consumer that cached the suspension.
    /// </summary>
    public static OutboxRecord DispatchResumed(Guid vehicleId, Guid ownerId, string reason) =>
        Record(
            OnboardingEventTypes.DispatchResumed,
            vehicleId,
            new { vehicleId, ownerId, dispatchState = DispatchStates.Active, reason });

    private static OutboxRecord Record(string eventType, Guid vehicleId, object payload) =>
        new(vehicleId, eventType, JsonSerializer.Serialize(payload, MageRideJson.StorageOptions));
}
