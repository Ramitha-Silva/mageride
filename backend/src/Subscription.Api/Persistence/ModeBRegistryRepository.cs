using Dapper;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.Subscriptions.Persistence;

/// <summary>
/// A Mode B vehicle as this surface needs it, together with what the caller may do to it.
/// </summary>
/// <param name="ModeBBilling">
/// <c>registry.vehicles.mode_b_billing</c> — the AL-51 "Service payment" setting. <see langword="null"/>
/// for a Mode A/C vehicle and for a Mode B one nobody has classified yet.
/// </param>
/// <param name="FleetId">
/// The org whose payout profile this vehicle's subscription money goes to, or <see langword="null"/>
/// for an individually-owned Mode B vehicle — which therefore has no payout profile and cannot be
/// Paid (AL-49).
/// </param>
public sealed record ModeBVehicle(
    Guid VehicleId,
    Guid OwnerId,
    string Mode,
    string Status,
    string? ModeBBilling,
    long? DefaultMonthlyFareMinor,
    Guid? FleetId,
    bool IsVehicleOwner,
    bool IsFleetOwner,
    bool IsFleetManager,
    bool IsAssignedDriver)
{
    /// <summary>
    /// May the caller work the vehicle's request queue and read its roster? US-23.1's
    /// "Owner/Manager … the same accept/reject is available to the assigned driver".
    /// </summary>
    public bool CanManage => IsVehicleOwner || IsFleetManager || IsAssignedDriver;

    /// <summary>
    /// May the caller decide money and membership — override a fare, confirm a transfer, mark cash
    /// received, delete a subscriber?
    /// </summary>
    /// <remarks>
    /// Owner only. US-23.6 is explicit — "only the fleet Owner can mark it received" — and US-23.7
    /// and item 17 put the fare override and the hard delete in the same hands. A manager who could
    /// mark cash received could settle a month nobody paid for, and the money is the owner's.
    /// </remarks>
    public bool CanOwn => IsVehicleOwner || IsFleetOwner;
}

/// <summary>The verified payout profile a pay sheet's <c>payTo</c> is composed from (AL-49, §26).</summary>
public sealed record PayoutProfile(
    Guid ProfileId,
    Guid FleetId,
    string Bank,
    string Branch,
    string AccountNo,
    string AccountHolderName,
    Guid? LankaqrUploadId,
    DateTimeOffset? VerifiedAt);

/// <summary>The counterparty details a request queue and a roster show (US-23.1, SCR-FP-011).</summary>
public sealed record UserContact(Guid UserId, string? Name, string? Phone);

/// <summary>
/// The <c>registry.*</c>, <c>iam.*</c> and <c>docs.*</c> reads the Mode B surface makes.
/// <b>This service writes none of them.</b>
/// </summary>
/// <remarks>
/// <para>
/// Same judgement as <see cref="IVehicleRepository"/> and registry-svc's own
/// <c>SubscriptionRepository</c>, which reads <c>subscription.grants</c> from the other direction:
/// read-only cross-context statements rather than a synchronous hop to a service that would have to
/// answer four questions per request. Every one of these is a fact another bounded context owns and
/// none of them is written here.
/// </para>
/// <para>
/// <b>The authority flags are computed in the same statement as the vehicle</b> rather than in three
/// follow-up queries. Every route on this surface needs both, and asking twice would let the roster
/// be read against one answer and written against another.
/// </para>
/// </remarks>
internal interface IModeBRegistryRepository
{
    /// <summary>The vehicle and what <paramref name="callerId"/> may do to it, or <see langword="null"/>.</summary>
    Task<ModeBVehicle?> ReadVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid callerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The org's single <c>verified</c> payout profile, or <see langword="null"/> when it has none.
    /// </summary>
    /// <remarks>
    /// <c>ux_payout_profile_verified</c> makes "the verified row" singular. A profile that has since
    /// been edited back to <c>pending_verification</c> leaves the previously verified row untouched —
    /// the table is versioned — so collection continues against the last verified snapshot and never
    /// against an unverified edit (AL-49, D5' §802).
    /// </remarks>
    Task<PayoutProfile?> ReadVerifiedPayoutProfileAsync(
        Guid fleetId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>docs.uploads.storage_url</c> for a payout profile's bank-app QR image, or
    /// <see langword="null"/> when the profile is not (or is no longer) the verified one.
    /// </summary>
    /// <remarks>
    /// The <c>status = 'verified'</c> predicate is on this read as well as on the one that mints the
    /// link: a link minted while a profile was verified must stop resolving if it stops being the
    /// row the platform collects against, and the link outlives the request that issued it.
    /// </remarks>
    Task<string?> ReadPayoutQrUrlAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>Name and mobile for a set of users, for the request queue and the roster.</summary>
    Task<IReadOnlyDictionary<Guid, UserContact>> ReadContactsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IModeBRegistryRepository"/>
internal sealed class ModeBRegistryRepository(INpgsqlConnectionFactory connections) : IModeBRegistryRepository
{
    public Task<ModeBVehicle?> ReadVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid callerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // default_monthly_fare_minor is INTEGER in §2 while every contract types money as int64, and
        // Dapper's constructor binding matches parameter types exactly — an Int32 column against an
        // Int64 parameter does not fail to convert, it fails to materialise the record at all.
        //
        // The fleet is a scalar subquery with LIMIT 1: registry.fleet_vehicles is keyed
        // (fleet_id, vehicle_id) and nothing stops a vehicle appearing on two rosters, so the choice
        // is made deterministically here rather than by whichever row the planner returned first.
        return connection.QuerySingleOrDefaultAsync<ModeBVehicle>(new CommandDefinition(
            """
            SELECT v.id                                   AS vehicle_id,
                   v.owner_id,
                   v.mode,
                   v.status,
                   v.mode_b_billing,
                   v.default_monthly_fare_minor::bigint   AS default_monthly_fare_minor,
                   (SELECT fv.fleet_id
                      FROM registry.fleet_vehicles fv
                     WHERE fv.vehicle_id = v.id
                     ORDER BY fv.fleet_id
                     LIMIT 1)                             AS fleet_id,
                   (v.owner_id = @CallerId)               AS is_vehicle_owner,
                   EXISTS (SELECT 1
                             FROM registry.fleet_vehicles fv
                             JOIN iam.fleet_members m ON m.fleet_id = fv.fleet_id
                            WHERE fv.vehicle_id = v.id
                              AND m.user_id = @CallerId
                              AND m.fleet_role = 'owner') AS is_fleet_owner,
                   EXISTS (SELECT 1
                             FROM registry.fleet_vehicles fv
                             JOIN iam.fleet_members m ON m.fleet_id = fv.fleet_id
                            WHERE fv.vehicle_id = v.id
                              AND m.user_id = @CallerId
                              AND m.fleet_role IN ('owner','manager')) AS is_fleet_manager,
                   EXISTS (SELECT 1
                             FROM registry.fleet_assignments a
                            WHERE a.vehicle_id = v.id
                              AND a.driver_id = @CallerId
                              AND a.revoked_at IS NULL)   AS is_assigned_driver
              FROM registry.vehicles v
             WHERE v.id = @VehicleId;
            """,
            new { VehicleId = vehicleId, CallerId = callerId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<PayoutProfile?> ReadVerifiedPayoutProfileAsync(
        Guid fleetId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<PayoutProfile>(new CommandDefinition(
            """
            SELECT p.id AS profile_id, p.fleet_id, p.bank, p.branch, p.account_no,
                   p.account_holder_name, p.lankaqr_upload_id, p.verified_at
              FROM registry.fleet_payout_profiles p
             WHERE p.fleet_id = @FleetId AND p.status = 'verified';
            """,
            new { FleetId = fleetId },
            cancellationToken: cancellationToken));
    }

    public async Task<string?> ReadPayoutQrUrlAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT u.storage_url
              FROM registry.fleet_payout_profiles p
              JOIN docs.uploads u ON u.id = p.lankaqr_upload_id
             WHERE p.id = @ProfileId AND p.status = 'verified';
            """,
            new { ProfileId = profileId },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<Guid, UserContact>> ReadContactsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(userIds);

        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, UserContact>();
        }

        var rows = await connection.QueryAsync<UserContact>(new CommandDefinition(
            """
            SELECT u.id AS user_id, u.first_name AS name, u.phone
              FROM iam.users u
             WHERE u.id = ANY(@UserIds);
            """,
            new { UserIds = userIds.Distinct().ToArray() },
            transaction,
            cancellationToken: cancellationToken));

        return rows.ToDictionary(static contact => contact.UserId);
    }
}
