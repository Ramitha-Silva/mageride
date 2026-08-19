import Foundation
import MageRideShared

/// **SCR-DI-010 and SCR-DI-011 are one state, because they are one screen.**
///
/// D2' re-tagged SCR-DI-012 `[MERGED → SCR-DI-010]` and made SCR-DI-011 *"the driver's home dashboard
/// whenever the active vehicle is a Mode A bus or a Mode B private vehicle"*. Home is therefore not
/// two destinations with a chooser in front of them — it is one destination whose sheet is decided by
/// ``LiveVehicle/isScheduledMode``, and modelling it as two states would need a rule to keep them in
/// step about the map, the header, the vehicle and the offer overlay, all of which they share.
///
/// - Parameters:
///   - isLoading: The first read is in flight.
///   - isBusy: A toggle or a journey command is in flight; the control stays put and spins.
///   - vehicles: What the driver has, and which one is live (US-9.6, D-03).
///   - standing: The status header and the standby sheet (SCR-DI-010).
///   - journey: The Mode A/B tracking session (SCR-DI-011).
///   - isOnline: Whether `POST /v1/standby/online` has been accepted for the live vehicle.
///   - position: The handset's own last fix — the **only** vehicle the home map draws (AL-31).
///   - journeyDistanceM: Metres accumulated on this device since the journey started (US-5.6).
///   - tickAt: The clock the live timers are rendered against, advanced once a second.
///   - autoEndAtDestination: Whether the next journey arms the 100 m destination geofence (US-5.4).
///   - activeRideId: A ride already in hand, restored on a cold start (SCR-DI-001's router).
///   - errorKey: Resolved copy for the last failure.
struct HomeState {

    var isLoading = true
    var isBusy = false
    var vehicles = LiveVehicle()
    var standing = DashboardStanding()
    var journey = JourneyStanding()
    var isOnline = false
    var position: Fix?
    var journeyDistanceM: Double = 0
    var tickAt = Date()
    var autoEndAtDestination = true
    var activeRideId: String?
    var errorKey: String?

    /// Whether Home is SCR-DI-011's Start/End Journey dashboard rather than SCR-DI-010's map.
    var isScheduledMode: Bool { vehicles.isScheduledMode }

    /// **US-9.6.** Whether the go-online toggle is live at all.
    ///
    /// `false` is the wireframe's *"Add or get assigned a vehicle to go online"* → SCR-DI-026a. A read
    /// still in flight is not the same as no vehicle, so the gate stays shut while loading and the
    /// copy under it does not appear until the answer is in.
    var canGoOnline: Bool { vehicles.canGoOnline }

    /// Whether the empty state routes to SCR-DI-026a rather than to My Vehicles' list.
    var needsVehicle: Bool { !isLoading && !vehicles.canGoOnline }

    /// How long the Mode A/B journey has been running (US-5.6).
    ///
    /// Measured from the session's own `startedAt` against a ticking clock, not from when this screen
    /// was opened: a driver who backgrounds the app for an hour comes back to an hour on the timer,
    /// which is what the fleet's record will say too.
    var journeyElapsedSeconds: Int64 {
        guard journey.isRunning, let startedAt = journey.startedAt else { return 0 }
        return max(Int64(tickAt.timeIntervalSince(startedAt)), 0)
    }

    /// The live vehicle's MAP-03 legend token, for the chip and the map marker.
    var liveToken: VehicleToken? {
        vehicles.live.map { VehicleToken.forVehicleType($0.vehicleType) }
    }

    /// *"Three-wheeler · ABC-1234"*. The type is trilingual; the plate is a proper noun.
    var liveVehicleLabel: String? {
        vehicles.live.map { $0.vehicleType.labelKey.localised + MageRideSymbols.separator + $0.registrationNumber }
    }
}

/// **SCR-DI-010 · the Mode C standby dashboard** and **SCR-DI-011 · the Mode A/B Start/End Journey
/// dashboard** — one destination, one state machine, two sheets.
///
/// Four rules live here and nowhere else on this surface:
///
/// * **US-9.6 — go-online is gated on a vehicle, not on a wish.** ``HomeState/canGoOnline`` is
///   `LiveVehicle.canGoOnline`, which is `VehicleSummary.canGoLive` (C087's rule, once); with no
///   eligible vehicle the toggle is dead and the empty state routes to SCR-DI-026a.
/// * **Going online is two things or it is neither.** `POST /v1/standby/online` puts the driver in the
///   candidate pool and ``PositionService`` starts publishing on `veh/{id}/pos/live`. A driver in the
///   pool who is not publishing is offered rides that cannot find them, so the publisher starts only
///   after the call is accepted and stops before the offline call is made.
/// * **DT-04 — going offline clears the Directional filter**, and the use is not refunded. The server
///   does it; this mirrors it locally rather than leaving a stale chip on the sheet.
/// * **AL-32 — the dashboard outranks the tracker.** Start and End are sent regardless of what the GPS
///   device has done; the banner reports the device's doing, it does not gate ours.
@MainActor
final class HomeModel: ObservableObject {

    @Published private(set) var state = HomeState()

    private let identity: DriverIdentity
    private let standby: StandbyRepository
    private let journeys: JourneyRepository
    private let rides: ActiveRideRepository
    private let location: DriverLocationSource
    private let publisher: PositionPublisher

    private var ticker: Task<Void, Never>?

    init(
        identity: DriverIdentity,
        standby: StandbyRepository,
        journeys: JourneyRepository,
        rides: ActiveRideRepository,
        location: DriverLocationSource,
        publisher: PositionPublisher
    ) {
        self.identity = identity
        self.standby = standby
        self.journeys = journeys
        self.rides = rides
        self.location = location
        self.publisher = publisher
    }

    deinit {
        ticker?.cancel()
    }

    /// Subscribes to the device's own position and starts the one-second clock.
    ///
    /// Separate from ``refresh()`` because the two have different lifetimes: the reads happen on every
    /// appearance, the subscriptions exactly once per model.
    func start() {
        guard ticker == nil else { return }

        location.start { [weak self] fix in
            self?.advance(by: fix)
        }
        // `guard let self` inside the loop rather than only `[weak self]` on it: a weak reference that
        // is merely *checked* each tick leaves the loop itself running for the life of the process
        // once the model has gone, and this one ticks every second.
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: Self.tickNanoseconds)
                guard !Task.isCancelled, let self else { return }
                self.state.tickAt = Date()
            }
        }
    }

    /// Drops both subscriptions. A dashboard that is not on screen must not hold a GNSS one open.
    func stop() {
        ticker?.cancel()
        ticker = nil
        location.stop()
    }

    /// Re-reads the vehicle, the standing, the journey and any ride already in hand.
    func refresh() async {
        state.isLoading = true
        state.errorKey = nil
        do {
            let driverId = identity.driverId
            let vehicles = try await identity.liveVehicle()

            state.vehicles = vehicles
            // With no session there is nothing driver-scoped to read and nothing to blank: the shell
            // is already routing to Login, and clearing the sheet on the way out would be a flicker
            // the driver sees instead of the login screen.
            if let driverId {
                state.standing = await standby.standing(driverId: driverId)
                state.activeRideId = try await rides.active(driverId: driverId)?.rideId
            }

            // SCR-DI-011 only. A Mode C vehicle has no tracking session, and asking trip-state-svc
            // about one would be the R-01 fence crossed for nothing.
            if let live = vehicles.live, vehicles.isScheduledMode {
                state.journey = try await journeys.standing(vehicleId: live.vehicleId)
            } else {
                state.journey = JourneyStanding()
            }
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    /// The wireframe's big **◉ ONLINE — Mode C** toggle (US-6A.1).
    ///
    /// Refused outright with no vehicle: a toggle that moved and then sprang back would read as a
    /// broken control rather than as the gate it is.
    func toggleOnline(_ desired: Bool) async {
        let current = state
        let vehicle = current.vehicles.live

        // Two refusals, both things the driver can act on: US-9.6's "no vehicle available" and "the
        // first fix has not arrived". Sending a `GoOnlineRequest` with a made-up point would put the
        // driver on the map somewhere they are not, and dispatch would score them from it.
        guard let vehicle, current.canGoOnline else {
            state.errorKey = "home_needs_vehicle"
            return
        }
        guard !desired || current.position != nil else {
            state.errorKey = "home_waiting_for_gps"
            return
        }

        state.isBusy = true
        state.errorKey = nil
        do {
            if desired {
                let presence = try await standby.goOnline(
                    vehicleId: vehicle.vehicleId,
                    position: requireNotNil(current.position).point
                )
                await publisher.start(
                    vehicleId: vehicle.vehicleId,
                    mode: vehicle.mode,
                    vehicleType: vehicle.vehicleType
                )
                state.isOnline = presence != PresenceState.offline
            } else {
                // Stop publishing first: a driver who is still on `veh/{id}/pos/live` after dispatch
                // has taken them out of the pool is a vehicle on the map that cannot be hired, which
                // US-7.16 is the passenger-side half of.
                publisher.stop()
                _ = try await standby.goOffline()
                state.isOnline = false
                state.standing.directional = state.standing.directional.map(Self.cleared)
            }
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isBusy = false
    }

    /// SCR-DI-011's green **Start Journey**. Sent whatever the tracker has done (AL-32).
    func startJourney() async {
        guard let vehicle = state.vehicles.live else { return }
        state.isBusy = true
        state.errorKey = nil
        do {
            let session = try await journeys.start(
                vehicleId: vehicle.vehicleId,
                mode: vehicle.mode,
                routeId: state.journey.route?.routeId,
                autoEndAtDestination: state.autoEndAtDestination
            )
            await publisher.start(vehicleId: vehicle.vehicleId, mode: vehicle.mode, vehicleType: vehicle.vehicleType)
            state.journeyDistanceM = 0
            state.journey.session = session
            state.journey.startedByDevice = false
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isBusy = false
    }

    /// SCR-DI-011's red **End Journey**, and US-5.10's **Restart** inside the five-minute grace.
    ///
    /// One entry point because they are the same button in two states, and because which one is legal
    /// is a property of the session rather than of the tap.
    func endOrRestartJourney() async {
        guard let session = state.journey.session, let vehicle = state.vehicles.live else { return }
        let isRestarting = state.journey.isRestartable
        state.isBusy = true
        state.errorKey = nil
        do {
            let updated: Session
            if isRestarting {
                updated = try await journeys.restart(sessionId: session.sessionId)
                await publisher.start(
                    vehicleId: vehicle.vehicleId,
                    mode: vehicle.mode,
                    vehicleType: vehicle.vehicleType
                )
            } else {
                updated = try await journeys.end(sessionId: session.sessionId)
                publisher.stop()
            }
            if !isRestarting { state.journeyDistanceM = 0 }
            state.journey.session = updated
            state.journey.startedByDevice = false
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isBusy = false
    }

    /// The route card's **Change ›**. Resolved against the active GTFS feed for its long name.
    func chooseRoute(_ routeId: String) async {
        state.journey.route = await journeys.routeOf(routeId: routeId)
    }

    /// US-5.4's 100 m destination geofence, armed for the **next** journey.
    func setAutoEndAtDestination(_ enabled: Bool) {
        state.autoEndAtDestination = enabled
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    /// Forgets a resumed ride once the screen has navigated to it, so returning from SCR-DI-015 does
    /// not immediately push it again.
    func consumeActiveRide() {
        state.activeRideId = nil
    }

    /// Folds one fix into the state: the marker moves, and a running journey grows by the leg.
    ///
    /// The distance is accumulated from consecutive fixes with `:shared`'s haversine — the same
    /// formula R-06's post-filter and DT-02's detour test use, so *"18.4 km"* on SCR-DI-011 means what
    /// the platform means by a kilometre. It is a **device** measurement and says so: the
    /// authoritative distance for a Mode A journey is what `position-processor-svc` reconstructs from
    /// the published track.
    ///
    /// Only while the session is actually running — a bus parked between journeys must not accumulate
    /// the drive home into the last one's distance.
    private func advance(by fix: Fix) {
        if let previous = state.position, state.journey.isRunning {
            state.journeyDistanceM += GeoDistanceKt.distanceMetres(from: previous.point, to: fix.point)
        }
        state.position = fix
    }

    /// DT-04's local mirror. The activation is spent either way (US-6A.19), so `usesRemaining` is
    /// deliberately carried through unchanged.
    private static func cleared(_ filter: DirectionalFilterState) -> DirectionalFilterState {
        DirectionalFilterState(
            active: false,
            destination: nil,
            label: nil,
            expiresAt: nil,
            timeRemainingSec: 0,
            usesRemaining: filter.usesRemaining
        )
    }

    /// One second — fine enough for a `01:12:40` timer and for a Directional countdown.
    private static let tickNanoseconds: UInt64 = 1_000_000_000
}

/// Unwraps a value the caller has already guarded, without a second optional at the call site.
///
/// Swift has no `requireNotNull`, and a bare `!` in the middle of a network call is the shape that
/// reads as a crash waiting to happen even where a `guard` two lines above makes it impossible.
private func requireNotNil<T>(_ value: T?) -> T {
    guard let value else { preconditionFailure("guarded value was nil") }
    return value
}
