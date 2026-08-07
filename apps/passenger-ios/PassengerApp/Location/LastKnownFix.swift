import Combine
import Foundation
import MageRideShared

/// The last position any screen saw, for the screens that need one and have no business subscribing.
///
/// **Why this exists rather than a second subscription.** ``PassengerLocationSource`` is *cold*: the
/// manager starts on the first subscriber and stops with the last, and the blue status-bar indicator
/// stays lit for as long as anything is subscribed. A picker that wanted to bias a geocoder, or a
/// booking that wanted a default pickup, would each be another subscriber holding it open — so
/// instead ``RecordingLocationSource`` records every fix **as it passes through the seam**, and
/// everybody else reads.
///
/// It is therefore *last known* rather than *current*, and that is the honest name: on a cold start
/// before the map has drawn, it is `nil`, and every reader already has an answer for that.
///
/// Three readers today, all of them cluster 3's:
///
/// 1. **SCR-PI-008's geocoder bias** — results ranked around the passenger (C096 left this `nil`).
/// 2. **A booking's default pickup** — *"Current location"* is what SCR-PI-009's summary and
///    SCR-PI-013's first row both print, and without this the draft has no pickup at all and
///    nothing quotes.
/// 3. **The map picker's opening camera**, so *"Select on map"* does not start over Colombo Fort
///    for a passenger standing in Kandy.
///
/// ### The defect it was written for (Δ C097)
///
/// **`BookingDraft.begin` takes an optional pickup and no production call site was passing one.** On
/// the Android side all three `draft.begin(…)` calls omitted it, so a booking begun from the home
/// sheet or from SCR-PA-008 had no pickup — and `RideBookingViewModel.refresh()` returns early on
/// exactly that, which meant **SCR-PA-009 showed no bus routes and no tiers at all**. The fix lives
/// *inside* the draft on both platforms, so a fourth call site cannot reintroduce it.
///
/// `@MainActor` and a plain box: it is written from a Combine operator already on the main actor and
/// read synchronously by a view. Nothing observes it — a reader takes it at the moment it needs one,
/// which is what *"last known"* means.
@MainActor
final class LastKnownFix {

    private(set) var point: GeoPoint?

    /// A fix on its way past — see ``RecordingLocationSource``.
    func record(_ fix: PassengerFix) {
        point = fix.point
    }

    /// The fix as a booking's pickup, with no address — a reverse-geocode for a label the passenger
    /// is about to overwrite is a call nobody asked for. SCR-PI-009's summary prints *"Current
    /// location"* where the address is absent.
    var asPlace: Place? {
        point.map { Place(lat: $0.lat, lng: $0.lng, address: nil) }
    }
}

/// The fix source, recording what passes through it.
///
/// **A decorator rather than a parameter on the map's model** (Δ C097). Writing to ``LastKnownFix``
/// from SCR-PI-010 alone would work — it is the one screen that subscribes for its whole life — but
/// it would put the rule in one of five subscribers, and the other four (SCR-PI-011's pin, C101's
/// address capture, C102's alarm, and whatever comes next) know the passenger's position just as
/// well. Recording at the seam means the last known fix is the last one *anybody* saw, and it costs
/// no extra subscription.
///
/// `handleEvents` rather than a subject: the delegate is **cold** and this must not change that. The
/// manager still starts on the first subscriber and stops with the last, which is what keeps the
/// blue status-bar indicator honest.
final class RecordingLocationSource: PassengerLocationSource {

    private let delegate: PassengerLocationSource
    private let lastFix: LastKnownFix

    init(delegate: PassengerLocationSource, lastFix: LastKnownFix) {
        self.delegate = delegate
        self.lastFix = lastFix
    }

    var fixes: AnyPublisher<PassengerFix, Never> {
        delegate.fixes
            .handleEvents(receiveOutput: { [lastFix] fix in
                // Core Location delivers on the main thread and every subscriber here is a view
                // model on the main actor, so the hop is a formality that keeps the isolation
                // honest rather than a thread change.
                Task { @MainActor in lastFix.record(fix) }
            })
            .eraseToAnyPublisher()
    }
}
