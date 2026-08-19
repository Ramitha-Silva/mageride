import MageRideShared
import SwiftUI

/// SCR-PI-010b — booking for somebody else (US-8.16–8.19, P-01…P-03).
///
/// The cell: `‹ Back · For someone else`, a grouped list with the rider's name and mobile, the four
/// **pickup methods** as a segmented control, the paste-link note, and — when Request is chosen —
/// *"Waiting for rider… 5:00"* over the CTA.
///
/// **The unregistered case is a first-class state, not an error** (P-03, US-8.19). A number that
/// belongs to nobody cannot be sent an FCM, so the screen says *"Not a MageRide user — enter pickup
/// manually"* and the Request method removes itself. The booking still works; it just captures the
/// pickup a different way.
///
/// **Δ the wireframe's `👥 Pick from Contacts` row is not drawn**, and that is the same call
/// `apps/passenger-android` made: `CNContactPickerViewController` would work here (the driver app
/// uses one on SCR-DI-029), but the Android twin has no picker either and adding one on this side
/// alone is a parity break rather than a port. Recorded in the C097 handoff.
///
/// - Parameter onSearch: The Search method — SCR-PI-008, with the result coming back through the
///   draft. The **Map** method is a sheet on this screen: no SCR-PI id names a map picker, so there
///   is no destination to navigate to. See ``MapPickSheet``.
@MainActor
struct ProxyRiderScreen: View {

    @StateObject private var model: ProxyRiderModel

    private let bookings: BookingRepository

    /// The geocoder behind the Map method's search box, and the fix it opens over. Held rather than
    /// reached for: ``MapPickSheet`` builds its own model, and a picker that opens on
    /// `MapCamera.colombo` opens on Colombo Fort for a booker standing anywhere else — with its CTA
    /// disabled until the first camera settle produces a centre.
    private let places: PassengerPlaces
    private let lastFix: LastKnownFix

    let onBack: () -> Void
    let onSearch: () -> Void
    let onDone: () -> Void

    @State private var isPasteOpen = false
    @State private var isMapOpen = false

    init(
        draft: BookingDraft,
        bookings: BookingRepository,
        live: PassengerLiveMap,
        places: PassengerPlaces,
        lastFix: LastKnownFix,
        onBack: @escaping () -> Void,
        onSearch: @escaping () -> Void,
        onDone: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: ProxyRiderModel(draft: draft, bookings: bookings, live: live))
        self.bookings = bookings
        self.places = places
        self.lastFix = lastFix
        self.onBack = onBack
        self.onSearch = onSearch
        self.onDone = onDone
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                LabelledTextField(
                    labelKey: "proxy_rider_name",
                    value: Binding(get: { model.state.riderName }, set: { model.onNameChanged($0) }),
                    placeholder: "proxy_rider_name_hint".localised,
                    textContentType: .name
                )

                SectionLabel(key: "proxy_rider_phone")
                PhoneNumberField(
                    value: Binding(get: { model.state.riderPhone }, set: { model.onPhoneChanged($0) })
                )

                // P-03 / US-8.19. Not an error colour: the booking is fine, one method is not.
                if model.state.riderRegistered == false {
                    Text(key: "proxy_not_registered")
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }

                SectionLabel(key: "proxy_pickup_method")
                LocationMethodPicker(
                    // The Request method disappears entirely for an unregistered rider rather than
                    // being drawn disabled — a control that cannot work is not a control.
                    methods: model.state.riderRegistered == false
                        ? PickupMethod.packagePickupMethods
                        : PickupMethod.allMethods,
                    selection: model.state.method,
                    onSelect: choose
                )

                Text(key: "capture_paste_note")
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)

                CapturedPlaceRow(place: model.state.pickup, emptyKey: "proxy_no_pickup")

                if model.state.method == .request {
                    requestRow
                }

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                Button(action: onDone) {
                    Text(key: "proxy_continue")
                }
                .buttonStyle(.mageCta)
                .disabled(!model.state.isComplete)
                .padding(.top, MageRideSpacing.xs)
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "proxy_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task { model.start() }
        .onAppear { model.setPickupFromDraft() }
        .sheet(isPresented: $isPasteOpen) {
            PasteLinkSheet(
                bookings: bookings,
                target: .pickup,
                onUse: { model.setPickup($0) },
                onPickOnMap: { isMapOpen = true },
                onDismiss: { isPasteOpen = false }
            )
        }
        .sheet(isPresented: $isMapOpen) {
            MapPickSheet(
                places: places,
                titleKey: "proxy_pickup_method",
                around: model.state.pickup?.point ?? lastFix.point,
                onUse: { model.setPickup($0) },
                onDismiss: { isMapOpen = false }
            )
        }
    }

    /// The P-02 round trip, as the wireframe draws it.
    ///
    /// Pending is a countdown rather than an indefinite spinner because the window is real and
    /// finite: five minutes, enforced by a durable timer server-side. A booker watching `0:41` knows
    /// whether to wait or to switch methods; a booker watching a spinner does not.
    @ViewBuilder
    private var requestRow: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
            switch model.state.requestState {
            case LocationRequestState.pending:
                HStack(spacing: MageRideSpacing.xs) {
                    ProgressView()
                    Text("proxy_waiting".localisedFormat(model.state.countdown))
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .monospacedDigit()
                }
                .padding(MageRideSpacing.sm)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(
                    MageRideColor.surfaceVariant,
                    in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                )

            case LocationRequestState.confirmed:
                Text(key: "proxy_confirmed")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.success)

            // Declined and Expired read the same to a booker — the rider did not share — and both
            // land on the same instruction. Naming which one it was would tell the booker their
            // rider refused, which is not theirs to know (P-02).
            case LocationRequestState.declined, LocationRequestState.expired:
                Text(key: "proxy_no_answer")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)

            default:
                EmptyView()
            }

            if model.state.requestState != LocationRequestState.confirmed {
                Button { model.requestRiderLocation() } label: {
                    Text(key: "proxy_request_location")
                }
                .buttonStyle(.mageCta)
                .disabled(!model.state.canRequestLocation)
            }
        }
    }

    private func choose(_ method: PickupMethod) {
        model.setMethod(method)
        switch method {
        case .search: onSearch()
        case .map: isMapOpen = true
        case .pasteLink: isPasteOpen = true
        case .request: break
        }
    }
}
