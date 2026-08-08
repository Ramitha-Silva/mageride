import MageRideShared
import SwiftUI

/// D-31's minimum-version gate, as the app sees it.
///
/// The gateway runs the check at the edge on **every** route, so any of the 177 app-facing
/// operations can answer `426`. C013 therefore raises it once on
/// `MageRideApiSignals.upgradeRequired` instead of making every call site handle it, and
/// ``PassengerShell`` is the single subscriber.
///
/// **SCR-PI-031 is two controls, not one, and the wireframe is explicit:** *"426 → mandatory
/// non-dismissible `.alert` → App Store; soft = dismissible banner (US-17.1/17.2). iOS has no forced
/// in-app update API."* So:
///
/// - **Mandatory** — an `.alert` with a single action. That is exactly SwiftUI's non-dismissible
///   form: there is no scrim tap and no swipe to swallow, unlike Compose's `onDismissRequest`, so
///   the driver app's note about "an alert with no cancel button" holds here too. A dismissible wall
///   would put the passenger in an app where every subsequent call answers `426` anyway and nothing
///   explains why.
/// - **Soft** — an inline banner with an Update action and a ✕. A wall would be a release that did
///   not require one blocking someone mid-journey. The Android twin uses a `Snackbar`; D2' §C maps
///   that to *"inline banner / `.alert`"* here, and a banner is the half that can carry an action
///   and stay put.
///
/// The driver app answers both with one dialog because D2' §SCR-DI-035 does not draw the split.
///
/// **What C102 finished, and the one thing it could not** (Δ C102). The cell draws the dialog with a
/// `⬆️` mark above its title and a full-width *"Update in App Store"* bar, and the banner as a `⬆️`
/// beside **two** lines (*"Update available"* over *"A newer version is ready"*) with the `Update`
/// textlink at its trailing edge. The banner is now exactly that. The alert's `⬆️` is **not drawn
/// and cannot be**: a SwiftUI `.alert` takes a title, a message and buttons and has no slot for an
/// image — the mark is what an `AlertDialog` gets on Android, and reaching for a custom presentation
/// to draw one would trade the platform's own non-dismissible wall for a view that has to
/// re-implement it. The bar's *label* is the cell's, which is the half that carries the meaning.
struct UpdateGate: ViewModifier {

    let signal: UpgradeRequiredSignal?
    let onUpdate: (String?) -> Void
    let onDismiss: () -> Void

    /// Only the mandatory half reaches the alert. `.constant` is right because a non-dismissible
    /// alert has nothing that can set it false — the only way out is the action.
    private var mandatory: UpgradeRequiredSignal? {
        guard let signal, signal.isMandatory else { return nil }
        return signal
    }

    private var soft: UpgradeRequiredSignal? {
        guard let signal, !signal.isMandatory else { return nil }
        return signal
    }

    func body(content: Content) -> some View {
        content
            .safeAreaInset(edge: .top, spacing: 0) {
                SoftUpdateBanner(
                    signal: soft,
                    onUpdate: { onUpdate(soft?.updateUrl) },
                    onDismiss: onDismiss
                )
            }
            .alert(
                Text("update_required_title", bundle: MageRideColor.bundle),
                isPresented: .constant(mandatory != nil),
                presenting: mandatory
            ) { gate in
                Button {
                    onUpdate(gate.updateUrl)
                } label: {
                    // The cell's own *"Update in App Store"*, which is a different label from the
                    // banner's bare *"Update"* — the wall names where it is sending the passenger
                    // because it is the only thing they can do, and the banner does not because it
                    // sits beside a ✕.
                    Text("update_action_store", bundle: MageRideColor.bundle)
                }
            } message: { _ in
                Text("update_mandatory_message", bundle: MageRideColor.bundle)
            }
    }
}

/// SCR-PI-031's soft half — *"dismissible banner"*.
///
/// Drawn on `primaryContainer` rather than on ``MageRideColor/warning``, which is
/// ``OfflineBanner``'s: the two can be on screen at once (a passenger below the version floor in a
/// tunnel), and two amber bars stacked would read as one condition described twice.
///
/// **Two lines and an up arrow, which is the cell's `.alertbanner`** (Δ C102): `⬆️`, then *"Update
/// available"* over *"A newer version is ready"*, then the `Update` textlink and the ✕. The arrow
/// points **up** because the wireframe's glyph does and because the action is an upgrade — a
/// download chevron would read as *"save this"*.
struct SoftUpdateBanner: View {

    let signal: UpgradeRequiredSignal?
    let onUpdate: () -> Void
    let onDismiss: () -> Void

    var body: some View {
        if signal != nil {
            HStack(spacing: MageRideSpacing.xs) {
                Image(systemName: "arrow.up.circle")
                    .imageScale(.small)

                VStack(alignment: .leading, spacing: 1) {
                    Text("update_optional_title", bundle: MageRideColor.bundle)
                        .mageFont(.label)
                    Text("update_optional_message", bundle: MageRideColor.bundle)
                        .mageFont(.caption)
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                Button(action: onUpdate) {
                    Text("update_action_now", bundle: MageRideColor.bundle)
                        .mageFont(.label)
                }

                Button(action: onDismiss) {
                    Image(systemName: "xmark")
                        .imageScale(.small)
                }
                .accessibilityLabel(Text("update_action_dismiss", bundle: MageRideColor.bundle))
            }
            .foregroundStyle(MageRideColor.onPrimaryContainer)
            .padding(.horizontal, MageRideSpacing.sm)
            .padding(.vertical, MageRideSpacing.xs)
            .background(MageRideColor.primaryContainer)
            .transition(.move(edge: .top).combined(with: .opacity))
        }
    }
}

extension View {

    /// Puts D-31's gate over this view. Applied once, by the shell.
    func updateGate(
        signal: UpgradeRequiredSignal?,
        onUpdate: @escaping (String?) -> Void,
        onDismiss: @escaping () -> Void
    ) -> some View {
        modifier(UpdateGate(signal: signal, onUpdate: onUpdate, onDismiss: onDismiss))
    }
}
