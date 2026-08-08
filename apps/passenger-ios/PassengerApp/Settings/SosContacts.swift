import Foundation
import MageRideShared

/// `iam.emergency_contacts` — SCR-PI-027b's *"SOS / emergency contacts"* list (AL-13, US-12.1).
///
/// **A list here, one contact on the driver side, and that is the wireframes' own difference.**
/// SCR-DI-029 draws a single row and `apps/driver-ios` updates it in place; SCR-PI-027b draws a list
/// under `＋ Add SOS contact`, so this seam carries all four operations.
///
/// **What the list feeds is a refusal.** `POST /v1/sos` answers `400 no-emergency-contact` when the
/// account has none, so the contact added here is what makes C102's SCR-PI-029 work at all — and
/// `EmergencyContact.isPrimary` marks the one iam-svc denormalises onto
/// `iam.users.emergency_contact_name/phone`, because D-33's budget is p99 ≤ 5 s and a join is not in
/// it. **The app never sets `isPrimary`**: `EmergencyContactInput` has no field for it, the server
/// promotes the first contact and re-promotes on a delete — which is why a removal here **re-reads**
/// the list rather than dropping the row locally.
protocol SosContacts: AnyObject {

    /// `GET /v1/me/emergency-contacts` — primary first.
    func list() async throws -> [EmergencyContact]

    /// `POST /v1/me/emergency-contacts` — the `＋ Add SOS contact` row.
    @discardableResult
    func add(name: String, phone: String, idempotencyKey: String?) async throws -> EmergencyContact

    /// `PUT /v1/me/emergency-contacts/{contactId}` — the ✎ on a row. Full replacement.
    @discardableResult
    func replace(contactId: String, name: String, phone: String) async throws -> EmergencyContact

    /// `DELETE /v1/me/emergency-contacts/{contactId}`.
    ///
    /// Deleting the last one puts `POST /v1/sos` back to `400 no-emergency-contact`, which is why
    /// SCR-PI-027b says so where the list is empty rather than only where the alarm is raised.
    func remove(contactId: String) async throws
}

/// ``SosContacts`` over C013's generated iam-svc client.
final class ApiSosContacts: SosContacts {

    private let iam: IamApi

    init(iam: IamApi) {
        self.iam = iam
    }

    func list() async throws -> [EmergencyContact] {
        try await iam.listEmergencyContacts().items
    }

    @discardableResult
    func add(name: String, phone: String, idempotencyKey: String?) async throws -> EmergencyContact {
        try await iam.createEmergencyContact(
            request: EmergencyContactInput(name: name.trimmed, phone: phone),
            idempotencyKey: idempotencyKey
        )
    }

    @discardableResult
    func replace(contactId: String, name: String, phone: String) async throws -> EmergencyContact {
        try await iam.updateEmergencyContact(
            contactId: contactId,
            request: EmergencyContactInput(name: name.trimmed, phone: phone)
        )
    }

    func remove(contactId: String) async throws {
        try await iam.deleteEmergencyContact(contactId: contactId)
    }
}
