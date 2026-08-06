import SwiftUI

/// **SCR-DI-007 · permissions** — the last gate before the dashboard.
///
/// The wireframe's two rows, each with its own green `Toggle`: *Location — Always* and
/// *Notifications*, then "Continue to dashboard". Tapping a row that is not granted asks for it; a
/// row the driver has already refused cannot be asked again by anyone, so it falls through to this
/// app's page in Settings — D2's *"denied → Settings deep-link"*.
///
/// **Continue is never disabled.** AL-27 puts nothing between Profile Setup and Home, and a screen a
/// driver cannot leave is one they uninstall. What a refusal costs them is going *online*, which is
/// the dashboard's gate (US-9.6) and says so there.
///
/// **Δ Section C:** two rows here where Android has five. Foreground and background location are one
/// grant on iOS, and neither the battery-optimisation exemption nor "display over other apps" exists
/// on this platform at all — see ``DriverPermission``.
///
/// `@MainActor` on the whole view, not on its initialiser: every member here reads a `@MainActor`
/// model, and annotating the type once is what keeps a helper added later from being the one
/// non-isolated member that stops compiling when C103 raises `SWIFT_STRICT_CONCURRENCY`.
@MainActor
struct PermissionsScreen: View {

    @StateObject private var model: PermissionsModel
    @Environment(\.scenePhase) private var scenePhase

    private let onContinue: () -> Void

    init(
        permissions: DriverPermissions,
        preferences: OnboardingPreferences,
        onContinue: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: PermissionsModel(permissions: permissions, preferences: preferences)
        )
        self.onContinue = onContinue
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                    Text(key: "permissions_intro")
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)

                    GroupedList {
                        ForEach(Array(DriverPermission.allCases.enumerated()), id: \.offset) { index, permission in
                            row(permission, isLast: index == DriverPermission.allCases.count - 1)
                        }
                    }

                    Button(action: cont) {
                        Text(key: "permissions_continue")
                    }
                    .buttonStyle(.mageCta)
                    .padding(.top, MageRideSpacing.md)
                }
                .padding(MageRideSpacing.md)
            }
            .background(MageRideColor.surface)
            .navigationTitle(Text(key: "permissions_title"))
            .navigationBarTitleDisplayMode(.large)
        }
        .task { await model.refresh() }
        // A Settings screen does not report back; the only signal that anything changed is coming
        // back to the foreground. Re-reading on `.active` is what makes the toggles true after one.
        .onChange(of: scenePhase) { phase in
            if phase == .active { Task { await model.refresh() } }
        }
    }

    /// One wireframe `.glist .gr`: the glyph, the title, the rationale, and the switch that
    /// reflects the OS's answer.
    ///
    /// The `Toggle` only ever travels one way here — a granted permission cannot be revoked from
    /// inside the app, and the OS's own settings are where it comes back off.
    private func row(_ permission: DriverPermission, isLast: Bool) -> some View {
        GroupedRow(
            titleKey: permission.titleKey,
            subtitleKey: permission.rationaleKey,
            symbolName: permission.symbolName,
            symbolTint: permission == .locationAlways ? MageRideColor.primary : MageRideColor.error,
            showsSeparator: !isLast
        ) {
            Toggle(
                "",
                isOn: Binding(
                    get: { model.state.isGranted(permission) },
                    set: { isOn in
                        guard isOn else { return }
                        Task { await model.ask(permission) }
                    }
                )
            )
            .labelsHidden()
            .disabled(model.state.asking != nil)
            // The system's own green, not a MageRide token: §0.2's palette has no "on" colour and
            // the wireframe's `.toggle.on` is `--iosGreen`, which is what a `Toggle` already is.
            .accessibilityLabel(Text(key: permission.titleKey))
        }
    }

    private func cont() {
        model.acknowledge()
        onContinue()
    }
}
