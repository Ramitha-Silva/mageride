using MageRide.TestKit;

namespace MageRide.Shared.Tests.Migrations;

/// <summary>
/// C010 — proves the third TestKit fixture actually starts a broker and that the D6' §2.1
/// topic registry can be created on it, so a wave-2 consumer test has a working harness on day
/// one rather than discovering the fixture is broken.
/// </summary>
[Collection<RedpandaCollection>]
public sealed class RedpandaHarnessTests(RedpandaFixture redpanda)
{
    [Fact]
    public void The_broker_exposes_a_bootstrap_address()
    {
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        Assert.Matches(@"^[^:]+:\d+$", redpanda.BootstrapServers);
    }

    [Fact]
    public async Task The_six_registry_topics_can_be_created()
    {
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await redpanda.CreateRegistryTopicsAsync();

        var topics = await redpanda.ListTopicsAsync();

        // The names, not just the count: a producer keyed on `ride.events` and a broker holding
        // `rides.events` fail at runtime, never at build time.
        foreach (var expected in RedpandaFixture.Topics)
        {
            Assert.Contains(expected, topics);
        }
    }

    [Fact]
    public async Task Creating_a_topic_twice_is_not_an_error()
    {
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await redpanda.CreateTopicAsync("harness.idempotent");
        await redpanda.CreateTopicAsync("harness.idempotent");

        Assert.Contains("harness.idempotent", await redpanda.ListTopicsAsync());
    }
}
