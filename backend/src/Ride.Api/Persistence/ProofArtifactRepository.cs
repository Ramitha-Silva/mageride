using Dapper;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.Ride.Persistence;

/// <summary>What the driver's camera produced, as <c>rides.proof_artifacts</c> keeps it.</summary>
/// <param name="StorageUrl">Where the bytes went. Postgres holds the pointer, never the image (D-36).</param>
/// <param name="Sha256">
/// The digest of the bytes as uploaded — tamper evidence for the dispute this artifact exists to
/// settle (P-10). Computed over what was actually written, so a re-encoded or truncated upload
/// cannot claim to be the original.
/// </param>
/// <param name="CapturedGeo">
/// Where the phone said it was. Optional: a delivery in a lift well has no fix, and refusing the
/// proof over it would strand the parcel.
/// </param>
/// <param name="Id">
/// Chosen by the caller rather than by <c>gen_random_uuid()</c>: the object-store key is named after
/// it before the row exists, so a database-generated id would leave the pointer and the file
/// disagreeing about which artifact this is.
/// </param>
public sealed record NewProofArtifact(
    Guid Id, Guid RideId, string Kind, string StorageUrl, byte[] Sha256, GeoPoint? CapturedGeo);

/// <summary>
/// <c>rides.proof_artifacts</c> (migration 0607) — P-10's photo proof of delivery.
/// </summary>
/// <remarks>
/// Append-only, like <c>rides.transitions</c>: a second attempt at a delivery photo is a second row,
/// never an edit, because the first one is evidence. 365-day retention and PDPA-erasable through
/// <c>pdpa.requests</c> (E-06); nothing here deletes.
/// </remarks>
public interface IProofArtifactRepository
{
    Task<Guid> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        NewProofArtifact artifact,
        CancellationToken cancellationToken);

    /// <summary>Every artifact on a ride, newest first. The receipt read AL-44 surfaces.</summary>
    Task<IReadOnlyList<Guid>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid rideId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IProofArtifactRepository"/>
public sealed class ProofArtifactRepository : IProofArtifactRepository
{
    public async Task<Guid> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        NewProofArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(artifact);

        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO rides.proof_artifacts (id, ride_id, kind, storage_url, sha256, captured_geo)
            VALUES (@Id, @RideId, @Kind, @StorageUrl, @Sha256, @CapturedGeo)
            RETURNING id;
            """,
            artifact,
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid rideId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM rides.proof_artifacts WHERE ride_id = @RideId ORDER BY captured_at DESC, id DESC;",
            new { RideId = rideId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
