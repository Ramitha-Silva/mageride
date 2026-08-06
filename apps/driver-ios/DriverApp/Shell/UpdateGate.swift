import MageRideShared
import SwiftUI

/// D-31's minimum-version gate, as the app sees it.
///
/// The gateway runs the check at the edge on **every** route, so any of the 176 operations can
/// answer `426`. C013 therefore raises it once on `MageRideApiSignals.upgradeRequired` instead of
/// making 176 call sites handle it, and ``DriverShell`` is the single subscriber.
///
/// **`isMandatory` decides whether there is a way out.** A mandatory gate is not dismissible and
/// has no "Later": every subsequent call would answer 426 anyway, so a dismissible wall would put
/// the driver in an app where nothing works and nothing explains why. An optional one is a nudge
/// and must be dismissible, or a driver mid-shift is blocked by a release that did not require it.
///
/// Two presentations for the two cases, which is D2' §C's "`.alert` / `.confirmationDialog`" row
/// read properly: an `.alert` with no dismissing action is exactly SwiftUI's non-dismissible form —
/// there is no scrim tap and no swipe to swallow, unlike Compose's `onDismissRequest`. The optional
/// gate is the same alert with a second button.
struct UpdateGate: ViewModifier {

    let signal: UpgradeRequiredSignal?
    let onUpdate: (String?) -> Void
    let onDismiss: () -> Void

    func body(content: Content) -> some View {
        content.alert(
            Text("update_required_title", bundle: MageRideColor.bundle),
            isPresented: .constant(signal != nil),
            presenting: signal
        ) { gate in
            Button {
                onUpdate(gate.updateUrl)
            } label: {
                Text("update_action_now", bundle: MageRideColor.bundle)
            }

            if !gate.isMandatory {
                Button(role: .cancel) {
                    onDismiss()
                } label: {
                    Text("update_action_later", bundle: MageRideColor.bundle)
                }
            }
        } message: { gate in
            Text(
                gate.isMandatory ? "update_mandatory_message" : "update_optional_message",
                bundle: MageRideColor.bundle
            )
        }
    }
}

extension View {

    /// Puts D-31's wall over this view. Applied once, by the shell.
    func updateGate(
        signal: UpgradeRequiredSignal?,
        onUpdate: @escaping (String?) -> Void,
        onDismiss: @escaping () -> Void
    ) -> some View {
        modifier(UpdateGate(signal: signal, onUpdate: onUpdate, onDismiss: onDismiss))
    }
}
