import Foundation
import MageRideShared

@testable import PassengerApp

/// Cluster 7's two seams, faked, plus the fixtures its suite is written against.
///
/// Same rule as ``OnboardingTestKit``, ``HomeTestKit``, ``BookingTestKit``, ``RideTestKit``,
/// ``HistoryTestKit`` and ``SubscriptionTestKit``: **these stand in for Swift protocols, never for
/// Kotlin classes.** ``AddressBook`` and ``SosContacts`` exist precisely because `IamApi` and
/// `QueryApi` are Kotlin interfaces with `suspend` methods that Swift cannot implement.
///
/// The profile half is ``FakePassengerProfileRepository``'s, which cluster 1 already owns and C101
/// extended rather than forking — same argument as `PassengerProfileRepository` itself having one
/// implementation across both clusters.
///
/// **No boxed Kotlin primitive is constructed here.** `SavedAddress.isHome` / `.isWork` are
/// `Boolean?`, and `IosSavedAddressKt.savedAddressWithShortcuts` is how a fixture sets them — the
/// same door the model uses, and the reason `IosSavedAddress.kt` exists.

// MARK: - The address book

final class FakeAddressBook: AddressBook, @unchecked Sendable {

    /// Programmable answers.
    var stored: [SavedAddress] = []

    /// What `describe` answers. `nil` is the geocoder that could not name the coordinate, which is
    /// the case AL-14 turns into a pre-fill that did not happen rather than into an error.
    var reverse: GeocodedPlace?

    /// Thrown by the **next** call to `list`, `add`, `replace` or `remove`, then cleared. One-shot,
    /// so a suite can arm a failure after a successful load. `describe` is deliberately outside it:
    /// that call cannot fail from a caller's point of view.
    var failWith: Error?

    /// Every call, in order.
    ///
    /// Named types rather than tuples, because **Swift has no key paths into tuples** — the C087
    /// finding `apps/driver-ios` records, and `added.map(\.label)` is exactly the expression it
    /// bites. ``SubscriptionTestKit``'s `PaidCall` is the same call.
    private(set) var listCount = 0
    private(set) var added: [SavedAddressInput] = []
    private(set) var replaced: [ReplacedAddress] = []
    private(set) var removed: [String] = []
    private(set) var described: [GeoPoint] = []
    private(set) var idempotencyKeys: [String?] = []

    func list() async throws -> [SavedAddress] {
        try raise()
        listCount += 1
        return stored
    }

    @discardableResult
    func add(_ input: SavedAddressInput, idempotencyKey: String?) async throws -> SavedAddress {
        try raise()
        added.append(input)
        idempotencyKeys.append(idempotencyKey)
        return AddressFixtures.address(addressId: AddressFixtures.newAddressId, from: input)
    }

    @discardableResult
    func replace(addressId: String, input: SavedAddressInput) async throws -> SavedAddress {
        try raise()
        replaced.append(ReplacedAddress(addressId: addressId, input: input))
        return AddressFixtures.address(addressId: addressId, from: input)
    }

    func remove(addressId: String) async throws {
        try raise()
        removed.append(addressId)
        stored.removeAll { $0.addressId == addressId }
    }

    func describe(_ point: GeoPoint) async -> GeocodedPlace? {
        described.append(point)
        return reverse
    }

    private func raise() throws {
        guard let failure = failWith else { return }
        failWith = nil
        throw failure
    }
}

/// One `PUT /v1/me/saved-addresses/{addressId}`, recorded.
struct ReplacedAddress {
    let addressId: String
    let input: SavedAddressInput
}

// MARK: - The SOS list

final class FakeSosContacts: SosContacts, @unchecked Sendable {

    var stored: [EmergencyContact] = []

    /// Thrown by the next call, then cleared.
    var failWith: Error?

    /// Thrown by `list` alone, and **not** cleared — SCR-PI-027b lets the contact read fail on its
    /// own without costing the passenger the name field, and asserting that needs a read that keeps
    /// failing while the profile read succeeds.
    var listFailure: Error?

    private(set) var listCount = 0
    private(set) var added: [SavedContact] = []
    private(set) var replaced: [SavedContact] = []
    private(set) var removed: [String] = []
    private(set) var idempotencyKeys: [String?] = []

    func list() async throws -> [EmergencyContact] {
        if let listFailure { throw listFailure }
        try raise()
        listCount += 1
        return stored
    }

    @discardableResult
    func add(name: String, phone: String, idempotencyKey: String?) async throws -> EmergencyContact {
        try raise()
        added.append(SavedContact(contactId: nil, name: name, phone: phone))
        idempotencyKeys.append(idempotencyKey)
        let contact = AddressFixtures.contact(
            contactId: AddressFixtures.newContactId,
            name: name,
            phone: phone,
            isPrimary: stored.isEmpty
        )
        stored.append(contact)
        return contact
    }

    @discardableResult
    func replace(contactId: String, name: String, phone: String) async throws -> EmergencyContact {
        try raise()
        replaced.append(SavedContact(contactId: contactId, name: name, phone: phone))
        let contact = AddressFixtures.contact(contactId: contactId, name: name, phone: phone)
        stored = stored.map { $0.contactId == contactId ? contact : $0 }
        return contact
    }

    func remove(contactId: String) async throws {
        try raise()
        removed.append(contactId)
        stored.removeAll { $0.contactId == contactId }
        // iam-svc promotes the next contact when the primary goes — which is the whole reason
        // SCR-PI-027b re-reads rather than dropping the row locally. The fake does it too, or the
        // assertion that it re-read would pass against a list that never changed.
        stored = stored.enumerated().map { index, contact in
            AddressFixtures.contact(
                contactId: contact.contactId,
                name: contact.name,
                phone: contact.phone,
                isPrimary: index == 0
            )
        }
    }

    private func raise() throws {
        guard let failure = failWith else { return }
        failWith = nil
        throw failure
    }
}

/// One `POST` or `PUT /v1/me/emergency-contacts`, recorded. `contactId` is `nil` on a create.
///
/// A named type for ``ReplacedAddress``' reason — `added.map(\.phone)` is a key path, and Swift has
/// none into a tuple.
struct SavedContact {
    let contactId: String?
    let name: String
    let phone: String
}

// MARK: -

enum AddressFixtures {

    static let homeId = "01JADR00000000000000000001"
    static let workId = "01JADR00000000000000000002"
    static let gymId = "01JADR00000000000000000003"
    static let newAddressId = "01JADR00000000000000000009"

    static let ammaId = "01JSOS00000000000000000001"
    static let thathaId = "01JSOS00000000000000000002"
    static let newContactId = "01JSOS00000000000000000009"

    /// The wireframe's own two: `221 Galle Rd, Dehiwala` and `WTC, Colombo 01`.
    static let homeLat = 6.8649
    static let homeLng = 79.8997
    static let workLat = 6.9344
    static let workLng = 79.8428

    /// Where the passenger is standing when SCR-PI-026 opens — Colombo 03, a few hundred metres
    /// from neither.
    static let fix = GeoPoint(lat: 6.9021, lng: 79.8560)

    static func address(
        addressId: String,
        label: String = "Gym",
        line1: String = "No. 42, Galle Road",
        line2: String? = "Kollupitiya",
        line3: String? = "Colombo 03",
        lat: Double = 6.9021,
        lng: Double = 79.8560,
        isHome: Bool = false,
        isWork: Bool = false
    ) -> SavedAddress {
        // Through `:shared`, for the reason the file note gives — the two flags are boxed.
        IosSavedAddressKt.savedAddressWithShortcuts(
            address: SavedAddress(
                addressId: addressId,
                label: label,
                line1: line1,
                line2: line2,
                line3: line3,
                lat: lat,
                lng: lng,
                isHome: nil,
                isWork: nil
            ),
            isHome: isHome,
            isWork: isWork
        )
    }

    /// The row a `POST` or `PUT` answers with — the input, as the server stored it.
    static func address(addressId: String, from input: SavedAddressInput) -> SavedAddress {
        address(
            addressId: addressId,
            label: input.label,
            line1: input.line1,
            line2: input.line2,
            line3: input.line3,
            lat: input.lat,
            lng: input.lng,
            isHome: IosSavedAddressKt.savedAddressInputIsHome(input: input),
            isWork: IosSavedAddressKt.savedAddressInputIsWork(input: input)
        )
    }

    static func home() -> SavedAddress {
        address(
            addressId: homeId,
            label: "Home",
            line1: "221 Galle Rd",
            line2: nil,
            line3: "Dehiwala",
            lat: homeLat,
            lng: homeLng,
            isHome: true
        )
    }

    static func work() -> SavedAddress {
        address(
            addressId: workId,
            label: "Work",
            line1: "WTC",
            line2: nil,
            line3: "Colombo 01",
            lat: workLat,
            lng: workLng,
            isWork: true
        )
    }

    static func gym() -> SavedAddress {
        address(addressId: gymId)
    }

    static func contact(
        contactId: String,
        name: String = "Amma",
        phone: String = "+94770001111",
        isPrimary: Bool = true
    ) -> EmergencyContact {
        EmergencyContact(contactId: contactId, isPrimary: isPrimary, name: name, phone: phone)
    }

    /// What `GET /v1/geo/reverse` answers for the pin — a street and a city and nothing between
    /// them, which is exactly the shape AL-26's line 2 has no field for.
    static func reverse(line1: String? = "No. 42, Galle Road", city: String? = "Colombo 03") -> GeocodedPlace {
        GeocodedPlace(
            lat: fix.lat,
            lng: fix.lng,
            displayName: "No. 42, Galle Road, Kollupitiya, Colombo 03",
            line1: line1,
            city: city,
            source: GeocodedPlaceSource.nominatim
        )
    }
}
