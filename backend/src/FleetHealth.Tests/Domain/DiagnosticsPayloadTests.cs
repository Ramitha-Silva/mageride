using System.Buffers;
using System.Text;
using MageRide.FleetHealth.Diagnostics;
using MageRide.FleetHealth.Domain;

namespace MageRide.FleetHealth.Tests.Domain;

/// <summary>
/// The <c>sys/diag/{vehicleId}</c> codec (D6' §3.1, US-3.12). Pure — no container.
/// </summary>
/// <remarks>
/// The payload shape is C044's (no spec prints one), so these are the tests that pin it. The two that
/// matter most are the out-of-domain ones: <c>ck_device_health_battery_pct</c> would reject a whole
/// flush batch over one device reporting 255 %, so the field has to be dropped here and not clamped
/// there.
/// </remarks>
public sealed class DiagnosticsPayloadTests
{
    private static readonly Guid Vehicle = Guid.Parse("2f1c1f22-0e3d-4a58-9e6f-4d5c1b7a9e01");

    private static readonly DateTimeOffset Received = new(2026, 7, 30, 9, 15, 0, TimeSpan.Zero);

    [Fact]
    public void A_full_frame_is_read_field_for_field()
    {
        var parsed = TryParse(
            """
            {"ts":"2026-07-30T09:14:30Z","signalStrength":24,"batteryPct":82,
             "batteryMv":4020,"satCount":11,"firmware":"GT06-1.2.3","uptimeSec":86400}
            """,
            out var report);

        Assert.True(parsed);
        Assert.Equal(Vehicle, report!.VehicleId);
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 9, 14, 30, TimeSpan.Zero), report.At);
        Assert.Equal((short)24, report.SignalStrength);
        Assert.Equal((short)82, report.BatteryPct);
        Assert.Equal(4020, report.BatteryMv);
        Assert.Equal((short)11, report.SatCount);
    }

    [Fact]
    public void Unknown_keys_are_ignored_rather_than_rejected()
    {
        // The platform is versioned but additive: a field a newer firmware starts sending must not take
        // an older consumer down. Same rule PositionSampleCodec is written under.
        Assert.True(TryParse("""{"batteryMv":3900,"somethingNew":{"nested":true}}""", out var report));
        Assert.Equal(3900, report!.BatteryMv);
    }

    [Fact]
    public void A_frame_with_no_ts_is_stamped_with_the_receive_instant()
    {
        Assert.True(TryParse("""{"satCount":9}""", out var report));
        Assert.Equal(Received, report!.At);
    }

    [Theory]
    // A GT06's status byte carries a voltage LEVEL of 0-6, so a firmware that reports it as a
    // percentage is out of domain and its battery reading is dropped, not scaled.
    [InlineData("""{"batteryPct":255}""")]
    [InlineData("""{"batteryPct":-1}""")]
    public void An_out_of_domain_battery_percentage_is_dropped(string json)
    {
        // Nothing else in the frame, so the whole frame is unusable.
        Assert.False(TryParse(json, out _));
    }

    [Fact]
    public void An_out_of_domain_field_does_not_take_the_usable_ones_with_it()
    {
        Assert.True(TryParse("""{"batteryPct":255,"batteryMv":4020}""", out var report));
        Assert.Null(report!.BatteryPct);
        Assert.Equal(4020, report.BatteryMv);
    }

    [Fact]
    public void A_reading_beyond_the_smallint_columns_is_dropped()
    {
        // A decode bug reporting 70,000 satellites must not overflow `sat_count SMALLINT` and fail the
        // flush for every other device in the batch.
        Assert.False(TryParse("""{"satCount":70000}""", out _));
    }

    [Fact]
    public void A_voltage_reported_as_a_real_number_is_rounded_rather_than_refused()
    {
        Assert.True(TryParse("""{"batteryMv":4020.0}""", out var report));
        Assert.Equal(4020, report!.BatteryMv);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"firmware":"GT06-1.2.3"}""")]
    [InlineData("""{"batteryMv":"4020"}""")]
    public void A_frame_with_nothing_usable_in_it_is_refused(string json) => Assert.False(TryParse(json, out _));

    private static bool TryParse(string json, out DeviceDiagnosticsReport? report) =>
        DiagnosticsPayload.TryParse(
            Vehicle, new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)), Received, out report);
}
