using MageRide.Shared.Primitives;
using MageRide.TcpAdapter.Protocols;
using MageRide.TestKit;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// What every Mode A/B scenario shares: the fleet, the skip when Docker is unreachable, and the
/// promise that a failure prints the vehicle.
/// </summary>
/// <remarks>
/// <para>
/// Derived classes carry <c>[Collection&lt;ModeAbCollection&gt;]</c> and
/// <c>[Trait("Category", "ModeAB")]</c> themselves rather than inheriting them: xUnit resolves a
/// collection from the concrete test class, and the verify command
/// (<c>--filter Category=ModeAB</c>) is not something to leave to attribute inheritance.
/// </para>
/// <para>
/// <b>Every scenario body runs inside <see cref="SessionJournal.AroundAsync"/></b>, and every
/// vehicle it touches is added to the list it is handed — so a failure eleven assertions later
/// still knows whose journeys to print.
/// </para>
/// </remarks>
public abstract class ModeAbScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
{
    /// <summary>
    /// Runs a scenario against the fleet, skipping when the containers are not available and
    /// attaching every named vehicle's history to whatever fails.
    /// </summary>
    private protected async Task RunAsync(Func<ModeAbFleet, ScenarioVehicles, Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Before the journal wrapper, so a skip is a skip rather than a failure with a history.
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        var fleet = await ModeAbFleet.SharedAsync(postgres, redis, redpanda, emqx);

        await fleet.Journal.AroundAsync(vehicles => body(fleet, new ScenarioVehicles(vehicles)));
    }

    /// <summary>
    /// An approved organisation with one tracked, driver-assigned Mode A/B vehicle, and the place it
    /// operates from.
    /// </summary>
    /// <remarks>
    /// The state most scenarios start in, and every step of it goes through a real route: the org is
    /// registered and approved, the vehicle is onboarded onto its roster, a driver is assigned, and
    /// a tracker is bound through US-13.12's Fleet Portal hop into provisioning-svc. Only the
    /// Verification Officer's vehicle decision is stood in for — see
    /// <see cref="ModeAbFleet.MarkVehicleApprovedAsync"/>.
    /// </remarks>
    private protected static async Task<TrackedVehicle> ArriveAsync(
        ModeAbFleet fleet, ScenarioVehicles vehicles, string mode = "A", string vehicleType = "bus")
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(vehicles);

        var org = await fleet.CreateApprovedOrgAsync();
        var driver = await fleet.CreateDriverAsync();
        var vehicle = await fleet.OnboardTrackedVehicleAsync(org, driver, mode, vehicleType);

        vehicles.Add(vehicle.VehicleId);

        return new TrackedVehicle(org, vehicle, ModeAbFleet.NextPlace());
    }

    /// <summary>
    /// Drives a vehicle along a straight line towards <paramref name="to"/>, reporting as it goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The step is fixed at 40 metres and the cadence at two seconds, whatever the distance</b> —
    /// which is 72 km/h, comfortably under every ADD §12.6 ceiling including a three-wheeler's 80.
    /// That is not decoration. position-processor-svc refuses a sample whose implied speed exceeds
    /// its type's ceiling, and <b>a refused sample never becomes the position the next one is
    /// measured against</b>, so one over-long step poisons every step after it: a scenario that
    /// walked a bus 100 m every two seconds found that exactly one fix out of six ever reached the
    /// session, and spent a minute waiting for an arrival fence that had never seen the vehicle
    /// move. Deriving the step count from the distance is what stops a scenario choosing a number
    /// that happens to be too big.
    /// </para>
    /// <para>
    /// Returns the last point reported and the instant its frame carried, which together are what
    /// <see cref="ModeAbFleet.WaitForFixAsync"/> waits for.
    /// </para>
    /// </remarks>
    private protected static async Task<ReportedFix> DriveAsync(
        TrackerDevice device, GeoPoint from, GeoPoint to, TimeSpan? cadence = null)
    {
        ArgumentNullException.ThrowIfNull(device);

        const double StepMetres = 40;

        var wait = cadence ?? TimeSpan.FromSeconds(2);
        var steps = Math.Max(1, (int)Math.Ceiling(ModeAbFleet.DistanceM(from, to) / StepMetres));
        var at = from;
        var capturedAt = DateTimeOffset.UtcNow;

        for (var step = 1; step <= steps; step++)
        {
            // The cadence is waited out BEFORE every frame, the first one included, and that is the
            // whole fix for the intermittent ModeB telemetry timeout of 2026-08-10.
            //
            // position-processor-svc measures a sample's implied speed from the last fix it ACCEPTED
            // — not from the start of this drive. So the first step's implied speed was
            // 40 m ÷ (however long the scenario's previous step happened to take), and the scenario
            // before a drive is HTTP against localhost. ModeB's grant flow sometimes finished inside
            // one second, which made the first step 40 m in ~1 s = 144 km/h, over a bus's 120 ceiling
            // (ADD §12.6). Refused — and `a refused sample never becomes the position the next one is
            // measured against`, so step two was then measured from the depot as well: 80 m over 2 s,
            // 144 km/h, refused too. Nothing landed, and the wait timed out having never seen a fix.
            //
            // Waiting first makes the gap at least `wait` no matter what preceded, so every step is
            // 40 m / 2 s = 72 km/h — the figure tests/E2E/CLAUDE.md already claims this drive has, and
            // which was only ever true of the second step onward.
            //
            // It also keeps the device inside D5' §5.2/AL-12's 1-sample-per-second cadence. Two frames
            // inside one second collide on `seq` (tcp-adapter sets it to the captured whole second in
            // Unix millis) and the later one is discarded by the T-05 replay watermark — see the
            // separate finding on seq's resolution.
            await Task.Delay(wait, TestContext.Current.CancellationToken);

            at = new GeoPoint(
                from.Latitude + ((to.Latitude - from.Latitude) * step / steps),
                from.Longitude + ((to.Longitude - from.Longitude) * step / steps));

            capturedAt = await device.ReportAsync(at);
        }

        return new ReportedFix(at, capturedAt);
    }

    /// <summary>
    /// Reports the same standing position until <paramref name="condition"/> holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A parked bus keeps publishing at its standby cadence, which is exactly what this does — and
    /// it is how a scenario waits for a fix to cross nine services without assuming how long that
    /// takes. Each frame carries a fresh capture instant, because <c>seq</c> <b>is</b> the capture
    /// instant (C043) and a re-sent frame with an unchanged stamp is discarded by T-05's watermark
    /// as a replay of itself.
    /// </para>
    /// <para>
    /// The position advances by nothing, so <c>last_movement_at</c> is not advanced either — D5's
    /// "reporting is not moving", which is what keeps US-5.3's timer reachable at all.
    /// </para>
    /// </remarks>
    private protected static async Task ReportUntilAsync(
        TrackerDevice device, GeoPoint at, Func<Task<bool>> condition, string what, TimeSpan? within = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(condition);

        var timeout = within ?? TimeSpan.FromSeconds(45);
        var deadline = DateTimeOffset.UtcNow + timeout;

        do
        {
            await device.ReportAsync(at, speedKph: 0);

            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1.2), TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail(
            $"{what} did not happen within {timeout.TotalSeconds:F0}s of a {ProtocolFamilies.Name(device.Family)} "
            + "device reporting from a fixed position.");
    }
}

/// <summary>The last fix a device sent, and the instant its frame carried.</summary>
/// <remarks>
/// The instant is what a scenario waits for the session to reach. Ending a journey while frames are
/// still crossing the hot path is not a hypothetical: the ones behind land on whatever session is
/// live when they arrive, which is how a bus that had just started its next journey was found
/// already standing at its own destination.
/// </remarks>
internal sealed record ReportedFix(GeoPoint At, DateTimeOffset CapturedAt);

/// <summary>An organisation, one of its tracked vehicles, and the place that vehicle operates from.</summary>
internal sealed record TrackedVehicle(FleetOrg Org, FleetVehicle Vehicle, GeoPoint Depot)
{
    public Guid VehicleId => Vehicle.VehicleId;

    public string Imei => Vehicle.Imei
        ?? throw new InvalidOperationException("This vehicle has no tracker bound to it.");
}

/// <summary>
/// The vehicles a scenario has created, so a failure knows whose history to print.
/// </summary>
/// <remarks>
/// A wrapper rather than a bare <c>List&lt;Guid&gt;</c> so the intent reads at the call site:
/// <c>vehicles.Add(vehicleId)</c> is "this vehicle is part of the diagnosis", not "remember this
/// for later".
/// </remarks>
public sealed class ScenarioVehicles(List<Guid> vehicles)
{
    public void Add(Guid vehicleId) => vehicles.Add(vehicleId);
}
