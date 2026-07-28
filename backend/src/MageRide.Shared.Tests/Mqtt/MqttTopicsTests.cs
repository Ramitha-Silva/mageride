using MageRide.Shared.Mqtt;

namespace MageRide.Shared.Tests.Mqtt;

/// <summary>
/// The EMQX topic tree and its shared-subscription filters
/// (<c>backend/contracts/realtime/mqtt-topics.md</c> §1/§4, ADD §7.2, D6' §3.1/§3.3, E-08).
/// </summary>
/// <remarks>
/// These are asserted against the literal strings the contract prints rather than against the
/// builders, because the builders are what is under test — and because <c>acl.conf</c> is written
/// in terms of the same shapes: a topic this file gets wrong is a publish EMQX refuses.
/// </remarks>
public sealed class MqttTopicsTests
{
    private static readonly Guid Vehicle = Guid.Parse("2f9d6b2c-0f4f-4a1e-9c3a-8f1d5b7e6a40");

    [Fact]
    public void The_five_device_topics_are_the_ones_the_contract_prints()
    {
        Assert.Equal($"veh/{Vehicle}/pos/live", MqttTopics.PositionLive(Vehicle));
        Assert.Equal($"veh/{Vehicle}/pos/replay", MqttTopics.PositionReplay(Vehicle));
        Assert.Equal($"veh/{Vehicle}/cmd", MqttTopics.Command(Vehicle));
        Assert.Equal($"veh/{Vehicle}/status", MqttTopics.Status(Vehicle));
        Assert.Equal($"sys/diag/{Vehicle}", MqttTopics.Diagnostics(Vehicle));
    }

    [Fact]
    public void The_bridge_subscribes_to_the_shared_filter_E_08_names()
    {
        // Getting this wrong is the E-08 failure mode: `veh/+/pos/live` without the `$share/` prefix
        // is an ordinary subscription, every replica receives every message, and `telemetry.raw`
        // silently gets one copy per replica.
        Assert.Equal("$share/posGroup/veh/+/pos/live", MqttTopics.SharedPositionLive());
        Assert.Equal("$share/posReplayGroup/veh/+/pos/replay", MqttTopics.SharedPositionReplay());
    }

    [Fact]
    public void Live_and_replay_are_separate_groups_so_a_backlog_cannot_drown_live_traffic() =>
        // R-09. One group for both would let a reconnect storm's replay share the same delivery
        // budget as the samples telling you where vehicles are right now.
        Assert.NotEqual(MqttTopics.LiveShareGroup, MqttTopics.ReplayShareGroup);

    [Theory]
    [InlineData("pos/live", MqttTopicKind.PositionLive)]
    [InlineData("pos/replay", MqttTopicKind.PositionReplay)]
    [InlineData("cmd", MqttTopicKind.Command)]
    [InlineData("status", MqttTopicKind.Status)]
    public void A_concrete_topic_parses_back_to_its_branch_and_its_vehicle(string tail, MqttTopicKind expected)
    {
        Assert.True(MqttTopics.TryParse($"veh/{Vehicle}/{tail}", out var parsed));
        Assert.Equal(expected, parsed.Kind);
        Assert.Equal(Vehicle, parsed.VehicleId);
    }

    [Fact]
    public void Diagnostics_parses_from_its_own_root()
    {
        Assert.True(MqttTopics.TryParse($"sys/diag/{Vehicle}", out var parsed));
        Assert.Equal(MqttTopicKind.Diagnostics, parsed.Kind);
        Assert.Equal(Vehicle, parsed.VehicleId);
    }

    [Theory]
    [InlineData("veh/not-a-uuid/pos/live")]
    [InlineData("veh/+/pos/live")]
    [InlineData("veh/2f9d6b2c-0f4f-4a1e-9c3a-8f1d5b7e6a40/pos")]
    [InlineData("veh/2f9d6b2c-0f4f-4a1e-9c3a-8f1d5b7e6a40/pos/other")]
    [InlineData("fleet/2f9d6b2c-0f4f-4a1e-9c3a-8f1d5b7e6a40/x/pos/live")]
    [InlineData("$SYS/brokers")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_vehicle_topic_does_not_parse(string? topic) =>
        // The bridge takes its Redpanda partition key from the topic, never from the payload,
        // because the topic is the half EMQX authenticated. A topic it cannot read is a message it
        // must not key.
        Assert.False(MqttTopics.TryParse(topic, out _));

    [Fact]
    public void A_wildcard_filter_is_not_mistaken_for_a_concrete_topic() =>
        // `veh/+/pos/live` is what the bridge subscribes with; it is never what it receives.
        Assert.False(MqttTopics.TryParse(MqttTopics.AllPositionsLive, out _));

    [Fact]
    public void The_status_payloads_are_the_two_literals_the_LWT_carries()
    {
        Assert.Equal("online", VehicleStatus.Online);
        Assert.Equal("offline", VehicleStatus.Offline);
    }
}
