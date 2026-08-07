import SwiftUI

/// D2' §SCR-PI-032 — *"Top banner 'Connection lost — showing last known' (US-15.6); current screen
/// preserved (not full takeover)"*, with the cell's own `Δ iOS` clause: **`.safeAreaInset` banner**.
///
/// An inline strip above the content rather than an alert or a transient toast: D2' §C maps
/// Android's `Snackbar` to "inline banner / `.alert`" on iOS, and the two are not interchangeable
/// here. A transient thing times out and the condition it describes does not; an `.alert` is a
/// takeover, which the requirement rules out in so many words.
///
/// **`.safeAreaInset` rather than an overlay, and the wireframe says so.** An overlay would sit
/// *on top of* the map and cover it; a safe-area inset takes space from the content, which is what
/// *"current screen preserved"* means when the screen underneath is a full-bleed map — the map's own
/// controls move down instead of disappearing behind a bar. The driver app uses an overlay because
/// its dashboard is a scroll view under a sheet and there is nothing at the top edge to cover.
/// ``PassengerShell`` applies it.
///
/// §SCR-PI-032 adds *"auto-clears on reconnect < 5 s"* — that is ``isVisible`` going false, which is
/// what ``ConnectivityMonitor`` publishes the moment a satisfied path is back, and what
/// ``PassengerLiveMap``'s R-09 backoff makes true for the socket (its first retry lands inside
/// 1.25 s). No separate timer.
struct OfflineBanner: View {

    let isVisible: Bool

    var body: some View {
        if isVisible {
            HStack(spacing: MageRideSpacing.xs) {
                Image(systemName: "wifi.slash")
                    .imageScale(.small)
                Text("offline_banner_message", bundle: MageRideColor.bundle)
                    .mageFont(.label)
            }
            .foregroundStyle(MageRideColor.onStatus)
            .padding(.horizontal, MageRideSpacing.sm)
            .padding(.vertical, MageRideSpacing.xs)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(MageRideColor.warning)
            .transition(.move(edge: .top).combined(with: .opacity))
            // One announcement, not a focus steal: VoiceOver says the banner arrived and leaves the
            // passenger where they were, which is the accessible reading of "screen preserved".
            .accessibilityAddTraits(.isStaticText)
        }
    }
}
