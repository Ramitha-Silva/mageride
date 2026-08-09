using MageRide.TestKit;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// What every money scenario shares: the fleet, the skip when Docker is unreachable, the promise
/// that a failure prints the statement — and the fence.
/// </summary>
/// <remarks>
/// <para>
/// Derived classes carry <c>[Collection&lt;MoneyCollection&gt;]</c> and
/// <c>[Trait("Category", "Money")]</c> themselves rather than inheriting them, for
/// <see cref="ModeCScenario"/>'s reason: xUnit resolves a collection from the concrete test class,
/// and the verify command (<c>--filter Category=Money</c>) is not something to leave to attribute
/// inheritance.
/// </para>
/// <para>
/// <b>The balanced-ledger assertion runs here and not in the scenarios.</b> C123's fence is that
/// "after every scenario the double-entry ledger must balance to zero — that assertion is not
/// optional", and an assertion every author has to remember is optional in exactly the way that
/// matters. Putting it in the wrapper makes it structural: a test added to this suite next year is
/// covered by it without anybody writing a line, and a scenario <em>cannot</em> opt out because
/// there is no parameter that would let it.
/// </para>
/// <para>
/// <b>It runs even when the body failed.</b> A scenario that fails halfway through is exactly when
/// an unbalanced ledger is most likely and most worth knowing about — a debit posted and its credit
/// lost is what a half-finished money path looks like. So the fence is in a <c>finally</c>, and when
/// both fail the body's failure is the one reported with the fence's appended to it: the assertion
/// the author wrote is the more informative of the two, and losing it to a generic "the ledger does
/// not balance" would send them looking in the wrong place.
/// </para>
/// </remarks>
public abstract class MoneyScenario(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    private protected async Task RunAsync(Func<MoneyFleet, ScenarioParties, Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Before the journal wrapper, so a skip is a skip rather than a failure with a statement.
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        var fleet = await MoneyFleet.SharedAsync(postgres, redis, redpanda);

        Exception? failure = null;

        try
        {
            await fleet.Journal.AroundAsync(owners => body(fleet, new ScenarioParties(owners)));
        }
        catch (Exception thrown)
        {
            failure = thrown;
        }

        try
        {
            await fleet.AssertLedgerBalancedAsync();
        }
        catch (Exception unbalanced) when (failure is not null)
        {
            throw new MoneyScenarioException(
                $"{failure.Message}\n\n── and the ledger did not balance either ──\n{unbalanced.Message}",
                failure);
        }

        if (failure is not null)
        {
            throw failure;
        }
    }
}

/// <summary>
/// The parties a scenario has created, so a failure knows whose statement to print.
/// </summary>
/// <remarks>
/// A wrapper rather than a bare <c>List&lt;Guid&gt;</c>, for <see cref="ScenarioRides"/>'s reason:
/// <c>parties.Add(driver.DriverId)</c> reads as "this wallet is part of the diagnosis" rather than
/// "remember this for later". Every id that will ever hold a <c>billing.accounts</c> row belongs
/// here — a driver, a passenger, a fleet — and the moment it exists, before the first assertion
/// about it.
/// </remarks>
public sealed class ScenarioParties(List<Guid> owners)
{
    public void Add(Guid ownerId) => owners.Add(ownerId);

    /// <summary>Adds several at once, for the scenarios that open with a whole cast.</summary>
    public void AddRange(params Guid[] ownerIds)
    {
        ArgumentNullException.ThrowIfNull(ownerIds);

        owners.AddRange(ownerIds);
    }
}
