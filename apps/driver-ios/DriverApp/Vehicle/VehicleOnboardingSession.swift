import Combine
import Foundation

/// Which vehicle the onboarding screens are talking about.
///
/// ``DriverRoute/vehicleOnboardingStatus`` carries **no arguments** — the shell fixed the route
/// table before any screen group existed (C085) — so "show the verdicts for the vehicle I just
/// submitted" cannot be expressed as a navigation argument. It is expressed here instead: the wizard
/// ``open(_:)``s the vehicle before navigating, My Vehicles does the same when a row is tapped, and
/// SCR-DI-006 reads ``vehicleId``.
///
/// Exactly the shape and the justification of ``DocumentCaptureCoordinator``, and a process-wide
/// single instance for the same reason: the screen that set the value is not on screen while the
/// screen that reads it is on top.
///
/// `@MainActor` + `@Published` rather than a `Flow`, again for ``DocumentCaptureCoordinator``'s
/// reason — a stored property already replays, and SwiftUI does not tear a view down to push another
/// one over it.
@MainActor
final class VehicleOnboardingSession: ObservableObject {

    /// The vehicle SCR-DI-006 should render, or `nil` when nothing has named one.
    @Published private(set) var vehicleId: String?

    /// Names the vehicle the next visit to SCR-DI-006 is about. Call before navigating.
    func open(_ vehicleId: String) {
        self.vehicleId = vehicleId
    }

    /// Forgets it.
    ///
    /// Called when a vehicle is deactivated: a status screen restored onto a deleted vehicle would
    /// ask registry-svc for it and render a `404` as an error the driver cannot act on.
    func close() {
        vehicleId = nil
    }
}
