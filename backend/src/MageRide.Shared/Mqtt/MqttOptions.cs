using System.ComponentModel.DataAnnotations;

namespace MageRide.Shared.Mqtt;

/// <summary>
/// How a component reaches EMQX and how its MQTT session JWT is minted (<c>Mqtt</c> section,
/// D6' §3.2, E-02, D-21).
/// </summary>
public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    /// <summary>Broker host, e.g. <c>emqx</c> on the compose network.</summary>
    [Required]
    public string Host { get; set; } = "emqx";

    /// <summary>
    /// Broker port. 1883 is the in-cluster plaintext listener; 8883 is the MQTTS one hardware
    /// trackers use and 8084 the WSS one mobile uses (<c>infra/deploy/emqx/emqx.conf</c>).
    /// </summary>
    [Range(1, 65_535)]
    public int Port { get; set; } = 1883;

    /// <summary>TLS on the broker connection. Off for the in-cluster 1883 listener.</summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// The HMAC secret EMQX validates session tokens with — its
    /// <c>EMQX_AUTHENTICATION__1__SECRET</c>. Must match exactly or every CONNECT is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the development form and it is temporary.</b> D6' §3.2 mints the session JWT in
    /// provisioning-svc (C030) and has EMQX validate it as RS256 against a JWKS with D-21's
    /// 15-minute cache; <c>emqx.conf</c> already carries that block, commented, for C125. A shared
    /// symmetric secret is what makes the walking skeleton self-contained before provisioning-svc
    /// exists — it also means anything holding this secret can mint a token for any vehicle, which
    /// is precisely why it does not survive into the replica.
    /// </para>
    /// <para>
    /// Never a real secret in git (<c>infra/CLAUDE.md</c>): it arrives as configuration.
    /// </para>
    /// </remarks>
    [Required]
    [MinLength(32)]
    public string SessionTokenSecret { get; set; } = string.Empty;

    /// <summary>Issuer stamped on a minted session token.</summary>
    public string Issuer { get; set; } = "mageride-provisioning";

    /// <summary>
    /// The floor on a session token's life. D6' §3.2: <c>TTL = max(active-ride + 2 h, 4 h)</c>,
    /// and <c>iam.yaml</c>'s <c>expiresIn</c> is documented as never less than 14400.
    /// </summary>
    /// <remarks>
    /// The four hours are the point of E-02: a 30-minute API token that fails to refresh in poor
    /// coverage must not take position publishing down with it, so the MQTT credential outlives it
    /// by design.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:05:00", "24:00:00")]
    public TimeSpan SessionTokenMinimumTtl { get; set; } = TimeSpan.FromHours(4);

    /// <summary>How long past an active ride's end a session token stays valid (D6' §3.2).</summary>
    [Range(typeof(TimeSpan), "00:05:00", "12:00:00")]
    public TimeSpan SessionTokenRideGrace { get; set; } = TimeSpan.FromHours(2);
}
