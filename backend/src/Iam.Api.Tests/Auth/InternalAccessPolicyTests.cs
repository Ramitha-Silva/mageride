using System.Net;
using MageRide.Iam.Auth;
using MageRide.Iam.Configuration;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.Tests.Auth;

/// <summary>
/// The optional IP allow-list AL-37 kept as a compensating control for internal roles, and the
/// browser binding that is the other half of "session binding".
/// </summary>
public sealed class InternalAccessPolicyTests
{
    private static InternalAccessPolicy Policy(params string[] allowList)
    {
        var options = new AuthPolicyOptions();
        foreach (var entry in allowList)
        {
            options.InternalRoleIpAllowList.Add(entry);
        }

        return new InternalAccessPolicy(Options.Create(options), NullLogger<InternalAccessPolicy>.Instance);
    }

    private static HttpContext From(string? address, bool forwarded = false)
    {
        var context = new DefaultHttpContext();

        if (forwarded)
        {
            context.Request.Headers["X-Forwarded-For"] = address;
        }
        else if (address is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        }

        return context;
    }

    /// <summary>
    /// The ADD calls the list optional, and a platform whose only super admin is on a dynamic
    /// address would otherwise be one DHCP lease from having nobody who can sign in.
    /// </summary>
    [Fact]
    public void An_empty_list_disables_the_check_entirely()
    {
        var policy = Policy();

        Assert.False(policy.IsEnabled);
        policy.Enforce(IPAddress.Parse("203.0.113.7"), [MageRideRoles.SuperAdmin]);
    }

    [Fact]
    public void An_internal_role_inside_the_allow_list_is_let_through()
    {
        Policy("10.20.0.0/16").Enforce(IPAddress.Parse("10.20.4.9"), [MageRideRoles.Admin]);
    }

    [Fact]
    public void An_internal_role_outside_it_is_refused()
    {
        var exception = Assert.Throws<MageRideException>(
            () => Policy("10.20.0.0/16").Enforce(IPAddress.Parse("203.0.113.7"), [MageRideRoles.Admin]));

        // The same 403 an unprivileged account gets. "Right password, wrong network" would
        // confirm the credential.
        Assert.Equal(MageRideErrors.Forbidden.Code, exception.Error.Code);
    }

    /// <summary>
    /// A fleet owner is a customer, not staff. An office range is not where a fleet owner signs
    /// in from, and applying the list to them would lock out every paying user.
    /// </summary>
    [Theory]
    [InlineData(MageRideRoles.FleetOwner)]
    [InlineData(MageRideRoles.Driver)]
    [InlineData(MageRideRoles.Passenger)]
    public void A_non_internal_role_is_never_checked(string role)
    {
        Policy("10.20.0.0/16").Enforce(IPAddress.Parse("203.0.113.7"), [role]);
    }

    /// <summary>Permissions are the union of the roles held (AL-06), so holding one is enough.</summary>
    [Fact]
    public void Holding_any_internal_role_brings_the_list_into_force()
    {
        Assert.Throws<MageRideException>(() => Policy("10.20.0.0/16")
            .Enforce(IPAddress.Parse("203.0.113.7"), [MageRideRoles.FleetOwner, MageRideRoles.SupportCsr]));
    }

    [Fact]
    public void A_bare_address_in_the_list_is_the_single_host_network()
    {
        var policy = Policy("203.0.113.7");

        policy.Enforce(IPAddress.Parse("203.0.113.7"), [MageRideRoles.Admin]);
        Assert.Throws<MageRideException>(() => policy.Enforce(IPAddress.Parse("203.0.113.8"), [MageRideRoles.Admin]));
    }

    /// <summary>
    /// A typo must not silently widen the list to nothing. The entry is dropped and logged, and
    /// the remaining entries still apply.
    /// </summary>
    [Fact]
    public void A_malformed_entry_is_ignored_without_disabling_the_rest()
    {
        var policy = Policy("not-a-cidr", "10.20.0.0/16");

        Assert.True(policy.IsEnabled);
        policy.Enforce(IPAddress.Parse("10.20.4.9"), [MageRideRoles.Admin]);
        Assert.Throws<MageRideException>(() => policy.Enforce(IPAddress.Parse("10.30.4.9"), [MageRideRoles.Admin]));
    }

    [Fact]
    public void An_unknown_address_is_refused_rather_than_allowed()
    {
        Assert.Throws<MageRideException>(() => Policy("10.20.0.0/16").Enforce(null, [MageRideRoles.Admin]));
    }

    [Fact]
    public void The_forwarded_header_carries_the_client_when_it_is_trusted()
    {
        // Every request arrives through the C008 gateway, so the socket address is the gateway's.
        var policy = Policy();

        Assert.Equal(
            IPAddress.Parse("203.0.113.7"),
            policy.ClientAddress(From("203.0.113.7, 10.0.0.1", forwarded: true)));
    }

    [Fact]
    public void A_forwarded_entry_with_a_port_still_resolves()
    {
        Assert.Equal(
            IPAddress.Parse("203.0.113.7"),
            Policy().ClientAddress(From("203.0.113.7:41234", forwarded: true)));
    }

    [Fact]
    public void The_socket_address_is_used_when_the_header_is_not_trusted()
    {
        var options = new AuthPolicyOptions { TrustForwardedFor = false };
        var policy = new InternalAccessPolicy(Options.Create(options), NullLogger<InternalAccessPolicy>.Instance);

        var context = From("10.0.0.1");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";

        Assert.Equal(IPAddress.Parse("10.0.0.1"), policy.ClientAddress(context));
    }

    /// <summary>An IPv4-mapped IPv6 address and its IPv4 form are the same host.</summary>
    [Fact]
    public void An_ipv4_mapped_address_matches_an_ipv4_network()
    {
        var policy = Policy("10.20.0.0/16");
        var mapped = policy.ClientAddress(From("::ffff:10.20.4.9"));

        Assert.Equal(IPAddress.Parse("10.20.4.9"), mapped);
        policy.Enforce(mapped, [MageRideRoles.Admin]);
    }

    [Fact]
    public void An_ipv6_network_works_too()
    {
        Policy("2001:db8::/32").Enforce(IPAddress.Parse("2001:db8::1"), [MageRideRoles.Auditor]);
    }
}

/// <summary>
/// The browser binding a portal session gets, in place of the <c>deviceId</c> the apps send.
/// </summary>
public sealed class WebDeviceKeyTests
{
    [Fact]
    public void The_same_browser_gets_the_same_key()
    {
        Assert.Equal(KeyFor("Mozilla/5.0 Firefox/141.0"), KeyFor("Mozilla/5.0 Firefox/141.0"));
    }

    [Fact]
    public void A_different_browser_gets_a_different_one()
    {
        Assert.NotEqual(KeyFor("Mozilla/5.0 Firefox/141.0"), KeyFor("Mozilla/5.0 Chrome/141.0"));
    }

    /// <summary>
    /// Deliberately not the address: an admin whose laptop hands off from Wi-Fi to a hotspot
    /// changes address mid-session and must not be signed out for it.
    /// </summary>
    [Fact]
    public void The_key_does_not_depend_on_where_the_request_came_from()
    {
        var first = new DefaultHttpContext();
        first.Request.Headers.UserAgent = "Mozilla/5.0 Firefox/141.0";
        first.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");

        var second = new DefaultHttpContext();
        second.Request.Headers.UserAgent = "Mozilla/5.0 Firefox/141.0";
        second.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        Assert.Equal(WebDeviceKeys.From(first), WebDeviceKeys.From(second));
    }

    [Fact]
    public void A_browser_that_sends_no_user_agent_still_gets_a_key()
    {
        var key = WebDeviceKeys.From(new DefaultHttpContext());

        Assert.StartsWith(WebDeviceKeys.Prefix, key, StringComparison.Ordinal);
    }

    /// <summary>The prefix is what stops a browser key colliding with a handset's install id.</summary>
    [Fact]
    public void Every_key_is_prefixed_and_fits_the_column()
    {
        var key = KeyFor("Mozilla/5.0 Firefox/141.0");

        Assert.StartsWith(WebDeviceKeys.Prefix, key, StringComparison.Ordinal);
        Assert.True(key.Length <= 128, $"device_key is capped at 128 characters; this one is {key.Length}");
    }

    private static string KeyFor(string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = userAgent;
        return WebDeviceKeys.From(context);
    }
}
