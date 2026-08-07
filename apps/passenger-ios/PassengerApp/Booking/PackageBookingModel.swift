import Foundation
import MageRideShared

/// SCR-PI-012's state.
struct PackageBookingState {

    /// P-06's S/M/L. Drives ``sizeHintKey`` and, on SCR-PI-009, which tiers are quoted.
    var size: PackageSize = PackageSize.s

    var packageDescription = ""
    var recipientName = ""
    var recipientPhone = ""

    var pickup: Place?
    var dropoff: Place?
    var pickupMethod: PickupMethod = .search
    var dropoffMethod: PickupMethod = .search

    var paymentMethod: PaymentMethod = PaymentMethod.cod

    var isEstimating = false

    /// The quote, once *"Get estimate & Book"* has fetched one.
    var estimateMinor: Int64?

    /// The `fareEstimateToken` that price is bound to. Booking without it is
    /// `400 invalid-fare-token`, which is exactly what stops a client naming its own delivery fee.
    var quoteToken: String?

    var isBooking = false
    var booked: String?

    /// **Shown once, never returned again** (P-07). The server keeps only its hash.
    var pickupOtp: String?

    var errorKey: String?

    /// P-06's per-size hint, which *"updates per pick"* — the wireframe's ⓘ line.
    var sizeHintKey: String {
        switch size {
        case PackageSize.m: return "package_hint_m"
        case PackageSize.l: return "package_hint_l"
        default: return "package_hint_s"
        }
    }

    /// Whether there is enough to ask fare-svc for a price.
    var canEstimate: Bool {
        !isEstimating && !isBooking &&
            !packageDescription.trimmed.isEmpty &&
            !recipientName.trimmed.isEmpty &&
            PhoneNumber.isValid(recipientPhone) &&
            pickup != nil &&
            dropoff != nil
    }

    /// Whether Book can fire — an estimate has to exist first, because a token binds the price.
    var canBook: Bool { canEstimate && estimateMinor != nil }
}

/// SCR-PI-012 — a parcel instead of a person.
///
/// **Same fare as a Mode C ride** (US-20.9), so this books through exactly the same
/// `POST /v1/rides/request` with `kind = package` and a `FareEstimateKind.package` quote. What is
/// genuinely different is the *cargo*: a size, a description, a recipient who is not the booker, and
/// two ends that are both somebody else's.
///
/// **The pickup OTP is shown once and never again** (P-07). `RequestRideResponse.pickupOtp` is
/// present only on a package booking and only in that one response — the server stores its hash — so
/// the state keeps it, ``PackageOtps`` keeps it for C099, and the screen must surface it before the
/// passenger leaves.
///
/// **COD is a booking-time method and a package-only one** (AL-22, US-20.8). `RidePaymentMethod` has
/// it; a passenger ride offering it would be `400 payment-method-invalid`, which is why the payment
/// row on this screen is ``PaymentRails/parcel`` and not SCR-PI-009's list.
@MainActor
final class PackageBookingModel: ObservableObject {

    @Published private(set) var state = PackageBookingState()

    private let draft: BookingDraft
    private let bookings: BookingRepository
    private let keys: IdempotencyKeys
    private let otps: PackageOtps

    private var work: [Task<Void, Never>] = []

    init(draft: BookingDraft, bookings: BookingRepository, keys: IdempotencyKeys, otps: PackageOtps) {
        self.draft = draft
        self.bookings = bookings
        self.keys = keys
        self.otps = otps

        let current = draft.state
        state.size = current.packageSize
        state.packageDescription = current.packageDescription
        state.recipientName = current.recipientName
        state.recipientPhone = current.recipientPhone
        state.pickup = current.packagePickup ?? current.pickup
        state.dropoff = current.packageDropoff ?? current.dropoff
    }

    deinit {
        work.forEach { $0.cancel() }
    }

    /// Re-reads the two ends from the draft. Called when the place picker has answered.
    func refreshPlaces() {
        let current = draft.state
        if let pickup = current.packagePickup { state.pickup = pickup }
        if let dropoff = current.packageDropoff { state.dropoff = dropoff }
    }

    /// P-06's selector. Changing it invalidates the quote — a bigger parcel is a bigger vehicle.
    func setSize(_ size: PackageSize) {
        state.size = size
        state.estimateMinor = nil
        state.quoteToken = nil
        draft.update {
            $0.packageSize = size
            $0.subject = .parcel
        }
    }

    func onDescriptionChanged(_ value: String) {
        state.packageDescription = value
        draft.update { $0.packageDescription = value }
    }

    func onRecipientNameChanged(_ value: String) {
        state.recipientName = value
        draft.update { $0.recipientName = value }
    }

    func onRecipientPhoneChanged(_ value: String) {
        let normalised = PhoneNumber.normalise(value)
        state.recipientPhone = normalised
        draft.update { $0.recipientPhone = normalised }
    }

    /// The Search / Map / Paste link (/ Request, drop-off only) control for one end.
    func setMethod(_ end: PackageEnd, _ method: PickupMethod) {
        // The fence, enforced rather than only drawn: there is nobody at the pickup to ask.
        guard !(end == .pickup && method == .request) else { return }
        switch end {
        case .pickup: state.pickupMethod = method
        case .dropoff: state.dropoffMethod = method
        }
    }

    /// Whatever the chosen method produced for one end. Moving either end invalidates the quote.
    func setPlace(_ end: PackageEnd, _ place: Place) {
        state.estimateMinor = nil
        state.quoteToken = nil
        switch end {
        case .pickup:
            state.pickup = place
            draft.update { $0.packagePickup = place }
        case .dropoff:
            state.dropoff = place
            draft.update { $0.packageDropoff = place }
        }
    }

    /// Cash / Wallet / Driver QR / **COD** — the last of which exists only here (US-20.8).
    func setPaymentMethod(_ method: PaymentMethod) {
        state.paymentMethod = method
        draft.update { $0.paymentMethod = method }
    }

    func clearError() {
        state.errorKey = nil
    }

    /// *"Get estimate & Book"* — the estimate half.
    ///
    /// A parcel is quoted on the **smallest vehicle its size fits**, which is the cheapest honest
    /// answer: P-06's hint has already told the sender that an L needs a van, so quoting them a van
    /// is what they were led to expect.
    func estimate() {
        guard state.canEstimate, let pickup = state.pickup, let dropoff = state.dropoff else { return }

        state.isEstimating = true
        state.errorKey = nil
        let size = state.size

        work.append(Task {
            do {
                let quote = try await bookings.estimate(
                    from: pickup.point,
                    to: dropoff.point,
                    vehicleType: Self.vehicle(for: size),
                    kind: FareEstimateKind.package
                )
                state.isEstimating = false
                state.estimateMinor = quote.amountMinor
                state.quoteToken = quote.fareEstimateToken
            } catch is CancellationError {
                state.isEstimating = false
            } catch {
                state.isEstimating = false
                state.errorKey = BookingErrors.messageKey(for: error)
            }
        })
    }

    /// The booking — `POST /v1/rides/request` with `kind = package`.
    ///
    /// The response's `pickupOtp` is kept because it is **shown once and never returned again**
    /// (P-07): the server holds only its hash, so a screen that navigated away without surfacing it
    /// would have destroyed the only copy the sender will ever get.
    func book() {
        guard state.canBook,
              let token = state.quoteToken,
              let pickup = state.pickup,
              let dropoff = state.dropoff
        else {
            return
        }

        state.isBooking = true
        state.errorKey = nil
        let current = state

        work.append(Task {
            do {
                let response = try await bookings.requestRide(
                    IosBookingRequestsKt.packageRideRequestFor(
                        clientRequestId: keys.next(),
                        pickup: pickup,
                        dropoff: dropoff,
                        vehicleType: Self.vehicle(for: current.size),
                        fareEstimateToken: token,
                        paymentMethod: PaymentRails.bookingValueOf(current.paymentMethod),
                        packageSize: current.size,
                        packageDescription: current.packageDescription,
                        recipientName: current.recipientName,
                        recipientPhone: PhoneNumber.toE164(current.recipientPhone)
                    )
                )
                otps.rememberPickup(rideId: response.rideId, otp: response.pickupOtp)
                draft.clear()
                state.isBooking = false
                state.booked = response.rideId
                state.pickupOtp = response.pickupOtp
            } catch is CancellationError {
                state.isBooking = false
            } catch {
                state.isBooking = false
                state.errorKey = BookingErrors.messageKey(for: error)
            }
        })
    }

    /// The screen has shown the OTP and navigated on.
    func onBookingConsumed() {
        state.booked = nil
        state.pickupOtp = nil
    }

    /// The smallest vehicle P-06 says this size fits.
    ///
    /// S is a motorbike box, M a three-wheeler, L a van — the same three the size hint names, so the
    /// price a sender is quoted matches the vehicle they were told it needs. `truck`/`mini_truck`
    /// exist for freight beyond this screen (AL-09).
    nonisolated static func vehicle(for size: PackageSize) -> RideVehicleType {
        switch size {
        case PackageSize.m: return RideVehicleType.threeWheeler
        case PackageSize.l: return RideVehicleType.van
        default: return RideVehicleType.motorbike
        }
    }
}
