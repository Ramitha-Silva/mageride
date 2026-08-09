using System.Globalization;
using System.Text;
using Dapper;
using MageRide.TestKit;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// Everyone a scenario moved money for, and every entry that moved it.
/// </summary>
/// <remarks>
/// <para>
/// C123's failures are almost never local either, and they are worse than C120's: a money assertion
/// that fails says "the balance is 40,000 and I expected 30,000", which tells you nothing at all
/// about <em>which</em> movement was wrong. So the diagnosis is the statement — every account a
/// scenario named, its balance and its mirror, and then every entry that touched any of them with
/// its kind, its idempotency key and both legs. Ninety per cent of the time the answer is visible in
/// the key column: a fee charged twice under two keys, a replay that was not a replay, a refund
/// keyed on the wrong row.
/// </para>
/// <para>
/// The two rows below the entries are the two invariants this suite exists for. <b>Σ postings</b> is
/// D-09 over the whole ledger and must be zero whatever else went wrong; <b>drift</b> is
/// <c>billing.accounts.balance_minor</c> against the sum of that account's own legs, which catches
/// the failure the balanced-entry trigger cannot see — a materialised balance that stopped agreeing
/// with the postings it is supposed to mirror.
/// </para>
/// <para>
/// Accounts are collected by the scenario rather than discovered, for <see cref="RideJournal"/>'s
/// reason: one database is shared with every other suite in the run, and a report that printed a
/// hundred strangers' wallets would be no better than none.
/// </para>
/// </remarks>
internal sealed class LedgerJournal(PostgresFixture postgres)
{
    /// <summary>Marks a message as already carrying a statement, so it is not appended twice.</summary>
    private const string Marker = "\n── ledger ──";

    /// <summary>
    /// Runs <paramref name="scenario"/> and, if anything in it fails, re-throws with the statement
    /// of every party it named appended.
    /// </summary>
    public async Task AroundAsync(Func<List<Guid>, Task> scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var owners = new List<Guid>();

        try
        {
            await scenario(owners);
        }
        catch (Exception failure) when (owners.Count > 0 && !ContainsJournal(failure))
        {
            throw new MoneyScenarioException(
                failure.Message + await DescribeAsync(owners), failure);
        }
    }

    /// <summary>
    /// The accounts of everyone named, every entry that touched one, and the two invariants.
    /// </summary>
    public async Task<string> DescribeAsync(IReadOnlyList<Guid> ownerIds)
    {
        ArgumentNullException.ThrowIfNull(ownerIds);

        try
        {
            await using var connection = await postgres.OpenAsync();

            var owners = ownerIds.Distinct().ToArray();
            var report = new StringBuilder(Marker).Append('\n');

            report.Append("  billing.accounts (balance | billing.wallets mirror):\n");

            var accounts = (await connection.QueryAsync<(Guid Id, string OwnerType, Guid? OwnerId, long Balance, long? Mirror)>(
                """
                SELECT a.id, a.owner_type, a.owner_id, a.balance_minor, w.balance_minor
                  FROM billing.accounts a
                  LEFT JOIN billing.wallets w ON w.account_id = a.id
                 WHERE a.owner_id = ANY(@Owners) OR a.owner_id IS NULL
                 ORDER BY a.owner_type, a.created_at;
                """,
                new { Owners = owners })).ToArray();

            foreach (var (id, ownerType, ownerId, balance, mirror) in accounts)
            {
                report.Append(CultureInfo.InvariantCulture,
                    $"    {ownerType,-9} {ownerId?.ToString() ?? "(singleton)",-36} {balance,12:N0}"
                    + $" | {(mirror is null ? "no mirror" : mirror.Value.ToString("N0", CultureInfo.InvariantCulture)),12}\n");
            }

            report.Append("  billing.journal_entries touching them:\n");

            var entries = await connection.QueryAsync<(Guid Id, string Kind, string Key, DateTimeOffset At)>(
                """
                SELECT DISTINCT e.id, e.kind, e.idempotency_key, e.ts
                  FROM billing.journal_entries e
                  JOIN billing.journal_postings p ON p.entry_id = e.id
                  JOIN billing.accounts a ON a.id = p.account_id
                 WHERE a.owner_id = ANY(@Owners)
                 ORDER BY e.ts, e.id;
                """,
                new { Owners = owners });

            foreach (var (entryId, kind, key, at) in entries)
            {
                report.Append(CultureInfo.InvariantCulture, $"    {at:HH:mm:ss.fff}  {kind,-18} {key}\n");

                foreach (var (ownerType, ownerId, amount) in
                         await connection.QueryAsync<(string, Guid?, long)>(
                             """
                             SELECT a.owner_type, a.owner_id, p.amount_minor
                               FROM billing.journal_postings p
                               JOIN billing.accounts a ON a.id = p.account_id
                              WHERE p.entry_id = @EntryId ORDER BY p.id;
                             """,
                             new { EntryId = entryId }))
                {
                    report.Append(CultureInfo.InvariantCulture,
                        $"        {amount,13:N0}  {ownerType} {ownerId?.ToString() ?? "(singleton)"}\n");
                }
            }

            var sum = await connection.ExecuteScalarAsync<long>(
                "SELECT COALESCE(sum(amount_minor), 0)::bigint FROM billing.journal_postings;");

            report.Append(CultureInfo.InvariantCulture,
                $"  Σ every posting on the platform: {sum:N0} (D-09 says 0)\n");

            report.Append("  accounts whose balance disagrees with their own legs:\n");

            var drifted = await connection.QueryAsync<(string OwnerType, Guid? OwnerId, long Balance, long Posted)>(
                """
                SELECT a.owner_type, a.owner_id, a.balance_minor,
                       COALESCE((SELECT sum(p.amount_minor) FROM billing.journal_postings p
                                  WHERE p.account_id = a.id), 0)::bigint
                  FROM billing.accounts a
                 WHERE a.balance_minor <> COALESCE(
                         (SELECT sum(p.amount_minor) FROM billing.journal_postings p
                           WHERE p.account_id = a.id), 0)
                 ORDER BY a.owner_type;
                """);

            var any = false;

            foreach (var (ownerType, ownerId, balance, posted) in drifted)
            {
                any = true;
                report.Append(CultureInfo.InvariantCulture,
                    $"    {ownerType,-9} {ownerId?.ToString() ?? "(singleton)",-36} balance {balance:N0} vs legs {posted:N0}\n");
            }

            if (!any)
            {
                report.Append("    (none)\n");
            }

            return report.ToString();
        }
        catch (Exception diagnosis) when (diagnosis is not OperationCanceledException)
        {
            // A diagnostic that throws would replace the real failure with its own, which is the one
            // outcome worse than no diagnostic at all.
            return $"{Marker} the statement could not be read ({diagnosis.Message})\n";
        }
    }

    private static bool ContainsJournal(Exception failure) =>
        failure.Message.Contains(Marker, StringComparison.Ordinal);
}

/// <summary>A scenario failure with the ledger statement attached.</summary>
/// <remarks>
/// A distinct type rather than a rethrow, for <see cref="RideScenarioException"/>'s reason: xUnit
/// prints the message of whatever it catches, and re-throwing the assertion would lose everything
/// appended to it.
/// </remarks>
internal sealed class MoneyScenarioException(string message, Exception inner) : Exception(message, inner);
