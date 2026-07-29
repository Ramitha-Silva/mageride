using Dapper;
using Npgsql;

namespace MageRide.Ride.Persistence;

/// <summary>
/// <c>safety.location_request_audit</c> (migration 0904) — P-12's durable record of every
/// booker→rider ping and what became of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A write outside the <c>rides</c> schema, and the only one this service makes.</b> ADD §11.15
/// assigns it here in as many words ("INSERT rides.location_requests …; audit
/// safety.location_request_audit"), and D5' §10 repeats it — "declines logged
/// <c>safety.location_request_audit</c> → repeated declines raise a booker reputation flag". The
/// row is written in the same transaction as the state change it describes, which is the property
/// that makes it an audit rather than a best-effort log: a decline that committed and an audit row
/// that did not would be a decline nobody can investigate.
/// </para>
/// <para>
/// <b>The subject is a digest, never a number</b> (P-03, §0 PII). The table's own column is
/// <c>rider_phone_hash BYTEA NOT NULL</c> for that reason, and the abuse question it answers — "is
/// this booker pinging the same person who keeps refusing?" — is a question about equality, which a
/// digest answers exactly as well as a number would.
/// </para>
/// <para>
/// <b>When safety-svc lands</b> the table is its own; the write becomes an event it consumes. Until
/// then it is here, because a request whose outcome nobody recorded is a P-12 control that does not
/// exist. Raised in the C037 handoff.
/// </para>
/// </remarks>
public interface ILocationRequestAuditRepository
{
    Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid bookerId,
        byte[] riderPhoneHash,
        Guid requestId,
        string decision,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILocationRequestAuditRepository"/>
public sealed class LocationRequestAuditRepository : ILocationRequestAuditRepository
{
    public Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid bookerId,
        byte[] riderPhoneHash,
        Guid requestId,
        string decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(riderPhoneHash);

        // `request_id` is the public handle rather than the surrogate id, and carries no foreign
        // key: migration 0904's header says why — the audit row has to survive the request row
        // being purged, which is the whole point of keeping it in another schema.
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO safety.location_request_audit (booker_id, rider_phone_hash, request_id, decision)
            VALUES (@BookerId, @RiderPhoneHash, @RequestId, @Decision);
            """,
            new
            {
                BookerId = bookerId,
                RiderPhoneHash = riderPhoneHash,
                RequestId = requestId,
                Decision = decision,
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}
