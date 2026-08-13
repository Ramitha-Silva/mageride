using System.Diagnostics;
using System.Globalization;
using System.Net;
using MageRide.Provisioning.Domain;
using MageRide.Shared.Errors;
using MageRide.TestKit;

namespace MageRide.Security.Tests.AntiSpoof.Trackers;

/// <summary>
/// C128's third definition-of-done item: <b>a cloned IMEI quarantines both devices within the
/// documented window</b> (T-08, D5' §13.2, D6' §4.3, US-3.4).
///
/// <para>
/// The documented window is 24 hours and the documented outcome is that <b>both</b> devices are
/// held — not the newcomer, and not the incumbent. That asymmetry is the whole rule: at bind time
/// the platform has two claims to one identity and no way to tell which device is the genuine one,
/// so the only safe answer is to serve neither and ask an operator. A rule that kept the incumbent
/// would let a clone displace nothing but would also let the real thief keep publishing if they
/// bound first; a rule that kept the newcomer would let a clone take a vehicle over by arriving
/// second.
/// </para>
///
/// <para>
/// What this class adds over provisioning-svc's own <c>AntiCloneTests</c> is the <b>window</b> and
/// the <b>timing</b>: that suite proves the quarantine happens, this one measures how long it takes
/// to bite on the live plane and proves the 24 h boundary is where the behaviour changes.
/// </para>
/// </summary>
[Collection<AntiSpoofCollection>]
[Trait("Category", "AntiSpoof")]
public sealed class ImeiCloneTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// T-12's budget. A clone is a credential compromise, so the same clock applies to it.
    /// </summary>
    private static readonly TimeSpan PropagationBudget = TimeSpan.FromSeconds(60);

    /// <summary>The DoD assertion.</summary>
    [Fact]
    public async Task A_cloned_imei_holds_both_devices_and_stops_serving_either_inside_the_budget()
    {
        RequireInfrastructure();

        await using var plane = await TrackerPlane.StartAsync(postgres, redis);

        var firstOwner = await plane.CreateDriverAsync();
        var secondOwner = await plane.CreateDriverAsync();
        var incumbentVehicle = await plane.CreateVehicleAsync(firstOwner);
        var cloneVehicle = await plane.CreateVehicleAsync(secondOwner);
        var imei = TrackerPlane.NextImei();

        var incumbent = await plane.BindAsync(firstOwner, imei, incumbentVehicle);

        // The incumbent is serving: it authenticates and the adapter's fast path resolves it.
        var before = await plane.ValidateAsync(
            imei, incumbent.Credential.Serial, IPAddress.Parse("203.0.113.7"));

        Assert.True(before.Valid);

        // A second device presents the same IMEI from a different address, claiming a different
        // vehicle. This is the clone.
        var elapsed = Stopwatch.StartNew();

        var refused = await Assert.ThrowsAsync<MageRideException>(
            () => plane.BindAsync(secondOwner, imei, cloneVehicle, IPAddress.Parse("198.51.100.23")));

        Assert.Equal(BindingStateReasons.ImeiDuplicate, refused.Error.Code);

        // Both rows are held, and both name the same reason so an operator's queue can be filtered
        // on it. The incumbent kept publishing until this moment, so leaving it ACTIVE would be the
        // clone winning by arriving second.
        var bindings = await plane.BindingsAsync(imei);

        Assert.Equal(2, bindings.Count);
        Assert.All(bindings, binding => Assert.Equal(BindingStates.Quarantined, binding.State));
        Assert.All(bindings, binding => Assert.Equal(BindingStateReasons.ImeiDuplicate, binding.Reason));

        Assert.Contains(bindings, binding => binding.VehicleId == incumbentVehicle);
        Assert.Contains(bindings, binding => binding.VehicleId == cloneVehicle);

        // Neither authenticates any more — including the one that was serving a second ago, and
        // including with the credential it had legitimately been issued.
        var after = await plane.ValidateAsync(
            imei, incumbent.Credential.Serial, IPAddress.Parse("203.0.113.7"));

        elapsed.Stop();

        Assert.False(after.Valid);
        Assert.Equal(BindingStates.Quarantined, after.State);

        // And the adapter's cached fast path is gone, so a device reconnecting goes back to
        // `validate` rather than being resolved from a stale entry for the cache's whole 24 h TTL.
        Assert.Null(await plane.CachedVehicleAsync(imei));

        Assert.True(
            elapsed.Elapsed < PropagationBudget,
            string.Create(
                CultureInfo.InvariantCulture,
                $"A clone took {elapsed.Elapsed.TotalSeconds:F2} s to stop both devices serving; T-12 budgets {PropagationBudget.TotalSeconds:F0} s."));
    }

    /// <summary>
    /// The window is a window: outside it the same two binds are a re-provision, not a clone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion that stops the rule from being "any rebind quarantines". An operator
    /// moving a tracker between vehicles a week later has cloned nothing, and holding both would
    /// need an admin to undo a legitimate re-provision — which, at fleet scale, is how a safety
    /// control gets switched off.
    /// </para>
    /// <para>
    /// The deployed 24 h is left in force and the <i>sighting trail</i> is aged instead. Shortening
    /// <c>Provisioning:AntiCloneWindow</c> to two seconds would have been the easier fixture and
    /// would have proved the mechanism while saying nothing about the number D6' §4.3 gives.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Outside_the_documented_window_the_same_pair_of_binds_is_a_re_provision()
    {
        RequireInfrastructure();

        await using var plane = await TrackerPlane.StartAsync(postgres, redis);

        Assert.Equal(TimeSpan.FromHours(24), plane.Options.AntiCloneWindow);

        var firstOwner = await plane.CreateDriverAsync();
        var secondOwner = await plane.CreateDriverAsync();
        var oldVehicle = await plane.CreateVehicleAsync(firstOwner);
        var newVehicle = await plane.CreateVehicleAsync(secondOwner);
        var imei = TrackerPlane.NextImei();

        await plane.BindAsync(firstOwner, imei, oldVehicle);

        // Twenty-five hours later — one hour past the boundary.
        await plane.AgeSightingsAsync(imei, TimeSpan.FromHours(25));

        var rebound = await plane.BindAsync(secondOwner, imei, newVehicle);

        Assert.Equal(newVehicle, rebound.Binding.VehicleId);
        Assert.Equal(BindingStates.Active, rebound.Binding.State);

        var bindings = await plane.BindingsAsync(imei);

        // Exactly one ACTIVE row, and it is the new one. The old binding is released rather than
        // held: `ux_tracker_imei_active` is what makes "one ACTIVE binding per IMEI" a database
        // property instead of a convention.
        Assert.Single(bindings, binding => binding.State == BindingStates.Active);
        Assert.DoesNotContain(bindings, binding => binding.State == BindingStates.Quarantined);
    }

    /// <summary>
    /// Just inside the window is still a clone — the boundary is asserted from both sides.
    /// </summary>
    /// <remarks>
    /// A one-sided boundary test passes on an implementation that quarantines everything and on one
    /// that quarantines nothing after any elapsed time at all. Twenty-three hours is inside 24;
    /// the previous test's twenty-five is outside.
    /// </remarks>
    [Fact]
    public async Task Just_inside_the_documented_window_is_still_a_clone()
    {
        RequireInfrastructure();

        await using var plane = await TrackerPlane.StartAsync(postgres, redis);

        var firstOwner = await plane.CreateDriverAsync();
        var secondOwner = await plane.CreateDriverAsync();
        var imei = TrackerPlane.NextImei();

        await plane.BindAsync(firstOwner, imei, await plane.CreateVehicleAsync(firstOwner));
        await plane.AgeSightingsAsync(imei, TimeSpan.FromHours(23));

        var cloneVehicle = await plane.CreateVehicleAsync(secondOwner);

        var refused = await Assert.ThrowsAsync<MageRideException>(
            () => plane.BindAsync(secondOwner, imei, cloneVehicle));

        Assert.Equal(BindingStateReasons.ImeiDuplicate, refused.Error.Code);

        var bindings = await plane.BindingsAsync(imei);
        Assert.All(bindings, binding => Assert.Equal(BindingStates.Quarantined, binding.State));
    }

    /// <summary>
    /// The other detection path: a clone that never binds, reported by the adapter that saw it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A real clone copies the credential rather than asking for one, so it never reaches
    /// <c>bind</c> at all — what distinguishes it is two live sockets holding one identity, which
    /// is state the adapter has and provisioning-svc does not. So the adapter reports and this
    /// service adjudicates. Without this path the T-08 rule would only ever catch the
    /// <i>incompetent</i> clone.
    /// </para>
    /// <para>
    /// The measurement is the same as the bind path's: from the report to neither device serving.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_clone_the_adapter_saw_is_held_on_report_and_stops_serving_inside_the_budget()
    {
        RequireInfrastructure();

        await using var plane = await TrackerPlane.StartAsync(postgres, redis);

        var owner = await plane.CreateDriverAsync();
        var vehicleId = await plane.CreateVehicleAsync(owner);
        var imei = TrackerPlane.NextImei();

        var bound = await plane.BindAsync(owner, imei, vehicleId);

        Assert.True((await plane.ValidateAsync(imei, bound.Credential.Serial)).Valid);

        var elapsed = Stopwatch.StartNew();

        var held = await plane.QuarantineAsync(
            imei, "tcp-adapter", "two sockets holding one identity");

        var after = await plane.ValidateAsync(imei, bound.Credential.Serial);

        elapsed.Stop();

        // `held` is the binding as it was found — the return value says a hold happened, and the
        // row is where the resulting state lives.
        Assert.NotNull(held);
        Assert.Equal(vehicleId, held.VehicleId);

        Assert.Equal(
            BindingStates.Quarantined,
            Assert.Single(await plane.BindingsAsync(imei)).State);

        Assert.False(after.Valid);
        Assert.Equal(BindingStates.Quarantined, after.State);
        Assert.Null(await plane.CachedVehicleAsync(imei));

        Assert.True(
            elapsed.Elapsed < PropagationBudget,
            string.Create(
                CultureInfo.InvariantCulture,
                $"An adapter clone report took {elapsed.Elapsed.TotalSeconds:F2} s to stop the device serving; T-12 budgets {PropagationBudget.TotalSeconds:F0} s."));
    }

    /// <summary>
    /// A quarantine is idempotent, because the adapter that reported it will report it again.
    /// </summary>
    /// <remarks>
    /// Redis pub/sub is fire-and-forget and the adapter re-validates every five minutes, so a
    /// device whose socket survived the first signal is reported repeatedly. A second report that
    /// raised a second alert would turn one clone into an operator queue full of one clone.
    /// </remarks>
    [Fact]
    public async Task A_repeated_clone_report_holds_nothing_further()
    {
        RequireInfrastructure();

        await using var plane = await TrackerPlane.StartAsync(postgres, redis);

        var owner = await plane.CreateDriverAsync();
        var imei = TrackerPlane.NextImei();

        await plane.BindAsync(owner, imei, await plane.CreateVehicleAsync(owner));

        Assert.NotNull(await plane.QuarantineAsync(imei, "tcp-adapter", "first"));
        Assert.Null(await plane.QuarantineAsync(imei, "tcp-adapter", "again"));

        var bindings = await plane.BindingsAsync(imei);
        Assert.Single(bindings);
    }

    private void RequireInfrastructure()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
    }
}
