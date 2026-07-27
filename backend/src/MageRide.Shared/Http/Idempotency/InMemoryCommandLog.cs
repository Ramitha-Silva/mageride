using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MageRide.Shared.Http.Idempotency;

/// <summary>
/// Process-local <see cref="ICommandLog"/>. For tests and single-instance local runs only — it
/// gives no replay guarantee across instances or restarts, which is the whole point of R-14.
/// </summary>
public sealed class InMemoryCommandLog : ICommandLog
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Task<CommandLogReservation> TryReserveAsync(CommandLogKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var reservation = new Entry(key.RequestHash, null);
        var existing = _entries.GetOrAdd(key.IdempotencyKey, reservation);

        if (ReferenceEquals(existing, reservation))
        {
            return Task.FromResult(CommandLogReservation.Reserved);
        }

        if (!CryptographicOperations.FixedTimeEquals(existing.RequestHash, key.RequestHash))
        {
            return Task.FromResult(CommandLogReservation.Mismatch);
        }

        return Task.FromResult(existing.Response is { } response
            ? CommandLogReservation.Replay(response)
            : CommandLogReservation.InProgress);
    }

    public Task CompleteAsync(string idempotencyKey, CommandLogResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(response);

        _entries.AddOrUpdate(
            idempotencyKey,
            _ => new Entry([], response),
            (_, existing) => existing with { Response = response });

        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (_entries.TryGetValue(idempotencyKey, out var existing) && existing.Response is null)
        {
            _entries.TryRemove(new KeyValuePair<string, Entry>(idempotencyKey, existing));
        }

        return Task.CompletedTask;
    }

    private sealed record Entry(byte[] RequestHash, CommandLogResponse? Response);
}
