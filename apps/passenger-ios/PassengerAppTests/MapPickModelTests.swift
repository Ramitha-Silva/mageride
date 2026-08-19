import Foundation
import MageRideShared
import XCTest

@testable import PassengerApp

/// The Map capture method's search box, and the one rule that makes it honest.
///
/// **A search result names the pin; it does not become the pin.** Everything below is a consequence
/// of that: tapping a result moves the camera and lends its name, nudging the map takes the name
/// back, and what is committed is always the coordinates under the marker the passenger is looking
/// at. A picker that committed the geocoder's point instead would move a pickup after it had been
/// placed — on SCR-PI-010b, somebody else's pickup.
final class MapPickModelTests: XCTestCase {

    private var places: FakePassengerPlaces!

    @MainActor
    override func setUp() {
        super.setUp()
        places = FakePassengerPlaces()
        places.searchResults = [MapPickFixtures.fort, MapPickFixtures.pettah]
    }

    @MainActor
    func testTheLookupIsDebouncedAndBiasedTowardsThePin() async {
        // Nominatim is self-hosted (D-14) and shared with every passenger in the country, so a
        // request per keystroke turns a search box into a load test — the same reason SCR-PI-008
        // debounces. The bias is the pin rather than the passenger: this sheet is routinely used to
        // place somebody ELSE's pickup, and the map is already where the booker was looking.
        let model = MapPickModel(places: places)
        model.opened(around: MapPickFixtures.colombo)

        model.onQueryChanged("Sta")
        model.onQueryChanged("Stat")
        model.onQueryChanged("Station")
        await eventually("results") { await MainActor.run { !model.state.predictions.isEmpty } }
        try? await Task.sleep(nanoseconds: MapPickFixtures.settle)

        XCTAssertEqual(places.searches.count, 1, "one request for the word, not one per letter")
        XCTAssertEqual(places.searches.last?.text, "Station")
        XCTAssertEqual(places.searches.last?.around?.lat, MapPickFixtures.colombo.lat)
    }

    @MainActor
    func testTwoCharactersSpendNoRequestAtAll() async {
        let model = MapPickModel(places: places)
        model.opened(around: MapPickFixtures.colombo)

        model.onQueryChanged("Fo")
        try? await Task.sleep(nanoseconds: MapPickFixtures.settle)

        XCTAssertTrue(model.state.showingDefaults)
        XCTAssertTrue(model.state.predictions.isEmpty)
        XCTAssertTrue(places.searches.isEmpty, "nothing went out for two characters")
    }

    @MainActor
    func testChoosingAResultFliesThePinToItAndLendsItTheName() async {
        // The camera move is the whole point of the search: without `focus` a result has nowhere to
        // go, and the passenger still has to pan across the city by hand.
        let model = MapPickModel(places: places)
        model.opened(around: MapPickFixtures.colombo)

        model.onPredictionChosen(MapPickFixtures.fort)

        XCTAssertEqual(model.state.focus?.lat, MapPickFixtures.fort.lat, "the map is asked to move")
        XCTAssertEqual(model.state.chosen?.displayName, MapPickFixtures.fort.displayName)
        XCTAssertTrue(model.state.predictions.isEmpty, "the list closes over the map it just moved")
    }

    @MainActor
    func testTheCameraSettlingOnTheResultKeepsItsName() {
        // `onCameraIdle` fires straight after the animation, a metre or two off what was asked for.
        // Without the tolerance the name would be dropped by the very move that fetched it.
        let model = MapPickModel(places: places)
        model.opened(around: MapPickFixtures.colombo)
        model.onPredictionChosen(MapPickFixtures.fort)

        model.onPinMoved(GeoPoint(lat: MapPickFixtures.fort.lat + 0.00005, lng: MapPickFixtures.fort.lng))

        XCTAssertEqual(model.state.chosen?.displayName, MapPickFixtures.fort.displayName)
    }

    @MainActor
    func testPanningOffANamedPlaceGivesTheCoordinatesBack() {
        // The honest half of the rule. The pin is no longer on Colombo Fort, so calling it Colombo
        // Fort would put a name on a point that is not it — and the label the booker reads, and the
        // address that reaches SCR-PI-009, would both be wrong.
        let model = MapPickModel(places: places)
        model.opened(around: MapPickFixtures.colombo)
        model.onPredictionChosen(MapPickFixtures.fort)

        let moved = GeoPoint(lat: 6.8480, lng: 79.9265)
        model.onPinMoved(moved)

        XCTAssertNil(model.state.chosen, "a pin two towns away is not that place")
        XCTAssertNil(model.state.focus, "and the camera request goes with it, so the same result can move it again")
        XCTAssertEqual(model.state.centre?.lat, moved.lat)
        XCTAssertNil(model.state.selection?.address)
    }

    @MainActor
    func testWhatIsCommittedIsWhereThePinIsUnderTheNameItWasGiven() {
        // Both halves of `selection` in one assertion: the coordinates are the marker's, the name is
        // the search result's. Nudging inside the tolerance keeps the name and still commits the
        // nudged point — the passenger aimed there.
        let model = MapPickModel(places: places)
        model.opened(around: MapPickFixtures.colombo)
        model.onPredictionChosen(MapPickFixtures.fort)
        let nudged = GeoPoint(lat: MapPickFixtures.fort.lat + 0.0001, lng: MapPickFixtures.fort.lng)
        model.onPinMoved(nudged)

        let selection = model.state.selection

        XCTAssertEqual(selection?.address, MapPickFixtures.fort.displayName)
        XCTAssertEqual(selection?.lat, nudged.lat)
        XCTAssertEqual(selection?.lng, nudged.lng)
    }

    @MainActor
    func testAGeocoderThatCannotAnswerLeavesThePinWorking() async {
        // AL-14 makes the same call about a reverse geocode: a lookup that fails costs a
        // convenience, never the capture. The passenger dropped the pin where they meant to.
        places.searchFailure = HomeFakeError.unreachable
        let model = MapPickModel(places: places)
        model.opened(around: MapPickFixtures.colombo)

        model.onQueryChanged("Station")
        await eventually("the failure lands") { await MainActor.run { model.state.geocoderDown } }

        XCTAssertFalse(model.state.searching)
        XCTAssertEqual(model.state.selection?.lat, MapPickFixtures.colombo.lat, "the pin is untouched")
    }

    @MainActor
    func testOpeningTheSheetAgainStartsFromTheFieldItWasOpenedFor() async {
        // One sheet serves SCR-PI-010b's pickup and both of SCR-PI-012's ends. A query left in the
        // field from the last capture would be somebody else's search, and a stale `chosen` would
        // name this pin after the last one.
        let model = MapPickModel(places: places)
        model.opened(around: MapPickFixtures.colombo)
        model.onQueryChanged("Station")
        await eventually("results") { await MainActor.run { !model.state.predictions.isEmpty } }
        model.onPredictionChosen(MapPickFixtures.fort)

        let elsewhere = GeoPoint(lat: 6.8480, lng: 79.9265)
        model.opened(around: elsewhere)

        XCTAssertEqual(model.state.query, "")
        XCTAssertTrue(model.state.predictions.isEmpty)
        XCTAssertNil(model.state.chosen)
        XCTAssertEqual(model.state.centre?.lat, elsewhere.lat)
    }
}

/// This suite's places. `HomeFixtures` owns the map's; these are the two a search answers with.
enum MapPickFixtures {

    static let colombo = GeoPoint(lat: 6.9271, lng: 79.8612)

    /// Comfortably past the 300 ms debounce, so a second request would have been seen.
    static let settle: UInt64 = 600_000_000

    static let fort = GeocodedPlace(
        lat: 6.9344,
        lng: 79.8428,
        displayName: "Colombo Fort",
        line1: "Olcott Mawatha",
        city: "Colombo",
        source: GeocodedPlaceSource.nominatim
    )

    static let pettah = GeocodedPlace(
        lat: 6.9355,
        lng: 79.8500,
        displayName: "Pettah",
        line1: nil,
        city: "Colombo",
        source: GeocodedPlaceSource.nominatim
    )
}
