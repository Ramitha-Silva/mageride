using MageRide.Ride.Configuration;
using MageRide.Ride.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Ride.Rides;

/// <summary>Whether this passenger may book at all (US-6A.10b, AL-16).</summary>
/// <remarks>
/// <para>
/// <b>An interim, and deliberately shaped to be replaced.</b> AL-16 puts the tally in
/// <c>reputation.counters.cancellations_continuous</c> and C033's fence says "counters live here
/// and nowhere else. Other services read <c>block_status</c> over gRPC; they do not keep their own
/// tallies." reputation-svc does not exist yet, and a component whose Definition of Done is "three
/// consecutive post-acceptance rider cancellations disable booking" cannot ship without an answer.
/// </para>
/// <para>
/// So the answer is <em>derived</em>, not stored: ride-svc counts the run of
/// <c>CancelledByRiderAfterAccept</c> outcomes at the head of the passenger's own ride history.
/// That is not a second copy of the counter — there is nothing to drift, because the rides are the
/// facts the counter would have been computed from. When C033 lands, this interface takes a gRPC
/// implementation reading <c>block_status</c>, the query below is deleted, and nothing else in this
/// service changes. Recorded in the C032 handoff.
/// </para>
/// <para>
/// <b>What is not implemented.</b> AL-16's re-enable path — "clear the outstanding Rs 50 balance;
/// access is restored after a configurable cooldown or once an admin/CSR reinstates it" — needs the
/// outstanding balance (billing's) and an admin surface (admin-bff's). Neither is ride-svc's, so a
/// passenger disabled here is re-enabled by completing a ride, which is the one lever this service
/// owns and is also what US-6A.10b says resets the counter.
/// </para>
/// </remarks>
public interface IBookingEligibility
{
    /// <summary>
    /// The number of consecutive post-acceptance cancellations, and whether it has reached the
    /// threshold that disables booking.
    /// </summary>
    Task<BookingEligibility> EvaluateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid passengerId,
        CancellationToken cancellationToken);
}

/// <param name="ConsecutiveCancellations">Since the passenger's last completed ride.</param>
/// <param name="Threshold">What <c>Ride:CancellationDisableThreshold</c> is set to (3, AL-16).</param>
public sealed record BookingEligibility(int ConsecutiveCancellations, int Threshold)
{
    public bool IsDisabled => ConsecutiveCancellations >= Threshold;

    /// <summary>How many more the passenger has before booking stops working.</summary>
    public int Remaining => Math.Max(0, Threshold - ConsecutiveCancellations);
}

/// <inheritdoc cref="IBookingEligibility"/>
public sealed class RideHistoryBookingEligibility(IRideRepository rides, IOptions<RideOptions> options)
    : IBookingEligibility
{
    private readonly RideOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<BookingEligibility> EvaluateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid passengerId,
        CancellationToken cancellationToken)
    {
        var consecutive = await rides.CountConsecutiveRiderCancellationsAsync(
            connection, transaction, passengerId, cancellationToken);

        return new BookingEligibility(consecutive, _options.CancellationDisableThreshold);
    }
}
