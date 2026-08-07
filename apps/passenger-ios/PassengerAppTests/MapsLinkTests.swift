import XCTest

@testable import PassengerApp

/// AL-20's paste, parsed on the device.
///
/// **The precedence is the interesting part**, not the regexes. A `/maps/place/…` URL carries both a
/// place pin (`!3d!4d`) and a viewport (`@lat,lng`), and they are routinely a hundred metres apart
/// because the map was panned before the link was shared — so preferring the viewport would drop a
/// pickup on whatever happened to be in the middle of the sender's screen.
///
/// The corpus is `apps/passenger-android/.../booking/MapsLinkTest.kt`'s, so the two parsers are held
/// to the same URLs.
final class MapsLinkTests: XCTestCase {

    private let placeLat = 6.9271
    private let placeLng = 79.8612

    /// A full URL never touches the network. That is the whole of AL-20's *"the device parses"*.
    func testAPlacePinIsReadWithNoNetworkAtAll() {
        let url = "https://www.google.com/maps/place/Colombo+Fort/@6.9000,79.8000,15z/data=!3m1!4b1!4d79.8612!3d6.9271"

        guard case .resolved(let lat, let lng) = MapsLink.parse(url) else {
            return XCTFail("a full URL resolves on the device")
        }
        XCTAssertEqual(lat, placeLat, accuracy: 0.00001)
        XCTAssertEqual(lng, placeLng, accuracy: 0.00001)
    }

    /// **The one that matters.** The same URL carries `@6.9000,79.8000` and the pin is 3 km away;
    /// the pin wins.
    func testThePlacePinBeatsTheViewport() {
        let url = "https://maps.google.com/maps/place/X/@6.9000,79.8000,17z/data=!4m5!3m4!3d6.9271!4d79.8612"

        XCTAssertEqual(MapsLink.parse(url), .resolved(lat: placeLat, lng: placeLng))
    }

    /// `!4d` can precede `!3d` in a real URL, so each is matched on its own.
    func testTheTwoHalvesOfAPlacePinNeedNotBeInOrder() {
        XCTAssertEqual(
            MapsLink.parse("https://google.com/maps/data=!4d79.8612!3m1!3d6.9271"),
            .resolved(lat: placeLat, lng: placeLng)
        )
    }

    func testAQueryParameterIsRead() {
        for url in [
            "https://www.google.com/maps?q=6.9271,79.8612",
            "https://www.google.com/maps?query=6.9271,79.8612",
            "https://maps.google.com/?q=loc:6.9271,79.8612",
            "https://www.google.com/maps?daddr=6.9271%2C79.8612",
        ] {
            XCTAssertEqual(MapsLink.parse(url), .resolved(lat: placeLat, lng: placeLng), url)
        }
    }

    /// `q=` outranks `@` for the place pin's reason: it is an explicit request for a point.
    func testAQueryParameterBeatsTheViewport() {
        XCTAssertEqual(
            MapsLink.parse("https://www.google.com/maps/@6.9000,79.8000,15z?q=6.9271,79.8612"),
            .resolved(lat: placeLat, lng: placeLng)
        )
    }

    /// The viewport is the last thing tried, and it is still an answer when it is the only one.
    func testTheViewportIsUsedWhenNothingElseIsThere() {
        XCTAssertEqual(
            MapsLink.parse("https://www.google.com/maps/@6.9271,79.8612,15z"),
            .resolved(lat: placeLat, lng: placeLng)
        )
    }

    /// A search term is not a coordinate, and falling through to the server is the right answer for
    /// a short link rather than a parse failure.
    func testAQueryThatIsNotACoordinateFallsThrough() {
        XCTAssertEqual(MapsLink.parse("https://www.google.com/maps?q=Colombo+Fort"), .unreadable)
    }

    /// Only the two short hosts reach transit-svc (D6' §I-23.1), and the URL travels intact.
    func testAShortLinkGoesToTheServerAndNothingElseDoes() {
        XCTAssertEqual(
            MapsLink.parse("https://maps.app.goo.gl/xK7vQ2"),
            .needsServer(url: "https://maps.app.goo.gl/xK7vQ2")
        )
        XCTAssertEqual(
            MapsLink.parse("https://goo.gl/maps/abc123"),
            .needsServer(url: "https://goo.gl/maps/abc123")
        )
    }

    /// The host check runs **before** the coordinate patterns, so a bare pair — or another mapping
    /// site that happens to embed an `@lat,lng` — is *"couldn't read that link"* rather than a
    /// silently accepted pin.
    func testSomethingThatIsNotAGoogleLinkIsNeverAPin() {
        for input in [
            "6.9271,79.8612",
            "https://www.openstreetmap.org/#map=17/6.9271/79.8612",
            "https://example.com/maps/@6.9271,79.8612,15z",
            "",
            "   ",
            "have you seen this place",
        ] {
            XCTAssertEqual(MapsLink.parse(input), .unreadable, input)
        }
    }

    /// Null Island is what a malformed URL degrades to far more often than it is what somebody
    /// meant, and it is not a pickup point.
    func testNullIslandIsNotAPickupPoint() {
        XCTAssertEqual(MapsLink.parse("https://www.google.com/maps?q=0,0"), .unreadable)
        XCTAssertEqual(MapsLink.parse("https://www.google.com/maps?q=0.0,0.0"), .unreadable)
    }

    /// Out of range is a parse failure; **outside the operating cities is not**. A link to Kandy or
    /// to London reads perfectly — whether the platform serves it is `400 unserviceable-area` on the
    /// fare estimate, in copy the platform owns.
    func testAnOutOfRangePairIsRefusedAndADistantOneIsNot() {
        XCTAssertEqual(MapsLink.parse("https://www.google.com/maps?q=95.0,79.8"), .unreadable)
        XCTAssertEqual(
            MapsLink.parse("https://www.google.com/maps?q=51.5074,-0.1278"),
            .resolved(lat: 51.5074, lng: -0.1278)
        )
    }

    /// Country domains are covered by the `google.` prefix rather than by a list nobody maintains.
    func testACountryDomainIsStillGoogle() {
        XCTAssertEqual(
            MapsLink.parse("https://www.google.lk/maps?q=6.9271,79.8612"),
            .resolved(lat: placeLat, lng: placeLng)
        )
    }
}
