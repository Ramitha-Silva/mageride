import MageRideShared
import SwiftUI

/// SCR-PI-012 — a parcel instead of a person (US-20.1/20.2, P-06).
///
/// The cell: `‹ Back · Send a package`, a segmented **S / M / L** with the size hint under it, a
/// grouped list of description and recipient, a **Pickup location** picker with three methods and a
/// **Drop-off location** picker with four, the paste/request note, the COD payment row, and *"Get
/// estimate & Book"*.
///
/// **The two ends are not symmetric, and that is a fence rather than an omission.** The sender is
/// standing at the pickup, so there is nobody there to ask; the recipient is the only person who
/// knows where the parcel is going. ``PackageEnd`` carries which methods each end offers and
/// ``PackageBookingModel/setMethod(_:_:)`` refuses the fourth on a pickup even if a layout ever
/// offered it.
///
/// **The pickup OTP is shown once** (P-07) — see ``PackageBookingModel``.
@MainActor
struct PackageBookingScreen: View {

    @StateObject private var model: PackageBookingModel

    private let bookings: BookingRepository

    /// The Map method's geocoder and opening camera — see ``ProxyRiderScreen`` for both.
    private let places: PassengerPlaces
    private let lastFix: LastKnownFix

    let onBack: () -> Void
    let onSearch: (PackageEnd) -> Void
    let onBooked: (String) -> Void

    @State private var pasteEnd: PackageEnd?
    @State private var mapEnd: PackageEnd?

    init(
        draft: BookingDraft,
        bookings: BookingRepository,
        keys: IdempotencyKeys,
        otps: PackageOtps,
        places: PassengerPlaces,
        lastFix: LastKnownFix,
        onBack: @escaping () -> Void,
        onSearch: @escaping (PackageEnd) -> Void,
        onBooked: @escaping (String) -> Void
    ) {
        _model = StateObject(
            wrappedValue: PackageBookingModel(draft: draft, bookings: bookings, keys: keys, otps: otps)
        )
        self.bookings = bookings
        self.places = places
        self.lastFix = lastFix
        self.onBack = onBack
        self.onSearch = onSearch
        self.onBooked = onBooked
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                SectionLabel(key: "package_size")
                Picker("", selection: sizeBinding) {
                    ForEach(Self.sizes, id: \.self) { size in
                        Text(Self.sizeLabel(size)).tag(size)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .accessibilityLabel(Text(key: "package_size"))

                InfoBanner(messageKey: model.state.sizeHintKey, tone: .muted, symbolName: "info.circle")

                details

                SectionLabel(key: "package_pickup")
                LocationMethodPicker(
                    methods: PackageEnd.pickup.methods,
                    selection: model.state.pickupMethod,
                    onSelect: { choose(.pickup, $0) }
                )
                CapturedPlaceRow(place: model.state.pickup, emptyKey: "package_not_set")

                SectionLabel(key: "package_dropoff")
                LocationMethodPicker(
                    methods: PackageEnd.dropoff.methods,
                    selection: model.state.dropoffMethod,
                    onSelect: { choose(.dropoff, $0) }
                )
                CapturedPlaceRow(place: model.state.dropoff, emptyKey: "package_not_set")

                Text(key: "package_capture_note")
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)

                // COD is booking-time and package-only (AL-22, US-20.8), which is why this list is
                // `PaymentRails.parcel` and not SCR-PI-009's three.
                PaymentChipRow(
                    method: model.state.paymentMethod,
                    rails: PaymentRails.parcel,
                    onChange: { model.setPaymentMethod($0) }
                )

                estimateRow

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                Button(action: submit) {
                    Text(key: model.state.estimateMinor == nil ? "package_get_estimate" : "package_book")
                }
                .buttonStyle(.mageCta(loading: model.state.isEstimating || model.state.isBooking))
                .disabled(!model.state.canEstimate)
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "package_title"))
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { model.refreshPlaces() }
        .onChange(of: model.state.booked) { rideId in
            guard let rideId else { return }
            onBooked(rideId)
            model.onBookingConsumed()
        }
        .sheet(item: $pasteEnd) { end in
            PasteLinkSheet(
                bookings: bookings,
                target: end == .pickup ? .pickup : .dropoff,
                onUse: { model.setPlace(end, $0) },
                onPickOnMap: { mapEnd = end },
                onDismiss: { pasteEnd = nil }
            )
        }
        .sheet(item: $mapEnd) { end in
            MapPickSheet(
                places: places,
                titleKey: end == .pickup ? "package_pickup" : "package_dropoff",
                around: (end == .pickup ? model.state.pickup : model.state.dropoff)?.point ?? lastFix.point,
                onUse: { model.setPlace(end, $0) },
                onDismiss: { mapEnd = nil }
            )
        }
    }

    private var details: some View {
        GroupedList {
            GroupedRow(titleKey: "package_description") {
                TextField(
                    "package_description_hint".localised,
                    text: Binding(get: { model.state.packageDescription }, set: { model.onDescriptionChanged($0) })
                )
                .mageFont(.body)
                .foregroundStyle(MageRideColor.onSurface)
                .multilineTextAlignment(.trailing)
            }
            GroupedRow(titleKey: "package_recipient_name") {
                TextField(
                    "package_recipient_name_hint".localised,
                    text: Binding(get: { model.state.recipientName }, set: { model.onRecipientNameChanged($0) })
                )
                .mageFont(.body)
                .foregroundStyle(MageRideColor.onSurface)
                .multilineTextAlignment(.trailing)
                .textContentType(.name)
            }
            GroupedRow(titleKey: "package_recipient_phone", showsSeparator: false) {
                TextField(
                    PhoneNumber.placeholder,
                    text: Binding(get: { model.state.recipientPhone }, set: { model.onRecipientPhoneChanged($0) })
                )
                .mageFont(.body)
                .foregroundStyle(MageRideColor.onSurface)
                .multilineTextAlignment(.trailing)
                .keyboardType(.phonePad)
                .textContentType(.telephoneNumber)
            }
        }
    }

    /// The quote, once there is one, and the *"same Mode C fare"* line the cell puts beside it
    /// (US-20.9).
    @ViewBuilder
    private var estimateRow: some View {
        if let amount = model.state.estimateMinor {
            HStack(spacing: MageRideSpacing.xs) {
                Text(MoneyFormat.rupees(amount))
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
                Text(key: "package_same_fare")
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                Spacer(minLength: 0)
            }
        }
    }

    /// One CTA for two steps: a price has to exist before a booking can be made, because the token
    /// is what binds it. The label says which step it is on.
    private func submit() {
        if model.state.estimateMinor == nil {
            model.estimate()
        } else {
            model.book()
        }
    }

    private func choose(_ end: PackageEnd, _ method: PickupMethod) {
        model.setMethod(end, method)
        switch method {
        case .search: onSearch(end)
        case .map: mapEnd = end
        case .pasteLink: pasteEnd = end
        // The recipient round trip is the one method with no wired answer yet — see the C097
        // handoff, which carries C079's fifth gap forward unchanged.
        case .request: break
        }
    }

    private var sizeBinding: Binding<PackageSize> {
        Binding(get: { model.state.size }, set: { model.setSize($0) })
    }

    /// P-06's three, in the wireframe's order. Written out rather than read off `entries`, which is
    /// a Kotlin static this bridge does not promise a spelling for.
    private static let sizes: [PackageSize] = [PackageSize.s, PackageSize.m, PackageSize.l]

    /// `S` / `M` / `L` — a letter, and the same one in all three scripts, so it is a value rather
    /// than three identical `.strings` entries.
    ///
    /// Switched rather than read off `KotlinEnum.name`: the exported spelling of a Kotlin enum's
    /// `name` is the compiler's business, and three characters are not worth depending on it for.
    private static func sizeLabel(_ size: PackageSize) -> String {
        switch size {
        case PackageSize.m: return "M"
        case PackageSize.l: return "L"
        default: return "S"
        }
    }
}

extension PackageEnd: Identifiable {
    /// `sheet(item:)` needs one, and there are exactly two ends.
    var id: String { self == .pickup ? "pickup" : "dropoff" }
}
