import MageRideShared
import SwiftUI

/// SCR-PI-009 — the multimodal list, and the booking.
///
/// The cell: a short map carrying the selected route's polyline, the blue walk-to-halt leg and the
/// two pins, over a scrolling sheet with the journey summary, the two segmented pickers, the GTFS
/// section, the Mode C tiers and one CTA.
///
/// **Two fences are visible in the layout.** A public row shows a route number, a description and a
/// Direct/Transit tag with a `PUBLIC` badge — never a price, because a bus is not booked here; a
/// private tier shows a price and nothing else, because no driver has been matched (AL-19). Choosing
/// a public route also **removes the payment chip entirely** rather than disabling it: there is no
/// fare to choose a rail for.
///
/// **Δ the cell draws no navigation bar**, so this screen hides the system's and puts the back
/// control on the map, which is also what `apps/passenger-android` does. A pushed SwiftUI screen
/// keeps its edge-swipe either way; what the drawn button adds is a target the wireframe's own frame
/// has room for and a `.toolbar` would take 44pt of map to provide.
@MainActor
struct RideBookingScreen: View {

    @StateObject private var model: RideBookingModel

    let onBack: () -> Void
    let onEditRoute: () -> Void
    let onProxyDetails: () -> Void
    let onPackageBooking: () -> Void
    let onSchedule: () -> Void
    let onBooked: (String) -> Void
    let onTrackRoute: () -> Void

    init(
        draft: BookingDraft,
        bookings: BookingRepository,
        keys: IdempotencyKeys,
        onBack: @escaping () -> Void,
        onEditRoute: @escaping () -> Void,
        onProxyDetails: @escaping () -> Void,
        onPackageBooking: @escaping () -> Void,
        onSchedule: @escaping () -> Void,
        onBooked: @escaping (String) -> Void,
        onTrackRoute: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: RideBookingModel(draft: draft, bookings: bookings, keys: keys))
        self.onBack = onBack
        self.onEditRoute = onEditRoute
        self.onProxyDetails = onProxyDetails
        self.onPackageBooking = onPackageBooking
        self.onSchedule = onSchedule
        self.onBooked = onBooked
        self.onTrackRoute = onTrackRoute
    }

    var body: some View {
        VStack(spacing: 0) {
            map
            sheet
        }
        .background(MageRideColor.surface)
        .toolbar(.hidden, for: .navigationBar)
        .task { model.start() }
        .onChange(of: model.state.booked) { rideId in
            guard let rideId else { return }
            onBooked(rideId)
            model.onBookingConsumed()
        }
    }

    private var map: some View {
        MageRideMap(
            routePolyline: model.state.routePolyline,
            walkPolyline: model.state.walkPolyline,
            pins: model.state.pins,
            camera: model.state.draft.pickup.map { MapCamera(lat: $0.lat, lng: $0.lng) } ?? .colombo
        )
        .frame(height: MageRideControl.bookingMapHeight)
        .overlay(alignment: .topLeading) {
            MapOverlayButton(symbolName: "chevron.left", accessibilityKey: "action_back", action: onBack)
                .padding(MageRideSpacing.sm)
        }
    }

    private var sheet: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                SheetGrabber()
                    .frame(maxWidth: .infinity)

                JourneySummaryCard(
                    pickup: model.state.draft.pickup,
                    pickupIsChosen: model.state.draft.pickupIsChosen,
                    dropoff: model.state.draft.dropoff,
                    onEdit: onEditRoute
                )

                pickers

                PublicSection(state: model.state, onSelect: { model.selectRoute($0) })

                SectionLabel(key: "booking_private_section")
                privateSection

                // *"Payment chip … private tiers only"*. A bus is not paid for in this app, so the
                // chip is not merely disabled on a public selection — it is absent.
                if !model.state.isPublicSelected {
                    PaymentChipRow(
                        method: model.state.draft.paymentMethod,
                        onChange: { model.setPaymentMethod($0) }
                    )
                }

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                cta
            }
            .padding(MageRideSpacing.md)
        }
        .background(
            MageRideColor.background,
            in: TopRoundedRectangle(radius: MageRideRadius.lg)
        )
    }

    /// The cell's two `seg` controls — *"For Me / Someone"* and *"Person / Package"*, side by side.
    ///
    /// A segmented `Picker` each, which is the cell's own `Δ iOS` clause. Choosing the second option
    /// of either **navigates**: a proxy rider and a parcel each have a screen of their own to fill
    /// in, and the picker is what opens it.
    private var pickers: some View {
        HStack(spacing: MageRideSpacing.xs) {
            Picker("", selection: bookingForBinding) {
                ForEach(BookingFor.allCases, id: \.self) { value in
                    Text(key: value.labelKey).tag(value)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .accessibilityLabel(Text(key: "booking_for_label"))

            Picker("", selection: subjectBinding) {
                ForEach(BookingSubject.allCases, id: \.self) { value in
                    Text(key: value.labelKey).tag(value)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .accessibilityLabel(Text(key: "booking_subject_label"))
        }
    }

    @ViewBuilder
    private var privateSection: some View {
        if model.state.tiersLoading {
            LoadingRow(messageKey: "booking_estimating")
        } else if model.state.tiers.isEmpty {
            MutedRow(messageKey: "booking_no_tiers")
        } else {
            ForEach(model.state.tiers) { quote in
                TierRow(
                    quote: quote,
                    isSelected: model.state.selection == .privateTier(quote),
                    onSelect: { model.selectTier(quote) }
                )
            }
        }
    }

    /// The one CTA, in whichever of its two forms applies.
    ///
    /// *"Public → Track Route · private → Book Now / Schedule"* is D2' §SCR-PA-009's own sketch.
    /// There is no Schedule under a public route because there is nothing to schedule: a bus runs to
    /// its own timetable and this app does not book a seat on it.
    @ViewBuilder
    private var cta: some View {
        if model.state.isPublicSelected {
            Button(action: onTrackRoute) {
                Text(key: "booking_track_route")
            }
            .buttonStyle(.mageCta)
        } else {
            Button { model.book() } label: {
                Text(key: "booking_book_now")
            }
            .buttonStyle(.mageCta(loading: model.state.booking))
            .disabled(!model.state.canBook)

            OutlinedAction(titleKey: "booking_schedule", action: onSchedule)
        }
    }

    private var bookingForBinding: Binding<BookingFor> {
        Binding(
            get: { model.state.draft.bookingFor },
            set: { value in
                model.setBookingFor(value)
                if value == .someoneElse { onProxyDetails() }
            }
        )
    }

    private var subjectBinding: Binding<BookingSubject> {
        Binding(
            get: { model.state.draft.subject },
            set: { value in
                model.setSubject(value)
                if value == .parcel { onPackageBooking() }
            }
        )
    }
}
