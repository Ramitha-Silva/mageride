import Foundation
import MageRideShared

/// Which vehicle this handset is Home *for*, and whether Home is SCR-DI-010 or SCR-DI-011.
///
/// - Parameters:
///   - vehicles: Everything the driver owns or has been assigned.
///   - live: The single active publisher (D-03), or `nil` when nothing is eligible.
struct LiveVehicle: Equatable {

    var vehicles: [VehicleSummary] = []
    var live: VehicleSummary?

    /// **US-9.6's gate.** The go-online toggle is disabled until at least one vehicle is available —
    /// an owned Mode C vehicle that is APPROVED, or a shared / temporarily-assigned Mode A/B one.
    var canGoOnline: Bool { live != nil }

    /// Whether the driver has any vehicle at all; `false` is what routes to SCR-DI-026a.
    var hasAnyVehicle: Bool { !vehicles.isEmpty }

    /// Whether Home is the Start/End Journey dashboard rather than the Mode C standby map.
    ///
    /// D2' §SCR-DI-011: *"This screen IS the driver's home dashboard whenever the active vehicle is a
    /// Mode A bus or a Mode B private vehicle"*. The mode of the **live** vehicle decides it — a
    /// driver who owns a tuk and drives a fleet bus sees the bus dashboard while the bus is the one
    /// selected, and the tuk's the moment they switch back.
    var isScheduledMode: Bool { live?.mode == ServiceMode.a || live?.mode == ServiceMode.b }

    static func == (lhs: LiveVehicle, rhs: LiveVehicle) -> Bool {
        lhs.live?.vehicleId == rhs.live?.vehicleId
            && lhs.vehicles.map(\.vehicleId) == rhs.vehicles.map(\.vehicleId)
    }
}

/// Who the driver is and what they are driving — the two answers every screen in this group needs
/// before it can read anything else.
///
/// A protocol for the reason every seam in this target is one: `AuthSessionManager` is a Kotlin
/// **class** and cannot be stood in for from Swift, so a model test with no gateway needs the seam on
/// this side of the bridge.
protocol DriverIdentity: AnyObject {

    /// The signed-in driver, or `nil` when the session has gone (the shell routes to Login).
    var driverId: String? { get }

    /// Resolves the single live publisher from `GET /v1/vehicles/mine` and the local choice.
    func liveVehicle() async throws -> LiveVehicle
}

/// ``DriverIdentity`` over C014's session and C087's vehicle reads.
///
/// **`GET /v1/vehicles/mine` is read through ``VehicleOnboardingRepository``, not a second client.**
/// That protocol already owns the call and `VehicleSummary.canGoLive` already owns US-9.6's
/// eligibility rule; a `RegistryApi` held here would be a second door to one read and, worse, a
/// second place the go-live rule could be spelled.
final class ApiDriverIdentity: DriverIdentity {

    private let sessions: DriverSessions
    private let vehicles: VehicleOnboardingRepository
    private let activeVehicle: ActiveVehicleStore

    init(sessions: DriverSessions, vehicles: VehicleOnboardingRepository, activeVehicle: ActiveVehicleStore) {
        self.sessions = sessions
        self.vehicles = vehicles
        self.activeVehicle = activeVehicle
    }

    var driverId: String? { sessions.userId }

    /// The stored choice wins while it is still eligible.
    ///
    /// When it is not — deactivated elsewhere, an assignment that lapsed, or a driver who has never
    /// opened My Vehicles — the first eligible vehicle is taken **and written back**, so SCR-DI-026's
    /// selection and this dashboard's chip never disagree about which vehicle is live. A driver with
    /// two eligible vehicles changes the pick there; picking one here rather than none is what stops
    /// a perfectly onboarded driver finding a disabled toggle on their first shift.
    func liveVehicle() async throws -> LiveVehicle {
        let all = try await vehicles.myVehicles()
        let eligible = all.filter(\.canGoLive)
        let held = activeVehicle.activeVehicleId.flatMap { id in eligible.first { $0.vehicleId == id } }
        let live = held ?? eligible.first

        activeVehicle.activeVehicleId = live?.vehicleId
        return LiveVehicle(vehicles: all, live: live)
    }
}
