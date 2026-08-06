package lk.mageride.passenger.settings

import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.SavedAddress
import lk.mageride.shared.data.models.iam.SavedAddressInput
import lk.mageride.shared.data.models.query.GeocodedPlace

/**
 * `iam.saved_addresses` as SCR-PA-026 uses it, plus the one geocoder call the screen needs.
 *
 * **Two services behind one seam, because the screen is one gesture.** AL-14 is *"OSM-pin +
 * reverse-geocode"*: dropping the pin and naming what is under it are halves of the same action,
 * and a screen that had to hold one client for the address book and another for the geocoder would
 * be a screen deciding which of them a failed save belongs to. `GET /v1/geo/reverse` is
 * query-svc's and everything else is iam-svc's; [describe] is the only method that leaves iam.
 *
 * **The server is the source of truth and there is no local mirror.** `mobile_db_schema.md` §2.1
 * declares an on-device `saved_addresses` table with `dirty`/`synced_at` columns, and nothing in
 * this app writes it — C078's shortcuts read `GET /v1/me/saved-addresses` directly, and so does
 * this screen. Adding a mirror here would make two writers for one list with no outbox between
 * them, which is worse than a round trip on a screen a passenger opens twice a year. Recorded in
 * the C083 handoff; US-22.6 already puts the list in the **eager-fetch** set, which is a
 * `GET /v1/me/bootstrap` concern rather than a table this screen owns.
 */
internal interface AddressBook {

    /** `GET /v1/me/saved-addresses` — Home and Work first, then everything else. */
    suspend fun list(): List<SavedAddress>

    /** `POST /v1/me/saved-addresses` — one new address (US-22.2). */
    suspend fun add(input: SavedAddressInput, idempotencyKey: String? = null): SavedAddress

    /**
     * `PUT /v1/me/saved-addresses/{addressId}` — a full replacement (US-22.3).
     *
     * The contract replaces the whole row, so the caller sends every field it wants kept —
     * including the Home/Work flag, which moving to another address clears from this one.
     */
    suspend fun replace(addressId: Ulid, input: SavedAddressInput): SavedAddress

    /** `DELETE /v1/me/saved-addresses/{addressId}` — hard delete (US-22.3). */
    suspend fun remove(addressId: Ulid)

    /**
     * `GET /v1/geo/reverse` — what is under the pin (AL-14).
     *
     * **A failure answers `null` rather than throwing**, and that is the difference between the
     * geocoder and the address book. Nominatim answers `404` for a coordinate in the sea and `503`
     * when it is unreachable, and neither is a reason to refuse to save an address: the passenger
     * dropped the pin where they meant to and can type the three lines themselves. What the lookup
     * buys is a pre-filled form, so its absence costs a pre-fill and nothing else.
     */
    suspend fun describe(point: GeoPoint): GeocodedPlace?
}

/** [AddressBook] over C013's generated iam-svc and query-svc clients. */
internal class ApiAddressBook(private val iam: IamApi, private val query: QueryApi) : AddressBook {

    override suspend fun list(): List<SavedAddress> = iam.listSavedAddresses().items

    override suspend fun add(input: SavedAddressInput, idempotencyKey: String?): SavedAddress =
        iam.createSavedAddress(input, idempotencyKey)

    override suspend fun replace(addressId: Ulid, input: SavedAddressInput): SavedAddress =
        iam.updateSavedAddress(addressId, input)

    override suspend fun remove(addressId: Ulid) {
        iam.deleteSavedAddress(addressId)
    }

    @Suppress("TooGenericExceptionCaught")
    override suspend fun describe(point: GeoPoint): GeocodedPlace? = try {
        query.reverseGeocode(lat = point.lat, lng = point.lng)
    } catch (cause: kotlinx.coroutines.CancellationException) {
        throw cause
    } catch (_: Throwable) {
        // See the interface KDoc: a coordinate the geocoder cannot name is still a coordinate the
        // passenger can save.
        null
    }
}
