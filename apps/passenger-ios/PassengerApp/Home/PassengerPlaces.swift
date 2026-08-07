import Foundation
import MageRideShared

/// The two reads a destination is chosen from: the geocoder, and the passenger's own saved places.
///
/// **One seam over two services, because they answer one question.** SCR-PI-008's empty state is
/// *"recents/saved"* and its typed state is `GET /v1/geo/search`, whose own `GeocodedPlace.source`
/// already blends `saved` and `recent` rows in with the geocoded ones — so from a screen's point of
/// view there is one list of places and query-svc versus iam-svc is an implementation detail. The
/// **third** source, §2.2's local table, is ``RecentPlaces``: it is a read of the *handset* rather
/// than of the platform, and nothing about it goes over a wire.
///
/// A Swift protocol rather than `QueryApi`/`IamApi` themselves, for the reason ``NearbySnapshots``
/// gives: both are Kotlin interfaces with `suspend` methods, and Swift can stand in for neither. Two
/// methods the app declares are testable with a handful of lines and cannot drift.
protocol PassengerPlaces: AnyObject {

    /// `GET /v1/geo/search` — forward geocoding, biased toward the passenger when their position is
    /// known.
    ///
    /// **Geo only (AL-17).** There is no route lookup on this protocol and there is not meant to be:
    /// `QueryApi.getBusesOnRoute` exists and SCR-PI-008 must never reach it. See
    /// ``SearchLocationModel``.
    func search(_ text: String, around: GeoPoint?, limit: Int) async throws -> [GeocodedPlace]

    /// `GET /v1/me/saved-addresses` — US-7.13's ★ Home / ★ Work chips, and SCR-PI-008's empty state.
    func savedAddresses() async throws -> [SavedAddress]
}

/// ``PassengerPlaces`` over the real clients.
///
/// The search goes through `IosGeoSearchKt` rather than straight at `QueryApi`, and that helper's
/// own note says why: three of `searchPlaces`'s four parameters are **optional primitives**, which
/// cross the bridge boxed, and a Kotlin default argument does not survive the export at all. The
/// saved-address read has neither problem, so it is the plain call it looks like.
final class ApiPassengerPlaces: PassengerPlaces {

    private let query: QueryApi
    private let iam: IamApi

    init(query: QueryApi, iam: IamApi) {
        self.query = query
        self.iam = iam
    }

    func search(_ text: String, around: GeoPoint?, limit: Int) async throws -> [GeocodedPlace] {
        try await IosGeoSearchKt.searchPlacesNear(
            query: query,
            text: text,
            around: around,
            limit: Int32(limit)
        )
    }

    func savedAddresses() async throws -> [SavedAddress] {
        try await iam.listSavedAddresses().items
    }
}

extension GeocodedPlace {

    /// A stable identity for a `ForEach` — the coordinate.
    ///
    /// A geocoded place has no id of its own: Nominatim answers no stable one across queries, which
    /// is exactly why §2.2's row id is derived from the coordinate too (see `IosPlaceRecents.kt`).
    /// Keying a list on the display name would collide the moment two junctions share one.
    var rowId: String { "\(lat),\(lng)" }
}

extension SavedAddress {

    /// The address as the one currency every destination hand-off in this app deals in.
    ///
    /// A shortcut, a recent and a prediction are the same thing to whatever comes next — a place
    /// with a coordinate — and `GeocodedPlace` is what `GET /v1/geo/search` already answers with, so
    /// this is a conversion into the shape rather than a third one. `line3` is the city on this
    /// contract's own address rows.
    var asPlace: GeocodedPlace {
        GeocodedPlace(
            lat: lat,
            lng: lng,
            displayName: label,
            line1: line1,
            city: line3,
            source: GeocodedPlaceSource.saved
        )
    }
}
