using System.Globalization;

namespace MageRide.PublicBff.Tracking;

/// <summary>
/// What the page has already been told: the instant of the last fix it drew and the last status it
/// rendered.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a description of the client's state, not an offset into a log.</b> That is the whole
/// design of this feed. A cursor that indexed a server-side buffer would make public-bff hold ride
/// history — and a stream that could replay it would be the historical replay D-34 forbids, arrived
/// at through the back door. Resuming means "tell me what has changed since I last knew this", and
/// the answer is computed from what is true now.
/// </para>
/// <para>
/// Two consequences worth stating. A client that was disconnected for an hour resumes correctly and
/// learns nothing about the hour — which is what it should see, because a marker's path while
/// nobody was watching is not this surface's to hand out. And a replica that never saw the earlier
/// connection resumes it exactly as well as the one that served it, so the stream survives a
/// reconnect through a different pod with no shared state at all.
/// </para>
/// <para>
/// The wire form is <c>{unixMillis}.{status}</c> — <c>0</c> for "no position yet" and an empty
/// status for "nothing rendered yet". Deliberately readable rather than opaque: it appears in a
/// query string and in an SSE <c>id:</c> field, and an operator reading a proxy log should be able
/// to see what a client thought it knew. It carries no identifier of anything, so there is nothing
/// in it to protect.
/// </para>
/// </remarks>
public readonly record struct TrackCursor(DateTimeOffset? PositionAt, string? Status)
{
    /// <summary>A client that has been told nothing.</summary>
    public static readonly TrackCursor Empty = new(null, null);

    /// <summary>`public-bff.yaml` bounds `?since` at 128 characters.</summary>
    public const int MaxLength = 128;

    public bool IsEmpty => PositionAt is null && string.IsNullOrEmpty(Status);

    /// <summary>
    /// Parses a client-supplied cursor, falling back to <see cref="Empty"/> rather than refusing.
    /// </summary>
    /// <remarks>
    /// A malformed cursor is answered with the current state, which is exactly what an unknown
    /// client should get. Refusing it with a 400 would strand a page whose cursor was mangled by a
    /// proxy on an error it cannot act on, and the worst case of accepting it is one redundant
    /// frame.
    /// </remarks>
    public static TrackCursor Parse(string? value)
    {
        if (value is not { Length: > 0 and <= MaxLength })
        {
            return Empty;
        }

        var separator = value.IndexOf('.', StringComparison.Ordinal);

        if (separator < 0)
        {
            return Empty;
        }

        var status = value[(separator + 1)..];

        return new TrackCursor(
            long.TryParse(value[..separator], CultureInfo.InvariantCulture, out var millis) && millis > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(millis)
                : null,
            status.Length == 0 ? null : status);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{PositionAt?.ToUnixTimeMilliseconds() ?? 0}.{Status ?? string.Empty}");
}
