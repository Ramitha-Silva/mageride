import MageRideShared
import SwiftUI

// SCR-PI-009's two lists, split out of `RideBookingScreen.swift` so neither file is a wall.
//
// The screen is the layout — map, summary, pickers, CTA — and this is what goes between: a GTFS
// section that degrades to one muted row (AL-55), and a tier row that can show a price and nothing
// else (AL-19). Both are the fences, drawn.

/// The GTFS half.
///
/// **AL-55's degradation is a muted row, not an error.** D2' §SCR-PA-009: when transit-svc has no
/// feed for the corridor or is unreachable, the section becomes *"Bus route info coming soon for
/// this area"* and everything else on the screen keeps working. Nothing blocks on GTFS coverage.
struct PublicSection: View {

    let state: RideBookingState
    let onSelect: (PublicRouteRow) -> Void

    var body: some View {
        SectionLabel(key: "booking_public_section")

        if state.routesLoading {
            LoadingRow(messageKey: "booking_loading_routes")
        } else if state.publicUnavailable {
            MutedRow(messageKey: "booking_no_gtfs")
        } else if state.routes.isEmpty {
            MutedRow(messageKey: "booking_no_routes")
        } else {
            ForEach(state.routes) { route in
                PublicRouteCard(
                    route: route,
                    isSelected: state.selection == .publicRoute(route),
                    onSelect: { onSelect(route) }
                )
            }
        }

        // "⓵ Walk 250 m to Pamankada halt — blue route on map", shown only when off-route.
        if let hint = state.walkHalt {
            FormattedBanner(
                messageKey: "booking_walk_hint",
                arguments: [hint.metres, hint.haltName],
                symbolName: "figure.walk"
            )
        }
    }
}

/// `🚌 138 · Pettah → Maharagama | via High Level Rd | Direct | PUBLIC`.
struct PublicRouteCard: View {

    let route: PublicRouteRow
    let isSelected: Bool
    let onSelect: () -> Void

    var body: some View {
        TierCard(
            // A GTFS option is a bus or a train, and the feed does not say which; §0.2's bus green
            // is the section's own colour and the badge under it says PUBLIC either way.
            symbolName: VehicleToken.bus.symbolName,
            symbolColor: VehicleToken.bus.color,
            title: title,
            subtitle: subtitle,
            isSelected: isSelected,
            action: onSelect
        ) {
            VStack(alignment: .trailing, spacing: MageRideSpacing.xxs) {
                StatusPill(
                    titleKey: route.isDirect ? "booking_tag_direct" : "booking_tag_transit",
                    tone: route.isDirect ? .ok : .muted
                )
                SolidBadge(text: "booking_tag_public".localised, color: VehicleToken.bus.color)
            }
        }
    }

    private var title: String {
        [route.routeShortName.isEmpty ? nil : route.routeShortName, route.headsign]
            .compactMap { $0 }
            .joined(separator: MageRideSymbols.separator)
    }

    /// The feed's own description, and the transfer count under it when there is one. The
    /// wireframe's *"every ~10 min"* is **not** drawn: no contract carries a headway (C079's gap).
    private var subtitle: String? {
        let parts = [
            route.routeDescription?.isEmpty == false ? route.routeDescription : nil,
            route.transfers > 0 ? "booking_transfers".localisedFormat(route.transfers) : nil,
        ]
        let joined = parts.compactMap { $0 }.joined(separator: MageRideSymbols.separator)
        return joined.isEmpty ? nil : joined
    }
}

/// One Mode C tier — **type, "Standby · upfront fare", and a price. Nothing else.**
///
/// AL-19 / BR-23.3: no minutes-away, no distance to driver, because no driver has been matched. The
/// card could not show one if it wanted to — ``TierQuote`` has no such field.
struct TierRow: View {

    let quote: TierQuote
    let isSelected: Bool
    let onSelect: () -> Void

    var body: some View {
        let token = VehicleToken.forType(quote.vehicleType.toVehicleType()) ?? .privateHire

        TierCard(
            symbolName: token.symbolName,
            symbolColor: token.color,
            title: token.nameKey.localised,
            subtitle: "booking_standby".localised,
            isSelected: isSelected,
            action: onSelect
        ) {
            Text(MoneyFormat.rupees(quote.amountMinor))
                .mageFont(.subtitle)
                .foregroundStyle(MageRideColor.onSurface)
        }
    }
}

/// The wireframe's `Payment: Cash ▾` chip row. C098's SCR-PI-016 is the full picker.
///
/// The three rails that survive AL-57/AL-59, and no surcharge on any of them. COD is absent because
/// it is package-only (AL-22, US-20.8) — SCR-PI-012 has its own row.
struct PaymentChipRow: View {

    let method: PaymentMethod
    var rails: [PaymentMethod] = PaymentRails.ride
    let onChange: (PaymentMethod) -> Void

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: MageRideSpacing.xs) {
                ForEach(rails, id: \.wire) { rail in
                    PlaceChip(
                        label: PaymentRails.labelKey(rail).localised,
                        symbolName: rail == method ? "checkmark" : "creditcard"
                    ) {
                        onChange(rail)
                    }
                    .opacity(rail == method ? 1 : 0.6)
                }
            }
            .padding(.vertical, 1)
        }
    }
}

/// A read in flight — the wireframe's shimmer, as the platform's own spinner.
struct LoadingRow: View {

    let messageKey: String

    var body: some View {
        HStack(spacing: MageRideSpacing.xs) {
            ProgressView()
            Text(key: messageKey)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
            Spacer(minLength: 0)
        }
        .frame(minHeight: MageRideControl.minimumTapTarget)
    }
}

/// A section that has nothing in it, and says which kind of nothing.
struct MutedRow: View {

    let messageKey: String

    var body: some View {
        Text(key: messageKey)
            .mageFont(.bodySmall)
            .foregroundStyle(MageRideColor.onSurfaceVariant)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.vertical, MageRideSpacing.xs)
    }
}

/// The wireframe's `card fill` summary — `● Galle Face` over `◉ Nugegoda ✎`, each end named.
///
/// **Both ends carry a label, and that is a defect fix rather than decoration** (Δ handset report,
/// found on the Android twin and ported here). A pickup with no street address prints
/// *"Current location"* — which ``LastKnownFix`` is right to do, since the fix is a coordinate
/// nobody has geocoded — and it is ALWAYS what the top row shows, because nothing in the app writes
/// an address onto a pickup. Two rows of the same type, one above the other, therefore read as a
/// caption over the place the passenger was travelling to. The `cdot`s were already here; the
/// labels are the rest of the answer.
///
/// **The ✎ moved to the destination row**, because `onEdit` sets ``CaptureTarget/bookingDropoff``
/// — it opens SCR-PI-008 to change where the journey ENDS. The wireframe draws it beside the
/// pickup, which is the cell describing a control the flow behind it does not have.
struct JourneySummaryCard: View {

    let pickup: Place?

    /// Whether ``pickup`` is a place somebody PICKED rather than the passenger's own fix — see
    /// ``BookingDraftState/pickupIsChosen``. It decides whether a pickup with no address reads as
    /// *"Current location"* or as the coordinates it was pinned at.
    let pickupIsChosen: Bool

    let dropoff: Place?
    let onEdit: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            SectionLabel(key: "booking_pickup_label")

            HStack(spacing: MageRideSpacing.xs) {
                Circle()
                    .fill(MageRideColor.success)
                    .frame(width: MageRideControl.routeDot, height: MageRideControl.routeDot)
                Text(pickupText)
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurface)
                    .lineLimit(1)
                Spacer(minLength: 0)
            }

            SectionLabel(key: "booking_destination_label")

            HStack(spacing: MageRideSpacing.xs) {
                Circle()
                    .fill(MageRideColor.error)
                    .frame(width: MageRideControl.routeDot, height: MageRideControl.routeDot)
                Text(dropoff.map(PlaceLabel.of) ?? "booking_no_destination".localised)
                    .mageFont(.bodySmall)
                    .foregroundStyle(dropoff == nil ? MageRideColor.onSurfaceVariant : MageRideColor.onSurface)
                    .lineLimit(1)
                Spacer(minLength: MageRideSpacing.xs)
                Button(action: onEdit) {
                    Image(systemName: "pencil")
                        .font(.system(size: MageRideControl.chipIcon))
                        .foregroundStyle(MageRideColor.primary)
                }
                .accessibilityLabel(Text(key: "booking_edit_route"))
            }
        }
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }

    /// A pickup somebody PICKED is named, or shown as the coordinates it was pinned at — only the
    /// passenger's own fix is *"Current location"*.
    ///
    /// A proxy rider's pin has no address and is not where the booker is standing, so printing the
    /// fix's label over it was a lie the booker could not correct.
    private var pickupText: String {
        guard let pickup else { return "search_current_location".localised }
        if pickupIsChosen { return PlaceLabel.of(pickup) }
        return pickup.address ?? "search_current_location".localised
    }
}

/// The Search / Map / Paste link / Request control, shared by SCR-PI-010b and SCR-PI-012.
///
/// One view rather than three copies, because the *set of methods* is a rule and not a layout: the
/// fence says a package **pickup** offers three and a **drop-off** offers four, and expressing that
/// as `methods:` at two call sites is what makes it checkable. See ``PackageEnd`` for why the two
/// ends differ.
///
/// A segmented `Picker`, which is the cell's own `seg` — `UISegmentedControl` in the wireframe's CSS.
struct LocationMethodPicker: View {

    let methods: [PickupMethod]
    let selection: PickupMethod
    let onSelect: (PickupMethod) -> Void

    var body: some View {
        Picker("", selection: Binding(get: { selection }, set: { onSelect($0) })) {
            ForEach(methods) { method in
                Text(key: method.labelKey).tag(method)
            }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
    }
}

/// What a captured place reads as: its **name**, or the coordinates it was pinned at.
///
/// A place with no address is not a place with no answer — a pin dropped on an unnamed lane is
/// exactly what the Map method is for, and *"Pinned at 6.92710, 79.86120"* is a passenger telling a
/// driver where to come. Every screen that shows a captured place goes through here, so
/// SCR-PI-009's summary and SCR-PI-010b's row cannot drift apart.
enum PlaceLabel {

    static func of(_ place: Place) -> String {
        if let address = place.address, !address.isEmpty { return address }
        return "capture_pinned".localisedFormat(place.lat, place.lng)
    }
}

/// What a captured place reads as under its control, or the prompt when there is none yet.
struct CapturedPlaceRow: View {

    let place: Place?
    let emptyKey: String

    var body: some View {
        Text(text)
            .mageFont(.bodySmall)
            .foregroundStyle(place == nil ? MageRideColor.onSurfaceVariant : MageRideColor.onSurface)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var text: String {
        guard let place else { return emptyKey.localised }
        return PlaceLabel.of(place)
    }
}
