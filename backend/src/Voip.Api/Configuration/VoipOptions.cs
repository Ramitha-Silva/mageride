using System.ComponentModel.DataAnnotations;

namespace MageRide.Voip.Configuration;

/// <summary>
/// voip-svc's knobs. Every default is argued at its declaration; the ones with no spec behind them
/// say so.
/// </summary>
public sealed class VoipOptions
{
    public const string SectionName = "Voip";

    public LiveKitOptions LiveKit { get; set; } = new();

    /// <summary>
    /// How long a minted join token may be presented for.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it; D6' §6 says "expiring at trip end", which a token cannot know.</b> A
    /// LiveKit token is a <em>join</em> credential — its <c>exp</c> is checked at connect and never
    /// again — so a token whose lifetime were the whole trip would be a five-minute join window
    /// stretched into an hour-long one for no benefit. Five minutes is long enough for a handset to
    /// mint, ring and connect over a bad mobile link, and short enough that a token that leaked is
    /// dead before it is useful. The property D6' §6 is actually asking for — that a call cannot
    /// outlive its ride — is held by <see cref="Messaging.RideTerminalHandler"/> closing the room,
    /// and by minting being refused once the ride is terminal.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan TokenTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether the room-teardown consumer runs.
    /// </summary>
    /// <remarks>
    /// <b>Off ⇒ a call can outlive its ride.</b> Left switchable only because a deployment with no
    /// broker cannot run it at all, and the service says so loudly at start-up rather than looking
    /// healthy.
    /// </remarks>
    public bool RoomTeardownEnabled { get; set; } = true;

    /// <summary>D6' §2: "consumer group per service".</summary>
    public string ConsumerGroup { get; set; } = "voip-svc";

    public sealed class LiveKitOptions
    {
        /// <summary>The SFU's websocket endpoint, handed to the client with its token.</summary>
        /// <remarks>Unset ⇒ no token can be minted and every call answers 503.</remarks>
        public string? WsUrl { get; set; }

        /// <summary>
        /// The server API root, for closing a room at trip end.
        /// </summary>
        /// <remarks>
        /// Usually the same host over https. Unset ⇒ rooms are never torn down and a call can
        /// outlive its ride — which is a warning at start-up, not a silent degradation.
        /// </remarks>
        public string? ApiUrl { get; set; }

        /// <summary>LiveKit API key. Becomes the token's <c>iss</c>.</summary>
        public string? ApiKey { get; set; }

        /// <summary>LiveKit API secret — the HS256 signing key. Never logged.</summary>
        public string? ApiSecret { get; set; }

        /// <summary>How long the server API may take to close a room.</summary>
        [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
        public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(5);
    }
}
