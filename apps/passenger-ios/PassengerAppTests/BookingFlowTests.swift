import Foundation
import MageRideShared
import XCTest

@testable import PassengerApp

/// The draft six screens edit, and the `CaptureTarget` that makes one picker serve five callers.
final class BookingDraftTests: XCTestCase {

    private var preferences: FakeAppPreferences!
    private var lastFix: LastKnownFix!
    private var draft: BookingDraft!

    @MainActor
    override func setUp() {
        super.setUp()
        preferences = FakeAppPreferences()
        lastFix = LastKnownFix()
        draft = BookingDraft(preferences: preferences, lastFix: lastFix)
    }

    /// **The defect this pair exists for** (Δ C097). `begin` takes an optional pickup and every
    /// production call site on the Android side omitted it, so the draft had none — and
    /// `RideBookingViewModel.refresh()` returns early on exactly that, which meant SCR-PA-009 loaded
    /// neither list. The fix is inside the draft rather than at the call sites, so a fourth one
    /// cannot reintroduce it.
    @MainActor
    func testABookingWithNoPickupTakesTheLastKnownFix() {
        lastFix.record(PassengerFix(lat: BookingFixtures.colombo.lat, lng: BookingFixtures.colombo.lng))

        draft.begin(dropoff: BookingFixtures.nugegoda)

        XCTAssertEqual(draft.state.pickup?.lat, BookingFixtures.colombo.lat)
        XCTAssertTrue(draft.state.isQuotable, "a booking with no pickup cannot be quoted at all")
    }

    /// A proxy rider's shared pin and a parcel's own end are both better answers than where the
    /// booker happens to be standing, so an explicit pickup always wins — and a cold start with no
    /// fix at all is honestly not quotable rather than quietly quoted from Colombo Fort.
    @MainActor
    func testAnExplicitPickupWinsAndNoFixIsNoPickup() {
        lastFix.record(PassengerFix(lat: BookingFixtures.colombo.lat, lng: BookingFixtures.colombo.lng))
        draft.begin(dropoff: BookingFixtures.colombo, pickup: BookingFixtures.nugegoda)
        XCTAssertEqual(draft.state.pickup?.lat, BookingFixtures.nugegoda.lat)

        let cold = BookingDraft(preferences: preferences, lastFix: LastKnownFix())
        cold.begin(dropoff: BookingFixtures.nugegoda)
        XCTAssertNil(cold.state.pickup)
        XCTAssertFalse(cold.state.isQuotable)
    }

    /// **A place with nobody waiting for it is a new booking.** That is the home sheet's *"Where
    /// to?"*, and it is the difference between beginning a booking and editing one.
    @MainActor
    func testAPlaceWithNoPendingCaptureIsNotConsumed() {
        XCTAssertFalse(draft.capture(BookingFixtures.nugegoda))
        XCTAssertNil(draft.state.dropoff)
    }

    @MainActor
    func testEachTargetPutsThePlaceWhereItBelongs() {
        let cases: [(CaptureTarget, (BookingDraftState) -> Place?)] = [
            (.bookingDropoff, \.dropoff),
            (.bookingPickup, \.pickup),
            (.proxyPickup, \.pickup),
            (.packagePickup, \.packagePickup),
            (.packageDropoff, \.packageDropoff),
            (.scheduleDropoff, \.dropoff),
        ]

        for (target, read) in cases {
            draft.clear()
            draft.expect(target)
            XCTAssertTrue(draft.capture(BookingFixtures.nugegoda))
            XCTAssertEqual(read(draft.state)?.lat, BookingFixtures.nugegoda.lat, "\(target)")
            XCTAssertNil(draft.pendingCapture, "a capture is consumed once")
        }
    }

    /// **Package wins over proxy.** `RideKind` has no `proxy_package`; the rider fields still
    /// travel, so nothing about who arranged it is lost.
    @MainActor
    func testTheKindIsDerivedAndAParcelOutranksAProxy() {
        XCTAssertEqual(draft.state.kind, RideKind.passenger)

        draft.update { $0.bookingFor = .someoneElse }
        XCTAssertEqual(draft.state.kind, RideKind.proxy)

        draft.update { $0.subject = .parcel }
        XCTAssertEqual(draft.state.kind, RideKind.package)
    }

    /// US-22.4 — a fresh draft opens on the stored rail, read **on every fresh draft** so a change
    /// made in Settings applies to the next booking without anything having to be told.
    @MainActor
    func testEveryFreshDraftReadsTheStoredRail() {
        XCTAssertEqual(draft.state.paymentMethod, PaymentMethod.cash)

        preferences.defaultPaymentMethod = PaymentMethod.wallet.wire
        draft.begin(dropoff: BookingFixtures.nugegoda)
        XCTAssertEqual(draft.state.paymentMethod, PaymentMethod.wallet)

        // A row written before AL-57/AL-59 names a rail this app no longer offers, and reads as Cash
        // rather than pre-selecting something no screen draws.
        preferences.defaultPaymentMethod = PaymentMethod.onepay.wire
        draft.clear()
        XCTAssertEqual(draft.state.paymentMethod, PaymentMethod.cash)
    }

    /// A draft left behind is the next booking's stale rider name.
    @MainActor
    func testBeginningABookingForgetsTheLastOne() {
        draft.update {
            $0.riderName = "Nimal"
            $0.packageDescription = "Documents"
        }

        draft.begin(dropoff: BookingFixtures.nugegoda, pickup: BookingFixtures.colombo)

        XCTAssertTrue(draft.state.riderName.isEmpty)
        XCTAssertTrue(draft.state.packageDescription.isEmpty)
        XCTAssertEqual(draft.state.pickup?.lat, BookingFixtures.colombo.lat)
    }
}

/// SCR-PI-010b — the P-02 round trip and P-03's lookup.
final class ProxyRiderModelTests: XCTestCase {

    private var bookings: FakeBookingRepository!
    private var draft: BookingDraft!
    private var live: PassengerLiveMap!

    @MainActor
    override func setUp() {
        super.setUp()
        SharedH3Grid.resetFailures()
        bookings = FakeBookingRepository()
        draft = BookingDraft(preferences: FakeAppPreferences(), lastFix: LastKnownFix())
        live = PassengerLiveMap(
            transport: FakeLiveHubTransport(),
            snapshots: FakeNearbySnapshots(),
            grid: SharedH3Grid()
        )
    }

    /// **P-03 is checked before anything is sent** (US-8.19). A number that belongs to nobody cannot
    /// be sent an FCM, so the Request method removes itself rather than leaving a dead button.
    @MainActor
    func testAnUnregisteredRiderCannotBeAskedAndTheMethodMovesOn() async {
        bookings.registered = false
        let model = started()
        model.setMethod(.request)

        model.onPhoneChanged(BookingFixtures.riderPhone)
        await eventually("looked up") { await MainActor.run { model.state.riderRegistered == false } }

        XCTAssertEqual(model.state.method, .search)
        XCTAssertFalse(model.state.canRequestLocation)
        XCTAssertTrue(bookings.locationRequestsFor.isEmpty, "nothing was sent to nobody")
    }

    /// A failed lookup leaves the answer **unknown** rather than unregistered — the safe direction,
    /// because the worst case is a request that expires.
    @MainActor
    func testAFailedLookupKeepsEveryMethodAvailable() async {
        bookings.locationFailure = BookingFakeError.unreachable
        let model = started()

        model.onPhoneChanged(BookingFixtures.riderPhone)
        await eventually("settled") { await MainActor.run { !model.state.isLooking } }

        XCTAssertNil(model.state.riderRegistered)
        XCTAssertTrue(model.state.canRequestLocation)
    }

    /// The number is normalised on every keystroke, so a phonebook entry stored as `077 123 4567`
    /// and one stored as `+94 77 123 4567` are the same rider.
    @MainActor
    func testTheNumberIsNormalisedOnItsWayToTheDraft() async {
        let model = started()

        model.onPhoneChanged("+94 77 123 4567")

        XCTAssertEqual(model.state.riderPhone, BookingFixtures.riderPhone)
        XCTAssertEqual(draft.state.riderPhone, BookingFixtures.riderPhone)
    }

    /// The E.164 form is what leaves the device, and P-12's bucket is respected: a second tap while
    /// one is still pending spends nothing.
    @MainActor
    func testARequestGoesOutOnceAndInE164() async {
        let model = started()
        model.onPhoneChanged(BookingFixtures.riderPhone)
        await eventually("looked up") { await MainActor.run { model.state.riderRegistered == true } }

        model.requestRiderLocation()
        await eventually("pending") {
            await MainActor.run { model.state.requestState == LocationRequestState.pending }
        }
        model.requestRiderLocation()

        XCTAssertEqual(bookings.locationRequestsFor, [PhoneNumber.toE164(BookingFixtures.riderPhone)])
    }

    /// **A Confirmed carries the pin; a Declined carries nothing at all** (P-02). The screen falls
    /// back to the other methods rather than pretending it was given something.
    @MainActor
    func testADeclineLeavesNoPickupAndFallsBackToSearch() async {
        bookings.locationRequest = BookingFixtures.locationRequest(state: LocationRequestState.declined)
        let model = started()
        model.onPhoneChanged(BookingFixtures.riderPhone)
        await eventually("looked up") { await MainActor.run { model.state.riderRegistered == true } }
        model.setMethod(.request)
        model.requestRiderLocation()

        await eventually("resolved") {
            await MainActor.run { model.state.requestState == LocationRequestState.declined }
        }

        XCTAssertNil(model.state.pickup)
        XCTAssertEqual(model.state.method, .search)
    }

    @MainActor
    private func started() -> ProxyRiderModel {
        let model = ProxyRiderModel(draft: draft, bookings: bookings, live: live)
        model.start()
        return model
    }
}

/// SCR-PI-011 — the one screen where privacy is the feature.
final class ConfirmPickupModelTests: XCTestCase {

    private var bookings: FakeBookingRepository!
    private var locations: FakePassengerLocationSource!

    @MainActor
    override func setUp() {
        super.setUp()
        bookings = FakeBookingRepository()
        locations = FakePassengerLocationSource()
    }

    /// **Declining sends no coordinates. None.** The repository method has no parameter to put one
    /// in, and this asserts it from the other end: what the fake was handed is an id and nothing
    /// else, even though a pin was on screen.
    @MainActor
    func testDecliningSendsAnIdAndNothingElse() async {
        let model = started()
        locations.emit(PassengerFix(lat: 6.9344, lng: 79.8428, accuracyMetres: 12))
        await eventually("pinned") { await MainActor.run { model.state.pin != nil } }

        model.decline()
        await eventually("declined") { await MainActor.run { model.state.outcome != nil } }

        XCTAssertEqual(bookings.declined, [BookingFixtures.requestId])
        XCTAssertTrue(bookings.confirmed.isEmpty, "no confirm, and therefore no coordinate")
    }

    /// A rider must never be left on a *"share"* screen because a decline failed — and nothing about
    /// the failure changes what was (not) sent.
    @MainActor
    func testADeclineThatFailedStillStandsLocally() async {
        let model = started()
        bookings.locationFailure = BookingFakeError.unreachable

        model.decline()
        await eventually("closed anyway") { await MainActor.run { model.state.outcome != nil } }

        XCTAssertEqual(model.state.outcome, LocationRequestState.declined)
        XCTAssertNil(model.state.errorKey)
    }

    /// The accuracy travels with the point: a 500 m cell-tower fix and a 5 m GPS lock are different
    /// instructions to a driver, and sending only the coordinate would make them look identical.
    @MainActor
    func testSharingCarriesTheAccuracyAndAZeroMeansAbsent() async {
        let model = started()
        locations.emit(PassengerFix(lat: 6.9344, lng: 79.8428, accuracyMetres: 12))
        await eventually("pinned") { await MainActor.run { model.state.pin != nil } }

        model.share()
        await eventually("shared") { await MainActor.run { model.state.outcome != nil } }

        XCTAssertEqual(bookings.confirmed.first?.at.accuracy?.doubleValue, 12)

        // Core Location's sentinel for "not reported" is a non-positive number, and a zero-radius
        // circle is not a measurement.
        let unmeasured = IosBookingRequestsKt.geoPointWithAccuracy(lat: 6.9, lng: 79.8, accuracyMetres: 0)
        XCTAssertNil(unmeasured.accuracy)
    }

    /// The clock starts from the request's **own** expiry, not from a fresh 300 s: the FCM may have
    /// sat in a low-power bucket, and a countdown that promised time the server had already spent
    /// would send the rider's tap into a `410`.
    @MainActor
    func testTheCountdownStartsFromTheRequestsOwnExpiry() async {
        bookings.locationRequest = BookingFixtures.locationRequest(
            state: LocationRequestState.pending,
            expiresInSeconds: 120
        )
        let model = started()

        await eventually("loaded") { await MainActor.run { model.state.secondsLeft > 0 } }

        XCTAssertLessThanOrEqual(model.state.secondsLeft, 120)
        XCTAssertGreaterThan(model.state.secondsLeft, 100, "and not a fresh five minutes")
    }

    /// A request that has already been answered opens terminal — there is nothing left to do.
    @MainActor
    func testAnAlreadyResolvedRequestOpensClosed() async {
        bookings.locationRequest = BookingFixtures.locationRequest(state: LocationRequestState.expired)
        let model = started()

        await eventually("terminal") { await MainActor.run { model.state.outcome != nil } }

        XCTAssertFalse(model.state.canShare)
    }

    @MainActor
    private func started() -> ConfirmPickupModel {
        let model = ConfirmPickupModel(
            requestId: BookingFixtures.requestId,
            bookings: bookings,
            locations: locations
        )
        model.start()
        return model
    }
}

/// SCR-PI-012 and SCR-PI-013 — the parcel and the future ride.
final class PackageAndScheduleTests: XCTestCase {

    private var bookings: FakeBookingRepository!
    private var draft: BookingDraft!
    private var otps: PackageOtps!

    @MainActor
    override func setUp() {
        super.setUp()
        bookings = FakeBookingRepository()
        draft = BookingDraft(preferences: FakeAppPreferences(), lastFix: LastKnownFix())
        otps = PackageOtps()
    }

    /// **There is nobody at the pickup to ask** — the fourth method is refused rather than only
    /// undrawn, so a layout that offered it could not act on it.
    @MainActor
    func testAPickupCannotAskTheRecipientWhereItIs() {
        let model = packageModel()

        model.setMethod(.pickup, .request)
        XCTAssertEqual(model.state.pickupMethod, .search)

        model.setMethod(.dropoff, .request)
        XCTAssertEqual(model.state.dropoffMethod, .request)

        XCTAssertEqual(PackageEnd.pickup.methods.count, 3)
        XCTAssertEqual(PackageEnd.dropoff.methods.count, 4)
    }

    /// P-06 — the smallest vehicle the size fits, which is the vehicle the hint already named.
    func testAParcelIsQuotedOnTheVehicleItsHintPromised() {
        XCTAssertEqual(PackageBookingModel.vehicle(for: PackageSize.s).wire, RideVehicleType.motorbike.wire)
        XCTAssertEqual(PackageBookingModel.vehicle(for: PackageSize.m).wire, RideVehicleType.threeWheeler.wire)
        XCTAssertEqual(PackageBookingModel.vehicle(for: PackageSize.l).wire, RideVehicleType.van.wire)
    }

    /// Moving an end, or changing the size, invalidates the quote: a token binds a price to a
    /// journey, and both of those change the journey.
    @MainActor
    func testChangingTheParcelInvalidatesItsQuote() async {
        let model = filledPackage()
        model.estimate()
        await eventually("quoted") { await MainActor.run { model.state.estimateMinor != nil } }
        XCTAssertTrue(model.state.canBook)

        model.setSize(PackageSize.l)

        XCTAssertNil(model.state.estimateMinor)
        XCTAssertNil(model.state.quoteToken)
        XCTAssertFalse(model.state.canBook, "a price has to exist before a booking can be made")
    }

    /// **P-07.** The pickup OTP comes back once and is never returned again, so it is caught on the
    /// way past — both for this screen and for C099's, which has no read for it.
    @MainActor
    func testThePickupOtpIsCaughtOnItsWayPast() async {
        let model = filledPackage()
        model.estimate()
        await eventually("quoted") { await MainActor.run { model.state.estimateMinor != nil } }

        model.book()
        await eventually("booked") { await MainActor.run { model.state.booked != nil } }

        XCTAssertEqual(model.state.pickupOtp, "4821")
        XCTAssertEqual(otps.pickupFor(rideId: BookingFixtures.rideId), "4821")
        XCTAssertEqual(bookings.requested.first?.kind, RideKind.package)
        XCTAssertEqual(bookings.requested.first?.packageSize, PackageSize.s)
        XCTAssertEqual(bookings.requested.first?.recipientPhone, PhoneNumber.toE164(BookingFixtures.riderPhone))
    }

    /// COD is booking-time and package-only (AL-22, US-20.8); every other rail books as `cash`
    /// because `ride.yaml`'s enum has no `wallet` and no `scan_driver_qr`.
    func testOnlyAParcelCanBookAsCod() {
        XCTAssertEqual(PaymentRails.bookingValueOf(PaymentMethod.cod), RidePaymentMethod.cod)
        for rail in PaymentRails.ride {
            XCTAssertEqual(PaymentRails.bookingValueOf(rail), RidePaymentMethod.cash, rail.wire)
        }
        XCTAssertFalse(PaymentRails.ride.contains(PaymentMethod.cod))
        XCTAssertTrue(PaymentRails.parcel.contains(PaymentMethod.cod))
        XCTAssertTrue(PaymentRails.parcel.allSatisfy { !PaymentRails.retired.contains($0) })
    }

    // MARK: - SCR-PI-013

    /// **AL-36.** A time alone is not a booking, and Confirm is what says so.
    @MainActor
    func testConfirmIsDisabledUntilThereIsADestination() {
        let model = ScheduleRideModel(draft: draft, bookings: bookings)
        model.setPickupTime(Date().addingTimeInterval(3_600))
        XCTAssertFalse(model.state.canConfirm)

        model.setDestination(BookingFixtures.nugegoda)
        XCTAssertTrue(model.state.canConfirm)
    }

    /// The Job Board opens at T-30 (US-6A.4/6A.5), so anything closer would be posted to a board it
    /// has already passed. Refusing it beats accepting a booking that quietly never dispatches.
    @MainActor
    func testATimeInsideTheJobBoardWindowIsRefused() {
        let model = ScheduleRideModel(draft: draft, bookings: bookings)

        model.setPickupTime(Date().addingTimeInterval(600))

        XCTAssertNil(model.state.pickupTime)
        XCTAssertEqual(model.state.errorKey, "schedule_time_past")
    }

    /// **No fare token, and that is the contract's own decision**: fare-svc meters a scheduled ride
    /// when it materialises, because *"the price of a ride thirty minutes from now is not the price
    /// quoted when it was booked"*.
    @MainActor
    func testASchedulePostsNoFareTokenAndClearsTheDraft() async {
        draft.begin(dropoff: BookingFixtures.nugegoda, pickup: BookingFixtures.colombo)
        let model = ScheduleRideModel(draft: draft, bookings: bookings)
        model.refreshPlaces()
        model.setPickupTime(Date().addingTimeInterval(7_200))

        model.confirm()
        await eventually("scheduled") { await MainActor.run { model.state.scheduled != nil } }

        XCTAssertEqual(bookings.scheduled.count, 1)
        XCTAssertTrue(bookings.requested.isEmpty, "this is dispatch-svc, not ride-svc")
        XCTAssertEqual(bookings.scheduled.first?.destLat, BookingFixtures.nugegoda.lat)
        XCTAssertEqual(bookings.scheduled.first?.pickupLat?.doubleValue, BookingFixtures.colombo.lat)
        XCTAssertNil(draft.state.dropoff)
    }

    /// A scheduled ride with no pickup is one that starts wherever the passenger is at the time —
    /// a legitimate booking, and the contract types it as nullable for that reason.
    @MainActor
    func testAScheduleWithNoPickupIsStillABooking() async {
        let model = ScheduleRideModel(draft: draft, bookings: bookings)
        model.setDestination(BookingFixtures.nugegoda)
        model.setPickupTime(Date().addingTimeInterval(7_200))

        model.confirm()
        await eventually("scheduled") { await MainActor.run { model.state.scheduled != nil } }

        XCTAssertNil(bookings.scheduled.first?.pickupLat)
    }

    // MARK: -

    @MainActor
    private func packageModel() -> PackageBookingModel {
        PackageBookingModel(draft: draft, bookings: bookings, keys: FakeIdempotencyKeys(), otps: otps)
    }

    @MainActor
    private func filledPackage() -> PackageBookingModel {
        let model = packageModel()
        model.onDescriptionChanged("Documents")
        model.onRecipientNameChanged("Sunethra")
        model.onRecipientPhoneChanged(BookingFixtures.riderPhone)
        model.setPlace(.pickup, BookingFixtures.colombo)
        model.setPlace(.dropoff, BookingFixtures.nugegoda)
        return model
    }
}

/// SCR-PI-012a — the paste sheet's four states.
final class PasteLinkModelTests: XCTestCase {

    private var bookings: FakeBookingRepository!

    @MainActor
    override func setUp() {
        super.setUp()
        bookings = FakeBookingRepository()
    }

    /// **A full URL never touches the network**, which is the whole of AL-20's split.
    @MainActor
    func testAFullUrlResolvesWithNoServerCall() async {
        let model = PasteLinkModel(bookings: bookings)

        model.onPasted("https://www.google.com/maps?q=6.9271,79.8612")

        XCTAssertTrue(bookings.parsedUrls.isEmpty)
        XCTAssertEqual(model.state.place?.lat, 6.9271)
        // The address arrives after the pin, so the preview is up before the courtesy lands.
        await eventually("named") { await MainActor.run { model.state.place?.address != nil } }
        XCTAssertEqual(model.state.place?.address, bookings.reverseName)
    }

    /// Only a short link reaches transit-svc, and the URL travels intact.
    @MainActor
    func testAShortLinkIsFollowedByTheServer() async {
        let model = PasteLinkModel(bookings: bookings)

        model.onPasted("https://maps.app.goo.gl/xK7vQ2")

        await eventually("resolved") { await MainActor.run { model.state.place != nil } }
        XCTAssertEqual(bookings.parsedUrls, ["https://maps.app.goo.gl/xK7vQ2"])
        XCTAssertEqual(model.state.place?.lat, bookings.parsed.lat)
    }

    /// **One retry, then the map.** A second attempt costs three more seconds; a third would cost
    /// nine, by which point picking on the map is faster and the sheet says so.
    @MainActor
    func testAShortLinkIsRetriedExactlyOnceAndThenGivesUp() async {
        bookings.parseFailure = BookingFakeError.unreachable
        let model = PasteLinkModel(bookings: bookings)

        model.onPasted("https://maps.app.goo.gl/xK7vQ2")

        await eventually("failed") { await MainActor.run { model.state == .failed } }
        XCTAssertEqual(bookings.parsedUrls.count, 2)
    }

    @MainActor
    func testSomethingThatIsNotALinkIsTheErrorStateImmediately() {
        let model = PasteLinkModel(bookings: bookings)

        model.onPasted("have you seen this place")

        XCTAssertEqual(model.state, .failed)
        XCTAssertTrue(bookings.parsedUrls.isEmpty)
    }

    /// Reopening the sheet for another field starts empty rather than showing the last field's pin.
    @MainActor
    func testResetForgetsThePreviousAnswer() {
        let model = PasteLinkModel(bookings: bookings)
        model.onPasted("https://www.google.com/maps?q=6.9271,79.8612")
        XCTAssertNotNil(model.state.place)

        model.reset()

        XCTAssertEqual(model.state, .empty)
    }
}
