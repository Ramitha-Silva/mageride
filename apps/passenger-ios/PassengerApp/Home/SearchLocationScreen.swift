import MageRideShared
import SwiftUI

/// SCR-PI-008 — the destination field.
///
/// The cell: a `‹ Back · Set route` nav row, the green *"Current location"* pickup field over the
/// red drop field, a **Predictions** label, a grouped list of rows, and `📌 Select on map` /
/// `＋ Add address` at the foot. Its `Δ iOS` clause is *"`List` + debounced Nominatim"*.
///
/// **GEO ONLY (AL-17).** Typing `138` searches for a *place* called 138 and returns places; no route
/// row ever appears in this list, and selecting a prediction always yields a coordinate. See
/// ``SearchLocationModel`` for the D2' conflict and the C096 handoff for what it costs US-7.9.
///
/// **`‹ Back` is the system's**, unlike SCR-PI-004's inert one: this screen *is* pushed, so the
/// navigation bar carries a real back button and the cell's own `‹ Back` is it.
@MainActor
struct SearchLocationScreen: View {

    @StateObject private var model: SearchLocationModel

    /// Selecting a prediction. C097's SCR-PI-009 is where this leads.
    let onPlaceChosen: (GeocodedPlace) -> Void

    /// The wireframe's *"Select on map"* — also the way out when the geocoder is down.
    let onPickOnMap: () -> Void

    /// *"＋ Add address"* — C101's SCR-PI-026.
    let onAddAddress: () -> Void

    init(
        places: PassengerPlaces,
        recents: RecentPlaces,
        around: GeoPoint?,
        onPlaceChosen: @escaping (GeocodedPlace) -> Void,
        onPickOnMap: @escaping () -> Void,
        onAddAddress: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: SearchLocationModel(places: places, recents: recents, around: around)
        )
        self.onPlaceChosen = onPlaceChosen
        self.onPickOnMap = onPickOnMap
        self.onAddAddress = onAddAddress
    }

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            // The pickup row. Fixed to the current location on this screen: SCR-PI-009 is where a
            // pickup becomes editable, and two editable fields here would be two ways to do the same
            // thing one screen apart.
            pickupRow
            dropField

            header

            List {
                ForEach(model.state.predictions, id: \.rowId) { place in
                    Button {
                        // §2.2's `place_recents`, which is what fills SCR-PI-010's "Recent" list.
                        // Local only — nothing about this is sent anywhere.
                        model.choose(place)
                        onPlaceChosen(place)
                    } label: {
                        PredictionRow(place: place)
                    }
                    .buttonStyle(.plain)
                    .listRowInsets(EdgeInsets())
                    .listRowBackground(MageRideColor.background)
                    .listRowSeparatorTint(MageRideColor.surfaceVariant)
                }
            }
            .listStyle(.plain)
            .scrollContentBackground(.hidden)

            HStack(spacing: MageRideSpacing.xs) {
                OutlinedAction(titleKey: "search_select_on_map", symbolName: "mappin.and.ellipse", action: onPickOnMap)
                OutlinedAction(titleKey: "search_add_address", symbolName: "plus", action: onAddAddress)
            }
            .padding(.bottom, MageRideSpacing.md)
        }
        .padding(.horizontal, MageRideSpacing.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "search_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task { await model.load() }
    }

    /// The wireframe's green `cdot` row — where the passenger is now.
    private var pickupRow: some View {
        HStack(spacing: MageRideSpacing.xs) {
            Circle()
                .fill(MageRideColor.success)
                .frame(width: MageRideControl.routeDot, height: MageRideControl.routeDot)
            Text(key: "search_current_location")
                .mageFont(.body)
                .foregroundStyle(MageRideColor.onSurface)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, MageRideSpacing.sm)
        .frame(minHeight: MageRideControl.minimumTapTarget)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .accessibilityElement(children: .combine)
    }

    /// The red `cdot` field. The hint says what this field takes, which is the fence made visible: a
    /// place or an address, never a route number (AL-17).
    private var dropField: some View {
        HStack(spacing: MageRideSpacing.xs) {
            Circle()
                .fill(MageRideColor.error)
                .frame(width: MageRideControl.routeDot, height: MageRideControl.routeDot)

            TextField(
                "search_drop_placeholder".localised,
                text: Binding(get: { model.state.query }, set: { model.onQueryChanged($0) })
            )
            .mageFont(.body)
            .foregroundStyle(MageRideColor.onSurface)
            .submitLabel(.search)
            .autocorrectionDisabled()
            .textInputAutocapitalization(.words)
            .accessibilityLabel(Text(key: "search_drop_label"))

            if model.state.isSearching {
                ProgressView()
            }
        }
        .padding(.horizontal, MageRideSpacing.sm)
        .frame(minHeight: MageRideControl.minimumTapTarget)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .overlay {
            if model.state.geocoderDown {
                RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                    .strokeBorder(MageRideColor.error, lineWidth: 1)
            }
        }
    }

    /// *Predictions* over a typed query, *Saved places* over the empty state, and the geocoder's own
    /// failure in place of either.
    @ViewBuilder
    private var header: some View {
        if model.state.geocoderDown {
            // No retry control, and that is the cell's own answer: *"geocoder down → 'Pick on
            // map'"*. A passenger with a map and a pin does not need a button that asks the same
            // question again — and typing another character re-arms the lookup anyway.
            FormErrorText(messageKey: "search_geocoder_down")
        } else {
            SectionLabel(key: model.state.showingDefaults ? "search_saved" : "search_predictions")
        }
    }
}

/// One prediction.
///
/// The leading glyph is the row's **source** — ★ for one of the passenger's own saved or recent
/// places, 📍 for a geocoded one. That is `GeocodedPlace.source`, which is why the search is one call
/// rather than three lists merged on the device.
private struct PredictionRow: View {

    let place: GeocodedPlace

    var body: some View {
        HStack(spacing: MageRideSpacing.sm) {
            Image(systemName: isOwn ? "star.fill" : "mappin.and.ellipse")
                .font(.system(size: MageRideControl.rowIcon * 0.8))
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .frame(width: MageRideControl.rowIcon)

            VStack(alignment: .leading, spacing: 1) {
                Text(place.displayName)
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)
                if let detail {
                    Text(detail)
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .lineLimit(1)
                }
            }

            Spacer(minLength: 0)
        }
        .padding(.vertical, MageRideSpacing.xs)
        .frame(minHeight: MageRideControl.minimumTapTarget)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }

    private var isOwn: Bool {
        place.source == GeocodedPlaceSource.saved || place.source == GeocodedPlaceSource.recent
    }

    private var detail: String? {
        let parts = [place.line1, place.city].compactMap { $0 }.filter { !$0.isEmpty }
        return parts.isEmpty ? nil : parts.joined(separator: MageRideSymbols.separator)
    }
}
