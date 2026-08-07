import Combine
import Foundation
import MageRideShared

/// One Mode C tier, as SCR-PI-009 is allowed to show it.
///
/// **There is no `etaSeconds` and no `distanceMetres` on this type, and that is AL-19 expressed as a
/// type.** D5' §BR-23.3: *"Before dispatch, Mode C private tiers expose the upfront price only —
/// 'minutes away' and 'distance to driver' are suppressed (no driver matched yet)."* A tier card
/// cannot render a number it was never given, so the fence holds even if somebody later adds a row
/// to the layout.
///
/// - `token` is the `fareEstimateToken` this price is bound to. `POST /v1/rides/request` rejects a
///   booking whose token does not match its quote (`400 invalid-fare-token`), which is what stops a
///   client naming its own fare.
struct TierQuote: Equatable, Identifiable {
    let vehicleType: RideVehicleType
    let amountMinor: Int64
    let token: String

    var id: String { vehicleType.wire }
}

/// A GTFS option as the list draws it — route number, description, and the Direct/Transit tag.
struct PublicRouteRow: Equatable, Identifiable {
    let routeId: String
    let routeShortName: String
    let headsign: String?
    let routeDescription: String?
    let isDirect: Bool
    let transfers: Int

    /// The row's identity in a `ForEach`. A transfer option and a direct one can name the same first
    /// leg, so the transfer count is part of it.
    var id: String { "\(routeId)|\(transfers)" }
}

/// Which of the two lists the passenger has picked from. Exactly one, or neither.
enum BookingSelection: Equatable {
    case publicRoute(PublicRouteRow)
    case privateTier(TierQuote)
}

/// The *"Walk 250 m to Pamankada halt"* hint under a selected public route.
struct WalkHint: Equatable {
    let haltName: String
    let metres: Int
}

/// SCR-PI-009's state.
struct RideBookingState {

    var draft = BookingDraftState()

    /// Direct options first, then transfer ones (BR-23.2's ordering, server-side).
    var routes: [PublicRouteRow] = []

    /// Whether transit-svc could answer. `noFeed` hides the whole public section behind a muted row
    /// rather than failing the screen (AL-55).
    var coverage: TransitCoverage = TransitCoverage.active

    /// transit-svc was unreachable. Renders as the same muted row: a passenger does not care which
    /// of the two happened, and the private tiers work either way.
    var routesFailed = false
    var routesLoading = false

    var tiers: [TierQuote] = []
    var tiersLoading = false

    var selection: BookingSelection?

    /// The selected route's GTFS shape, decoded. Empty for a private tier.
    var routePolyline: [GeoPoint] = []

    /// The blue line to the nearest halt, drawn only when the passenger is not already on the route.
    var walkPolyline: [GeoPoint] = []

    /// Which halt that line ends at, and how far. `nil` when they are on-route.
    var walkHalt: WalkHint?

    var booking = false

    /// The ride this screen created, once it has. The screen navigates on it.
    var booked: String?

    var errorKey: String?

    /// Whether the public section is replaced by *"Bus route info coming soon for this area"*.
    var publicUnavailable: Bool {
        !routesLoading && (routesFailed || coverage == TransitCoverage.noFeed)
    }

    /// A public route is tracked, never booked — no fare, no payment chip, no `POST /rides`.
    var isPublicSelected: Bool {
        if case .publicRoute = selection { return true }
        return false
    }

    /// Whether Book Now can fire.
    var canBook: Bool {
        guard !booking, case .privateTier = selection else { return false }
        return draft.isQuotable
    }

    /// The two ends, as §0.3's pins.
    var pins: [MapPin] {
        var pins: [MapPin] = []
        if let pickup = draft.pickup {
            pins.append(MapPin(kind: VehicleLayers.pinPickup, lat: pickup.lat, lng: pickup.lng))
        }
        if let dropoff = draft.dropoff {
            pins.append(MapPin(kind: VehicleLayers.pinDropoff, lat: dropoff.lat, lng: dropoff.lng))
        }
        return pins
    }
}

/// SCR-PI-009 — the multimodal list, and the booking.
///
/// **Two fences, and they are the reason this type exists rather than a screen reading two APIs.**
///
/// 1. **AL-19 / BR-23.3.** A Mode C tier shows the upfront price and nothing else before a driver is
///    matched. ``TierQuote`` carries no ETA and no distance, so the card physically cannot.
/// 2. **AL-18 / BR-23.2.** Mode A options are the GTFS feed's — route number, headsign, Direct or
///    Transit — and they are *tracked*, not booked. Selecting one clears the tier, empties the
///    payment decision and changes the CTA; there is no fare on a bus.
///
/// **The two lists load independently and neither blocks the other.** transit-svc being down or
/// having no feed for a corridor (AL-55) hides the public section behind a muted row while the
/// private tiers quote normally — *"nothing blocks on GTFS coverage"* is D2' §SCR-PA-009's own
/// wording. The reverse holds too: a fare-svc outage leaves the bus routes usable.
@MainActor
final class RideBookingModel: ObservableObject {

    @Published private(set) var state = RideBookingState()

    private let draft: BookingDraft
    private let bookings: BookingRepository
    private let keys: IdempotencyKeys

    private var subscriptions: Set<AnyCancellable> = []
    private var work: [Task<Void, Never>] = []

    init(draft: BookingDraft, bookings: BookingRepository, keys: IdempotencyKeys) {
        self.draft = draft
        self.bookings = bookings
        self.keys = keys
        state.draft = draft.state
    }

    deinit {
        work.forEach { $0.cancel() }
    }

    /// Subscribes to the draft and reads both lists. Idempotent — `.task` may run twice.
    func start() {
        guard subscriptions.isEmpty else { return }
        draft.$state
            .sink { [weak self] latest in self?.state.draft = latest }
            .store(in: &subscriptions)
        refresh()
    }

    /// Re-reads both lists. Called on entry and whenever an end of the journey moves.
    func refresh() {
        let current = draft.state
        guard let from = current.pickup?.point, let to = current.dropoff?.point else { return }

        work.append(Task { await loadRoutes(from: from, to: to) })
        work.append(Task { await loadTiers(from: from, to: to, current: current) })
    }

    /// SCR-PI-009's *"For Me / Someone"*. The screen navigates to SCR-PI-010b on the second.
    func setBookingFor(_ value: BookingFor) {
        draft.update { $0.bookingFor = value }
    }

    /// SCR-PI-009's *"Person / Package"*. A parcel re-quotes: the tiers and the kind both change.
    func setSubject(_ value: BookingSubject) {
        draft.update {
            $0.subject = value
            $0.vehicleType = nil
        }
        state.selection = nil
        state.tiers = []
        refresh()
    }

    /// The payment chip. Private tiers only — a bus is not paid for in this app.
    func setPaymentMethod(_ method: PaymentMethod) {
        draft.update { $0.paymentMethod = method }
    }

    /// A tier was chosen.
    ///
    /// Clears the route line as well as the selection: a map showing a bus route under a *"Book
    /// Now"* for a tuk-tuk is two answers to one question.
    func selectTier(_ quote: TierQuote) {
        draft.update { $0.vehicleType = quote.vehicleType }
        state.selection = .privateTier(quote)
        state.routePolyline = []
        state.walkPolyline = []
        state.walkHalt = nil
        state.errorKey = nil
    }

    /// A public route was chosen — draw it.
    ///
    /// The CTA becomes *"Track route"*, the tier is dropped from the draft and no fare is quoted:
    /// D2' §SCR-PA-009 is explicit that *"no fare/payment is charged (public transport)"*.
    func selectRoute(_ route: PublicRouteRow) {
        draft.update { $0.vehicleType = nil }
        state.selection = .publicRoute(route)
        state.errorKey = nil
        work.append(Task { await drawRoute(route) })
    }

    /// Book Now — `POST /v1/rides/request`.
    ///
    /// **The `clientRequestId` is minted once per attempt and reused as the idempotency key.** R-18
    /// dedupes on `(passengerId, clientRequestId)`, so a retry after a timeout returns the ride the
    /// first call created rather than booking a second one; a fresh id per retry would be the bug
    /// that rule exists to prevent.
    func book() {
        guard case .privateTier(let quote) = state.selection else { return }
        guard let pickup = state.draft.pickup, let dropoff = state.draft.dropoff, !state.booking else { return }

        state.booking = true
        state.errorKey = nil
        let current = state.draft

        work.append(Task {
            do {
                let response = try await bookings.requestRide(
                    IosBookingRequestsKt.rideRequestFor(
                        clientRequestId: keys.next(),
                        kind: current.kind,
                        pickup: pickup,
                        dropoff: dropoff,
                        vehicleType: quote.vehicleType,
                        fareEstimateToken: quote.token,
                        paymentMethod: PaymentRails.bookingValueOf(current.paymentMethod),
                        riderName: current.riderName,
                        riderPhone: current.riderPhone.isEmpty ? nil : PhoneNumber.toE164(current.riderPhone)
                    )
                )
                // The booking is a ride now; the draft has done its job and must not survive into
                // the next one.
                draft.clear()
                state.booking = false
                state.booked = response.rideId
            } catch is CancellationError {
                state.booking = false
            } catch {
                state.booking = false
                state.errorKey = BookingErrors.messageKey(for: error)
            }
        })
    }

    /// The screen has navigated away from the created ride.
    func onBookingConsumed() {
        state.booked = nil
    }

    /// Dismisses an inline failure.
    func clearError() {
        state.errorKey = nil
    }

    // MARK: -

    /// The GTFS half.
    ///
    /// A failure is **not** an error state: AL-55 and D2' §SCR-PA-009 both say the private tiers and
    /// the live map keep working when route matching cannot answer, so this sets a flag the list
    /// renders as one muted row and nothing else changes.
    private func loadRoutes(from: GeoPoint, to: GeoPoint) async {
        state.routesLoading = true
        state.routesFailed = false
        do {
            let answer = try await bookings.transitOptions(from: from, to: to)
            guard !Task.isCancelled else { return }
            state.routes = answer.options.map(PublicRouteRow.init(option:))
            state.coverage = answer.coverage
            state.routesLoading = false
        } catch {
            guard !Task.isCancelled else { return }
            state.routesLoading = false
            state.routesFailed = true
            state.routes = []
        }
    }

    /// The Mode C half — one `GET /v1/fare/estimate` per tier, concurrently.
    ///
    /// One call per tier because that is what the contract offers: `estimateFare` takes a single
    /// `vehicleType` and answers a single token, and a token is what a booking must carry. Run in
    /// parallel because six sequential round trips on a 3G connection is the difference between a
    /// screen that fills and one that crawls.
    ///
    /// A tier whose estimate failed is **left out** rather than shown priceless. A card with no
    /// price is a card a passenger will tap.
    private func loadTiers(from: GeoPoint, to: GeoPoint, current: BookingDraftState) async {
        state.tiersLoading = true
        let kind = current.kind == RideKind.package ? FareEstimateKind.package : FareEstimateKind.passenger
        let types = Self.tiers(for: current)

        var quotes: [TierQuote] = []
        await withTaskGroup(of: TierQuote?.self) { group in
            for type in types {
                group.addTask { [bookings] in
                    guard
                        let quote = try? await bookings.estimate(from: from, to: to, vehicleType: type, kind: kind)
                    else {
                        return nil
                    }
                    return TierQuote(
                        vehicleType: type,
                        amountMinor: quote.amountMinor,
                        token: quote.fareEstimateToken
                    )
                }
            }
            for await quote in group {
                if let quote { quotes.append(quote) }
            }
        }

        guard !Task.isCancelled else { return }
        // A `TaskGroup` yields in completion order, so the list is re-sorted into the wireframe's
        // cheapest-first order rather than into whichever estimate answered first.
        let order = types.map(\.wire)
        func rank(_ quote: TierQuote) -> Int { order.firstIndex(of: quote.vehicleType.wire) ?? order.count }
        state.tiers = quotes.sorted { rank($0) < rank($1) }
        state.tiersLoading = false
    }

    /// Draws the selected route, and the walk to it when the passenger is not already on it.
    private func drawRoute(_ route: PublicRouteRow) async {
        guard let from = draft.state.pickup?.point else { return }
        do {
            let detail = try await bookings.transitRoute(routeId: route.routeId, around: from)
            guard !Task.isCancelled else { return }

            let shape = EncodedPolyline.decode(detail.shape)
            let stops = detail.nearestStops ?? detail.stops
            let halt = stops.min { GeoDistanceKt.distanceMetres(from: from, to: $0.point) <
                GeoDistanceKt.distanceMetres(from: from, to: $1.point) }
            let metres = halt.map { Int(GeoDistanceKt.distanceMetres(from: from, to: $0.point)) }

            state.routePolyline = shape
            // *"If off-route a blue walking polyline routes to the closest halt"* — and if they are
            // already at one, no line and no hint. On-route is the common case, and drawing a
            // two-metre line for it would be visual noise.
            if let halt, let metres, metres > Self.onRouteMetres {
                state.walkPolyline = [from, halt.point]
                state.walkHalt = WalkHint(haltName: halt.name, metres: metres)
            } else {
                state.walkPolyline = []
                state.walkHalt = nil
            }
        } catch {
            guard !Task.isCancelled else { return }
            // The row stays selected and the CTA still tracks; there is simply no line to draw.
            state.routePolyline = []
            state.walkPolyline = []
            state.walkHalt = nil
        }
    }

    /// Which tiers to quote.
    ///
    /// A person gets the six passenger types; a parcel gets the ones its size fits in, which is
    /// P-06's own hint made operational — offering a motorbike for a fridge is a driver arriving at
    /// a job they cannot do. `truck`/`mini_truck` are delivery-only (AL-09) and appear here and
    /// nowhere else in the app.
    nonisolated static func tiers(for current: BookingDraftState) -> [RideVehicleType] {
        guard current.kind == RideKind.package else { return passengerTiers }
        switch current.packageSize {
        case PackageSize.m: return [RideVehicleType.threeWheeler, RideVehicleType.flex, RideVehicleType.sedan]
        case PackageSize.l: return [RideVehicleType.van, RideVehicleType.miniTruck, RideVehicleType.truck]
        default: return [RideVehicleType.motorbike, RideVehicleType.threeWheeler]
        }
    }

    /// AL-09's bookable passenger types, cheapest-first as the wireframe lists them.
    ///
    /// `nonisolated`, like ``tiers(for:)``: a static of a `@MainActor` type is main-actor isolated
    /// too, and both of these are pure tables a test reads without one.
    nonisolated static let passengerTiers: [RideVehicleType] = [
        RideVehicleType.motorbike,
        RideVehicleType.threeWheeler,
        RideVehicleType.flex,
        RideVehicleType.sedan,
        RideVehicleType.miniVan,
        RideVehicleType.van,
    ]

    /// Close enough to a halt to count as being at it.
    ///
    /// BR-23.2's halt radius is an admin setting defaulting to 400 m — the distance at which
    /// transit-svc considers a stop to serve a point at all. Using the same figure here means
    /// *"on-route"* on this screen and *"direct"* on the server agree about the same passenger.
    private static let onRouteMetres = 400
}

extension PublicRouteRow {

    /// A transit option as one row.
    ///
    /// The **first** leg is what the row is named after, because that is the vehicle the passenger
    /// boards; a transfer's second route is described by the transfer count, which is what the
    /// wireframe draws (*"1 transfer at Nugegoda"*).
    init(option: TransitOption) {
        let first = option.legs.first
        self.init(
            routeId: first?.routeId ?? "",
            routeShortName: first?.routeShortName ?? "",
            headsign: first?.headsign,
            // `description` collides with `NSObject`'s on the bridge — see
            // `IosBookingRequests.kt`, and C093's `IosTicket.kt` for the trap itself.
            routeDescription: first.flatMap { IosBookingRequestsKt.transitLegDescription(leg: $0) },
            isDirect: option.kind == TransitOptionKind.direct,
            transfers: max(option.legs.count - 1, 0)
        )
    }
}
