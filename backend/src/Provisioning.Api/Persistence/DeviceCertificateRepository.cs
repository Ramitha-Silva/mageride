using Dapper;
using MageRide.Provisioning.Domain;
using Npgsql;

namespace MageRide.Provisioning.Persistence;

/// <summary>
/// <c>prov.device_certs</c> — the credential ledger (T-02, migration 0401).
/// </summary>
/// <remarks>
/// <b>A binding has many credentials, and more than one may be live at once.</b> That is what
/// makes rotation safe: the replacement is issued while the outgoing certificate is still inside
/// its own validity, so a tracker that has been out of coverage can still present the old one and
/// come back for the new. Revocation is the other thing entirely — it stamps <c>revoked_at</c> on
/// every row for the binding at once, which is what T-12 measures.
/// </remarks>
public interface IDeviceCertificateRepository
{
    Task<DeviceCertificate> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid bindingId,
        string serial,
        string kind,
        byte[] materialHash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    /// <summary>Revokes every live credential on a binding. Returns the serials it stamped.</summary>
    Task<IReadOnlyList<string>> RevokeForBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid bindingId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken);


    /// <summary>
    /// Whether <paramref name="serial"/> is a credential that may still be presented: issued for
    /// this binding, not revoked, not expired.
    /// </summary>
    Task<bool> IsPresentableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid bindingId,
        string serial,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every revoked certificate that has not yet expired — the whole CRL.
    /// </summary>
    /// <remarks>
    /// Expired entries are dropped because a verifier rejects an expired certificate on its dates
    /// alone; keeping them would grow the list without bound for no added protection (RFC 5280
    /// §3.3 "removed from the CRL after the certificate's expiry").
    /// </remarks>
    Task<IReadOnlyList<Credentials.RevokedCredential>> ListRevokedAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDeviceCertificateRepository"/>
public sealed class DeviceCertificateRepository : IDeviceCertificateRepository
{
    private const string Columns =
        "id, binding_id, serial, kind, issued_at, expires_at, revoked_at, revocation_reason";

    public Task<DeviceCertificate> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid bindingId,
        string serial,
        string kind,
        byte[] materialHash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleAsync<DeviceCertificate>(new CommandDefinition(
            $"""
             INSERT INTO prov.device_certs (binding_id, serial, kind, pem_or_token_hash, issued_at, expires_at)
             VALUES (@BindingId, @Serial, @Kind, @MaterialHash, @IssuedAt, @ExpiresAt)
             RETURNING {Columns};
             """,
            new
            {
                BindingId = bindingId,
                Serial = serial,
                Kind = kind,
                MaterialHash = materialHash,
                IssuedAt = issuedAt,
                ExpiresAt = expiresAt,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<string>> RevokeForBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid bindingId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var serials = await connection.QueryAsync<string>(new CommandDefinition(
            """
            UPDATE prov.device_certs
               SET revoked_at = @Now, revocation_reason = @Reason
             WHERE binding_id = @BindingId AND revoked_at IS NULL
            RETURNING serial;
            """,
            new { BindingId = bindingId, Reason = reason, Now = now },
            transaction,
            cancellationToken: cancellationToken));

        return [.. serials];
    }


    public async Task<bool> IsPresentableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid bindingId,
        string serial,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (
              SELECT 1 FROM prov.device_certs
               WHERE binding_id = @BindingId AND serial = @Serial
                 AND revoked_at IS NULL AND expires_at > @Now);
            """,
            new { BindingId = bindingId, Serial = serial, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Credentials.RevokedCredential>> ListRevokedAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<Credentials.RevokedCredential>(new CommandDefinition(
            $"""
             SELECT serial AS "Serial", revoked_at AS "RevokedAt", revocation_reason AS "Reason"
               FROM prov.device_certs
              WHERE revoked_at IS NOT NULL AND expires_at > @Now AND kind = '{CredentialTypes.X509}'
              ORDER BY revoked_at;
             """,
            new { Now = now },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
