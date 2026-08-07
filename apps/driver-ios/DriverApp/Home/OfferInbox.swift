import Foundation
import MageRideShared

/// Where a `ride_offer` push becomes the driver's live offer.
///
/// **The push is the delivery, not the payload.** notification-svc's `offer.created` handler
/// (`EventHandlers.OfferAsync`) writes exactly five data values — `kind`, `offerId`, `rideId`,
/// `expiresAt` and a **rendered** `fare`/`distance` pair that exists because the SMS fallback
/// interpolates the same dictionary. That is enough to start the fifteen-second clock and not enough
/// to draw SCR-DI-014, so ``OfferModel`` reads `GET /v1/rides/{rideId}` for the badges, the pickup and
/// the drop — inside the window, once, and only because the envelope cannot carry them.
///
/// **A backgrounded app has no socket** (`signalr-hub.md` §6), so on iOS this path is not a nicety: it
/// is the only delivery there is until the app is in front of the driver — E-01's silent
/// `content-available: 1` is what wakes the process at all. ``DriverAppDelegate`` feeds it through
/// ``PushRouter``, which is why this is a process singleton rather than a screen's.
@MainActor
final class OfferInbox {

    private let offers: OfferSession
    private let sessions: DriverSessions

    init(offers: OfferSession, sessions: DriverSessions) {
        self.offers = offers
        self.sessions = sessions
    }

    /// Takes delivery of a push. Anything that is not an offer, or arrives signed out, is dropped.
    ///
    /// `OfferSession.onOfferPushed` **replaces** whatever was held, which is correct rather than
    /// lossy: a second offer reaching this device means the first is already dead — dispatch cannot
    /// have reserved one driver twice (ADD Appendix B.2 invariant 3).
    func receive(_ message: PushMessage) {
        guard message.kind == PushMessage.kindRideOffer else { return }
        guard let driverId = sessions.userId else { return }
        guard let offer = OfferInbox.offer(from: message.data, driverId: driverId, now: Date()) else { return }
        offers.onOfferPushed(offer: offer)
    }

    // MARK: - The envelope
    //
    // `static` and pure so `OfferInboxTests` can assert them with no session and no push service,
    // which is where the Android side puts them too.

    /// `data.offerId` — the `dispatch.offers` row, echoed on accept and decline.
    static let keyOfferId = "offerId"

    /// `data.rideId`. Absent only on a malformed envelope; without it nothing can be accepted.
    static let keyRideId = "rideId"

    /// `data.expiresAt` — ride-svc's own deadline, round-tripped as ISO-8601.
    static let keyExpiresAt = "expiresAt"

    /// `data.fare` — **rendered rupees**, e.g. `1,240.00`. See ``rupeesToMinor(_:)``.
    static let keyFare = "fare"

    /// Builds the offer the countdown runs on.
    ///
    /// [now] is what the fifteen seconds are measured from **only when the envelope carried no
    /// deadline**. When it did, that deadline wins: a push that took two seconds to arrive should show
    /// thirteen seconds of ring, not fifteen, and `RideOffer.progress` derives exactly that from
    /// `expiresAt` against the fixed TTL.
    static func offer(from data: [String: String], driverId: String, now: Date) -> RideOffer? {
        guard let offerId = data[keyOfferId]?.nilIfBlank else { return nil }
        guard let rideId = data[keyRideId]?.nilIfBlank else { return nil }

        let deadline = data[keyExpiresAt]?.nilIfBlank.flatMap(IosInstantKt.parseTimestampOrNull(text:))
            ?? IosInstantKt.timestampFromEpochMillis(
                millis: Int64((now.timeIntervalSince1970 + ttlSeconds) * 1000)
            )

        return RideOffer(
            offerId: offerId,
            rideId: rideId,
            driverId: driverId,
            expiresAt: deadline,
            kind: RideKind.passenger,
            isProxy: false,
            riderName: nil,
            riderPhoneMasked: nil,
            packageSize: nil,
            packageDescription: nil,
            directionalMatched: false,
            fareEstimateMinor: rupeesToMinor(data[keyFare]) ?? 0,
            currency: Currency.lkr,
            paymentMethod: RidePaymentMethod.cash,
            pickup: nil,
            dropoff: nil,
            version: nil
        )
    }

    /// `1,240.00` back into `124000` minor units — **exactly**, with no `Double` anywhere.
    ///
    /// notification-svc formats the fare for the SMS fallback and puts the same string on the push, so
    /// the only number available before the ride is read is a rendered one. Parsing it through a
    /// floating-point type is the bug C012's *"money is `Long` minor units, never `Double`"* fence
    /// exists to prevent, so the rupees and the cents are parsed as separate integers. A value this
    /// cannot read answers `nil`, and the ride read fills it in.
    static func rupeesToMinor(_ value: String?) -> Int64? {
        guard let cleaned = value?.replacingOccurrences(of: ",", with: "")
            .trimmingCharacters(in: .whitespaces)
            .nilIfBlank
        else { return nil }

        let isNegative = cleaned.hasPrefix("-")
        let parts = (isNegative ? String(cleaned.dropFirst()) : cleaned).components(separatedBy: ".")
        guard parts.count <= 2, let rupees = Int64(parts[0]) else { return nil }

        var cents: Int64 = 0
        if parts.count == 2 {
            let padded = String((parts[1] + "00").prefix(minorDigits))
            guard let parsed = Int64(padded) else { return nil }
            cents = parsed
        }

        let minor = rupees * minorUnits + cents
        return isNegative ? -minor : minor
    }

    /// The offer window (US-6A.3, D5' §3.5) — the fallback deadline for an envelope that carried none.
    ///
    /// `RideOffer.TTL` is a `kotlin.time.Duration`, which the Objective-C export flattens to an opaque
    /// `Int64` of nanoseconds; fifteen seconds is spelled here rather than unpacked from it, and the
    /// two are held together by `OfferModelTests`.
    private static let ttlSeconds: TimeInterval = 15

    private static let minorUnits: Int64 = 100
    private static let minorDigits = 2
}
