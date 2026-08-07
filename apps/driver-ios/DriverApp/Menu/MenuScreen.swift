import SwiftUI

/// **SCR-DI-036 · the Menu tab.**
///
/// The wireframe draws a large title, a driver card and one grouped list of eight rows over a tab bar
/// with **Menu** selected.
///
/// **AL-31 is why this is a tab rather than a corner affordance.** *"The dashboard has NO top-left
/// hamburger; navigation is the bottom Menu tab"*, and ``DriverTab/menu`` is a peer of Home.
///
/// **Δ iOS — this is a `List`, where the Android twin is a `ModalDrawerSheet`.** The cell's own `Δ iOS`
/// clause is *"`List` + `NavigationLink`"*, and it is the right shape here for a reason beyond the
/// wireframe: a modal drawer is Material's answer to a navigation surface, and on this platform a tab
/// whose content is a scrim with a panel beside it would be a drawer wearing a tab's clothes. The rows
/// push onto the Menu tab's own stack, which is what makes the system's `‹ Menu` back button say the
/// right thing on every screen that hangs off here.
///
/// The rows are `Button`s rather than `NavigationLink`s because ``DriverNavigator`` owns the four
/// stacks — a `NavigationLink(value:)` would push onto whichever stack it happened to be inside and
/// leave the navigator's copy of the path behind. The destination is still ``DriverRoute``'s, and it is
/// still resolved by the one `navigationDestination` in the app.
@MainActor
struct MenuScreen: View {

    let driverId: String?
    let onOpen: (DriverRoute) -> Void

    var body: some View {
        List {
            Section {
                driverCard
                    .listRowInsets(EdgeInsets())
                    .listRowBackground(Color.clear)
            }

            Section {
                ForEach(MenuDestination.allCases) { destination in
                    row(destination)
                }
            }
        }
        .listStyle(.insetGrouped)
        .navigationTitle(Text(key: "nav_menu"))
        .background(MageRideColor.background)
    }

    /// The wireframe's driver card.
    ///
    /// It draws a name, an `L3` badge, a `DRV-22011` id and `★4.8`. **The rating has no app-facing
    /// read** (see ``HomeScreen``'s status bar) and the display name is the profile group's own read
    /// (C092, SCR-DI-029), which this component does not own. What is left is the driver's own platform
    /// id, which the session already holds and which SCR-DI-029 is where a driver reads. A wrong name
    /// is worse than none and an invented rating is worse still; the card carries what is true.
    private var driverCard: some View {
        HStack(spacing: MageRideSpacing.sm) {
            Image(systemName: "person.crop.circle.fill")
                .font(.system(size: MageRideControl.avatarSmall))
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            VStack(alignment: .leading, spacing: 1) {
                Text(key: "menu_driver")
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
                Text(driverId ?? MageRideSymbols.unknown)
                    .mageFont(.label)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }
            Spacer(minLength: 0)
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .accessibilityElement(children: .combine)
    }

    /// One `.glist .gr` row: a coloured glyph tile, the label, and the chevron.
    private func row(_ destination: MenuDestination) -> some View {
        Button { onOpen(destination.route) } label: {
            HStack(spacing: MageRideSpacing.sm) {
                Image(systemName: destination.symbolName)
                    .font(.footnote)
                    .foregroundStyle(MageRideColor.onStatus)
                    .frame(width: MageRideControl.listRowIcon, height: MageRideControl.listRowIcon)
                    .background(
                        destination.accent,
                        in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
                    )

                Text(key: destination.labelKey)
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)

                Spacer(minLength: MageRideSpacing.xs)

                Image(systemName: "chevron.right")
                    .font(.footnote)
                    .foregroundStyle(MageRideColor.outlineVariant)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}
