using Dapper;
using MageRide.Analytics.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.Analytics.Persistence;

/// <summary>
/// The three real-time cards of SCR-AP-002, read from the operational tables at request time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not from the rollup, and not cached.</b> D6' §I-28.5: the live block "bypasses the period
/// filter" and is fetched real-time. These are facts about <em>this instant</em> — how many drivers
/// are online, how deep the verification queues are, how many tickets are unresolved — and a
/// materialised copy of any of them is a different question with a similar name. An operator filters
/// the dashboard to last March; the three cards must not change.
/// </para>
/// <para>
/// <b>One statement, three subqueries.</b> The card row is drawn together, so the three counts
/// should be consistent with each other and cost one round trip rather than three.
/// </para>
/// </remarks>
internal sealed class LiveCountersRepository(INpgsqlConnectionFactory connections)
{
    /// <summary>
    /// The three live counters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Online drivers is presence <em>and</em> freshness.</b> <c>dispatch.driver_presence</c> is
    /// the durable half of a state whose live half is a Redis hash with a 60 s TTL (R-08), so a
    /// driver whose app was killed leaves the row saying <c>AVAILABLE</c> for ever. The cutoff
    /// arrives as a parameter computed from the service's <see cref="TimeProvider"/> rather than as
    /// <c>now()</c>, so the boundary is one clock and a test can state where it falls.
    /// <c>ON_RIDE</c> and <c>OFFERED</c> count: a driver carrying a passenger is online.
    /// </para>
    /// <para>
    /// <b>Pending verifications is the sum of AL-39's three queues</b> — driving licence, vehicle
    /// registration, fleet-org approval — because that is what SCR-AP-003 is after the split and the
    /// card on SCR-AP-002 links there. Each is counted by its <em>subject</em> (a driver, a vehicle,
    /// an organisation) and not by its rows: a licence with four doubtful fields is one driver to
    /// review, and counting fields would tell an officer their queue was four times as deep as it is.
    /// admin-bff's own queue endpoints (C063) must agree with these three predicates; if they ever
    /// disagree, the card and the screen it opens disagree, which is worse than either being wrong.
    /// </para>
    /// <para>
    /// <b>Open tickets counts <c>IN_PROGRESS</c> too.</b> "Open" on an operations dashboard is the
    /// work outstanding, and a ticket an agent has picked up is still outstanding. Counted as
    /// "not <c>RESOLVED</c>" rather than as a list of live values, so a fourth status added to
    /// <c>support.tickets</c> later is counted rather than silently dropped.
    /// </para>
    /// </remarks>
    internal const string LiveSql =
        """
        SELECT
            (SELECT count(*) FROM dispatch.driver_presence p
              WHERE p.state <> @PresenceOffline
                AND p.last_seen_at IS NOT NULL
                AND p.last_seen_at >= @PresenceCutoff)::int AS online_drivers,

            ((SELECT count(DISTINCT d.driver_id)
                FROM registry.documents d
                JOIN registry.document_fields f ON f.document_id = d.id
               WHERE d.kind = @DrivingLicenceKind
                 AND d.driver_id IS NOT NULL
                 AND f.verify_status = @FieldPending)
           + (SELECT count(DISTINCT s.vehicle_id)
                FROM registry.onboarding_steps s
               WHERE s.status = @StepPendingReview)
           + (SELECT count(*)
                FROM registry.fleets fl
               WHERE fl.status = @FleetPending))::int AS pending_verifications,

            (SELECT count(*) FROM support.tickets t
              WHERE t.status <> @TicketResolved)::int AS open_tickets;
        """;

    /// <summary>Reads the three cards. <paramref name="presenceCutoff"/> is the oldest heartbeat that still counts as online.</summary>
    public async Task<DashboardLive> ReadAsync(DateTimeOffset presenceCutoff, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleAsync<DashboardLive>(new CommandDefinition(
            LiveSql,
            new
            {
                PresenceCutoff = presenceCutoff,
                PresenceOffline = AnalyticsVocabulary.PresenceOffline,
                DrivingLicenceKind = AnalyticsVocabulary.DrivingLicenceKind,
                FieldPending = AnalyticsVocabulary.FieldPending,
                StepPendingReview = AnalyticsVocabulary.StepPendingReview,
                FleetPending = AnalyticsVocabulary.FleetPending,
                TicketResolved = AnalyticsVocabulary.TicketResolved,
            },
            commandTimeout: connections.CommandTimeoutSeconds,
            cancellationToken: cancellationToken));
    }
}
