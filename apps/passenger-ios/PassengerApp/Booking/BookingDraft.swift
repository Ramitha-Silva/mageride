import Combine
import Foundation
import MageRideShared

/// SCR-PI-009's *"For Me / Someone"* picker (US-8.16).
enum BookingFor: String, CaseIterable {
    case me
    case someoneElse

    var labelKey: String {
        switch self {
        case .me: return "booking_for_me"
        case .someoneElse: return "booking_for_someone"
        }
    }
}

/// SCR-PI-009's *"Person / Package"* picker (US-20.1).
/// `parcel` rather than `package`, which Swift 5.9 made a declaration modifier — the domain word
/// reads better anyway, and `RideKind.package` is still what goes on the wire.
enum BookingSubject: String, CaseIterable {
    case person
    case parcel

    var labelKey: String {
        switch self {
        case .person: return "booking_person"
        case .parcel: return "booking_package"
        }
    }
}

/// Which field the next chosen place belongs to.
///
/// SCR-PI-008 is **one** screen reached from five places — the home sheet, SCR-PI-009's edit row,
/// SCR-PI-010b's Search method, SCR-PI-012's two ends and SCR-PI-013's destination picker — and it
/// has no way of knowing which. A navigation value would carry the coordinate coming *back* but not
/// the intent going *out*, so the intent is parked on the draft: whoever opens the picker says what
/// they opened it for, and ``BookingDraft/capture(_:)`` puts the answer where it belongs.
///
/// `nil` means *"a fresh booking"* — the home sheet's *"Where to?"*, which begins a draft rather
/// than editing one.
enum CaptureTarget {
    case bookingDropoff
    case bookingPickup
    case proxyPickup
    case packagePickup
    case packageDropoff
    case scheduleDropoff
}

/// One booking, as the passenger has filled it in so far.
struct BookingDraftState {

    /// Where it starts. Defaults to the passenger's fix and is editable everywhere.
    var pickup: Place?

    /// Where it ends. SCR-PI-008 sets it; SCR-PI-013 makes it mandatory (AL-36).
    var dropoff: Place?

    var bookingFor: BookingFor = .me
    var subject: BookingSubject = .person

    /// The Mode C tier picked on SCR-PI-009, or `nil` while a public route is selected — a bus is
    /// not a tier and is never booked.
    var vehicleType: RideVehicleType?

    /// The **settlement-time** rail (`fare.yaml`'s `PaymentMethod`), because that is what a
    /// passenger actually chooses and what C098's SCR-PI-016 confirms. `ride.yaml`'s booking-time
    /// enum is narrower and has not caught up with AL-57/AL-59 — see ``PaymentRails/bookingValueOf(_:)``.
    var paymentMethod: PaymentMethod = PaymentMethod.cash

    /// Proxy only (P-01). Blank until SCR-PI-010b.
    var riderName = ""

    /// Proxy only, national form with no `+94`. Hashed at rest server-side (P-03).
    var riderPhone = ""

    var packageSize: PackageSize = PackageSize.s
    var packageDescription = ""
    var recipientName = ""
    var recipientPhone = ""

    /// SCR-PI-012 captures ends of its own: a parcel's destination is the recipient's, not the
    /// sender's, and the recipient can be asked for it (Request).
    var packagePickup: Place?
    var packageDropoff: Place?

    /// What `POST /v1/rides/request` is being asked for.
    ///
    /// Derived rather than stored, because the two pickers are what a passenger actually touches and
    /// a third field holding their product would be a third thing to keep in step. **Package wins
    /// over proxy**: `RideKind` has no `proxy_package` and the contract's own ordering makes a parcel
    /// a package however it was booked — the rider fields still travel, so nothing about who
    /// arranged it is lost.
    var kind: RideKind {
        if subject == .parcel { return RideKind.package }
        if bookingFor == .someoneElse { return RideKind.proxy }
        return RideKind.passenger
    }

    /// Whether SCR-PI-009 can quote at all — a fare needs both ends.
    var isQuotable: Bool { pickup != nil && dropoff != nil }

    /// AL-36's gate on SCR-PI-013: Confirm stays disabled until a destination is set.
    var hasDestination: Bool { dropoff != nil }

    /// Whether SCR-PI-010b has enough to book a proxy ride.
    ///
    /// A name and a number, because the driver's offer carries both (P-05) and the rider is called
    /// on the number — an unnamed rider is a driver arriving at a stranger.
    var isProxyComplete: Bool {
        !riderName.trimmed.isEmpty && !riderPhone.isEmpty
    }

    /// Whether SCR-PI-012 has enough to quote a parcel.
    var isPackageComplete: Bool {
        !packageDescription.trimmed.isEmpty &&
            !recipientName.trimmed.isEmpty &&
            !recipientPhone.isEmpty &&
            packagePickup != nil &&
            packageDropoff != nil
    }
}

/// The booking being assembled, shared by every screen in cluster 3.
///
/// **One instance for the process, and deliberately so.** SCR-PI-008 chooses a destination,
/// SCR-PI-009 picks a tier, SCR-PI-010b adds a rider, SCR-PI-012 adds a parcel, SCR-PI-013 adds a
/// time and C098's SCR-PI-016 changes the payment method — six screens editing **one** booking. The
/// alternatives are both worse: a `NavigationPath` value would serialise a rider's phone number and
/// a package description into the back stack, and a model per screen with no shared home would make
/// *"go back and change the pickup"* lose everything downstream of it.
///
/// Its lifetime is the process's, which is why ``clear()`` exists and is called the moment a booking
/// becomes a ride. A draft left behind is the next booking's stale rider name.
///
/// **Every booking starts on the passenger's default payment rail** (US-22.4). C101's SCR-PI-027
/// stores it and this is what *"pre-selected at booking/checkout"* means in code: a fresh draft — a
/// new one, a ``begin(dropoff:pickup:)`` and a ``clear()`` alike — opens on the stored rail, read
/// **on every fresh draft** rather than captured once, so a change made in Settings applies to the
/// next booking without anything having to be told. Changing it on SCR-PI-009 still changes only
/// that booking, because a preference is not a command.
///
/// **Every booking also starts from where the passenger is** (Δ C097). ``begin(dropoff:pickup:)``
/// falls back to ``LastKnownFix`` when it is given no pickup, and that is a **defect fix rather than
/// a convenience**: on the Android side every production `begin(…)` call site omitted the argument,
/// so `pickup` was null and `RideBookingViewModel.refresh()` returned early on exactly that —
/// SCR-PA-009 showed no routes and no tiers at all. Defaulting inside the draft rather than at the
/// call sites is what stops a fourth one reintroducing it, and both apps now do it here.
@MainActor
final class BookingDraft: ObservableObject {

    @Published private(set) var state = BookingDraftState()

    /// What the place picker was opened for, or `nil` for a fresh booking.
    ///
    /// Plain state rather than published: nothing renders it, and it lives for exactly as long as
    /// one navigation to SCR-PI-008 and back.
    private(set) var pendingCapture: CaptureTarget?

    private let preferences: AppPreferences
    private let lastFix: LastKnownFix

    init(preferences: AppPreferences, lastFix: LastKnownFix) {
        self.preferences = preferences
        self.lastFix = lastFix
        state.paymentMethod = defaultRail
    }

    /// A screen is about to open the picker.
    func expect(_ target: CaptureTarget) {
        pendingCapture = target
    }

    /// The picker answered. Returns whether the place was consumed by a pending capture.
    ///
    /// `false` means nobody was waiting for it, which is the *"Where to?"* case — the caller starts
    /// a new booking with ``begin(dropoff:pickup:)`` instead.
    @discardableResult
    func capture(_ place: Place) -> Bool {
        guard let target = pendingCapture else { return false }
        pendingCapture = nil

        switch target {
        case .bookingDropoff, .scheduleDropoff: state.dropoff = place
        case .bookingPickup, .proxyPickup: state.pickup = place
        case .packagePickup: state.packagePickup = place
        case .packageDropoff: state.packageDropoff = place
        }
        return true
    }

    /// Applies `change`. Every screen edits through here so there is one writer.
    func update(_ change: (inout BookingDraftState) -> Void) {
        change(&state)
    }

    /// Starts a fresh booking from a chosen destination.
    ///
    /// Called by SCR-PI-008 rather than by SCR-PI-009, because choosing a destination is what
    /// *begins* a booking — arriving at the booking screen with the previous attempt's rider still
    /// attached is the bug this prevents.
    ///
    /// - Parameter pickup: Where it starts. **Defaults to the last known fix**, because a booking
    ///   with no pickup cannot be quoted at all — see the type's note and ``LastKnownFix``. A caller
    ///   with a better answer (a proxy rider's shared pin, a parcel's own end) passes one.
    func begin(dropoff: Place, pickup: Place? = nil) {
        pendingCapture = nil
        var fresh = BookingDraftState()
        fresh.dropoff = dropoff
        fresh.pickup = pickup ?? lastFix.asPlace
        fresh.paymentMethod = defaultRail
        state = fresh
    }

    /// Throws the draft away — after a ride is requested, or when the passenger leaves the flow.
    func clear() {
        pendingCapture = nil
        var fresh = BookingDraftState()
        fresh.paymentMethod = defaultRail
        state = fresh
    }

    /// The stored rail, or Cash. A value this build has never heard of reads as Cash rather than as
    /// nothing — see ``AppPreferences/preferredRail``, which C101 made the one door onto it (Δ C101;
    /// this used to spell the same expression here, and the Settings screen would have been a third
    /// copy of it).
    private var defaultRail: PaymentMethod { preferences.preferredRail }
}

extension String {
    /// Trimmed of whitespace and newlines — the check every "is this field filled in" makes.
    var trimmed: String { trimmingCharacters(in: .whitespacesAndNewlines) }
}
