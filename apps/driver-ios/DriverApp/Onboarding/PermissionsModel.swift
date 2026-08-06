import Foundation

/// SCR-DI-007's state — the two rows, and whether each is held.
struct PermissionsState {

    /// What the OS says right now, per row.
    var granted: [DriverPermission: Bool] = [:]

    /// The row whose sheet is on screen. Its switch is inert while it is.
    var asking: DriverPermission?

    func isGranted(_ permission: DriverPermission) -> Bool { granted[permission] == true }
}

/// SCR-DI-007 — the last gate before the dashboard.
///
/// **Continue is never disabled.** AL-27 puts nothing between Profile Setup and Home, and a screen
/// a driver cannot leave is one they uninstall. What a refusal costs them is going *online*, which
/// is the dashboard's gate (US-9.6) and says so there.
///
/// The screen refreshes on appear and on every return to the foreground, because that is the only
/// signal a Settings trip gives back — iOS does not report a permission changed outside the app.
@MainActor
final class PermissionsModel: ObservableObject {

    @Published private(set) var state = PermissionsState()

    private let permissions: DriverPermissions
    private let preferences: OnboardingPreferences

    init(permissions: DriverPermissions, preferences: OnboardingPreferences) {
        self.permissions = permissions
        self.preferences = preferences
    }

    /// Re-reads every row. Call on appear and on `willEnterForeground`.
    func refresh() async {
        await permissions.refresh()
        var granted: [DriverPermission: Bool] = [:]
        for permission in DriverPermission.allCases {
            granted[permission] = permissions.isGranted(permission)
        }
        state.granted = granted
    }

    /// A row was tapped.
    ///
    /// A permission iOS has already refused shows no sheet at all — the call would return with
    /// nothing on screen and the driver would conclude the switch is broken — so a permanently
    /// denied row goes to Settings instead. That is D2's *"denied → Settings deep-link"*, and it is
    /// the same rule the Android screen applies after two refusals.
    func ask(_ permission: DriverPermission) async {
        guard !state.isGranted(permission), state.asking == nil else { return }

        if permissions.isDeniedPermanently(permission) {
            permissions.openSettings()
            return
        }

        state.asking = permission
        _ = await permissions.request(permission)
        state.asking = nil
        await refresh()
    }

    /// The CTA. Remembers that the screen has been shown, so a denial does not trap the driver in
    /// it on the next cold start — the *grants* are the OS's and are asked for again on the
    /// dashboard.
    func acknowledge() {
        preferences.permissionsAcknowledged = true
    }
}
