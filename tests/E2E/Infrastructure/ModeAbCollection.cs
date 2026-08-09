using MageRide.TestKit;

// -------------------------------------------------------------------------------------------
// One collection at a time, for the whole assembly.
//
// This assembly holds two fleets, and each one owns global state in the containers its collection
// starts: C120's `ModeCFleet` truncates the dispatch plane and FLUSHES REDIS at start-up, and
// C121's `ModeAbFleet` does the same to the tracker and session planes. xUnit runs collections in
// parallel by default, so without this the two would race — and the failure would not look like a
// race. It would look like a Mode B passenger losing their entitlement half way through a scenario,
// or a tracker's IMEI cache entry vanishing, because a fleet in the other collection flushed the
// keyspace the first one was using.
//
// The collections take a fixture instance each (the TestKit gives one container per collection), so
// the two never share a database — but they do share this process, this build host's memory and
// this machine's Docker daemon, and eight containers plus thirteen services at once is not what
// either suite was measured at. Sequential collections also mean the first collection's containers
// are disposed before the second's start.
// -------------------------------------------------------------------------------------------
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// One Postgres, one Redis, one Redpanda and one EMQX, shared by every Mode A/B scenario.
/// </summary>
/// <remarks>
/// <para>
/// All four, always — a Mode A/B journey touches every one of them. The session, the org and the
/// grant are Postgres; the IMEI binding, the entitlement SET, <c>veh:meta</c> and the cell streams
/// are Redis; the outboxes, <c>telemetry.raw</c> and <c>telemetry.normalized</c> are Redpanda; and
/// the tracker's own publish, the bridge's shared subscription and T-04's retained presence pair
/// are the broker.
/// </para>
/// <para>
/// A separate collection from <see cref="ModeCCollection"/> rather than a shared one, so the two
/// fleets get a container set each. They reset different things at start-up and each reset is
/// destructive to the other; sharing one Postgres would make "which suite ran first" part of every
/// assertion here.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ModeAbCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>,
      ICollectionFixture<RedpandaFixture>,
      ICollectionFixture<EmqxFixture>
{
    public const string Name = "mageride-e2e-mode-ab";
}
