using Dapper;
using MageRide.Registry.Domain;
using MageRide.Shared.Observability;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Registry.Observability;

/// <summary>
/// ADD §13.3.1 row 8 — a lapsed document on a vehicle E-03 has not suspended — as a gauge (C119).
/// </summary>
/// <remarks>
/// <para>
/// §13.3.1 writes the row as "<c>documents.expires_at &lt; now()</c> AND driver still
/// <c>dispatch_active</c>", and names the cause: "doc-expiry job not running". The column that says
/// so is <c>registry.vehicles.dispatch_state</c> (0303), whose CHECK comment is literally "E-03
/// doc-expiry auto-suspend" and whose only writer is <see cref="Onboarding.DocumentExpiryWorker"/>.
/// A document past <c>expires_at</c> whose vehicle is still <c>ACTIVE</c> is that worker not having
/// run.
/// </para>
/// <para>
/// <b>Two earlier attempts at this predicate were wrong, and both would have been quiet during the
/// outage they exist to catch.</b> Matching on <c>registry.documents.status &lt;&gt; 'EXPIRED'</c>
/// alone counts paperwork rather than exposure — E-03 marks the document *and* suspends the
/// vehicle, and it is the suspension that stops a passenger getting into the car. Joining
/// <c>dispatch.driver_presence</c> instead makes the gauge follow driver shift patterns: E-03 dying
/// at 02:00 would be invisible until the morning, and it drops fleet-owned documents entirely
/// (<c>driver_id IS NULL</c>, AL-50) even though registry-svc's own CLAUDE.md says suspension is
/// per vehicle "because that is where the column is".
/// </para>
/// <para>
/// Here rather than in the analytics read model for the same reason: this is one schema with one
/// owner, and registry-svc is a scrape target in both deployment shapes while the read model's only
/// host (admin-bff) is a container in neither.
/// </para>
/// </remarks>
internal static class ExpiredDocumentsGauge
{
    /// <remarks>
    /// <para>
    /// Counted by <em>vehicle</em>, because a vehicle is what dispatch offers and what a passenger
    /// gets into. Four lapsed documents on one car is one car to take off the road.
    /// </para>
    /// <para>
    /// The instant is a parameter rather than <c>now()</c> so one clock decides and a test can state
    /// where the boundary falls — the same rule the rest of this service's timed queries follow.
    /// </para>
    /// </remarks>
    internal const string CountSql =
        """
        SELECT count(DISTINCT d.vehicle_id)::int
          FROM registry.documents d
          JOIN registry.vehicles v ON v.id = d.vehicle_id
         WHERE d.expires_at IS NOT NULL
           AND d.expires_at < @AsOf
           AND v.dispatch_state = @DispatchActive;
        """;

    /// <summary>Publishes the gauge onto <paramref name="gauges"/>.</summary>
    public static void Publish(ScrapedGauges gauges)
    {
        ArgumentNullException.ThrowIfNull(gauges);

        gauges.Publish(
            MageRideDiagnostics.ExpiredDocumentsDispatchingGauge,
            "{vehicle}",
            "Vehicles still dispatchable on a lapsed document E-03 has not suspended (ADD §13.3.1).",
            CountAsync);
    }

    /// <summary>The count behind the gauge. Internal so a test can assert the rule, not the scrape.</summary>
    public static async Task<int> CountAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        var connections = services.GetRequiredService<INpgsqlConnectionFactory>();
        var clock = services.GetRequiredService<TimeProvider>();

        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            CountSql,
            new { AsOf = clock.GetUtcNow(), DispatchActive = DispatchStates.Active },
            commandTimeout: connections.CommandTimeoutSeconds,
            cancellationToken: cancellationToken));
    }
}
