import SwiftUI

/// **SCR-PI-005 · location permission** — the rationale before the system dialog.
///
/// The wireframe's centred column: an illustration panel, the title *Allow location access*, one
/// sentence, a spacer, the `Allow location` CTA and a `Not now` link underneath — with the system's
/// own *"Allow Once / Allow While Using App / Don't Allow"* sheet drawn over it. That sheet is iOS's
/// and this screen only asks for it: the cell's `Δ iOS` clause is
/// `CLLocationManager.requestWhenInUseAuthorization()`.
///
/// **Neither control gates anything.** Both continue to the map — see ``PermissionsModel``. The CTA
/// changes to *"Open Settings"* once a refusal has made asking a no-op, which is the one thing on
/// this screen a passenger cannot recover from any other way.
@MainActor
struct PermissionsScreen: View {

    @StateObject private var model: PermissionsModel

    private let onContinue: () -> Void

    @Environment(\.scenePhase) private var scenePhase

    init(
        permissions: LocationPermission,
        preferences: AppPreferences,
        onContinue: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: PermissionsModel(permissions: permissions, preferences: preferences))
        self.onContinue = onContinue
    }

    var body: some View {
        VStack(spacing: MageRideSpacing.sm) {
            Spacer(minLength: 0)

            IllustrationPanel(
                symbolName: "location.fill",
                caption: "permission_location_caption".localised,
                height: MageRideControl.illustrationPanel
            )

            Text(key: "permission_location_title")
                .mageFont(.headline)
                .foregroundStyle(MageRideColor.onSurface)
                .multilineTextAlignment(.center)

            Text(key: "permission_location_body")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .multilineTextAlignment(.center)

            Spacer(minLength: MageRideSpacing.md)

            Button {
                Task {
                    await model.primaryAction()
                    onContinue()
                }
            } label: {
                Text(key: model.state.opensSettings ? "permission_open_settings" : "permission_allow_location")
            }
            .buttonStyle(.mageCta(loading: model.state.isAsking))
            .disabled(model.state.isAsking)

            TextLink(key: "permission_not_now", isEnabled: !model.state.isAsking) {
                model.skip()
                onContinue()
            }
        }
        .padding(.horizontal, MageRideSpacing.md)
        .padding(.bottom, MageRideSpacing.md)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MageRideColor.surface)
        .task { model.refresh() }
        // Settings is where a denial is undone, and the app is not running while that happens. Every
        // return to the foreground re-reads the grant, so the CTA is never offering a door that has
        // since opened.
        .onChange(of: scenePhase) { phase in
            if phase == .active { model.refresh() }
        }
    }
}
