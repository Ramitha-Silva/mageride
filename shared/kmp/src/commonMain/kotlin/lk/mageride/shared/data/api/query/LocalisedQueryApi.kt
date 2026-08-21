package lk.mageride.shared.data.api.query

import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.query.PlaceSearchResponse
import kotlin.coroutines.cancellation.CancellationException

/**
 * The language a geocode should be read in, as the app that has one supplies it.
 *
 * D-26 makes every user-facing string trilingual, and a place name a passenger reads on a
 * destination list is one. Where that answer comes from is the app's business — SCR-PA-002 /
 * SCR-DA-002 store it and `attachBaseContext` applies it — so `:shared` takes a supplier rather
 * than a value: the graph is a `single` and outlives the `recreate()` a language change performs,
 * so a snapshot taken at start-up would be the language the app opened in for the rest of the
 * process.
 *
 * Binding one is **optional**. An app that does not is answered exactly as it was before this
 * existed: no `lang`, and OSM's own `name`.
 */
public fun interface AppLanguage {

    /** The language in force now, or `null` before one has been chosen. */
    public fun current(): Language?
}

/**
 * Fills in [AppLanguage] on the two geocoding calls, and changes nothing else.
 *
 * **A decorator rather than a parameter at each call site**, which is the same argument
 * `TrackerPositionPublisher` makes on the driver app: five doors reach the geocoder in the
 * passenger app alone — SCR-PA-008's destination field, SCR-PA-009's map picker, the pasted-link
 * resolver, the address book and the recents re-label — and a rule written at one of them is a
 * rule missing from the other four. A sixth screen gets it without knowing it exists.
 *
 * An explicit `lang` still wins. Nothing passes one today; the parameter is kept honest so a
 * caller that genuinely wants a fixed language — an operator tool reading a Sinhala address in
 * English, say — is not fighting the decorator to get it.
 *
 * **Only the Koin binding is wrapped.** `MageRideApi.query` is still the bare client, because
 * `MageRideApi` is the transport graph and knows nothing about a person's preferences; inject
 * `QueryApi` and you get this, reach through `MageRideApi` and you get the raw one.
 */
internal class LocalisedQueryApi(private val delegate: QueryApi, private val language: AppLanguage?) :
    QueryApi by delegate {

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun searchPlaces(
        query: String,
        lat: Double?,
        lng: Double?,
        limit: Int?,
        lang: Language?,
    ): PlaceSearchResponse = delegate.searchPlaces(query, lat, lng, limit, lang ?: language?.current())

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun reverseGeocode(lat: Double, lng: Double, lang: Language?): GeocodedPlace =
        delegate.reverseGeocode(lat, lng, lang ?: language?.current())
}
