using MageRide.Shared.Primitives;

namespace MageRide.Shared.Telemetry;

/// <summary>
/// Which stack produced a fix (<c>telemetry.positions.source</c>, <c>ck_positions_source</c>).
/// </summary>
/// <remarks>
/// Encoded as the small integer the column's CHECK constrains to <c>0…4</c> and the CBOR payload
/// carries — not as a name. The KMP module's <c>PositionSource</c> serialises the same way.
/// </remarks>
public enum PositionSource
{
    /// <summary>The driver app's own GPS.</summary>
    Mobile = 0,

    /// <summary>Concox GT06 / GT06N family, including TK103 and ST-901, via <c>adapter-gt06</c>.</summary>
    Gt06 = 1,

    /// <summary>JT/T 808 trackers via <c>adapter-jt808</c>.</summary>
    Jt808 = 2,

    /// <summary>H02 / H02X ASCII bus trackers via <c>adapter-h02</c>.</summary>
    H02 = 3,

    /// <summary>Teltonika / Queclink firmware speaking NMEA over native MQTT — no adapter.</summary>
    NmeaMqtt = 4,
}

/// <summary>
/// The canonical position event — one GNSS fix from one vehicle
/// (<c>backend/contracts/realtime/mqtt-topics.md</c> §2.1, D6' §2.2).
/// </summary>
/// <remarks>
/// <para>
/// This is the single shape that travels the whole telemetry plane: the driver app and every
/// hardware tracker publish it to <c>veh/{vehicleId}/pos/live</c>, position-processor-svc
/// republishes it onto <c>telemetry.normalized</c>, and persistence-writer-svc (C040) will COPY it
/// into the <c>telemetry.positions</c> hypertable. It is the .NET half of the KMP module's
/// <c>lk.mageride.shared.data.models.PositionSample</c> (C012) and the two must stay identical —
/// the app encodes what this decodes.
/// </para>
/// <para>
/// <b>Spec divergence — ADD Appendix A.</b> Appendix A prints an older, looser shape: <c>ts</c> as
/// epoch millis, <c>source</c> as a free string, plus <c>altitude</c>/<c>tripSessionId</c>/
/// <c>traceId</c>, and <b>no <c>seq</c></b>. Three later sources agree against it —
/// <c>mqtt-topics.md</c> §2.1, D6' §2.2, and the landed <c>telemetry.positions</c> DDL (C006), whose
/// columns are exactly the members below. Modelling Appendix A would produce a DTO that cannot be
/// written to its own sink and that omits <c>seq</c>, the replay dedupe key R-17/T-05 exists for.
/// The later, runnable shape wins; the micro-change-set is already recorded in the C012 handoff.
/// </para>
/// <para>
/// Every optional member is nullable because a cheap tracker reports few of them and the CBOR
/// encoder omits defaults. <c>lat</c>, <c>lng</c>, <c>seq</c> and <c>sampleTs</c> are not optional:
/// a sample without them is not a position.
/// </para>
/// </remarks>
/// <param name="VehicleId">The publishing vehicle. EMQX binds it from the device credential, so a
/// device physically cannot publish under another vehicle's topic.</param>
/// <param name="SampleTs">GNSS capture instant — <b>not</b> the receive time. The hypertable's time
/// dimension.</param>
/// <param name="Seq">Monotonic per vehicle, <c>&gt;= 0</c>. The replay dedupe key (R-17, T-05).</param>
/// <param name="Lat">Degrees, −90…90. Range-checked by <c>ck_positions_lat</c>.</param>
/// <param name="Lng">Degrees, −180…180. Range-checked by <c>ck_positions_lng</c>.</param>
/// <param name="Source">Which stack produced the fix.</param>
/// <param name="ReceivedTs">When the platform saw it. The gap from <paramref name="SampleTs"/> is
/// the replay lag.</param>
/// <param name="SpeedMps">Ground speed in metres per second.</param>
/// <param name="HeadingDeg">Course over ground, 0…359.</param>
/// <param name="AccuracyM">Horizontal accuracy in metres.</param>
/// <param name="Hdop">Horizontal dilution of precision, as the receiver reports it.</param>
/// <param name="SatCount">Satellites used in the fix.</param>
/// <param name="Mode">The vehicle's operating mode at capture time — <c>A</c>, <c>B</c> or
/// <c>C</c>.</param>
/// <param name="VehicleType">Canonical vehicle type, denormalised so a consumer needs no registry
/// lookup.</param>
/// <param name="FleetId">Denormalised for fleet-scoped reads and RLS; null when the vehicle is not
/// fleet-owned.</param>
/// <param name="TripId">The Mode A/B tracking session this belongs to; absent for Mode C.</param>
public sealed record PositionSample(
    Guid VehicleId,
    DateTimeOffset SampleTs,
    long Seq,
    double Lat,
    double Lng,
    PositionSource Source,
    DateTimeOffset? ReceivedTs = null,
    double? SpeedMps = null,
    int? HeadingDeg = null,
    double? AccuracyM = null,
    double? Hdop = null,
    int? SatCount = null,
    string? Mode = null,
    string? VehicleType = null,
    Guid? FleetId = null,
    Guid? TripId = null)
{
    /// <summary>The fix as a plain coordinate.</summary>
    public GeoPoint Point => new(Lat, Lng);

    /// <summary>
    /// Whether the sample is inside the CHECK domains <c>telemetry.positions</c> enforces.
    /// </summary>
    /// <remarks>
    /// <c>mqtt-topics.md</c> §2.1: "a cheap tracker reporting 0/999 degrees is a bug C039 must filter
    /// before the batch arrives — the constraints make it loud instead of silently poisoning a
    /// rollup." This is that filter's cheap half; the anti-spoof work (speed plausibility, jump
    /// detection, the Kalman pass) is C039/C040 and is deliberately not here.
    /// </remarks>
    public bool IsWellFormed =>
        Seq >= 0
        && !double.IsNaN(Lat) && Lat is >= -90 and <= 90
        && !double.IsNaN(Lng) && Lng is >= -180 and <= 180
        && Enum.IsDefined(Source)
        && VehicleId != Guid.Empty;
}
