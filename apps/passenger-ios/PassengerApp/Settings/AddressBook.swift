import Foundation
import MageRideShared

/// `iam.saved_addresses` as SCR-PI-026 uses it, plus the one geocoder call the screen needs.
///
/// **Two services behind one seam, because the screen is one gesture.** AL-14 is *"OSM-pin +
/// reverse-geocode"*: dropping the pin and naming what is under it are halves of the same action,
/// and a screen holding one client for the address book and another for the geocoder would be a
/// screen deciding which of them a failed save belongs to. `GET /v1/geo/reverse` is query-svc's and
/// everything else is iam-svc's; ``describe(_:)`` is the only method that leaves iam.
///
/// **`GET /v1/me/saved-addresses` is read here *and* by ``PassengerPlaces/savedAddresses()``, and
/// that is deliberate** — the same split `apps/passenger-android` makes, where `home/` calls `IamApi`
/// directly and only `settings/` has an `AddressBook`. They are two questions: SCR-PI-008 and
/// SCR-PI-010 want a *destination chooser's* shortcut list and take `GeocodedPlace`s
/// (``SavedAddress/asPlace``), and this wants the address book itself — rows with ids, which is what
/// an edit and a delete need. Neither writes what the other reads, and both re-read on appear.
///
/// **The server is the source of truth and there is no local mirror.** `mobile_db_schema.md` §2.1
/// declares an on-device `saved_addresses` table with `dirty`/`synced_at` columns and nothing in
/// either app writes it. Adding a mirror here would make two writers for one list with no outbox
/// between them, which is worse than a round trip on a screen a passenger opens twice a year.
/// US-22.6 already puts the list in the **eager-fetch** set, which is a `GET /v1/me/bootstrap`
/// concern rather than a table this screen owns. C083 recorded it and this restates it.
///
/// A Swift protocol rather than `IamApi`/`QueryApi` themselves, for ``PassengerPlaces``' reason:
/// both are Kotlin interfaces with `suspend` methods and Swift can stand in for neither.
protocol AddressBook: AnyObject {

    /// `GET /v1/me/saved-addresses` — Home and Work first, then everything else.
    func list() async throws -> [SavedAddress]

    /// `POST /v1/me/saved-addresses` — one new address (US-22.2).
    @discardableResult
    func add(_ input: SavedAddressInput, idempotencyKey: String?) async throws -> SavedAddress

    /// `PUT /v1/me/saved-addresses/{addressId}` — a full replacement (US-22.3).
    ///
    /// The contract replaces the whole row, so the caller sends every field it wants kept —
    /// including the Home/Work flag, which moving to another address clears from this one.
    @discardableResult
    func replace(addressId: String, input: SavedAddressInput) async throws -> SavedAddress

    /// `DELETE /v1/me/saved-addresses/{addressId}` — hard delete (US-22.3).
    func remove(addressId: String) async throws

    /// `GET /v1/geo/reverse` — what is under the pin (AL-14).
    ///
    /// **A failure answers `nil` rather than throwing**, and that is the difference between the
    /// geocoder and the address book. Nominatim answers `404` for a coordinate in the sea and `503`
    /// when it is unreachable, and neither is a reason to refuse to save an address: the passenger
    /// dropped the pin where they meant to and can type the three lines themselves. What the lookup
    /// buys is a pre-filled form, so its absence costs a pre-fill and nothing else.
    func describe(_ point: GeoPoint) async -> GeocodedPlace?
}

/// ``AddressBook`` over C013's generated iam-svc and query-svc clients.
final class ApiAddressBook: AddressBook {

    private let iam: IamApi
    private let query: QueryApi

    init(iam: IamApi, query: QueryApi) {
        self.iam = iam
        self.query = query
    }

    func list() async throws -> [SavedAddress] {
        try await iam.listSavedAddresses().items
    }

    @discardableResult
    func add(_ input: SavedAddressInput, idempotencyKey: String?) async throws -> SavedAddress {
        // Both arguments at every call site: a Kotlin default does not survive the Objective-C
        // export, so `idempotencyKey` has no default here however the contract declares it.
        try await iam.createSavedAddress(request: input, idempotencyKey: idempotencyKey)
    }

    @discardableResult
    func replace(addressId: String, input: SavedAddressInput) async throws -> SavedAddress {
        try await iam.updateSavedAddress(addressId: addressId, request: input)
    }

    func remove(addressId: String) async throws {
        try await iam.deleteSavedAddress(addressId: addressId)
    }

    /// See the protocol's note: a coordinate the geocoder cannot name is still a coordinate the
    /// passenger can save.
    ///
    /// `try?` rather than a `catch` that re-throws `CancellationError`: this is the last statement of
    /// the call and there is nothing after it to cancel — the Swift counterpart of the Android
    /// implementation's explicit `CancellationException` re-throw is simply that the task that owns
    /// this await is the one being cancelled, and it checks for itself at the next suspension point.
    func describe(_ point: GeoPoint) async -> GeocodedPlace? {
        try? await query.reverseGeocode(lat: point.lat, lng: point.lng, lang: nil)
    }
}

extension SavedAddress {

    /// `221 Galle Rd, Dehiwala` — the stored lines, on one line.
    ///
    /// The three AL-26 lines joined by the wireframe's own `, `, empties dropped. A row is one line
    /// of supporting text; the whole address is what SCR-PI-026a shows, field by field.
    var oneLine: String {
        [line1, line2, line3]
            .compactMap { $0 }
            .filter { !$0.trimmed.isEmpty }
            .joined(separator: AddressFormat.lineSeparator)
    }
}

/// The punctuation between two address lines.
///
/// **Data, not copy** — a comma and a space are the same characters in all three scripts, so putting
/// them in the three `.strings` files would be three identical values, which is exactly what
/// `LocalizationTests` fails on. The same call ``Coordinates`` and ``MageRideSymbols`` make.
enum AddressFormat {
    static let lineSeparator = ", "
}
