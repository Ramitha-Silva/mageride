import SwiftUI

/// The eight destinations SCR-DI-036 lists, in the order the wireframe prints them.
///
/// **This is AL-31's whole replacement for the hamburger.** The list lives here rather than inside the
/// view so a later group adding a destination adds a row to a table, and so the coverage test can walk
/// it. Every entry resolves to a real ``DriverRoute``; there are no dead rows.
///
/// The same eight rows as `apps/driver-android/.../menu/MenuDestination.kt`, in the same order, with
/// the same string keys — a divergence between the two driver apps is a diff.
enum MenuDestination: String, CaseIterable, Identifiable {

    /// SCR-DI-026 (C087).
    case myVehicles

    /// SCR-DI-004 — the Mode-C wizard, which AL-30 resumes or restarts (C087).
    case vehicleOnboarding

    /// SCR-DI-027 (C092).
    case trackerPairing

    /// SCR-DI-028 — Mode B sharing (C092).
    case sharing

    /// SCR-DI-029 (C092).
    case profile

    /// SCR-DI-030 — ride history and the rate-passenger sheet (C092).
    case rideHistory

    /// SCR-DI-033 — support and the daily-fee refund request (C093).
    case support

    /// SCR-DI-034 (C093).
    case notifications

    var id: String { rawValue }

    /// Where the row goes.
    var route: DriverRoute {
        switch self {
        case .myVehicles: return .vehicles
        case .vehicleOnboarding: return .vehicleOnboarding
        case .trackerPairing: return .trackerPairing
        case .sharing: return .sharing
        case .profile: return .profile
        case .rideHistory: return .rideHistory
        case .support: return .support
        case .notifications: return .notifications
        }
    }

    /// Trilingual copy. The same keys as `values*/strings.xml`.
    var labelKey: String {
        switch self {
        case .myVehicles: return "menu_my_vehicles"
        case .vehicleOnboarding: return "menu_vehicle_onboarding"
        case .trackerPairing: return "menu_tracker_pairing"
        case .sharing: return "menu_sharing"
        case .profile: return "menu_profile"
        case .rideHistory: return "menu_ride_history"
        case .support: return "menu_support"
        case .notifications: return "menu_notifications"
        }
    }

    /// The wireframe's leading glyph, as an SF Symbol (D2' §C's "Material Symbols / SF Symbols").
    var symbolName: String {
        switch self {
        case .myVehicles: return "car.fill"
        case .vehicleOnboarding: return "plus.circle.fill"
        case .trackerPairing: return "antenna.radiowaves.left.and.right"
        case .sharing: return "link"
        case .profile: return "person.fill"
        case .rideHistory: return "list.bullet.rectangle.portrait.fill"
        case .support: return "bubble.left.and.bubble.right.fill"
        case .notifications: return "bell.fill"
        }
    }

    /// The tile colour behind the glyph — the wireframe's `.glist .gr .ic` background.
    var accent: Color {
        switch self {
        case .myVehicles: return MageRideVehicleColor.sedan
        case .vehicleOnboarding: return MageRideVehicleColor.modeC
        case .trackerPairing: return MageRideColor.onSurfaceVariant
        case .sharing: return MageRideVehicleColor.privateHire
        case .profile: return MageRideColor.secondary
        case .rideHistory: return MageRideColor.primary
        case .support: return MageRideColor.success
        case .notifications: return MageRideColor.error
        }
    }
}
