import MageRideShared
import SwiftUI

/// SCR-PI-012a — a Google Maps link becomes a pin.
///
/// The cell: a scrim over the map, a grabber, the title with a `📦 DROP-OFF` badge, the explanatory
/// line, the link field with its Paste control, a pin-preview card carrying the reverse-geocoded
/// address and the coordinates, and *"Pick on map"* / *"Use this drop-off"*.
///
/// **`UIPasteControl`, which is the fence rather than a convenience.** iOS 16 shows a banner every
/// time an app reads the pasteboard programmatically; a system paste button reads it with the user's
/// own tap and no banner, and — the part that matters — the app never touches the clipboard until
/// they ask. The cell's own `Δ iOS` clause names both `UIPasteboard` and `UIPasteControl`; this uses
/// the control, and the edit-menu Paste on the field is the same gesture by another route.
struct PasteLinkSheet: View {

    @StateObject private var model: PasteLinkModel

    /// What the sheet is filling in — the badge, and the CTA's own wording.
    let target: PasteTarget
    let onUse: (Place) -> Void
    let onPickOnMap: () -> Void
    let onDismiss: () -> Void

    init(
        bookings: BookingRepository,
        target: PasteTarget,
        onUse: @escaping (Place) -> Void,
        onPickOnMap: @escaping () -> Void,
        onDismiss: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: PasteLinkModel(bookings: bookings))
        self.target = target
        self.onUse = onUse
        self.onPickOnMap = onPickOnMap
        self.onDismiss = onDismiss
    }

    /// Which field opened the sheet. Only the copy differs, which is exactly why it is a parameter
    /// and not three sheets.
    enum PasteTarget {
        case pickup
        case dropoff

        var titleKey: String { "paste_title" }
        var badgeKey: String { self == .pickup ? "paste_label_pickup" : "paste_label_dropoff" }
        var useKey: String { "paste_use" }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            HStack(spacing: MageRideSpacing.xs) {
                Text(key: target.titleKey)
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
                Spacer(minLength: 0)
                SolidBadge(text: target.badgeKey.localised, color: MageRideColor.secondary)
            }

            Text(key: "paste_explainer")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            pasteField

            preview

            HStack(spacing: MageRideSpacing.xs) {
                OutlinedAction(titleKey: "search_select_on_map", symbolName: "mappin.and.ellipse") {
                    onPickOnMap()
                    onDismiss()
                }
                Button {
                    guard let place = model.state.place else { return }
                    onUse(place)
                    onDismiss()
                } label: {
                    Text(key: target.useKey)
                }
                .buttonStyle(.mageCta)
                .disabled(model.state.place == nil)
            }
        }
        .padding(.horizontal, MageRideSpacing.md)
        .padding(.top, MageRideSpacing.md)
        .padding(.bottom, MageRideSpacing.lg)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(MageRideColor.surface)
        .presentationDetents([.height(MageRideControl.pasteSheetHeight), .large])
        .presentationDragIndicator(.visible)
    }

    /// The wireframe's outlined `.field` with `🔗` and a Paste affordance.
    private var pasteField: some View {
        HStack(spacing: MageRideSpacing.xs) {
            Image(systemName: "link")
                .font(.system(size: MageRideControl.chipIcon))
                .foregroundStyle(MageRideColor.primary)

            Text(fieldText)
                .mageFont(.bodySmall)
                .foregroundStyle(model.state == .empty ? MageRideColor.onSurfaceVariant : MageRideColor.onSurface)
                .lineLimit(1)
                .truncationMode(.middle)

            Spacer(minLength: MageRideSpacing.xs)

            // `payloadType:` rather than a bare trailing closure: `PasteButton` has no
            // closure-only initialiser, and `String` is the `Transferable` a Maps link arrives as.
            PasteButton(payloadType: String.self) { pasted in
                guard let text = pasted.first else { return }
                model.onPasted(text)
            }
            .labelStyle(.iconOnly)
            .buttonBorderShape(.capsule)
            .tint(MageRideColor.primary)
        }
        .padding(.horizontal, MageRideSpacing.sm)
        .frame(minHeight: MageRideControl.minimumTapTarget)
        .background(
            MageRideColor.primaryContainer,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .overlay {
            RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                .strokeBorder(MageRideColor.primary, lineWidth: MageRideControl.selectedBorder)
        }
    }

    /// The four states, one at a time (D2' §SCR-PA-012a's own list).
    @ViewBuilder
    private var preview: some View {
        switch model.state {
        case .empty:
            Text(key: "paste_hint")
                .mageFont(.caption)
                .foregroundStyle(MageRideColor.onSurfaceVariant)

        case .parsing:
            LoadingRow(messageKey: "paste_reading")

        case .resolved(let lat, let lng, let address):
            VStack(alignment: .leading, spacing: 0) {
                MageRideMap(
                    pins: [MapPin(kind: VehicleLayers.pinDropoff, lat: lat, lng: lng)],
                    camera: MapCamera(lat: lat, lng: lng)
                )
                .frame(height: MageRideControl.pinPreview)
                .allowsHitTesting(false)

                VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
                    StatusPill(titleKey: "paste_resolved", tone: .ok)
                    if let address, !address.isEmpty {
                        Text(address)
                            .mageFont(.subtitle)
                            .foregroundStyle(MageRideColor.onSurface)
                    }
                    // The coordinates are shown whether or not the reverse-geocode landed: they are
                    // the answer, and the address is a courtesy.
                    Text(Coordinates.format(lat: lat, lng: lng))
                        .mageFont(.caption)
                        .monospacedDigit()
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }
                .padding(MageRideSpacing.sm)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .background(
                MageRideColor.background,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
            .clipShape(RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))

        case .failed:
            FormErrorText(messageKey: "paste_unreadable")
        }
    }

    private var fieldText: String {
        switch model.state {
        case .empty, .failed: return "paste_placeholder".localised
        case .parsing: return "paste_reading".localised
        case .resolved(let lat, let lng, let address): return address ?? Coordinates.format(lat: lat, lng: lng)
        }
    }
}

/// The **Map** capture method — search for the area, then drop a pin by moving the map under it.
///
/// **This is not a wireframe screen and has no SCR-PI id**, which is itself worth stating: the
/// frames offer *"Map pin"* / *"Map"* as a capture method on SCR-PI-010b and SCR-PI-012, and
/// *"Select on map"* on SCR-PI-008, but draw no screen for any of them. A `.sheet` is therefore the
/// conservative reading — it is what SCR-PI-012a is for its own method, it needs no back-stack
/// entry, and it leaves the form underneath exactly as the passenger left it. Recorded in the C079
/// handoff and restated in C097's.
///
/// **The pin is fixed and the map moves.** MapLibre's draggable-annotation API is not in the
/// distribution this app links, and the centre-pin pattern is the one every ride app uses anyway: it
/// works with one thumb and needs no precise touch on a small marker. ``MageRideMap`` `onCameraIdle`
/// is the half that reports where it settled.
///
/// **The search box is what makes the pin usable across a city** (Δ handset report, ported from the
/// Android twin). Panning was the only way to move this map, so a pickup two towns away meant
/// dragging there at whatever zoom the sheet opened on — and with no fix passed in it opened on
/// `MapCamera.colombo` wherever the booker actually was. Typing puts the map on the junction; the
/// pin still decides the metres. See ``MapPickModel`` for what a search result does and does not
/// commit.
///
/// **`.large` only**, which is a defect fix rather than a preference: at `.medium` the map pushed
/// *"Use this location"* off the bottom of the sheet, and the gesture a passenger would try to
/// reveal it lands on a map that pans instead.
struct MapPickSheet: View {

    @StateObject private var model: MapPickModel

    let titleKey: String
    let around: GeoPoint?
    let onUse: (Place) -> Void
    let onDismiss: () -> Void

    init(
        places: PassengerPlaces,
        titleKey: String,
        around: GeoPoint?,
        onUse: @escaping (Place) -> Void,
        onDismiss: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: MapPickModel(places: places))
        self.titleKey = titleKey
        self.around = around
        self.onUse = onUse
        self.onDismiss = onDismiss
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                Text(key: titleKey)
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)

                LabelledTextField(
                    labelKey: "map_pick_search",
                    value: Binding(
                        get: { model.state.query },
                        set: { model.onQueryChanged($0) }
                    ),
                    placeholder: "search_drop_placeholder".localised
                )

                ZStack(alignment: .top) {
                    MageRideMap(
                        camera: around.map { MapCamera(lat: $0.lat, lng: $0.lng) } ?? .colombo,
                        // A search result moves the map — see `focus`, which is what this sheet
                        // needed and what `camera` cannot be used for while the map moves the pin.
                        focus: model.state.focus,
                        onCameraIdle: { model.onPinMoved($0) }
                    )

                    // The fixed marker, drawn over the map rather than as an annotation — which is
                    // what makes it stay exactly at the centre through every gesture.
                    Image(systemName: "mappin")
                        .font(.system(size: MageRideControl.listRowIcon))
                        .foregroundStyle(MageRideColor.primary)
                        .accessibilityHidden(true)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)

                    // Over the map rather than above it: the sheet keeps one height whether or not a
                    // search is open, so the CTA never moves under the passenger's thumb.
                    predictions
                }
                .frame(maxWidth: .infinity)
                .frame(height: MageRideControl.pinPreview * 2.5)
                .clipShape(RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))

                Text(pinLabel)
                    .mageFont(.bodySmall)
                    .monospacedDigit()
                    .foregroundStyle(
                        model.state.selection == nil
                            ? MageRideColor.onSurfaceVariant
                            : MageRideColor.onSurface
                    )

                Button {
                    guard let selection = model.state.selection else { return }
                    onUse(selection)
                    onDismiss()
                } label: {
                    Text(key: "paste_use")
                }
                .buttonStyle(.mageCta)
                .disabled(model.state.selection == nil)
            }
            .padding(.horizontal, MageRideSpacing.md)
            .padding(.top, MageRideSpacing.md)
            .padding(.bottom, MageRideSpacing.lg)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(MageRideColor.surface)
        .presentationDetents([.large])
        .presentationDragIndicator(.visible)
        .onAppear { model.opened(around: around) }
    }

    /// The search results, as a card over the top of the map.
    ///
    /// Nothing at all when there is nothing to say — an empty field, or a query too short to spend a
    /// request on — so the map is unobstructed for the gesture the sheet is actually for.
    @ViewBuilder
    private var predictions: some View {
        if !model.state.predictions.isEmpty || model.state.searching || model.state.geocoderDown {
            VStack(alignment: .leading, spacing: 0) {
                if model.state.searching {
                    ProgressView()
                        .frame(maxWidth: .infinity)
                        .padding(MageRideSpacing.xs)
                } else if model.state.geocoderDown {
                    // The pin is unaffected by a geocoder that cannot answer, so this says so and
                    // leaves the map alone — the same call AL-14 makes about a reverse geocode.
                    Text(key: "search_geocoder_down")
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .padding(MageRideSpacing.xs)
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 0) {
                            ForEach(model.state.predictions, id: \.displayName) { place in
                                Button {
                                    model.onPredictionChosen(place)
                                } label: {
                                    predictionRow(place)
                                }
                                .buttonStyle(.plain)
                            }
                        }
                    }
                    .frame(maxHeight: MageRideControl.predictionOverlay)
                }
            }
            .background(
                MageRideColor.surface,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
            .padding(MageRideSpacing.xs)
        }
    }

    /// One result: the star or pin its source earns it, and what it is called.
    private func predictionRow(_ place: GeocodedPlace) -> some View {
        let saved = place.source == GeocodedPlaceSource.saved || place.source == GeocodedPlaceSource.recent

        return HStack(spacing: MageRideSpacing.xs) {
            Image(systemName: saved ? "star" : "mappin.and.ellipse")
                .font(.system(size: MageRideControl.chipIcon))
                .foregroundStyle(MageRideColor.onSurfaceVariant)
            Text(place.displayName)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurface)
                .lineLimit(1)
            Spacer(minLength: 0)
        }
        .padding(MageRideSpacing.xs)
        .contentShape(Rectangle())
    }

    /// What the pin is currently called: the searched name, the coordinates, or the prompt.
    private var pinLabel: String {
        if let chosen = model.state.chosen { return chosen.displayName }
        if let centre = model.state.centre { return Coordinates.format(lat: centre.lat, lng: centre.lng) }
        return "map_pick_move".localised
    }
}

/// A coordinate pair, printed.
///
/// **Data, not copy** — five decimals is about a metre, the same precision §2.2's row id uses, and
/// the digits are the same in all three scripts. Three identical values in the three `.strings`
/// files is exactly what `LocalizationTests` fails on, so this is a Swift constant like
/// ``MoneyFormat/prefix`` and ``MageRideSymbols/separator``.
enum Coordinates {

    static func format(lat: Double, lng: Double) -> String {
        String(format: "%.5f, %.5f", lat, lng)
    }
}
