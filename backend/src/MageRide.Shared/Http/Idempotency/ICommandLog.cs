namespace MageRide.Shared.Http.Idempotency;

/// <summary>
/// Per-service command log backing idempotent replay (R-14, R-18; ADD §11.13).
/// </summary>
/// <remarks>
/// <para>
/// The contract is the two-phase shape the ADD sequence diagram specifies: reserve the key with
/// <c>INSERT … ON CONFLICT (idempotency_key) DO NOTHING</c>, execute only if the insert won, then
/// write the response back. A duplicate arriving later reads the stored response and returns it
/// verbatim.
/// </para>
/// <para>
/// Implementations must store <see cref="CommandLogResponse.Body"/> losslessly. The kernel
/// promises callers a byte-for-byte replay and cannot deliver that over a store that re-encodes
/// the body — see <see cref="Postgres.PostgresCommandLog"/> for the concrete column choice.
/// </para>
/// <para>
/// Each bounded context owns its own table (<c>rides.command_log</c>, and the equivalent for other
/// services), so registration is per-service.
/// </para>
/// </remarks>
public interface ICommandLog
{
    /// <summary>
    /// Claims <paramref name="key"/> for this caller, or reports what is already there.
    /// Must be atomic — two concurrent callers cannot both receive
    /// <see cref="CommandLogOutcome.Reserved"/>.
    /// </summary>
    Task<CommandLogReservation> TryReserveAsync(CommandLogKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the response produced by the caller that held the reservation. Subsequent
    /// duplicates of the same key replay it.
    /// </summary>
    Task CompleteAsync(string idempotencyKey, CommandLogResponse response, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a reservation whose command did not produce a recordable response (a 5xx, or a
    /// crash mid-flight), so the client's retry can execute rather than replay a failure.
    /// </summary>
    Task ReleaseAsync(string idempotencyKey, CancellationToken cancellationToken = default);
}
