using Dapper;
using MageRide.Iam.Domain;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary>
/// <c>pdpa.requests</c> — the row <c>DELETE /v1/users/me</c> leaves behind (E-06, US-1.8).
/// </summary>
/// <remarks>
/// <b>Recording only.</b> Fulfilment — the export ZIP, the soft-anonymisation, the statutory hold
/// list — is admin-bff's (C065), and the 30-day clock is the table's own
/// <c>due_by DEFAULT now() + INTERVAL '30 days'</c> rather than something computed here. iam-svc
/// writing a `Fulfilled` status, or touching the account it names, would be doing C065's job
/// without C065's audit trail.
/// </remarks>
public interface IPdpaRequestRepository
{
    /// <summary>The caller's request of this kind that is still open, if any.</summary>
    Task<PdpaRequest?> FindOpenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string kind,
        CancellationToken cancellationToken);

    Task<PdpaRequest> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string kind,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPdpaRequestRepository"/>
public sealed class PdpaRequestRepository : IPdpaRequestRepository
{
    public const string Erasure = "erasure";
    public const string Export = "export";

    /// <summary>
    /// The two statuses that mean "not finished". <c>FulfilledHold</c> is deliberately absent —
    /// it is a completed erasure that a statute forced to retain a subset, not work in flight.
    /// </summary>
    private const string OpenStatuses = "('Received','InProgress')";

    private const string Columns = "id, user_id, kind, status, requested_at, due_by";

    public Task<PdpaRequest?> FindOpenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<PdpaRequest>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM pdpa.requests
              WHERE user_id = @UserId AND kind = @Kind AND status IN {OpenStatuses}
              ORDER BY requested_at
              LIMIT 1;
             """,
            new { UserId = userId, Kind = kind },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PdpaRequest> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleAsync<PdpaRequest>(new CommandDefinition(
            $"INSERT INTO pdpa.requests (user_id, kind) VALUES (@UserId, @Kind) RETURNING {Columns};",
            new { UserId = userId, Kind = kind },
            transaction,
            cancellationToken: cancellationToken));
    }
}
