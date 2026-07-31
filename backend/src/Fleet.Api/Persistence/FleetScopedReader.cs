using Dapper;
using MageRide.Fleet.Configuration;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Fleet.Persistence;

/// <summary>
/// Runs a read inside a transaction that Postgres itself has scoped to one organisation.
/// </summary>
/// <remarks>
/// <para>
/// This is the C058 fence — "every read is row-level-security scoped to the caller's org; a
/// cross-org read is a security bug" — expressed as the only way to open a connection in this
/// service. Repositories take a <see cref="NpgsqlConnection"/> and an
/// <see cref="NpgsqlTransaction"/> and cannot obtain either any other way, so a read that forgot
/// to scope itself is not a bug that can be written here: there is nothing to write it with.
/// </para>
/// <para>
/// <b>What the transaction does before the query.</b>
/// <code>
/// SET LOCAL ROLE mageride_fleet_reader;
/// SELECT set_config('app.fleet_id', $1, true);
/// </code>
/// Both are transaction-local, which is what makes this correct under PgBouncer transaction
/// pooling: the next transaction on the same server connection inherits neither. The role change
/// is the load-bearing half — RLS is not applied to a superuser, nor to a table's owner without
/// <c>FORCE</c>, and the service's login role is one or both in every environment this repo runs
/// in. Assuming the reader role for the duration of the read is what puts migration 1806's
/// policies in the path at all.
/// </para>
/// <para>
/// <b>Read-only.</b> The reader role holds <c>SELECT</c> and nothing else, so a write attempted
/// inside a scope fails on a privilege rather than on a policy. Writes run through
/// <see cref="IUnitOfWorkFactory"/> as the service's own login role and are authorised by the
/// membership the endpoint filter resolved — RLS scopes what a fleet can <em>see</em>; who may
/// change it is a sub-role question (US-13.A5).
/// </para>
/// <para>
/// <b>The application still passes <c>fleet_id</c> to every query.</b> Not redundant: the
/// predicate keeps the plan sane (a policy is an extra qual, not an index hint) and it keeps the
/// SQL readable. It is the second lock. The definition of done asks for the first one, and
/// <c>RowLevelSecurityTests</c> asserts it against the database directly, with no application SQL
/// in the way.
/// </para>
/// </remarks>
public interface IFleetScopedReader
{
    /// <summary>Runs <paramref name="read"/> against a connection scoped to <paramref name="fleetId"/>.</summary>
    Task<T> ReadAsync<T>(
        Guid fleetId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> read,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The two statements that put a transaction inside one organisation.
/// </summary>
/// <remarks>
/// Separate from <see cref="IFleetScopedReader"/> because a <b>write</b> needs one of them and not
/// the other: the fleet reader role holds <c>SELECT</c> only, so a transaction that has to update
/// a row stays as the service's login role — but it still has to set <c>app.fleet_id</c>, or the
/// security-barrier views (migration 1806) it reads through match nothing and the request answers
/// "no such vehicle" for an organisation's own vehicle.
/// </remarks>
public static class FleetScope
{
    /// <summary>The NOLOGIN group role migration 1804 creates and 1806 grants the org relations to.</summary>
    public const string ReaderRole = "mageride_fleet_reader";

    /// <summary>The session setting migration 1806's policies and views read.</summary>
    public const string FleetIdSetting = "app.fleet_id";

    /// <summary>
    /// Scopes the transaction to one organisation.
    /// </summary>
    /// <remarks>
    /// <c>set_config(..., is_local => true)</c> rather than <c>SET</c>: the value is a caller's
    /// fleet id and must be a parameter, and transaction-local is what makes it safe under
    /// PgBouncer transaction pooling — the next transaction on this server connection inherits
    /// nothing.
    /// </remarks>
    public static async Task ApplyFleetIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT set_config(@Setting, @FleetId, true);",
            new { Setting = FleetIdSetting, FleetId = fleetId.ToString() },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Drops to the read-only fleet role for the rest of the transaction.
    /// </summary>
    /// <remarks>
    /// The load-bearing half of the fence: RLS is not applied to a superuser, nor to a table's
    /// owner without <c>FORCE</c>, and the service's login role is one or both in every
    /// environment this repo runs in. <c>SET LOCAL ROLE</c> takes an identifier rather than a
    /// parameter, so the role name is a constant in this assembly and never anything a request
    /// supplied.
    /// </remarks>
    public static async Task AssumeReaderRoleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await connection.ExecuteAsync(new CommandDefinition(
            $"SET LOCAL ROLE {ReaderRole};", transaction: transaction, cancellationToken: cancellationToken));
    }
}

/// <inheritdoc cref="IFleetScopedReader"/>
internal sealed class FleetScopedReader(
    INpgsqlConnectionFactory connections,
    IOptions<FleetOptions> options,
    ILogger<FleetScopedReader> logger) : IFleetScopedReader
{
    private readonly FleetOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<T> ReadAsync<T>(
        Guid fleetId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (fleetId == Guid.Empty)
        {
            // Never reached through an endpoint — the filter resolves a membership first — but an
            // empty GUID would set the GUC to the zero UUID and quietly match nothing, which is a
            // "no such organisation" that is really a bug in the caller.
            throw new ArgumentOutOfRangeException(nameof(fleetId), "A fleet-scoped read needs a fleet.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The GUC first, then the role: setting a configuration parameter is something the login
        // role may do and the reader role may not need to, and this order is the one that works
        // whichever grants a deployment has given.
        await FleetScope.ApplyFleetIdAsync(connection, transaction, fleetId, cancellationToken);

        if (_options.RlsEnabled)
        {
            await FleetScope.AssumeReaderRoleAsync(connection, transaction, cancellationToken);
        }

        var result = await read(connection, transaction);

        // A read-only transaction, but committed rather than rolled back: a rollback would log an
        // aborted transaction per request in Postgres's statistics and make a genuinely failed one
        // impossible to spot. Both endings reset the role and the GUC.
        await transaction.CommitAsync(cancellationToken);

        return result;
    }

    /// <summary>
    /// Says, once and loudly, when this service is not doing the one thing its fence names.
    /// </summary>
    internal void WarnIfUnscoped()
    {
        if (!_options.RlsEnabled)
        {
            logger.LogError(
                "Fleet:RlsEnabled is false, so reads run as the service's own login role rather than {Role}: "
                + "{Setting} is still set, so the security-barrier views scope, but migration 1806's policies are "
                + "not in the path and A CROSS-ORG READ OF A BASE TABLE IS THEN PREVENTED BY APPLICATION SQL ALONE "
                + "(C058 fence, ADD §9.5 item 8). The only supported reason to set this is a login role that has "
                + "not been granted membership of that role — fix that and turn it back on.",
                FleetScope.ReaderRole,
                FleetScope.FleetIdSetting);
        }
    }
}
