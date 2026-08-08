import Foundation
import MageRideShared

/// Which of the two shortcuts an address is, if either (AL-26, US-22.1).
///
/// **A flag pair on the wire and one value here.** `SavedAddress` carries `isHome` and `isWork`
/// independently, which admits a row that is somehow both; the server's partial unique indexes allow
/// at most one of each per user, but nothing stops one row setting both. Collapsing them into an
/// enum is what makes *"Home, Work or neither"* the only shape a screen can express.
///
/// **The flags are read through `:shared`, not off the field.** Both are `Boolean?` and therefore
/// boxed — see `IosSavedAddress.kt`, which is where this cluster's whole answer to that lives.
enum AddressShortcut: Equatable {
    case home
    case work
    case none

    /// What `address` is. Home wins if a row somehow claims both — it is the row drawn first.
    static func of(_ address: SavedAddress) -> AddressShortcut {
        if IosSavedAddressKt.savedAddressIsHome(address: address) { return .home }
        if IosSavedAddressKt.savedAddressIsWork(address: address) { return .work }
        return .none
    }

    /// The row's title key, for the two that have one. A labelled address uses its own label.
    var titleKey: String? {
        switch self {
        case .home: return "addresses_home"
        case .work: return "addresses_work"
        case .none: return nil
        }
    }
}

/// SCR-PI-026a — the save-address sheet, as it is being filled in.
///
/// The wireframe's four fields and nothing else (AL-26's fence): Address Line 1, Address Line 2,
/// Address Line 3 and a free-text Label. ``shortcut`` carries no control of its own — it is decided
/// by *which row was tapped* on the screen behind the sheet, so a passenger editing Home never has
/// to be told they are.
struct AddressSheetState: Equatable {

    /// Where the pin was when the sheet opened. The sheet does not move it — the map is behind the
    /// scrim — so it is a value here rather than a stream.
    let lat: Double
    let lng: Double

    /// The row being replaced, or `nil` when this is a new address.
    var addressId: String?

    var shortcut: AddressShortcut = .none
    var label = ""
    var line1 = ""
    var line2 = ""
    var line3 = ""

    /// Whether the reverse geocode is still in flight. The fields are editable throughout; the
    /// lookup only fills in what is still empty.
    var isLocating = false

    var isSaving = false

    /// *"Save address"* is live once the row can be identified and found.
    ///
    /// `label` and `line1` are the contract's two required fields, and trimming is what stops a row
    /// of spaces passing an `isEmpty` check on the way to a `400`.
    var canSave: Bool { !isSaving && !label.trimmed.isEmpty && !line1.trimmed.isEmpty }

    /// An existing row offers Delete; a new one has nothing to delete (US-22.3).
    var isEditing: Bool { addressId != nil }

    /// The pin, printed. See ``Coordinates`` for why the digits are not copy.
    var pinned: String { Coordinates.format(lat: lat, lng: lng) }
}

/// SCR-PI-026's state.
struct SavedAddressesState {

    var addresses: [SavedAddress] = []

    /// Where the map's fixed centre marker is. `nil` until the first fix or the first camera settle
    /// — the map opens on its default centre and the `＋` waits for one.
    var pin: GeoPoint?

    var isLoading = true

    /// SCR-PI-026a, or `nil` when it is closed.
    var sheet: AddressSheetState?

    /// The row whose delete is in flight.
    var busyWith: String?

    var errorKey: String?

    /// The ★ Home row's address. `nil` renders the wireframe's row with *"Not set"* under it.
    var home: SavedAddress? { addresses.first { AddressShortcut.of($0) == .home } }

    /// The ★ Work row's.
    var work: SavedAddress? { addresses.first { AddressShortcut.of($0) == .work } }

    /// Everything else — the 📍 rows, in the order the server sent them.
    /// `AddressShortcut.none` spelled out: a bare `.none` beside an optional would resolve to
    /// `Optional.none`, and this enum having a case by that name is a trap worth naming once.
    var labelled: [SavedAddress] { addresses.filter { AddressShortcut.of($0) == AddressShortcut.none } }

    /// Whether the `＋` can do anything yet. There is nowhere to put an address until the map has
    /// settled somewhere, which is one camera-idle away on every device and immediate on one that
    /// already has a fix.
    var canAdd: Bool { pin != nil }
}

/// SCR-PI-026 and SCR-PI-026a — the passenger's address book.
///
/// **Home and Work are rows, not a mode.** The wireframe draws them first and always, above the
/// labelled addresses, and its states line says *"Home & Work via OSM pin"* while drawing no control
/// for it: the row's own `›` is that control. Tapping it opens the same sheet the navigation bar's
/// `＋` does, with the shortcut already decided and the label pre-filled — which is why nothing in
/// SCR-PI-026a has to ask *"is this your Home?"*, and why AL-26's four fields are all the sheet has.
///
/// **The pin is the map's centre, not a marker the passenger drags.** MapLibre's
/// draggable-annotation API is not in the distribution this app links, so the map moves under a
/// fixed pin — the same centre-pin picker ``MapPickSheet`` and SCR-PI-011 use, and the reason
/// ``MageRideMap`` has an `onCameraIdle` at all. The cell's caption says *"Drop / drag pin"*; what
/// is dragged is the map.
///
/// **A reverse geocode is a pre-fill, never a gate** (AL-14). `GET /v1/geo/reverse` answers `404` in
/// the sea and `503` when Nominatim is down, and neither stops a passenger saving the place they
/// just pinned: ``AddressBook/describe(_:)`` answers `nil`, the three lines open empty, and the
/// keyboard is already up.
@MainActor
final class SavedAddressesModel: ObservableObject {

    @Published private(set) var state = SavedAddressesState()

    private let addresses: AddressBook
    private let lastFix: LastKnownFix
    private let keys: IdempotencyKeys

    /// **One fix, taken rather than subscribed to.** The pin is where the passenger is *when the
    /// screen opens*; following the handset afterwards would drag the map out from under a thumb
    /// that had just positioned it — which is what the Android twin achieves by collecting exactly
    /// one value off the fix flow. ``LastKnownFix`` is this platform's answer (Δ C097) and costs no
    /// second subscription at all; the alternative here would be the fifth subscriber to a cold
    /// `CLLocationManager`.
    init(addresses: AddressBook, lastFix: LastKnownFix, keys: IdempotencyKeys) {
        self.addresses = addresses
        self.lastFix = lastFix
        self.keys = keys
        state.pin = lastFix.point
    }

    /// Reads the list. What `.task` and `.refreshable` both call.
    func refresh() async {
        state.isLoading = true
        state.errorKey = nil
        // A fix that landed after the screen was constructed still opens the map in the right place.
        if state.pin == nil { state.pin = lastFix.point }

        do {
            state.addresses = try await addresses.list()
            state.isLoading = false
        } catch is CancellationError {
            return
        } catch {
            state.isLoading = false
            state.errorKey = SettingsErrors.messageKey(for: error)
        }
    }

    func clearError() {
        state.errorKey = nil
    }

    /// The map settled somewhere.
    func onPinMoved(_ point: GeoPoint) {
        state.pin = point
    }

    /// The navigation bar's `＋` — a new, unlabelled address at the pin.
    func addAddress() async {
        await openSheet(existing: nil, shortcut: .none, defaultLabel: "")
    }

    /// The Home or Work row (US-22.1).
    ///
    /// An existing shortcut is **edited where it is** — its own coordinates, not the pin — because
    /// tapping a row that already has an address is a request to correct that address, and moving it
    /// to wherever the map happens to be centred would be a different action behind the same
    /// affordance. A shortcut with no address yet takes the pin, which is *"Home & Work via OSM
    /// pin"*.
    func editShortcut(_ shortcut: AddressShortcut) async {
        let existing: SavedAddress?
        switch shortcut {
        case .home: existing = state.home
        case .work: existing = state.work
        case .none: existing = nil
        }
        await openSheet(
            existing: existing,
            shortcut: shortcut,
            defaultLabel: shortcut.titleKey?.localised ?? ""
        )
    }

    /// A labelled row.
    func edit(_ address: SavedAddress) async {
        await openSheet(existing: address, shortcut: AddressShortcut.of(address), defaultLabel: address.label)
    }

    func dismissSheet() {
        state.sheet = nil
    }

    func onLabelChanged(_ value: String) { state.sheet?.label = value }

    func onLine1Changed(_ value: String) { state.sheet?.line1 = value }

    func onLine2Changed(_ value: String) { state.sheet?.line2 = value }

    func onLine3Changed(_ value: String) { state.sheet?.line3 = value }

    /// *"Save address"* — `POST` for a new row, `PUT` for one being edited (US-22.2/22.3).
    ///
    /// **All three lines and the label travel, empty ones as `nil`** — the trimming and the
    /// blank-to-absent rule are `savedAddressInputOf`'s, next to the contract that declares them.
    func save() async {
        guard let sheet = state.sheet, sheet.canSave else { return }

        let input = IosSavedAddressKt.savedAddressInputOf(
            label: sheet.label.trimmed,
            line1: sheet.line1.trimmed,
            line2: sheet.line2.trimmed,
            line3: sheet.line3.trimmed,
            lat: sheet.lat,
            lng: sheet.lng,
            isHome: sheet.shortcut == .home,
            isWork: sheet.shortcut == .work
        )

        state.sheet?.isSaving = true
        state.errorKey = nil

        let saved: SavedAddress
        do {
            if let addressId = sheet.addressId {
                saved = try await addresses.replace(addressId: addressId, input: input)
            } else {
                saved = try await addresses.add(input, idempotencyKey: keys.next())
            }
        } catch is CancellationError {
            state.sheet?.isSaving = false
            return
        } catch {
            state.sheet?.isSaving = false
            state.errorKey = SettingsErrors.messageKey(for: error)
            return
        }

        state.sheet = nil
        state.addresses = SavedAddressesModel.merge(saved, into: state.addresses)
    }

    /// Deletes the address being edited (US-22.3).
    ///
    /// From inside the sheet, because that is the only place either wireframe leaves for it:
    /// SCR-PI-026 draws a `›` per row and no ✕, and its own states line says *"edit/delete"*.
    /// Reaching delete through edit also means the passenger has just been shown which address it
    /// is.
    func delete() async {
        guard let addressId = state.sheet?.addressId else { return }

        state.sheet = nil
        state.busyWith = addressId
        state.errorKey = nil

        do {
            try await addresses.remove(addressId: addressId)
        } catch is CancellationError {
            state.busyWith = nil
            return
        } catch {
            state.busyWith = nil
            state.errorKey = SettingsErrors.messageKey(for: error)
            return
        }

        state.addresses.removeAll { $0.addressId == addressId }
        state.busyWith = nil
    }

    // MARK: -

    /// Opens SCR-PI-026a and runs the lookup behind it.
    ///
    /// The sheet is put on screen **before** the `await`, which is what makes this one function
    /// rather than two: `@Published` has already published by the time the geocode is in flight, so
    /// the passenger is typing into the form while Nominatim is being asked.
    ///
    /// An existing row opens on its own stored lines and skips the geocoder entirely: it has been
    /// named already, and overwriting a passenger's own wording with Nominatim's is the opposite of
    /// what an edit is for.
    private func openSheet(existing: SavedAddress?, shortcut: AddressShortcut, defaultLabel: String) async {
        let lat = existing?.lat ?? state.pin?.lat
        let lng = existing?.lng ?? state.pin?.lng
        guard let lat, let lng else { return }

        state.errorKey = nil
        state.sheet = AddressSheetState(
            lat: lat,
            lng: lng,
            addressId: existing?.addressId,
            shortcut: shortcut,
            label: existing?.label ?? defaultLabel,
            line1: existing?.line1 ?? "",
            line2: existing?.line2 ?? "",
            line3: existing?.line3 ?? "",
            isLocating: existing == nil
        )

        guard existing == nil else { return }
        await describe(GeoPoint(lat: lat, lng: lng))
    }

    /// Fills the empty lines in from `GET /v1/geo/reverse`.
    ///
    /// `line1` and `city` are the two fields `GeocodedPlace` carries, and they are AL-26's first and
    /// **third** lines — street then city — because line 2 is *"area/suburb"* and Nominatim's answer
    /// has no such field. Guessing one by splitting `displayName` on commas would fill a form with
    /// an answer nobody checked; the passenger is looking at the sheet and can type it.
    ///
    /// Nothing already typed is overwritten: the lookup is slower than the keyboard, and a field
    /// that rewrote itself under a thumb would be worse than an empty one. The sheet is re-checked
    /// on the way back for the same reason — a passenger who dismissed it and opened another row
    /// must not have that one's fields filled in from this answer.
    private func describe(_ point: GeoPoint) async {
        let place = await addresses.describe(point)

        guard let sheet = state.sheet, sheet.lat == point.lat, sheet.lng == point.lng else { return }
        state.sheet?.isLocating = false
        if sheet.line1.isEmpty { state.sheet?.line1 = place?.line1 ?? "" }
        if sheet.line3.isEmpty { state.sheet?.line3 = place?.city ?? "" }
    }

    /// Puts `saved` into the list, replacing whatever it replaced.
    ///
    /// **Also clears the shortcut off whichever row used to hold it**, because that is what the
    /// server just did: moving the Home or Work flag to this address clears it from whichever
    /// address held it. Re-reading the list would learn the same thing at the cost of a round trip,
    /// and would leave two Home rows on screen until it came back.
    ///
    /// `static` so a test can put the rule under a microscope without a model, and because it is a
    /// function of its two arguments and of nothing else.
    static func merge(_ saved: SavedAddress, into current: [SavedAddress]) -> [SavedAddress] {
        let position = current.firstIndex { $0.addressId == saved.addressId }
        let savedIsHome = IosSavedAddressKt.savedAddressIsHome(address: saved)
        let savedIsWork = IosSavedAddressKt.savedAddressIsWork(address: saved)

        let others = current
            .filter { $0.addressId != saved.addressId }
            .map { address -> SavedAddress in
                let losesHome = savedIsHome && AddressShortcut.of(address) == .home
                let losesWork = savedIsWork && AddressShortcut.of(address) == .work
                guard losesHome || losesWork else { return address }
                // The `copy` is Kotlin's — see `IosSavedAddress.kt`. Rebuilding the row field by
                // field on this side would be nine chances to drop one.
                return IosSavedAddressKt.savedAddressWithShortcuts(
                    address: address,
                    isHome: !losesHome && AddressShortcut.of(address) == .home,
                    isWork: !losesWork && AddressShortcut.of(address) == .work
                )
            }

        // An edit keeps its position in a list the server ordered; a new address joins the end.
        guard let position else { return others + [saved] }
        return Array(others.prefix(position)) + [saved] + Array(others.dropFirst(position))
    }
}
