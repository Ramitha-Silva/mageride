import Foundation

/// Which vehicle this handset publishes as — **D-03's single active publisher**, and SCR-DI-026's
/// *"Only one vehicle can be live at a time"*.
///
/// **This is deliberately local.** `backend/contracts/registry.yaml` has no "set my active vehicle"
/// operation, and it does not need one: the choice is a property of *this device*, not of the
/// driver. It becomes visible to the platform when the driver goes online — the MQTT username **is**
/// the vehicle id (D6' §3, and `apps/driver-ios/CLAUDE.md` says the same) — so a driver with two
/// approved vehicles picks one here and the broker learns which on CONNECT. A server-side field
/// would be a second answer to a question the connection already answers, and the two would disagree
/// the moment the driver switched handsets.
///
/// C088's dashboard reads this for the *"Live: 3W · ABC-1234"* chip and for the US-9.6 go-online
/// gate; C087 is what writes it.
///
/// A protocol with a `UserDefaults` implementation for the same reason ``OnboardingPreferences`` is
/// one: a model test has no store, and a fake is what makes the selection settable.
protocol ActiveVehicleStore: AnyObject {

    /// The vehicle selected to go live, or `nil` when the driver has not chosen one.
    var activeVehicleId: String? { get set }
}

/// ``ActiveVehicleStore`` over the app's own `UserDefaults` suite. Nothing here is a secret — the
/// choice is on the screen that made it — so neither the Keychain nor C018's encrypted database is
/// the right home for it.
final class UserDefaultsActiveVehicleStore: ActiveVehicleStore {

    private let store: UserDefaults

    init(store: UserDefaults = .standard) {
        self.store = store
    }

    var activeVehicleId: String? {
        get { store.string(forKey: Keys.activeVehicle) }
        set { store.set(newValue, forKey: Keys.activeVehicle) }
    }

    /// The same value `driver_vehicles`' `active_vehicle_id` holds on Android, prefixed so it cannot
    /// collide with anything else in the standard suite.
    private enum Keys {
        static let activeVehicle = "mageride.vehicles.active_vehicle_id"
    }
}
