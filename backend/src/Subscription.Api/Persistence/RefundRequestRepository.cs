using Dapper;
using MageRide.Shared.Persistence;

namespace MageRide.Subscriptions.Persistence;

/// <summary>A raised fee-refund request — one <c>support.tickets</c> row, as it actually stands.</summary>
/// <remarks>
/// Exactly the columns 1303 has, and no more. The disputed day and amount are <em>not</em> here:
/// <c>support.tickets</c> has no column for either, and reconstructing them from the description a CSR
/// may have edited would report numbers the platform does not hold. The create response carries them
/// because at that moment this service has just read them from its own charge row.
/// </remarks>
public sealed record FeeRefundTicket(
    Guid RequestId, Guid DriverId, string Status, string Description, DateTimeOffset CreatedAt);

/// <summary>US-9.23's fee-refund intake, which is a support ticket (migration 1303).</summary>
internal interface IRefundRequestRepository
{
    /// <summary>Raises the ticket and returns it.</summary>
    Task<FeeRefundTicket> CreateAsync(
        Guid driverId, string description, Guid? rideId, CancellationToken cancellationToken);

    /// <summary>The driver's own fee-refund requests, newest first.</summary>
    Task<IReadOnlyList<FeeRefundTicket>> ListAsync(
        Guid driverId, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRefundRequestRepository"/>
/// <remarks>
/// <para>
/// <b>The only row this service writes outside <c>billing.*</c>, and it is a deliberate, named
/// exception.</b> Migration 1303's own table comment says <c>support.tickets</c> "also carries the
/// driver daily-fee refund request (US-9.23) as an ordinary category" — the row was designed for this
/// caller. What makes it this service's to write is the validation: only subscription-svc can say
/// whether the driver was in fact charged on the day they are disputing, and a ticket raised against a
/// day with no charge is a queue item Finance has to close by hand.
/// </para>
/// <para>
/// <b>The lifecycle is not ours.</b> This writes one <c>OPEN</c> row and reads back the driver's own;
/// it never sets <c>status</c>, never writes <c>admin_response</c> and never resolves anything.
/// support-svc (C053) owns the queue and admin-bff (C065) owns the reversal that answers it —
/// <c>POST /v1/admin/drivers/wallet/{id}/reverse-fee</c>, which is a wallet credit of kind
/// <c>adjustment</c> and no business of this service's. When C053 lands, this becomes a forward to its
/// ticket route and the SQL below is deleted; raised in the C047 handoff.
/// </para>
/// </remarks>
internal sealed class RefundRequestRepository(INpgsqlConnectionFactory connections) : IRefundRequestRepository
{
    /// <summary>
    /// The <c>support.tickets.category</c> a fee-refund request carries.
    /// </summary>
    /// <remarks>
    /// 1303 leaves the column free text on purpose ("support-svc owns it rather than a CHECK an admin
    /// cannot edit"), so the value is a constant here rather than an enum — and it is distinct from
    /// content-svc's <c>daily_fee</c> FAQ category, so the Support queue can tell a question about the
    /// fee from a claim that one was taken in error.
    /// </remarks>
    public const string Category = "daily_fee_refund";

    private const string SelectColumns =
        "id AS request_id, user_id AS driver_id, status, description, created_at";

    public async Task<FeeRefundTicket> CreateAsync(
        Guid driverId, string description, Guid? rideId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleAsync<FeeRefundTicket>(
            new CommandDefinition(
                $"""
                INSERT INTO support.tickets (user_id, category, description, ride_id)
                VALUES (@DriverId, @Category, @Description, @RideId)
                RETURNING {SelectColumns};
                """,
                new { DriverId = driverId, Category, Description = description, RideId = rideId },
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<FeeRefundTicket>> ListAsync(
        Guid driverId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<FeeRefundTicket>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                  FROM support.tickets
                 WHERE user_id = @DriverId AND category = @Category
                 ORDER BY created_at DESC
                 LIMIT @Limit;
                """,
                new { DriverId = driverId, Category, Limit = limit },
                cancellationToken: cancellationToken));

        return [.. rows];
    }
}
