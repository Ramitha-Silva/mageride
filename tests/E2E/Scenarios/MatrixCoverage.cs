using MageRide.E2E.Infrastructure;
using MageRide.Ride.Domain;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// Which cells of the §11.12 matrix this suite drives, and which it cannot — with the reason.
/// </summary>
/// <remarks>
/// <para>
/// C120's deliverable is "every cell of the cancellation/no-show matrix", and the only honest way
/// to make that claim is to derive the denominator from the matrix itself and account for every
/// cell that is missing from the numerator. This is C118's ratchet applied to a different table:
/// the list below cannot grow without somebody writing down why, and
/// <see cref="Every_cell_is_either_driven_end_to_end_or_recorded_as_unreachable"/> fails if a cell
/// is in neither set — which is what happens the day a row is added to the matrix.
/// </para>
/// <para>
/// It fails in the other direction too. An entry that becomes reachable and is left here is a
/// failure, so the ledger shrinks when the platform does the work.
/// </para>
/// </remarks>
[Collection<ModeCCollection>]
[Trait("Category", "ModeC")]
public sealed class MatrixCoverage(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    : ModeCScenario(postgres, redis, redpanda, emqx)
{
    /// <summary>
    /// Cells no caller in the running platform can produce, each with what would have to change.
    /// </summary>
    /// <remarks>
    /// Every one of them is still asserted as a *rule* by ride-svc's own suite
    /// (<c>RideCancellationMatrixTests</c> walks the table, <c>CancellationMatrixTests</c> drives
    /// the service). What is absent is an end-to-end path, and that absence is a finding about the
    /// platform rather than about this suite.
    /// </remarks>
    public static readonly IReadOnlyList<(string State, RideCancellationTrigger Trigger, string Why)> Unreachable =
    [
        (RideStates.DriverArrived, RideCancellationTrigger.DriverNoShow,
            "Nothing can raise DriverNoShow against a ride in DriverArrived. The only producer of the "
            + "trigger is the `arrival_grace` timer, and RideStateWriter retires that kind on the move "
            + "into DriverArrived (it is in LifecycleKinds) — correctly, because a driver who has "
            + "arrived has not failed to arrive. `system-cancel` declares four reasons and "
            + "`driver_no_show` is not among them, so no operator or service can raise it either. "
            + "The row is defensive: it says what would happen if some future caller did. C120 handoff."),
    ];

    /// <summary>The cells <see cref="CancellationMatrixScenario"/> drives, one test case each.</summary>
    public static IEnumerable<(string State, RideCancellationTrigger Trigger, RideCancellationOutcome Outcome)>
        Reachable =>
        RideCancellationMatrix.All
            .Where(cell => !Unreachable.Any(gap => gap.State == cell.State && gap.Trigger == cell.Trigger))
            .OrderBy(cell => cell.Trigger)
            .ThenBy(cell => cell.State, StringComparer.Ordinal);

    [Fact]
    public void Every_cell_is_either_driven_end_to_end_or_recorded_as_unreachable()
    {
        var driven = Reachable.Select(cell => (cell.State, cell.Trigger)).ToHashSet();
        var excused = Unreachable.Select(gap => (gap.State, gap.Trigger)).ToHashSet();

        foreach (var (state, trigger, _) in RideCancellationMatrix.All)
        {
            Assert.True(
                driven.Contains((state, trigger)) || excused.Contains((state, trigger)),
                $"{state} × {trigger} is a cell of the §11.12 matrix that C120 neither drives nor "
                + "accounts for. Add a path to CancellationMatrixScenario, or an entry to "
                + "MatrixCoverage.Unreachable saying what would have to change.");
        }

        // The other direction: an excuse for a cell the matrix no longer has is a note about a rule
        // that was deleted, and it would go on quietly excusing nothing.
        foreach (var (state, trigger, _) in Unreachable)
        {
            Assert.True(
                RideCancellationMatrix.TryResolve(state, trigger, out _),
                $"MatrixCoverage.Unreachable excuses {state} × {trigger}, which is not a cell of the matrix.");
        }

        Assert.Empty(driven.Intersect(excused));
    }

    /// <summary>
    /// The one recorded gap, asserted as a gap: nothing the platform can be asked to do reaches
    /// <c>NoShowDriver</c> from <c>DriverArrived</c>.
    /// </summary>
    /// <remarks>
    /// A ledger entry nobody checks is a comment. This drives the ride to <c>DriverArrived</c> and
    /// shows that the timer which would have produced the trigger is gone — so the day somebody
    /// stops retiring it, or gives <c>system-cancel</c> a fifth reason, this fails and the entry
    /// above comes out of the list.
    /// </remarks>
    [Fact]
    public Task The_recorded_gap_is_still_a_gap() => RunAsync(async (fleet, rides) =>
    {
        var ride = await DriveToAsync(fleet, rides, RideStates.DriverArrived);

        // The arrival grace — the only producer of a DriverNoShow — was retired by the arrival
        // itself, and what the ride carries instead is the rider's five minutes.
        Assert.Empty(await fleet.ReadRideTimersAsync(ride.RideId, "arrival_grace"));
        Assert.Single(await fleet.ReadRideTimersAsync(ride.RideId, "no_show"));

        // And `system-cancel` will not take the reason that would produce it.
        using var refused = await fleet.SystemCancelAsync(ride.RideId, "driver_no_show");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("validation-failed", await ModeCFleet.ProblemCodeAsync(refused));

        Assert.Equal(RideStates.DriverArrived, (await fleet.ReadRideAsync(ride.RideId)).State);
    });
}
