import MageRideShared
import SwiftUI

/// SCR-PI-033 — the **Menu** tab.
///
/// The cell: a large title, the identity card, a `glist` of 🚐 Private transport / 🔑 My
/// subscriptions / ★ Saved addresses / ⚙ Profile & settings, a second `glist` holding 💬 Help &
/// support, and the tab bar with **Menu** selected.
///
/// **This is a tab, not a drawer, and that is the largest Section C delta in this app.**
/// `passenger_android.html` draws a `ModalNavigationDrawer` behind a scrim, opened from a `≡` in
/// every app bar; this cell's own `Δ iOS` clause is *"`List` with `NavigationLink` rows"* and it
/// draws a fourth tab. Nothing in this target hosts a drawer and no screen has a `≡` — see
/// ``PassengerTab`` and ``PassengerShell``. The Android drawer is **not** an AL-31 violation and
/// this is not a repeal of it: AL-31 is a rule about the *driver* dashboard.
///
/// **The rows are ``PassengerMenuDestination``'s and this screen adds none.** That table is the
/// shell's — it is a statement about the *route table* — and `NavigationShellTests` pins its count,
/// its order and its two groups, so a later screen group cannot quietly add a sixth row or point one
/// at the nearest existing screen. What C101 adds is the list, the links and the card above them.
///
/// **The card has no chevron here and does on SCR-PI-027**, which is what the two frames draw: this
/// one is an identity block and that one opens SCR-PI-027b. *Profile & settings* is the row that
/// goes there.
///
/// **Log out is deliberately not a row.** The Android drawer has one because a drawer is the only
/// place it can be; here SCR-PI-027 is a full screen with room for it, and C014's `RouteToLogin` has
/// exactly one subscriber (``PassengerShellModel``) whichever door is used.
///
/// A hand-built `GroupedList` rather than a `List` of `NavigationLink`s: this app's navigation is
/// value-based through ``PassengerNavigator``, so a row is a `Button` that opens a route — and the
/// `glist` is the shape every other grouped list in this cluster already draws.
@MainActor
struct MenuScreen: View {

    @ObservedObject var identity: PassengerIdentity

    let onOpen: (PassengerRoute) -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.md) {
                IdentityCard(name: identity.profile?.firstName, identity: identityLine)

                GroupedList {
                    // The last row asked for by identity rather than by index: **Swift has no key
                    // paths into tuples**, so `ForEach(Array(x.enumerated()), id: \.element.id)`
                    // does not compile — the C087 finding, which `ModeFilterSheet` and
                    // `PaymentMethodScreen` already follow.
                    ForEach(PassengerMenuDestination.primary) { row in
                        menuRow(row, showsSeparator: row != PassengerMenuDestination.primary.last)
                    }
                }

                GroupedList {
                    ForEach(PassengerMenuDestination.secondary) { row in
                        menuRow(row, showsSeparator: false)
                    }
                }
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "nav_menu"))
        .navigationBarTitleDisplayMode(.large)
        // Only when nothing has been read at all — SCR-PI-027 and SCR-PI-027b both hand their
        // profile over, so a passenger who has opened either has already filled this in.
        .task { await identity.refresh() }
    }

    // MARK: -

    private func menuRow(_ row: PassengerMenuDestination, showsSeparator: Bool) -> some View {
        Button {
            onOpen(row.route)
        } label: {
            GroupedRow(
                titleKey: row.labelKey,
                symbolName: row.symbolName,
                symbolTint: row.tint,
                showsSeparator: showsSeparator
            ) {
                RowChevron()
            }
        }
        .buttonStyle(.plain)
    }

    /// `ID: 01J… · +94 77 123 4567` — the same pair SCR-PI-027's card prints, and the same ULID.
    private var identityLine: String? {
        identity.profile.map { "settings_identity".localisedFormat($0.userId, $0.phone) }
    }
}
