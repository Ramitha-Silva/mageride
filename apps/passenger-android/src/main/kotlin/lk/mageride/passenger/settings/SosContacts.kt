package lk.mageride.passenger.settings

import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.models.PhoneE164
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.iam.EmergencyContactInput

/**
 * `iam.emergency_contacts` — SCR-PA-027b's *"SOS / emergency contacts"* list (AL-13, US-12.1).
 *
 * **A list here, one contact on the driver side, and that is the wireframes' own difference.**
 * SCR-DA-029 draws a single row and the driver app updates it in place; SCR-PA-027b draws a list
 * under a `＋ Add SOS contact` button, so this seam carries all four operations.
 *
 * **What the list feeds is a refusal.** `POST /v1/sos` answers `400 no-emergency-contact` when the
 * account has none, so the contact added here is what makes C084's SCR-PA-029 work at all — and
 * `EmergencyContact.isPrimary` marks the one iam-svc denormalises onto
 * `iam.users.emergency_contact_name/phone`, because D-33's budget is p99 ≤ 5 s and a join is not in
 * it. **The app never sets `isPrimary`**: the contract has no field for it on the input, and the
 * server promotes the first contact and re-promotes on a delete.
 */
internal interface SosContacts {

    /** `GET /v1/me/emergency-contacts` — primary first. */
    suspend fun list(): List<EmergencyContact>

    /** `POST /v1/me/emergency-contacts` — the `＋ Add SOS contact` button. */
    suspend fun add(name: String, phone: PhoneE164, idempotencyKey: String? = null): EmergencyContact

    /** `PUT /v1/me/emergency-contacts/{contactId}` — the ✎ on a row. Full replacement. */
    suspend fun replace(contactId: Ulid, name: String, phone: PhoneE164): EmergencyContact

    /**
     * `DELETE /v1/me/emergency-contacts/{contactId}`.
     *
     * Deleting the last one puts `POST /v1/sos` back to `400 no-emergency-contact`, which is why
     * SCR-PA-027b says so where the list is empty rather than only where the alarm is raised.
     */
    suspend fun remove(contactId: Ulid)
}

/** [SosContacts] over C013's generated iam-svc client. */
internal class ApiSosContacts(private val iam: IamApi) : SosContacts {

    override suspend fun list(): List<EmergencyContact> = iam.listEmergencyContacts().items

    override suspend fun add(name: String, phone: PhoneE164, idempotencyKey: String?): EmergencyContact =
        iam.createEmergencyContact(EmergencyContactInput(name = name.trim(), phone = phone), idempotencyKey)

    override suspend fun replace(contactId: Ulid, name: String, phone: PhoneE164): EmergencyContact =
        iam.updateEmergencyContact(contactId, EmergencyContactInput(name = name.trim(), phone = phone))

    override suspend fun remove(contactId: Ulid) {
        iam.deleteEmergencyContact(contactId)
    }
}
