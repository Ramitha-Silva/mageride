import MageRideShared
import SwiftUI

/// SCR-PI-010 — the live map, and the app's home. SCR-PI-032 is a **state** of it.
///
/// The wireframe: a full-bleed MapLibre map carrying the `⦿` filter FAB and the `⊕` recentre FAB,
/// over a bottom sheet with *"Where to?"*, the ★ Home / ★ Work chips and the recent destinations,
/// over the tab bar. The tab bar and SCR-PI-032's connection banner are the shell's (C094) and are
/// drawn around this.
///
/// **SCR-PI-032 is a state of this screen, not a screen of its own.** When the live plane is not
/// connected the markers fade to last-known (US-15.2), a *"Reconnecting…"* capsule appears over the
/// map, and the shell's banner says why. Nothing is erased: a passenger who has lost signal still
/// wants to know where the bus was.
///
/// **Δ the cell's own `Δ iOS` clause — the home sheet is drawn, not presented.** That clause reads
/// *"`.sheet` detents `.medium/.large`"*, and the cell **draws the tab bar and the sheet at the same
/// time** with a map still visible between them. A presented `.sheet` cannot be that: it covers the
/// tab bar, and below iOS 16.4 (this app's floor is 16.0) nothing behind it is interactive — which
/// would make every vehicle marker untappable and take MAP-07, SCR-PI-007 and AL-23's Mode B route
/// with it. So the panel is drawn above the tab bar exactly as the frame shows it, which is also the
/// call `apps/driver-ios` made for SCR-DI-010's `DashboardSheet`. The two halves of the cell
/// disagree; the drawing and the functional requirement agree with each other. Recorded in the C096
/// handoff as a micro-change-set candidate.
///
/// SCR-PI-006 and SCR-PI-007 **are** presented sheets — both are drawn over a scrim with no tab bar
/// in their own cells, and neither has a back-stack entry (see ``PassengerRoute``).
@MainActor
struct LiveMapScreen: View {

    @StateObject private var model: LiveMapModel

    /// SCR-PI-008.
    let onSearch: () -> Void

    /// SCR-PI-024, with the vehicle id pre-filled (AL-23, US-4.6).
    let onRequestModeBAccess: (String) -> Void

    /// A shortcut or a recent **is** a chosen destination, so both go where a chosen place goes.
    let onPlaceChosen: (GeocodedPlace) -> Void

    /// SCR-PI-026 — the ＋ chip, which is how a passenger gets their first shortcut.
    let onAddAddress: () -> Void

    @State private var isFilterOpen = false

    init(
        live: PassengerLiveMap,
        locations: PassengerLocationSource,
        places: PassengerPlaces,
        snapshots: NearbySnapshots,
        recents: RecentPlaces,
        onSearch: @escaping () -> Void,
        onRequestModeBAccess: @escaping (String) -> Void,
        onPlaceChosen: @escaping (GeocodedPlace) -> Void,
        onAddAddress: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: LiveMapModel(
                live: live,
                locations: locations,
                places: places,
                snapshots: snapshots,
                recents: recents
            )
        )
        self.onSearch = onSearch
        self.onRequestModeBAccess = onRequestModeBAccess
        self.onPlaceChosen = onPlaceChosen
        self.onAddAddress = onAddAddress
    }

    var body: some View {
        VStack(spacing: 0) {
            map
            HomeSheet(
                shortcuts: model.state.shortcuts,
                recents: model.state.recents,
                fix: model.state.fix,
                onSearch: onSearch,
                onShortcut: { onPlaceChosen($0.asPlace) },
                onAddAddress: onAddAddress,
                onRecent: onPlaceChosen
            )
        }
        .background(MageRideColor.background)
        // The cell draws no navigation bar at all — the tab bar is this screen's whole chrome.
        .toolbar(.hidden, for: .navigationBar)
        .task { model.start() }
        .onDisappear { model.stop() }
        // The sheet's two lists are written on OTHER screens — recents on SCR-PI-008, saved
        // addresses on SCR-PI-026 — and neither source has a change feed, so both are re-read
        // whenever the map comes back to the front. Two tasks rather than one, so a slow or failing
        // read cannot hold the other up. See `LiveMapModel.loadShortcuts()`.
        .onAppear {
            Task { await model.reloadRecents() }
            Task { await model.loadShortcuts() }
        }
        .sheet(isPresented: $isFilterOpen) {
            // Closure literals rather than `model.setMode` / `model.setType`: a method reference on
            // a `@MainActor` model is a `@MainActor` function value, and handing one to a plain
            // `(ModeToken, Bool) -> Void` parameter loses the isolation. A literal inherits it.
            ModeFilterSheet(
                filter: model.state.filter,
                onModeChanged: { model.setMode($0, enabled: $1) },
                onTypeChanged: { model.setType($0, enabled: $1) },
                onDismiss: { isFilterOpen = false }
            )
        }
        .sheet(isPresented: popupBinding) {
            if let vehicle = model.state.selected {
                VehiclePopup(vehicle: vehicle, detail: model.state.detail, around: model.state.fix)
            }
        }
    }

    private var map: some View {
        MageRideMap(
            vehicles: model.state.vehicles,
            userPosition: model.state.fix,
            camera: model.state.camera,
            // §0.3's `⊕` recentre FAB. Non-nil is what draws it; the animation back to the blue dot
            // is `MageRideMap`'s, because only it holds the `MLNMapView`. This screen has nothing to
            // add — the map does not follow the passenger, so there is no follow mode to re-arm.
            onRecentre: {},
            onVehicleTap: { vehicleId in
                switch model.onMarkerTapped(vehicleId) {
                case .requestModeBAccess(let id):
                    onRequestModeBAccess(id)
                // The popup opens from the model's own state; nothing to do here.
                case .showPopup, .ignored:
                    break
                }
            },
            dimmed: model.state.stale
        )
        // The wireframe's `⦿`. D2' §SCR-PA-010's component table calls it the *"Mode filter"* FAB and
        // routes it to SCR-*-006; on Android it is the app bar's trailing icon, and this platform has
        // no app bar on this screen.
        .overlay(alignment: .topTrailing) {
            MapOverlayButton(
                symbolName: "line.3.horizontal.decrease.circle",
                accessibilityKey: "filter_title",
                action: { isFilterOpen = true }
            )
            .padding(MageRideSpacing.sm)
        }
        .overlay(alignment: .top) { notices }
    }

    /// SCR-PI-032's reconnect indicator, and US-7.14's *"why is this map empty"*.
    ///
    /// Both can be true at once — a dropped socket empties the map *and* is being reconnected — so
    /// they stack rather than rank.
    @ViewBuilder
    private var notices: some View {
        VStack(spacing: 0) {
            if model.state.status == .connecting {
                MapNotice(messageKey: "map_reconnecting")
            }
            switch model.state.emptyReason {
            case .filteredOut: MapNotice(messageKey: "map_empty_filtered")
            case .nothingNearby: MapNotice(messageKey: "map_empty_nearby")
            case .offline: MapNotice(messageKey: "map_empty_offline")
            case .none: EmptyView()
            }
        }
    }

    /// SCR-PI-007's presentation, derived from the model rather than held beside it — the popup also
    /// closes when the vehicle leaves the map, which is a fact the model owns.
    private var popupBinding: Binding<Bool> {
        Binding(
            get: { model.state.selected != nil },
            set: { presented in if !presented { model.dismissPopup() } }
        )
    }
}

/// The wireframe's bottom panel: the grabber, the search entry, US-7.13's shortcuts and the recents.
private struct HomeSheet: View {

    let shortcuts: [SavedAddress]
    let recents: [GeocodedPlace]
    let fix: MapFix?
    let onSearch: () -> Void
    let onShortcut: (SavedAddress) -> Void
    let onAddAddress: () -> Void
    let onRecent: (GeocodedPlace) -> Void

    var body: some View {
        VStack(spacing: MageRideSpacing.xs) {
            SheetGrabber()

            SearchBarButton(titleKey: "map_where_to", action: onSearch)

            // `★ Home | ★ Work | ＋ Add`. The ＋ is always there, including — especially — when there
            // are no shortcuts at all, because it is how a passenger gets their first one.
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: MageRideSpacing.xs) {
                    ForEach(shortcuts, id: \.addressId) { address in
                        PlaceChip(label: address.label, symbolName: "star.fill") { onShortcut(address) }
                    }
                    PlaceChip(label: "map_add_address".localised, symbolName: "plus", action: onAddAddress)
                }
                .padding(.vertical, 1)
            }

            if !recents.isEmpty {
                SectionLabel(key: "map_recent")
                ForEach(recents, id: \.rowId) { place in
                    RecentRow(place: place, fix: fix) { onRecent(place) }
                }
            }
        }
        .padding(.horizontal, MageRideSpacing.sm)
        .padding(.top, MageRideSpacing.xs)
        .padding(.bottom, MageRideSpacing.sm)
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background, in: TopRoundedRectangle(radius: MageRideRadius.lg))
        .mageElevated()
    }
}

/// One `🕘 Nugegoda Junction · 2.4 km` row — a place this handset has been looking for (§2.2, local
/// only).
///
/// **The second line is the distance, which is what the wireframe draws.** The Android twin prints
/// the address line there instead; the frame's own row is `2.4 km`, the passenger's position is
/// already on this screen for MAP-02, and a straight-line distance is honest about what it is. The
/// address line is the fallback for a passenger with no fix yet — see the C096 handoff.
private struct RecentRow: View {

    let place: GeocodedPlace
    let fix: MapFix?
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: MageRideSpacing.sm) {
                Image(systemName: "clock")
                    .font(.system(size: MageRideControl.rowIcon * 0.8))
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .frame(width: MageRideControl.rowIcon)

                VStack(alignment: .leading, spacing: 1) {
                    Text(place.displayName)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)
                        .lineLimit(1)
                    if let secondary {
                        Text(secondary)
                            .mageFont(.caption)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                            .lineLimit(1)
                    }
                }

                Spacer(minLength: 0)
            }
            .frame(minHeight: MageRideControl.minimumTapTarget)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
    }

    private var secondary: String? {
        if fix != nil { return MapFormat.distance(from: fix, to: place.point) }
        return place.line1?.isEmpty == false ? place.line1 : nil
    }
}
