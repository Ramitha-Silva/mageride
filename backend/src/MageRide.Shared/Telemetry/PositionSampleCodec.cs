using System.Buffers;
using System.Formats.Cbor;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MageRide.Shared.Telemetry;

/// <summary>
/// The wire codec for <see cref="PositionSample"/> — CBOR, with JSON accepted on the way in
/// (<c>backend/contracts/realtime/mqtt-topics.md</c> §2.1, D6' §2.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>CBOR is the wire form, and it is not a different model.</b> The KMP module encodes the same
/// field names through <c>kotlinx.serialization.cbor</c> (<c>PositionCodec</c>, C017) because a
/// metered mobile link should not carry JSON five times a second. This is the server side of that
/// one format.
/// </para>
/// <para>
/// <b>JSON is accepted but never produced.</b> Two things publish positions that are not the driver
/// app: a developer with <c>mosquitto_pub</c>, and any future producer written against ADD
/// Appendix A, which prints JSON. Both are worth reading rather than dropping — and the sniff is
/// unambiguous, because a CBOR map begins <c>0xA0…0xBF</c> and JSON's <c>{</c> is <c>0x7B</c>, a
/// CBOR text-string head that a <see cref="PositionSample"/> can never start with.
/// </para>
/// <para>
/// <b>Unknown keys are skipped, missing optional keys are null.</b> The platform is versioned but
/// additive (the KMP codec sets <c>ignoreUnknownKeys</c> for the same reason), so a field a newer
/// producer starts emitting must not take an older consumer down. What is <i>not</i> tolerated is a
/// malformed value: a string where a latitude belongs is a bug to surface, not to coerce.
/// </para>
/// </remarks>
public static class PositionSampleCodec
{
    // The field names are the contract. They are spelled once here and nowhere else, because the
    // KMP encoder derives its own from the Kotlin property names and a typo on this side would show
    // up as a silently absent field rather than as an error.
    private const string FieldVehicleId = "vehicleId";
    private const string FieldSampleTs = "sampleTs";
    private const string FieldReceivedTs = "receivedTs";
    private const string FieldSeq = "seq";
    private const string FieldLat = "lat";
    private const string FieldLng = "lng";
    private const string FieldSpeedMps = "speedMps";
    private const string FieldHeadingDeg = "headingDeg";
    private const string FieldAccuracyM = "accuracyM";
    private const string FieldHdop = "hdop";
    private const string FieldSatCount = "satCount";
    private const string FieldSource = "source";
    private const string FieldMode = "mode";
    private const string FieldVehicleType = "vehicleType";
    private const string FieldFleetId = "fleetId";
    private const string FieldTripId = "tripId";

    /// <summary>
    /// JSON settings for the telemetry plane. Deliberately <b>not</b>
    /// <see cref="Http.MageRideJson.Options"/>: that instance carries a
    /// <see cref="JsonStringEnumConverter"/> which would render <c>source</c> as <c>"mobile"</c>,
    /// and both the wire contract and <c>ck_positions_source</c> want the integer <c>0…4</c>.
    /// Converters registered on the options win over a <c>[JsonConverter]</c> on the type, so the
    /// only way to keep the number is to keep this plane's options separate.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict,
    };

    /// <summary>The MQTT <c>Content-Type</c> a well-behaved publisher sets on a position payload.</summary>
    public const string CborContentType = "application/cbor";

    /// <summary>Encodes a sample as the CBOR the device plane and <c>telemetry.normalized</c> carry.</summary>
    /// <remarks>
    /// A definite-length map, where <c>kotlinx.serialization</c> writes an indefinite-length one.
    /// Both are valid CBOR and each side reads the other; definite is written here because it is
    /// what <see cref="CborWriter"/> produces naturally and it is a byte shorter.
    /// </remarks>
    public static byte[] Encode(PositionSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var writer = new CborWriter(CborConformanceMode.Lax);
        writer.WriteStartMap(definiteLength: null);

        WriteText(writer, FieldVehicleId, sample.VehicleId.ToString());
        WriteText(writer, FieldSampleTs, FormatInstant(sample.SampleTs));

        writer.WriteTextString(FieldSeq);
        writer.WriteInt64(sample.Seq);

        writer.WriteTextString(FieldLat);
        writer.WriteDouble(sample.Lat);

        writer.WriteTextString(FieldLng);
        writer.WriteDouble(sample.Lng);

        writer.WriteTextString(FieldSource);
        writer.WriteInt32((int)sample.Source);

        if (sample.ReceivedTs is { } receivedTs)
        {
            WriteText(writer, FieldReceivedTs, FormatInstant(receivedTs));
        }

        WriteOptionalDouble(writer, FieldSpeedMps, sample.SpeedMps);
        WriteOptionalInt(writer, FieldHeadingDeg, sample.HeadingDeg);
        WriteOptionalDouble(writer, FieldAccuracyM, sample.AccuracyM);
        WriteOptionalDouble(writer, FieldHdop, sample.Hdop);
        WriteOptionalInt(writer, FieldSatCount, sample.SatCount);
        WriteOptionalText(writer, FieldMode, sample.Mode);
        WriteOptionalText(writer, FieldVehicleType, sample.VehicleType);
        WriteOptionalText(writer, FieldFleetId, sample.FleetId?.ToString());
        WriteOptionalText(writer, FieldTripId, sample.TripId?.ToString());

        writer.WriteEndMap();
        return writer.Encode();
    }

    /// <summary>
    /// Decodes a payload — CBOR, or JSON when the bytes begin with <c>{</c>. Throws on anything
    /// that is neither.
    /// </summary>
    public static PositionSample Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new FormatException("A position payload cannot be empty.");
        }

        return LooksLikeJson(payload) ? DecodeJson(payload) : DecodeCbor(payload);
    }

    /// <summary>
    /// <see cref="Decode"/>, answering <see langword="null"/> rather than throwing.
    /// </summary>
    /// <remarks>
    /// This is what the hot path uses: a malformed sample from one misbehaving handset must be
    /// dropped and counted, not allowed to stall the partition every other vehicle shares.
    /// </remarks>
    public static PositionSample? TryDecode(ReadOnlySpan<byte> payload)
    {
        try
        {
            return Decode(payload);
        }
        catch (Exception ex) when (ex is FormatException or CborContentException or JsonException
                                       or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Whether the payload is JSON rather than CBOR.</summary>
    /// <remarks>
    /// <c>0x7B</c> is <c>{</c>. In CBOR it is major type 3 (text string) with an immediate length of
    /// 27 — a bare 27-character string, which is never a position sample. Leading whitespace is
    /// skipped because a JSON payload pasted from a shell may carry some.
    /// </remarks>
    private static bool LooksLikeJson(ReadOnlySpan<byte> payload)
    {
        foreach (var b in payload)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return b == (byte)'{';
        }

        return false;
    }

    private static PositionSample DecodeJson(ReadOnlySpan<byte> payload)
    {
        var dto = JsonSerializer.Deserialize<PositionSampleJson>(payload, Json)
            ?? throw new FormatException("A position payload cannot be JSON null.");

        return dto.ToSample();
    }

    private static PositionSample DecodeCbor(ReadOnlySpan<byte> payload)
    {
        // Lax: a device encoder is not required to sort its map keys canonically, and a stricter
        // mode would reject a perfectly readable payload from a tracker nobody controls.
        var reader = new CborReader(new ReadOnlyMemory<byte>(payload.ToArray()), CborConformanceMode.Lax);

        if (reader.PeekState() != CborReaderState.StartMap)
        {
            throw new FormatException($"A position payload must be a CBOR map, not {reader.PeekState()}.");
        }

        var length = reader.ReadStartMap();
        var fields = new PositionSampleJson();
        var read = 0;

        while (reader.PeekState() != CborReaderState.EndMap && (length is null || read < length))
        {
            read++;

            if (reader.PeekState() != CborReaderState.TextString)
            {
                // A non-text key is not ours. Skip the pair rather than failing the whole sample.
                reader.SkipValue();
                reader.SkipValue();
                continue;
            }

            var key = reader.ReadTextString();

            if (reader.PeekState() is CborReaderState.Null)
            {
                reader.ReadNull();
                continue;
            }

            switch (key)
            {
                case FieldVehicleId: fields.VehicleId = reader.ReadTextString(); break;
                case FieldSampleTs: fields.SampleTs = reader.ReadTextString(); break;
                case FieldReceivedTs: fields.ReceivedTs = reader.ReadTextString(); break;
                case FieldSeq: fields.Seq = ReadInt64(reader); break;
                case FieldLat: fields.Lat = ReadNumber(reader); break;
                case FieldLng: fields.Lng = ReadNumber(reader); break;
                case FieldSpeedMps: fields.SpeedMps = ReadNumber(reader); break;
                case FieldHeadingDeg: fields.HeadingDeg = (int)ReadInt64(reader); break;
                case FieldAccuracyM: fields.AccuracyM = ReadNumber(reader); break;
                case FieldHdop: fields.Hdop = ReadNumber(reader); break;
                case FieldSatCount: fields.SatCount = (int)ReadInt64(reader); break;
                case FieldSource: fields.Source = (int)ReadInt64(reader); break;
                case FieldMode: fields.Mode = reader.ReadTextString(); break;
                case FieldVehicleType: fields.VehicleType = reader.ReadTextString(); break;
                case FieldFleetId: fields.FleetId = reader.ReadTextString(); break;
                case FieldTripId: fields.TripId = reader.ReadTextString(); break;
                default: reader.SkipValue(); break;
            }
        }

        reader.ReadEndMap();
        return fields.ToSample();
    }

    private static long ReadInt64(CborReader reader) => reader.PeekState() switch
    {
        CborReaderState.UnsignedInteger => (long)reader.ReadUInt64(),
        CborReaderState.NegativeInteger => reader.ReadInt64(),
        // A tracker that encodes a whole number as a float is common enough to be worth reading.
        CborReaderState.HalfPrecisionFloat or CborReaderState.SinglePrecisionFloat
            or CborReaderState.DoublePrecisionFloat => checked((long)ReadNumber(reader)),
        var state => throw new FormatException($"Expected a CBOR integer, found {state}."),
    };

    private static double ReadNumber(CborReader reader) => reader.PeekState() switch
    {
        CborReaderState.UnsignedInteger => reader.ReadUInt64(),
        CborReaderState.NegativeInteger => reader.ReadInt64(),
        CborReaderState.HalfPrecisionFloat => (double)reader.ReadHalf(),
        CborReaderState.SinglePrecisionFloat => reader.ReadSingle(),
        CborReaderState.DoublePrecisionFloat => reader.ReadDouble(),
        var state => throw new FormatException($"Expected a CBOR number, found {state}."),
    };

    private static void WriteText(CborWriter writer, string key, string value)
    {
        writer.WriteTextString(key);
        writer.WriteTextString(value);
    }

    private static void WriteOptionalText(CborWriter writer, string key, string? value)
    {
        // Absent, not null: the encoder on the other side omits defaults too, and a CBOR null costs
        // a byte to say nothing.
        if (!string.IsNullOrEmpty(value))
        {
            WriteText(writer, key, value);
        }
    }

    private static void WriteOptionalDouble(CborWriter writer, string key, double? value)
    {
        if (value is { } number)
        {
            writer.WriteTextString(key);
            writer.WriteDouble(number);
        }
    }

    private static void WriteOptionalInt(CborWriter writer, string key, int? value)
    {
        if (value is { } number)
        {
            writer.WriteTextString(key);
            writer.WriteInt32(number);
        }
    }

    private static string FormatInstant(DateTimeOffset instant) =>
        // ISO-8601 UTC with an explicit Z, which is what the contract prints and what
        // kotlin.time.Instant.parse reads back without ambiguity.
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseInstant(string value, string field) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : throw new FormatException($"'{field}' is not an ISO-8601 instant: '{value}'.");

    /// <summary>
    /// The loose intermediate both decoders fill, so CBOR and JSON share one set of required-field
    /// checks rather than each inventing its own.
    /// </summary>
    private sealed class PositionSampleJson
    {
        public string? VehicleId { get; set; }

        public string? SampleTs { get; set; }

        public string? ReceivedTs { get; set; }

        public long? Seq { get; set; }

        public double? Lat { get; set; }

        public double? Lng { get; set; }

        public double? SpeedMps { get; set; }

        public int? HeadingDeg { get; set; }

        public double? AccuracyM { get; set; }

        public double? Hdop { get; set; }

        public int? SatCount { get; set; }

        public int? Source { get; set; }

        public string? Mode { get; set; }

        public string? VehicleType { get; set; }

        public string? FleetId { get; set; }

        public string? TripId { get; set; }

        public PositionSample ToSample()
        {
            var vehicleId = ParseGuid(VehicleId, FieldVehicleId)
                ?? throw new FormatException($"'{FieldVehicleId}' is required on a position sample.");

            var sampleTs = SampleTs is null
                ? throw new FormatException($"'{FieldSampleTs}' is required on a position sample.")
                : ParseInstant(SampleTs, FieldSampleTs);

            return new PositionSample(
                vehicleId,
                sampleTs,
                Seq ?? throw new FormatException($"'{FieldSeq}' is required — it is the replay dedupe key (R-17)."),
                Lat ?? throw new FormatException($"'{FieldLat}' is required on a position sample."),
                Lng ?? throw new FormatException($"'{FieldLng}' is required on a position sample."),
                (PositionSource)(Source ?? throw new FormatException($"'{FieldSource}' is required (0…4).")),
                ReceivedTs is null ? null : ParseInstant(ReceivedTs, FieldReceivedTs),
                SpeedMps,
                HeadingDeg,
                AccuracyM,
                Hdop,
                SatCount,
                Mode,
                VehicleType,
                ParseGuid(FleetId, FieldFleetId),
                ParseGuid(TripId, FieldTripId));
        }

        private static Guid? ParseGuid(string? value, string field)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            return Guid.TryParse(value, out var parsed)
                ? parsed
                : throw new FormatException($"'{field}' is not a UUID: '{value}'.");
        }
    }
}
