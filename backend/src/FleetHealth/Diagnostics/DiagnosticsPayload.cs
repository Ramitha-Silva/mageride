using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MageRide.FleetHealth.Domain;

namespace MageRide.FleetHealth.Diagnostics;

/// <summary>
/// The <c>sys/diag/{vehicleId}</c> payload (D6' §3.1, QoS 0, no retain) — US-3.12's per-tracker
/// diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// <b>The topic tree gives this branch a name and no payload.</b> D6' §3.1 and
/// <c>backend/contracts/realtime/mqtt-topics.md</c> §1 both print the row as "diagnostics" and stop
/// there, so the shape is C044's and is added to <c>mqtt-topics.md</c> §2.4 in the same change.
/// Answering the question C043's handoff left open ("C044 either consumes a diagnostics topic this
/// component would have to start publishing, or the fields stay null — decide which"): the former.
/// </para>
/// <para>
/// <b>JSON, not CBOR.</b> <c>pos/live</c> is CBOR because a metered mobile link carries it five times
/// a second; a diagnostics frame is one message every several minutes at QoS 0 and there is no
/// bandwidth argument for a second encoder. Everything that will ever publish here follows a schema
/// this repository defines, so there is no legacy encoding to accommodate either.
/// </para>
/// <para>
/// <b>The vehicle comes from the topic, never from the payload</b> — EMQX bound the topic to the
/// device's credential (<c>acl.conf</c>'s <c>sys/diag/${username}</c>) and the payload is
/// self-asserted. Same rule C039 applies to a position sample.
/// </para>
/// <para>
/// <b>An out-of-domain value is dropped field by field, not clamped and not fatal.</b> Two reasons:
/// <c>ck_device_health_battery_pct</c> would reject the whole flush batch over one device reporting
/// 255 %, and a clamped value is a number on an operator's screen that no device said. A frame whose
/// every field is unusable is simply not stored.
/// </para>
/// </remarks>
public static class DiagnosticsPayload
{
    /// <summary>Wire field names. Spelled once, because the publisher is in another component.</summary>
    private const string FieldTs = "ts";
    private const string FieldSignalStrength = "signalStrength";
    private const string FieldBatteryPct = "batteryPct";
    private const string FieldBatteryMv = "batteryMv";
    private const string FieldSatCount = "satCount";

    /// <summary>
    /// Largest value the <c>SMALLINT</c> diagnostics columns hold. A field beyond it is a decode bug
    /// upstream, not a reading.
    /// </summary>
    private const int MaxSmallInt = short.MaxValue;

    /// <summary>
    /// Reads a diagnostics frame for <paramref name="vehicleId"/>, or <see langword="false"/> when
    /// there is nothing usable in it.
    /// </summary>
    /// <param name="vehicleId">From the topic — the authenticated identity.</param>
    /// <param name="payload">The raw MQTT payload. A <see cref="ReadOnlySequence{T}"/> because that is
    /// what MQTTnet 5 hands over and what <see cref="JsonDocument"/> parses without a copy.</param>
    /// <param name="receivedAt">Fallback for a frame carrying no <c>ts</c>.</param>
    /// <param name="report">The parsed report.</param>
    public static bool TryParse(
        Guid vehicleId,
        ReadOnlySequence<byte> payload,
        DateTimeOffset receivedAt,
        [NotNullWhen(true)] out DeviceDiagnosticsReport? report)
    {
        report = null;

        if (payload.IsEmpty)
        {
            return false;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = document.RootElement;

            // A device clock is not trusted for anything the state ladder reads, so `ts` is recorded
            // and the receive instant is the fallback. It is never compared against last_ping_at.
            var at = TryReadTimestamp(root, FieldTs) ?? receivedAt;

            var candidate = new DeviceDiagnosticsReport(
                vehicleId,
                at,
                SignalStrength: TryReadSmallInt(root, FieldSignalStrength, 0, MaxSmallInt),
                BatteryMv: TryReadInt(root, FieldBatteryMv, 0, int.MaxValue),
                BatteryPct: TryReadSmallInt(root, FieldBatteryPct, 0, 100),
                SatCount: TryReadSmallInt(root, FieldSatCount, 0, MaxSmallInt));

            if (!candidate.HasAnyValue)
            {
                return false;
            }

            report = candidate;
            return true;
        }
    }

    private static DateTimeOffset? TryReadTimestamp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static short? TryReadSmallInt(JsonElement root, string name, int min, int max) =>
        TryReadInt(root, name, min, max) is { } value ? (short)value : null;

    private static int? TryReadInt(JsonElement root, string name, int min, int max)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        // A tracker firmware that reports 4.02 volts as `4020.0` rather than `4020` is reporting the
        // same number; a string where a number belongs is a bug to surface as a missing field, not to
        // coerce.
        var parsed = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDouble(out var real) && real is >= int.MinValue and <= int.MaxValue =>
                (int)Math.Round(real, MidpointRounding.AwayFromZero),
            _ => (int?)null,
        };

        return parsed is { } candidate && candidate >= min && candidate <= max ? candidate : null;
    }
}
