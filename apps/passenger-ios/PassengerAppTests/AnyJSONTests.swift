import MageRideShared
import XCTest

@testable import PassengerApp

/// The identity binding, checked on the shapes `signalr-hub.md` §3 actually sends.
///
/// **This type is the one place a hub payload can be silently corrupted.** Everything above it
/// decodes with `:shared`'s own `Json` against the contract's models; everything below it is the
/// client's `JSONDecoder`. If ``AnyJSON`` re-serialises `1` as `1.0`, `VehicleFrame.heading` — an
/// `Int?` — stops decoding and the map loses MAP-06's arrows with no error anywhere.
final class AnyJSONTests: XCTestCase {

    /// **An integer stays an integer.** This is the whole reason the type is hand-written rather
    /// than `JSONSerialization` or a `[String: Any]`: `JSONDecoder` reads `90` happily as a `Double`
    /// and re-encoding a `Double` writes `90`… or `90.0`, depending on the value, and kotlinx
    /// refuses the second for an `Int` field.
    func testAnIntegerSurvivesTheRoundTrip() throws {
        let text = try roundTrip(#"{"heading":90,"speed":8.5,"count":0,"big":9007199254740993}"#)

        XCTAssertTrue(text.contains("\"heading\":90"), "heading became a float: \(text)")
        XCTAssertTrue(text.contains("\"count\":0"))
        XCTAssertTrue(text.contains("\"big\":9007199254740993"), "a 64-bit integer lost precision: \(text)")
        XCTAssertTrue(text.contains("8.5"))
    }

    /// A negative number, a zero and a fraction — every numeric shape a position payload carries.
    func testNegativeAndFractionalNumbersSurvive() throws {
        let text = try roundTrip(#"{"lat":6.9344,"lng":-79.8428,"delta":-3}"#)

        XCTAssertTrue(text.contains("6.9344"))
        XCTAssertTrue(text.contains("-79.8428"))
        XCTAssertTrue(text.contains("\"delta\":-3"))
    }

    /// `VehiclePositions` is a top-level **array**, which is the shape a `JSONSerialization`-based
    /// implementation would have had to special-case.
    func testATopLevelArraySurvives() throws {
        let source = #"[{"vehicleId":"V1","type":"three_wheeler"},{"vehicleId":"V2","type":"bus"}]"#
        let text = try roundTrip(source)

        XCTAssertTrue(text.hasPrefix("["))
        XCTAssertTrue(text.contains("three_wheeler"))
        XCTAssertTrue(text.contains("V2"))
    }

    func testNullsBooleansAndNestingSurvive() throws {
        let text = try roundTrip(#"{"heading":null,"live":true,"at":{"lat":1,"tags":["a","b"]}}"#)

        XCTAssertTrue(text.contains("\"heading\":null"))
        XCTAssertTrue(text.contains("\"live\":true"))
        XCTAssertTrue(text.contains("\"lat\":1"))
        XCTAssertTrue(text.contains("\"a\""))
    }

    /// A URL inside a payload must come back as the string the server sent, not as one with `\/` in
    /// it. Still valid JSON either way; this keeps a logged payload readable and a compared string
    /// equal.
    func testASlashIsNotEscaped() throws {
        let text = try roundTrip(#"{"url":"https://tiles.mageride.lk/x.pmtiles"}"#)

        XCTAssertTrue(text.contains("https://tiles.mageride.lk/x.pmtiles"), text)
        XCTAssertFalse(text.contains("\\/"))
    }

    /// The whole point: what comes out of this type is something `:shared`'s decoders accept. A
    /// `VehicleFrame` batch is the shape that matters, and `three_wheeler` is the value that broke
    /// the equivalent binding on Android.
    func testTheRoundTrippedTextStillDecodesAsAVehicleFrame() throws {
        let source = """
        [{"vehicleId":"V1","lat":6.9344,"lng":79.8428,"heading":90,"speed":8.5,\
        "type":"three_wheeler","mode":"C"}]
        """
        let text = try roundTrip(source)
        let frames = IosLiveHubPayloadsKt.decodeVehicleFrames(json: text)

        XCTAssertEqual(frames?.count, 1)
        XCTAssertEqual(frames?.first?.vehicleId, "V1")
        XCTAssertEqual(frames?.first?.heading?.int32Value, 90)
        XCTAssertEqual(frames?.first?.type?.wire, "three_wheeler")
    }

    // MARK: -

    private func roundTrip(_ json: String) throws -> String {
        let value = try JSONDecoder().decode(AnyJSON.self, from: Data(json.utf8))
        return try XCTUnwrap(value.jsonText)
    }
}
