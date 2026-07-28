using System.Formats.Cbor;
using System.Text;
using MageRide.Shared.Telemetry;

namespace MageRide.Shared.Tests.Telemetry;

/// <summary>
/// The wire codec for <c>pos/live</c>, <c>pos/replay</c> and <c>telemetry.normalized</c>
/// (<c>backend/contracts/realtime/mqtt-topics.md</c> §2.1, D6' §2.2).
/// </summary>
/// <remarks>
/// A round trip through this codec proves nothing on its own — it would pass just as happily with
/// the wrong field names. So the CBOR tests assert against <b>bytes written by hand</b> in the shape
/// <c>kotlinx.serialization.cbor</c> produces, including its indefinite-length map, because that is
/// what the driver app actually puts on the wire.
/// </remarks>
public sealed class PositionSampleCodecTests
{
    private static readonly Guid Vehicle = Guid.Parse("2f9d6b2c-0f4f-4a1e-9c3a-8f1d5b7e6a40");
    private static readonly DateTimeOffset SampleTs = new(2026, 6, 13, 10, 15, 30, TimeSpan.Zero);

    private static PositionSample Sample(long seq = 84_213) => new(
        Vehicle, SampleTs, seq, 6.9271, 79.8612, PositionSource.Gt06,
        ReceivedTs: SampleTs.AddSeconds(1),
        SpeedMps: 11.8,
        HeadingDeg: 270,
        AccuracyM: 7.5,
        Hdop: 0.9,
        SatCount: 11,
        Mode: "C",
        VehicleType: "three_wheeler");

    [Fact]
    public void A_sample_survives_a_CBOR_round_trip_intact()
    {
        var decoded = PositionSampleCodec.Decode(PositionSampleCodec.Encode(Sample()));

        Assert.Equal(Sample(), decoded);
    }

    [Fact]
    public void The_encoded_form_is_a_CBOR_map_not_JSON()
    {
        var bytes = PositionSampleCodec.Encode(Sample());

        // Major type 5. Anything in 0xA0..0xBF is a map head; 0xBF is the indefinite-length one.
        Assert.InRange(bytes[0], (byte)0xA0, (byte)0xBF);
        Assert.NotEqual((byte)'{', bytes[0]);
    }

    /// <summary>
    /// The field names are the contract with the KMP encoder, which derives them from its own
    /// Kotlin property names. A rename on either side is invisible until a map goes empty, so the
    /// key set is asserted directly off the encoded bytes.
    /// </summary>
    [Fact]
    public void The_keys_are_the_ones_mqtt_topics_md_2_1_prints()
    {
        var reader = new CborReader(PositionSampleCodec.Encode(Sample()), CborConformanceMode.Lax);
        var keys = new List<string>();

        reader.ReadStartMap();
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            keys.Add(reader.ReadTextString());
            reader.SkipValue();
        }

        Assert.Equal(
            [
                "vehicleId", "sampleTs", "seq", "lat", "lng", "source", "receivedTs", "speedMps",
                "headingDeg", "accuracyM", "hdop", "satCount", "mode", "vehicleType",
            ],
            keys);
    }

    [Fact]
    public void Source_is_the_integer_the_CHECK_domain_constrains_not_a_name()
    {
        // `ck_positions_source` is `source BETWEEN 0 AND 4` and the KMP enum serialises as its code.
        // The kernel's MageRideJson would have written "gt06" here, which is exactly why the codec
        // keeps its own JSON options.
        var reader = new CborReader(PositionSampleCodec.Encode(Sample()), CborConformanceMode.Lax);
        reader.ReadStartMap();

        long? source = null;
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            if (reader.ReadTextString() == "source")
            {
                source = (long)reader.ReadUInt64();
            }
            else
            {
                reader.SkipValue();
            }
        }

        Assert.Equal(1, source);
    }

    [Fact]
    public void Absent_optional_fields_are_omitted_rather_than_written_as_null()
    {
        // Bandwidth, and symmetry with the KMP encoder, which does not encode defaults. A CBOR null
        // costs a byte to say nothing five times a second per vehicle.
        var bare = new PositionSample(Vehicle, SampleTs, 1, 6.9, 79.8, PositionSource.Mobile);
        var text = Encoding.UTF8.GetString(PositionSampleCodec.Encode(bare));

        Assert.DoesNotContain("speedMps", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tripId", text, StringComparison.Ordinal);
        Assert.Null(PositionSampleCodec.Decode(PositionSampleCodec.Encode(bare)).SpeedMps);
    }

    /// <summary>
    /// The shape the driver app actually publishes: an <b>indefinite-length</b> map, which is what
    /// <c>kotlinx.serialization.cbor</c> writes by default and is not what this codec emits.
    /// </summary>
    [Fact]
    public void An_indefinite_length_map_from_the_KMP_encoder_decodes()
    {
        var writer = new CborWriter(CborConformanceMode.Lax);
        writer.WriteStartMap(definiteLength: null);
        writer.WriteTextString("vehicleId");
        writer.WriteTextString(Vehicle.ToString());
        writer.WriteTextString("sampleTs");
        writer.WriteTextString("2026-06-13T10:15:30Z");
        writer.WriteTextString("seq");
        writer.WriteInt64(84_213);
        writer.WriteTextString("lat");
        writer.WriteDouble(6.9271);
        writer.WriteTextString("lng");
        writer.WriteDouble(79.8612);
        writer.WriteTextString("source");
        writer.WriteInt32(0);
        writer.WriteEndMap();

        var decoded = PositionSampleCodec.Decode(writer.Encode());

        Assert.Equal(Vehicle, decoded.VehicleId);
        Assert.Equal(SampleTs, decoded.SampleTs);
        Assert.Equal(84_213, decoded.Seq);
        Assert.Equal(PositionSource.Mobile, decoded.Source);
    }

    [Fact]
    public void A_field_a_newer_producer_added_is_skipped_rather_than_fatal()
    {
        // Additive versioning: the KMP codec sets ignoreUnknownKeys for the same reason. An older
        // position-processor must not fall over because a newer app started sending battery level.
        var writer = new CborWriter(CborConformanceMode.Lax);
        writer.WriteStartMap(definiteLength: null);
        writer.WriteTextString("vehicleId");
        writer.WriteTextString(Vehicle.ToString());
        writer.WriteTextString("batteryPct");
        writer.WriteInt32(64);
        writer.WriteTextString("sampleTs");
        writer.WriteTextString("2026-06-13T10:15:30Z");
        writer.WriteTextString("seq");
        writer.WriteInt64(7);
        writer.WriteTextString("lat");
        writer.WriteDouble(6.9271);
        writer.WriteTextString("lng");
        writer.WriteDouble(79.8612);
        writer.WriteTextString("source");
        writer.WriteInt32(4);
        writer.WriteTextString("diagnostics");
        writer.WriteStartArray(definiteLength: null);
        writer.WriteTextString("noise");
        writer.WriteEndArray();
        writer.WriteEndMap();

        var decoded = PositionSampleCodec.Decode(writer.Encode());

        Assert.Equal(7, decoded.Seq);
        Assert.Equal(PositionSource.NmeaMqtt, decoded.Source);
    }

    [Fact]
    public void A_whole_number_sent_as_a_float_still_reads_as_a_sequence()
    {
        // Cheap trackers do this. `seq` is the replay dedupe key (R-17/T-05), so refusing the
        // sample would drop a real position over an encoding preference.
        var writer = new CborWriter(CborConformanceMode.Lax);
        writer.WriteStartMap(definiteLength: null);
        writer.WriteTextString("vehicleId");
        writer.WriteTextString(Vehicle.ToString());
        writer.WriteTextString("sampleTs");
        writer.WriteTextString("2026-06-13T10:15:30Z");
        writer.WriteTextString("seq");
        writer.WriteDouble(42);
        writer.WriteTextString("lat");
        writer.WriteInt32(7);
        writer.WriteTextString("lng");
        writer.WriteInt32(80);
        writer.WriteTextString("source");
        writer.WriteInt32(2);
        writer.WriteEndMap();

        var decoded = PositionSampleCodec.Decode(writer.Encode());

        Assert.Equal(42, decoded.Seq);
        Assert.Equal(7d, decoded.Lat);
    }

    [Fact]
    public void JSON_is_accepted_on_the_way_in_so_a_mosquitto_pub_payload_is_readable()
    {
        var json = """
            {"vehicleId":"2f9d6b2c-0f4f-4a1e-9c3a-8f1d5b7e6a40","sampleTs":"2026-06-13T10:15:30Z",
             "seq":84213,"lat":6.9271,"lng":79.8612,"speedMps":11.8,"headingDeg":270,
             "accuracyM":7.5,"hdop":0.9,"satCount":11,"source":1,"mode":"C",
             "vehicleType":"three_wheeler","fleetId":null,"tripId":null}
            """;

        var decoded = PositionSampleCodec.Decode(Encoding.UTF8.GetBytes(json));

        Assert.Equal(Vehicle, decoded.VehicleId);
        Assert.Equal(84_213, decoded.Seq);
        Assert.Equal(PositionSource.Gt06, decoded.Source);
        Assert.Equal("three_wheeler", decoded.VehicleType);
        Assert.Null(decoded.FleetId);
    }

    [Fact]
    public void A_payload_missing_seq_is_refused_because_seq_is_the_replay_dedupe_key()
    {
        var json = """{"vehicleId":"2f9d6b2c-0f4f-4a1e-9c3a-8f1d5b7e6a40","sampleTs":"2026-06-13T10:15:30Z","lat":6.9,"lng":79.8,"source":0}""";

        Assert.Throws<FormatException>(() => PositionSampleCodec.Decode(Encoding.UTF8.GetBytes(json)));
        Assert.Null(PositionSampleCodec.TryDecode(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not cbor at all")]
    [InlineData("{ this is not json")]
    public void Garbage_decodes_to_null_rather_than_taking_the_partition_down(string payload) =>
        Assert.Null(PositionSampleCodec.TryDecode(Encoding.UTF8.GetBytes(payload)));

    [Fact]
    public void A_bare_CBOR_string_is_not_a_sample() =>
        // 0x7B is `{` in ASCII and a 27-byte text-string head in CBOR — the one byte the JSON sniff
        // turns on. It must be rejected as CBOR rather than mistaken for an object.
        Assert.Null(PositionSampleCodec.TryDecode(new CborWriter().Then(w => w.WriteTextString("x")).Encode()));

    [Fact]
    public void An_out_of_range_position_is_well_formed_syntax_but_not_a_valid_fix()
    {
        // `ck_positions_lat` / `ck_positions_lng` reject these at the sink; the codec surfaces them
        // so C039 can drop them before a batch, as mqtt-topics.md §2.1 requires.
        var wild = new PositionSample(Vehicle, SampleTs, 1, 0, 999, PositionSource.Mobile);

        Assert.False(wild with { Lng = 999 } is { IsWellFormed: true });
        Assert.False(new PositionSample(Vehicle, SampleTs, -1, 6.9, 79.8, PositionSource.Mobile).IsWellFormed);
        Assert.False(new PositionSample(Guid.Empty, SampleTs, 1, 6.9, 79.8, PositionSource.Mobile).IsWellFormed);
        Assert.True(Sample().IsWellFormed);
    }

    [Fact]
    public void Timestamps_survive_as_UTC_instants_whatever_offset_they_arrived_in()
    {
        var colombo = new DateTimeOffset(2026, 6, 13, 15, 45, 30, TimeSpan.FromHours(5.5));
        var sample = new PositionSample(Vehicle, colombo, 1, 6.9, 79.8, PositionSource.Mobile);

        var decoded = PositionSampleCodec.Decode(PositionSampleCodec.Encode(sample));

        Assert.Equal(colombo.ToUniversalTime(), decoded.SampleTs);
        Assert.Equal(TimeSpan.Zero, decoded.SampleTs.Offset);
    }
}

/// <summary>Lets a <see cref="CborWriter"/> be built in an expression.</summary>
internal static class CborWriterExtensions
{
    public static CborWriter Then(this CborWriter writer, Action<CborWriter> write)
    {
        write(writer);
        return writer;
    }
}
