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

    @StateObject private var model: MenuModel
    private let onOpen: (DriverRoute) -> Void

    /// `@autoclosure` and `_model =`, the same shape SCR-DI-029 uses: a `@StateObject` must be
    /// assigned through its wrapper, and building the model eagerly at the call site would
    /// construct one on every parent redraw and throw it away.
    init(model: @autoclosure @escaping () -> MenuModel, onOpen: @escaping (DriverRoute) -> Void) {
        _model = StateObject(wrappedValue: model())
        self.onOpen = onOpen
    }

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
        .task {
            // Δ MCS-27 — the cache first, so the tab opens on a name; the reads refresh behind it.
            await model.paintFromCache()
            await model.load()
        }
        .task { await model.loadPhoto() }
        .listStyle(.insetGrouped)
        .navigationTitle(Text(key: "nav_menu"))
        .background(MageRideColor.background)
    }

    /// The wireframe's driver card.
    ///
    /// **Δ MCS-24 — the DRIVER, not a label.** It drew `menu_driver` above the platform id, and the
    /// reason was recorded and was a good one: this component did not own the profile read, so *"a
    /// wrong name is worse than none and an invented rating is worse still"*. That reasoning
    /// survives — the rating is drawn only if one exists, and none does — but the premise does not.
    /// ``MenuModel`` owns the read now.
    ///
    /// The layout is ``DriverHeader``, which SCR-DI-029 also draws. Before this they were two
    /// independent pieces of layout for one block, and they had drifted in the same direction: both
    /// showed an identifier where the wireframe draws the vehicle.
    private var driverCard: some View {
        DriverHeader(state: model.header)
            .padding(MageRideSpacing.sm)
    }

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
