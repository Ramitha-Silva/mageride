using Dapper;
using MageRide.Fare.Domain;
using MageRide.Shared.Observability;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Fare.Observability;

/// <summary>
/// ADD §13.3.1 row 7 — payments sitting in <c>Overpaid</c> (R-19, ADD §11.14) — as a gauge (C119).
/// </summary>
/// <remarks>
/// <para>
/// A late gateway callback on a ride that was already settled in cash. The platform is holding the
/// fare twice and owes a refund, and §13.3.1 pages when any row sits there for more than an hour.
/// </para>
/// <para>
/// <b>Here rather than in the analytics read model, because it is one table with one owner.</b>
/// C119 put it there first on the theory that §13.3.1's last two rows each span two bounded
/// contexts; that is not true of this one — the query is a single <c>count(*)</c> over
/// <c>fares.ride_payments</c>, which is fare-svc's. It also mattered practically: the read model is
/// a library hosted only by admin-bff, which is neither a container in
/// <c>docker-compose.skeleton.yml</c> nor a scrape target, so the page was silent in the deployment
/// shape the code is actually built in. fare-svc is both.
/// </para>
/// <para>
/// Migration 1008's <c>ix_ride_payments_overpaid</c> is the partial index this count runs on — the
/// same one behind admin-bff's refund queue (SCR-AP-006), so the page and the screen an operator
/// opens from it are counting the same rows.
/// </para>
/// </remarks>
internal static class OverpaidGauge
{
    /// <remarks>
    /// A count and nothing else: <c>Overpaid</c> is a state, not an age, and §13.3.1 puts the hour
    /// in the alert's <c>for:</c> rather than in the predicate. Held in a <c>const</c> so the
    /// service's SQL conventions apply to it like any other statement.
    /// </remarks>
    internal const string CountSql =
        "SELECT count(*)::int FROM fares.ride_payments WHERE state = @Overpaid;";

    /// <summary>Publishes the gauge onto <paramref name="gauges"/>.</summary>
    public static void Publish(ScrapedGauges gauges)
    {
        ArgumentNullException.ThrowIfNull(gauges);

        gauges.Publish(
            MageRideDiagnostics.PaymentsOverpaidGauge,
            "{payment}",
            "Payments in Overpaid awaiting a Finance decision (ADD §13.3.1, R-19).",
            CountAsync);
    }

    /// <summary>The count behind the gauge. Internal so a test can assert the rule, not the scrape.</summary>
    public static async Task<int> CountAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        var connections = services.GetRequiredService<INpgsqlConnectionFactory>();

        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            CountSql,
            new { Overpaid = RidePaymentStates.Overpaid },
            commandTimeout: connections.CommandTimeoutSeconds,
            cancellationToken: cancellationToken));
    }
}
