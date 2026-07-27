namespace MageRide.Shared.Http.Idempotency;

/// <summary>
/// The identity of a mutating command, as stored in a service's command log
/// (ADD §9.1 <c>rides.command_log</c>, R-14).
/// </summary>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> header. Primary key.</param>
/// <param name="Command">Logical command name — the route pattern, e.g. <c>POST /v1/rides/request</c>.</param>
/// <param name="RequestHash">SHA-256 over method, path, query and body. Detects key reuse with a
/// different payload.</param>
/// <param name="ActorType">Who issued it: <c>passenger</c>, <c>driver</c>, <c>admin</c>, <c>system</c>…</param>
/// <param name="ActorId">Authenticated subject, when there is one.</param>
/// <param name="AggregateId">Aggregate the command targets (<c>ride_id</c> in <c>rides.command_log</c>),
/// when it is known before execution.</param>
public sealed record CommandLogKey(
    string IdempotencyKey,
    string Command,
    byte[] RequestHash,
    string ActorType,
    Guid? ActorId = null,
    Guid? AggregateId = null);

/// <summary>A response captured for replay.</summary>
/// <param name="Status">Status of the original response.</param>
/// <param name="Body">The original body, byte for byte.</param>
/// <param name="ContentType">Content type of the original response.</param>
public sealed record CommandLogResponse(int Status, byte[] Body, string? ContentType);

/// <summary>What a reservation attempt found.</summary>
public enum CommandLogOutcome
{
    /// <summary>This caller owns the key and must now execute the command.</summary>
    Reserved,

    /// <summary>The key already completed; <see cref="CommandLogReservation.Response"/> is the
    /// original response and must be returned verbatim (R-14, ADD §11.13).</summary>
    Replay,

    /// <summary>The key is reserved but not yet complete — a concurrent in-flight duplicate.</summary>
    InProgress,

    /// <summary>The key exists against a different request payload. Not replayable.</summary>
    Mismatch,
}

/// <param name="Outcome">What the store found.</param>
/// <param name="Response">Set only when <see cref="CommandLogOutcome.Replay"/>.</param>
public sealed record CommandLogReservation(CommandLogOutcome Outcome, CommandLogResponse? Response = null)
{
    public static readonly CommandLogReservation Reserved = new(CommandLogOutcome.Reserved);
    public static readonly CommandLogReservation InProgress = new(CommandLogOutcome.InProgress);
    public static readonly CommandLogReservation Mismatch = new(CommandLogOutcome.Mismatch);

    public static CommandLogReservation Replay(CommandLogResponse response) =>
        new(CommandLogOutcome.Replay, response ?? throw new ArgumentNullException(nameof(response)));
}
