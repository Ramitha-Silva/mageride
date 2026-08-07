import Foundation

/// Any JSON value, decoded without knowing its shape and re-serialised without losing it.
///
/// **This is Gson's `JsonElement`, which Swift has no equivalent of.** The Android transport binds
/// each hub argument as a `JsonElement` — the identity binding, which cannot be wrong — and hands the
/// text up for `:shared`'s `Json` to decode, because the socket and the REST surface must share one
/// set of models (`signalr-hub.md` §3) and the client's own binder spells enums differently from
/// `@SerialName`. `SignalR-Client-Swift` binds through `Decodable`, so the identity binding has to be
/// written; this is it.
///
/// **Numbers are the whole difficulty and the reason this is not `[String: Any]` + `JSONSerialization`.**
/// `JSONDecoder` will happily read `1` as a `Double`, and re-encoding a `Double` writes `1.0` — which
/// `kotlinx.serialization` then refuses for `VehicleFrame.heading`, an `Int?`. So an integer is
/// decoded as an integer and stays one. `speed` (a `Double?`) arriving as `12` is decoded as
/// `Int64(12)` and re-encoded as `12`, which kotlinx reads back as a `Double` without complaint —
/// JSON has one number type and the *receiving* schema is what decides.
///
/// Round-tripping through this type is lossless for every shape `signalr-hub.md` §3 declares. Key
/// **order** is not preserved (`[String: AnyJSON]` is unordered), which no JSON parser cares about.
indirect enum AnyJSON: Codable, Equatable {
    case null
    case bool(Bool)
    case integer(Int64)
    case number(Double)
    case string(String)
    case array([AnyJSON])
    case object([String: AnyJSON])

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()

        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Int64.self) {
            // Before `Double`, deliberately — see this type's documentation.
            self = .integer(value)
        } else if let value = try? container.decode(Double.self) {
            self = .number(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode([AnyJSON].self) {
            self = .array(value)
        } else if let value = try? container.decode([String: AnyJSON].self) {
            self = .object(value)
        } else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "not JSON")
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case .bool(let value): try container.encode(value)
        case .integer(let value): try container.encode(value)
        case .number(let value): try container.encode(value)
        case .string(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .object(let value): try container.encode(value)
        }
    }

    /// This value as JSON text, or `nil` if it cannot be written.
    ///
    /// `.withoutEscapingSlashes` because a URL inside a payload — `LocationRequestResolved` carries
    /// none today, but `RideStateChanged` is a shape a contract change could add one to — would
    /// otherwise come back with `\/` and be a different string from the one the server sent. It is
    /// still valid JSON either way; this keeps a logged payload readable.
    ///
    /// **A fragment has to be allowed.** `VehiclePositions` is a top-level *array*, which
    /// `JSONEncoder` handles, but a hub argument could in principle be a bare string or number and
    /// `JSONSerialization` would refuse one. `JSONEncoder` has no such restriction, which is the
    /// second reason this is `Codable` rather than `JSONSerialization`.
    var jsonText: String? {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        guard let data = try? encoder.encode(self) else { return nil }
        return String(data: data, encoding: .utf8)
    }
}
