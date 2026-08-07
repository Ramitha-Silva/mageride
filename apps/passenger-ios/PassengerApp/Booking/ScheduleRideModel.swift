import Foundation
import MageRideShared

/// SCR-PI-013's state.
struct ScheduleRideState {

    /// Defaults to the passenger's current location and is editable — a ride booked tonight for
    /// tomorrow morning does not start where they are standing now.
    var pickup: Place?

    /// **Mandatory** (AL-36). `nil` is what keeps Confirm disabled, and it is the only thing on this
    /// screen that can be.
    var dropoff: Place?

    /// When it should happen, in wall-clock terms the passenger chose.
    var pickupTime: Date?

    var vehicleType: RideVehicleType = RideVehicleType.threeWheeler

    var isSaving = false

    /// The created row, once Confirm has succeeded.
    var scheduled: String?

    var errorKey: String?

    /// AL-36, and the Definition-of-Done line that says *"Confirm on Schedule Ride is disabled until
    /// a destination is chosen"*.
    ///
    /// A time alone is not a booking, and the cell's own state line is explicit — *"destination is
    /// mandatory before scheduling … Confirm disabled until a destination is set"*. The time is
    /// separately required because the contract makes `pickupTime` non-null.
    var canConfirm: Bool { !isSaving && dropoff != nil && pickupTime != nil }
}

/// SCR-PI-013 — a ride in the future (US-6A.4).
///
/// **This is dispatch-svc, not ride-svc.** `POST /v1/rides/schedule` creates a
/// `dispatch.scheduled_rides` row that goes on the Job Board thirty minutes before the pickup
/// (US-6A.4/6A.5); at T-30 dispatch materialises it into a real ride through an internal command.
/// There is deliberately **no fare quote and no `fareEstimateToken` here** —
/// `MaterialiseScheduledRideRequest`'s own contract note says why: *"the price of a ride thirty
/// minutes from now is not the price quoted when it was booked"*, so fare-svc meters it at the time.
/// A screen that showed a price would be showing one nobody promised.
///
/// **The reminders are the platform's, not this screen's.** US-10.9's 1 h and 15 min notifications
/// are scheduled server-side off the same row and arrive as pushes; the screen states that they are
/// set, which is what the wireframe's line does.
@MainActor
final class ScheduleRideModel: ObservableObject {

    @Published private(set) var state = ScheduleRideState()

    private let draft: BookingDraft
    private let bookings: BookingRepository
    private let now: () -> Date

    private var work: [Task<Void, Never>] = []

    init(draft: BookingDraft, bookings: BookingRepository, now: @escaping () -> Date = Date.init) {
        self.draft = draft
        self.bookings = bookings
        self.now = now

        state.pickup = draft.state.pickup
        state.dropoff = draft.state.dropoff
        state.vehicleType = draft.state.vehicleType ?? RideVehicleType.threeWheeler
    }

    deinit {
        work.forEach { $0.cancel() }
    }

    /// Re-reads the destination from the draft. Called when the place picker has answered.
    func refreshPlaces() {
        state.pickup = draft.state.pickup
        if let dropoff = draft.state.dropoff { state.dropoff = dropoff }
    }

    /// The destination picker's answer — the wireframe's *"Select destination…"* row.
    func setDestination(_ place: Place) {
        state.dropoff = place
        state.errorKey = nil
        draft.update { $0.dropoff = place }
    }

    /// The editable pickup row.
    func setPickup(_ place: Place) {
        state.pickup = place
        draft.update { $0.pickup = place }
    }

    /// The `DatePicker`'s answer.
    ///
    /// **A past instant is refused here rather than only being greyed out** — the cell says *"past
    /// time disabled"*, and a picker opened at 08:29 and confirmed at 08:31 can produce one that was
    /// future when it was chosen.
    func setPickupTime(_ at: Date) {
        guard at > now().addingTimeInterval(Self.minimumLeadSeconds) else {
            state.errorKey = "schedule_time_past"
            return
        }
        state.pickupTime = at
        state.errorKey = nil
    }

    func setVehicleType(_ type: RideVehicleType) {
        state.vehicleType = type
        draft.update { $0.vehicleType = type }
    }

    func clearError() {
        state.errorKey = nil
    }

    /// *"Confirm schedule"* — `POST /v1/rides/schedule`.
    func confirm() {
        guard state.canConfirm, let dropoff = state.dropoff, let at = state.pickupTime else { return }

        state.isSaving = true
        state.errorKey = nil
        let pickup = state.pickup
        let vehicleType = state.vehicleType

        work.append(Task {
            do {
                let row = try await bookings.scheduleRide(
                    IosBookingRequestsKt.scheduleRideRequestFor(
                        // Nullable in the contract: a scheduled ride with no pickup is one that
                        // starts wherever the passenger is at the time, which is a legitimate
                        // booking and not a missing field.
                        pickup: pickup,
                        dropoff: dropoff,
                        pickupTime: IosInstantKt.timestampFromEpochMillis(
                            millis: Int64(at.timeIntervalSince1970 * 1000)
                        ),
                        vehicleType: vehicleType
                    )
                )
                draft.clear()
                state.isSaving = false
                state.scheduled = row.scheduledRideId
            } catch is CancellationError {
                state.isSaving = false
            } catch {
                state.isSaving = false
                state.errorKey = BookingErrors.messageKey(for: error)
            }
        })
    }

    /// The screen has navigated away from the created row.
    func onScheduleConsumed() {
        state.scheduled = nil
    }

    /// The earliest a scheduled ride may be.
    ///
    /// The Job Board opens at T-30 (US-6A.4/6A.5), so anything closer would be posted to a board it
    /// has already passed — a scheduled ride nobody can claim. Refusing it here is a better answer
    /// than accepting a booking that quietly never dispatches.
    nonisolated static let minimumLeadSeconds: TimeInterval = 30 * 60
}
