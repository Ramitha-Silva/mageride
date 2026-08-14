namespace MageRide.Security.Tests.AntiSpoof.Mqtt;

/// <summary>
/// The half of "a cross-vehicle publish is refused in <b>every tested configuration</b>" that a
/// connection cannot answer: what the deployed broker policy says about the listeners nobody dialled.
///
/// <para>
/// EMQX has three live listeners in this deployment and they share an authenticator, an ACL file
/// and nothing else. <c>messages_rate</c>, <c>enable_authn</c> and the TLS settings are per
/// listener, so a live test proves the port it connected to and is silent about the other two.
/// These assertions are what make the matrix in <see cref="CrossVehiclePublishTests"/> a matrix
/// rather than a sample.
/// </para>
/// </summary>
[Trait("Category", "AntiSpoof")]
public sealed class BrokerPolicyTests
{
    /// <summary>D-17's per-vehicle publish ceiling, ADD §12.6 and D5' §5.3.</summary>
    private const string PublishCeiling = "5/s";

    /// <summary>
    /// The in-cluster listener's ceiling. Not D-17's: 1883 carries the platform's own connections,
    /// so a per-connection limit there is a per-fleet limit on ingest. Above ADD §3.2's 15,000
    /// msg/s burst budget, so it is a guard rail and never a constraint.
    /// </summary>
    private const string ServiceCeiling = "20000/s";

    [Fact]
    public void The_deployment_has_exactly_the_listeners_it_is_meant_to_have()
    {
        // A fourth live listener is a fourth surface the ACL has to hold, and the one that would
        // appear by accident is plaintext WebSocket — EMQX ships `ws:default` enabled.
        Assert.Equal(
            ["ssl", "tcp", "ws", "wss"],
            BrokerPolicy.Listeners.Keys.Order(StringComparer.Ordinal));

        Assert.True(
            BrokerPolicy.Declares(BrokerPolicy.Listeners["ws"], "enable", "false"),
            "listeners.ws.default (plaintext WebSocket, 8083) must stay disabled: the replica "
            + "publishes only the WSS path, and an open plaintext websocket on the same broker is a "
            + "bypass of it.");
    }

    /// <summary>
    /// D-17's ceiling is on every listener a DEVICE can reach, not just the one the tests dial —
    /// and it is deliberately NOT on the one only the platform reaches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This asserted `5/s` on all three listeners until 2026-08-14, and the `tcp` one was the
    /// ~10 msg/s ingest ceiling C129 measured against a 1,200 msg/s launch target. 1883 is
    /// in-cluster only — the LoadBalancers publish 8883 and 8084, `Mqtt__Port=1883` is every
    /// platform service, and what holds a connection there is mqtt-bridge-svc carrying E-08's
    /// shared subscription for the whole fleet. A per-connection message limit on that listener is
    /// a per-FLEET limit on ingest, and 5/s is one vehicle's rate.
    /// </para>
    /// <para>
    /// So the property is split rather than dropped. D-17 is per vehicle and is asserted where
    /// vehicles are; the service listener still carries a ceiling, because "every live listener
    /// carries one" is worth keeping if 1883 is ever exposed by mistake — it is just not allowed
    /// to be a per-vehicle one.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_device_listener_carries_the_five_messages_a_second_ceiling()
    {
        // ssl = 8883 trackers, wss = 8084 mobile. `ws` is disabled; `tcp` is the service plane.
        var deviceListeners = BrokerPolicy.Listeners
            .Where(listener => listener.Key is "ssl" or "wss");

        var without = deviceListeners
            .Where(listener => !BrokerPolicy.Declares(listener.Value, "messages_rate", PublishCeiling))
            .Select(listener => $"listeners.{listener.Key}.default")
            .ToList();

        Assert.True(
            without.Count == 0,
            $"D-17 sets a 5 msg/s publish ceiling and these DEVICE listeners do not carry it: "
            + string.Join(", ", without)
            + ". A device that connects to an unlimited listener is not rate-limited by the broker "
            + "at all, and position-processor-svc's second line is at twice the rate.");

        // The service listener has a ceiling, and it must not be a per-vehicle one.
        var serviceListener = BrokerPolicy.Listeners["tcp"];

        Assert.False(
            BrokerPolicy.Declares(serviceListener, "messages_rate", PublishCeiling),
            "listeners.tcp.default carries D-17's per-VEHICLE 5 msg/s ceiling. No device reaches "
            + "1883 — it is in-cluster only, and what holds a connection there is mqtt-bridge-svc "
            + "with E-08's shared subscription for the whole fleet — so this caps the platform's "
            + "entire ingest at one vehicle's publish rate. It is the ~10 msg/s ceiling C129 "
            + "measured against a 1,200 msg/s target; load/report.md opens with the correction.");

        Assert.True(
            BrokerPolicy.Declares(serviceListener, "messages_rate", ServiceCeiling),
            $"listeners.tcp.default must still carry a ceiling, and it must be {ServiceCeiling} — "
            + "above ADD §3.2's 15,000 msg/s burst budget, so it bounds a runaway service without "
            + "ever binding on legitimate traffic.");

        // R-09's reconnect-storm control, same argument.
        var withoutConnRate = BrokerPolicy.Listeners
            .Where(listener => !string.Equals(listener.Key, "ws", StringComparison.Ordinal))
            .Where(listener => !BrokerPolicy.Declares(listener.Value, "max_conn_rate", "500/s"))
            .Select(listener => $"listeners.{listener.Key}.default")
            .ToList();

        Assert.Empty(withoutConnRate);
    }

    [Fact]
    public void Authorization_fails_closed_and_says_so_out_loud()
    {
        // EMQX's shipped default for no_match is ALLOW, which would make every rule in acl.conf
        // advisory. This is the single most consequential line in the file.
        Assert.True(
            BrokerPolicy.Declares(BrokerPolicy.ActiveBroker, "no_match", "deny"),
            "authorization.no_match must be `deny`; EMQX defaults it to `allow`.");

        // `ignore` would drop a refused publish silently, and MQTT 3.1.1 gives a device no error
        // code for one — a misprovisioned tracker would publish into a void for its whole 90-day
        // credential and every ACL assertion that watches for a disconnect would hang instead.
        Assert.True(
            BrokerPolicy.Declares(BrokerPolicy.ActiveBroker, "deny_action", "disconnect"),
            "authorization.deny_action must be `disconnect`.");

        Assert.EndsWith("{deny, all}.", BrokerPolicy.ActiveAcl.TrimEnd().TrimEnd('\n'), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every device grant is scoped by <c>${username}</c>, and the username is a verified claim.
    /// </summary>
    /// <remarks>
    /// The two halves are separate mechanisms and both are needed: <c>verify_claims</c> stops a
    /// device <i>authenticating</i> as another vehicle, and the <c>${username}</c> rules stop an
    /// authenticated device <i>publishing</i> under another vehicle's topic. A rule that granted a
    /// bare <c>veh/+/pos/live</c> would pass every ACL test that only ever checks its own topic.
    /// </remarks>
    [Fact]
    public void Every_device_grant_is_bound_to_the_connecting_principal()
    {
        Assert.True(
            BrokerPolicy.Declares(BrokerPolicy.ActiveBroker, "verify_claims", "{ vehicleId = \"${username}\" }")
            || BrokerPolicy.ActiveBroker.Contains("vehicleId = \"${username}\"", StringComparison.Ordinal),
            "emqx.conf must bind the session token's vehicleId claim to the MQTT username, or a "
            + "stolen token would authorise whatever username was typed beside it.");

        // On the tracker listener the username is the certificate's CN instead, which is what lets
        // a hardware device be confined by the same rules with no rule of its own (T-02).
        Assert.True(
            BrokerPolicy.Declares(BrokerPolicy.ActiveBroker, "peer_cert_as_username", "cn"),
            "The tracker zone must derive the username from the client certificate's CN.");

        // Every device topic in the grant is under veh/${username} or sys/diag/${username}. A
        // wildcard here is the whole ACL.
        var deviceGrants = BrokerPolicy.ActiveAcl
            .Split('\n')
            .SkipWhile(line => !line.Contains("{allow, all, publish", StringComparison.Ordinal))
            .TakeWhile(line => !line.Contains("{deny", StringComparison.Ordinal))
            .Where(line => line.Contains("veh/", StringComparison.Ordinal)
                || line.Contains("sys/", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();

        Assert.NotEmpty(deviceGrants);

        Assert.All(deviceGrants, grant => Assert.True(
            grant.Contains("${username}", StringComparison.Ordinal),
            $"An ACL grant reachable by any device is not scoped to the principal: {grant}"));
    }

    /// <summary>
    /// The E-08 shared subscription belongs to the platform, and only to the platform.
    /// </summary>
    /// <remarks>
    /// A device that could join <c>$share/posGroup/…</c> would not merely read every vehicle's
    /// position — it would <i>take</i> messages out of the group, and mqtt-bridge-svc would
    /// silently stop seeing them. The wildcard grants are the only rules in the file that are not
    /// principal-scoped, so the prefix that guards them is load-bearing.
    /// </remarks>
    [Fact]
    public void Only_platform_principals_hold_the_wildcard_subscriptions()
    {
        var wildcard = BrokerPolicy.ActiveAcl
            .Split('\n')
            .Where(line => line.Contains("$share/", StringComparison.Ordinal)
                || line.Contains("veh/#", StringComparison.Ordinal)
                || line.Contains("fleet/#", StringComparison.Ordinal))
            .Where(line => line.TrimStart().StartsWith("{allow", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(wildcard);

        Assert.All(wildcard, rule => Assert.True(
            rule.Contains("\"^svc-\"", StringComparison.Ordinal),
            $"A wildcard grant is not restricted to the `svc-` prefix: {rule.Trim()}"));

        // And nothing at all reaches the broker's own tree.
        Assert.Contains("$SYS/#", BrokerPolicy.ActiveAcl, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tracker listener switches the JWT chain off, and mutual TLS is what replaces it.
    /// </summary>
    /// <remarks>
    /// <c>enable_authn = false</c> is correct on 8883 — a hardware tracker has no session token to
    /// present as a password — but it is only correct while the handshake is genuinely mutual. If
    /// <c>verify_peer</c> or <c>fail_if_no_peer_cert</c> were ever relaxed, that one line would turn
    /// the tracker plane into an unauthenticated listener, and nothing else in the file would object.
    /// </remarks>
    [Fact]
    public void The_listener_that_switches_authentication_off_is_the_one_that_demands_a_certificate()
    {
        var tracker = BrokerPolicy.Listeners["ssl"];

        Assert.True(BrokerPolicy.Declares(tracker, "enable_authn", "false"));
        Assert.True(
            BrokerPolicy.Declares(tracker, "verify", "verify_peer"),
            "The tracker listener has no authenticator, so `verify_peer` IS its authentication.");
        Assert.True(
            BrokerPolicy.Declares(tracker, "fail_if_no_peer_cert", "true"),
            "Without fail_if_no_peer_cert a client that simply offers no certificate is admitted.");

        // The other two listeners keep the JWT chain. A stray `enable_authn = false` on the mobile
        // plane would admit anyone who could reach the port.
        foreach (var name in (string[])["tcp", "wss"])
        {
            Assert.False(
                BrokerPolicy.Declares(BrokerPolicy.Listeners[name], "enable_authn", "false"),
                $"listeners.{name}.default must keep the JWT authenticator (E-02, D-21).");
        }
    }

    /// <summary>
    /// T-12's MQTT path, and the finding C128 opened about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This asserts the current state, not the intended one.</b> <c>enable_crl_check</c> and
    /// <c>crl_cache.refresh_interval</c> are commented out in the deployed <c>emqx.conf</c>, so no
    /// broker in any environment checks a certificate against the CRL provisioning-svc publishes —
    /// a revoked tracker keeps completing the 8883 handshake. See
    /// <c>security/remediation-backlog.md</c> (C128-01) for why it cannot simply be switched on, and
    /// <c>RevocationPropagationTests</c> for the measurement that shows the control works when it is.
    /// </para>
    /// <para>
    /// The assertion is written so that <b>turning it on fails this test</b> and sends the reader to
    /// the backlog entry to delete it. A ledger that outlives its defect is worse than none.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_broker_does_not_yet_check_the_revocation_list_and_that_is_recorded()
    {
        var trackerListener = BrokerPolicy.Listeners["ssl"];

        Assert.False(
            BrokerPolicy.Declares(trackerListener, "enable_crl_check", "true"),
            "enable_crl_check is now on in infra/deploy/emqx/emqx.conf. If the device fleet has been "
            + "re-minted with a CRL distribution point and StepCa:CrlDistributionPoint is set, that "
            + "closes C128-01 — delete the finding from security/remediation-backlog.md and invert "
            + "this assertion. If it was switched on WITHOUT those two, every tracker whose "
            + "certificate carries no CRL distribution point is now refused at the handshake.");

        // The commented-out form has to stay, because it is the procedure. A reader who deletes it
        // loses the only place that records what turning the control on requires.
        Assert.Contains("enable_crl_check", BrokerPolicy.Broker, StringComparison.Ordinal);
        Assert.Contains("crl_cache.refresh_interval", BrokerPolicy.Broker, StringComparison.Ordinal);
    }
}
