using System.Net;
using System.Security.Cryptography;
using System.Text;
using MageRide.Iam.Configuration;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Iam.Auth;

/// <summary>
/// The optional IP allow-list AL-37 kept as a compensating control for the six internal roles.
/// </summary>
/// <remarks>
/// <para>
/// Off unless <c>Auth:InternalRoleIpAllowList</c> names at least one CIDR — the ADD calls it
/// optional, and a platform whose only super admin is at home on a dynamic address would
/// otherwise be one DHCP lease from having nobody who can sign in.
/// </para>
/// <para>
/// It applies to <em>internal</em> roles only. A fleet owner is a customer, not staff, and an
/// office range is not where a fleet owner signs in from.
/// </para>
/// </remarks>
public sealed class InternalAccessPolicy
{
    private readonly IReadOnlyList<IPNetwork> _allowed;
    private readonly AuthPolicyOptions _options;
    private readonly ILogger<InternalAccessPolicy> _logger;

    public InternalAccessPolicy(IOptions<AuthPolicyOptions> options, ILogger<InternalAccessPolicy> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;

        var networks = new List<IPNetwork>();
        foreach (var entry in _options.InternalRoleIpAllowList)
        {
            if (TryParseNetwork(entry, out var network))
            {
                networks.Add(network);
            }
            else
            {
                // Loud, and it does not disable the list: a typo that silently widened the
                // allow-list to nothing would be a security control that quietly stopped
                // working, which is worse than one that refuses a legitimate admin.
                _logger.LogError(
                    "Auth:InternalRoleIpAllowList entry '{Entry}' is not a CIDR or an IP address and was ignored", entry);
            }
        }

        _allowed = networks;
    }

    /// <summary>Whether the list is in force at all.</summary>
    public bool IsEnabled => _allowed.Count > 0;

    /// <summary>
    /// Refuses an internal-role sign-in from outside the allow-list.
    /// </summary>
    /// <param name="address">The caller's address, from <see cref="ClientAddress"/>.</param>
    /// <param name="roles">Every role the account holds; the union decides (AL-06).</param>
    public void Enforce(IPAddress? address, IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (!IsEnabled || !roles.Any(MageRideRoles.Internal.Contains))
        {
            return;
        }

        if (address is not null && _allowed.Any(network => network.Contains(address)))
        {
            return;
        }

        _logger.LogWarning(
            "Refused an internal-role sign-in from {Address}: outside Auth:InternalRoleIpAllowList (AL-37)",
            address?.ToString() ?? "an unknown address");

        // Deliberately the same 403 an unprivileged account gets. Telling a caller "right
        // password, wrong network" confirms the credential.
        throw new MageRideException(
            MageRideErrors.Forbidden, "This account may not sign in from this network.");
    }

    /// <summary>
    /// The caller's address. <c>X-Forwarded-For</c> when configured, because every request
    /// arrives through the YARP gateway and the socket address is the gateway's own.
    /// </summary>
    /// <remarks>
    /// The leftmost entry is the client as the first proxy saw it. It is client-controlled and
    /// therefore forgeable by anyone who can reach this service directly — which is exactly why
    /// <c>Auth:TrustForwardedFor</c> exists and why the allow-list is a compensating control
    /// rather than the primary one.
    /// </remarks>
    public IPAddress? ClientAddress(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_options.TrustForwardedFor)
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                var first = forwarded.Split(',')[0].Trim();

                // "203.0.113.7:41234" — some proxies append the source port.
                if (IPAddress.TryParse(first, out var parsed) ||
                    (first.LastIndexOf(':') > first.LastIndexOf(']') &&
                     IPAddress.TryParse(first[..first.LastIndexOf(':')], out parsed)))
                {
                    return Normalise(parsed);
                }
            }
        }

        var remote = context.Connection.RemoteIpAddress;
        return remote is null ? null : Normalise(remote);
    }

    /// <summary>An IPv4-mapped IPv6 address and its IPv4 form are the same host.</summary>
    private static IPAddress Normalise(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool TryParseNetwork(string? entry, out IPNetwork network)
    {
        network = default;

        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var text = entry.Trim();

        if (IPNetwork.TryParse(text, out network))
        {
            return true;
        }

        // A bare address is the single-host network, which is how an operator will write it.
        if (IPAddress.TryParse(text, out var single))
        {
            network = new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        return false;
    }
}

/// <summary>
/// Derives the <c>device_id</c> a browser session is bound to.
/// </summary>
/// <remarks>
/// <para>
/// The portal sign-in bodies carry no <c>deviceId</c> — the contract's password and OIDC schemas
/// are <c>{email, password}</c> and <c>{idToken}</c> — but every session needs one: it is the
/// <c>device_id</c> claim, it is the <c>iam.devices</c> row a session references, and it is the
/// "session binding" AL-37 keeps as a compensating control. So it is derived from what the browser
/// does send.
/// </para>
/// <para>
/// The user agent alone, deliberately, and not the address: an admin whose phone hands off from
/// Wi-Fi to mobile data changes address mid-session and must not be signed out for it. That makes
/// the binding coarse — it separates browsers, not people — which is the honest description of
/// what a cookie-less server-side binding can be. It is a second lock on a door that the password
/// and the allow-list already hold.
/// </para>
/// </remarks>
public static class WebDeviceKeys
{
    /// <summary>Prefix, so a browser key can never collide with a handset's install id.</summary>
    public const string Prefix = "web-";

    public static string From(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            userAgent = "unknown";
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(userAgent));

        // 128 bits of the digest: enough that two real browsers do not collide, short enough to
        // read in a session row.
        return Prefix + Base64UrlEncoder.Encode(digest[..16]);
    }
}
