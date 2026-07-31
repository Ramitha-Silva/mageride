using MageRide.Fleet.Configuration;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Fleet.Organisation;

/// <summary>What a Fleet Owner submits to register an organisation (US-13.A7).</summary>
public sealed record RegisterFleetCommand(
    Guid OwnerId, string Name, string BusinessReg, string ContactPhone, string? ContactEmail, string? Address);

/// <summary>Registering an organisation, reading it, and provisioning its team (AL-03, Epic 13).</summary>
public interface IFleetService
{
    Task<FleetOrganisation> RegisterAsync(RegisterFleetCommand command, CancellationToken cancellationToken);

    /// <summary>The organisation, read inside its own row-level-security scope.</summary>
    Task<FleetOrganisation> ReadAsync(Guid fleetId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FleetMember>> ListMembersAsync(Guid fleetId, CancellationToken cancellationToken);

    Task<FleetMember> AddMemberAsync(
        Guid fleetId, Guid actorId, string email, string? name, string fleetRole, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IFleetService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>The organisation and its first membership commit together.</b> A <c>registry.fleets</c> row
/// whose owner has no <c>iam.fleet_members</c> seat is an organisation nobody can open — every
/// route on this service resolves the caller's seat from that table, so the person who just
/// registered would be refused from their own org. One transaction, both rows.
/// </para>
/// <para>
/// <b>Registration does not need an approved anything.</b> The gate is US-13.A7's, and it applies
/// to what an org <em>does</em>, not to its existence: a PENDING org can be read, can provision its
/// team and — deliberately — can edit its payout profile, because the payout documents are part of
/// what the Verification Officer reads before approving it (AL-49).
/// </para>
/// </remarks>
internal sealed class FleetService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetScopedReader scopedReader,
    IFleetRepository fleets,
    IFleetMemberRepository members,
    IPortalUserRepository portalUsers,
    IOptions<FleetOptions> options,
    ILogger<FleetService> logger) : IFleetService
{
    private readonly FleetOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<FleetOrganisation> RegisterAsync(
        RegisterFleetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        if (await fleets.BusinessRegistrationIsTakenAsync(
                unitOfWork.Connection, unitOfWork.Transaction, command.BusinessReg, cancellationToken))
        {
            throw new MageRideException(
                FleetErrors.BusinessRegistrationExists,
                "Another live organisation is already registered under this business registration number.");
        }

        FleetOrganisation fleet;

        try
        {
            fleet = await fleets.CreateAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                command.OwnerId,
                command.Name,
                command.BusinessReg,
                command.ContactPhone,
                command.ContactEmail,
                command.Address,
                cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The pre-check above lost a race to a concurrent registration.
            // `ux_fleets_business_reg_active` is what actually holds the rule; this turns its
            // 23505 into the same 409 the pre-check produces, rather than a 500.
            throw new MageRideException(
                FleetErrors.BusinessRegistrationExists,
                "Another live organisation is already registered under this business registration number.");
        }

        // The registrant becomes the Owner. `owner_id` on the row and the membership say two
        // different things — one is the account the organisation belongs to, the other is a seat
        // that can be held by several people — and both are needed.
        await members.AddAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleet.Id, command.OwnerId, FleetRoles.Owner, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Fleet organisation {FleetId} registered by {OwnerId} and is PENDING verification (US-13.A7).",
            fleet.Id,
            command.OwnerId);

        return fleet;
    }

    public async Task<FleetOrganisation> ReadAsync(Guid fleetId, CancellationToken cancellationToken) =>
        await scopedReader.ReadAsync(
            fleetId,
            async (connection, transaction) =>
                await fleets.FindAsync(connection, transaction, fleetId, cancellationToken)
                ?? throw new MageRideException(FleetErrors.FleetNotFound, "No such fleet organisation."),
            cancellationToken);

    public async Task<IReadOnlyList<FleetMember>> ListMembersAsync(
        Guid fleetId, CancellationToken cancellationToken) =>
        await scopedReader.ReadAsync(
            fleetId,
            async (connection, transaction) => await members.ListAsync(
                connection, transaction, fleetId, _options.MaxPageSize, cancellationToken),
            cancellationToken);

    public async Task<FleetMember> AddMemberAsync(
        Guid fleetId,
        Guid actorId,
        string email,
        string? name,
        string fleetRole,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var count = await members.CountAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken);

        if (count >= _options.MaxMembersPerFleet)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"This organisation already holds its maximum of {_options.MaxMembersPerFleet} members "
                + "(Fleet:MaxMembersPerFleet). Raise the limit or remove a member.");
        }

        var user = await portalUsers.EnsureFleetPortalUserAsync(
            unitOfWork.Connection, unitOfWork.Transaction, email, name, actorId, cancellationToken);

        var membership = await members.AddAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, user.Id, fleetRole, cancellationToken)
            ?? throw new MageRideException(
                FleetErrors.MemberExists,
                "This person already holds a sub-role in the organisation. Changing a seat is a separate decision.");

        await unitOfWork.CommitAsync(cancellationToken);

        if (user.WasCreated)
        {
            // Said once per new account, because nothing tells the invitee. There is no fleet-org
            // template in content.notification_templates (migration 1904) and no invitation route
            // anywhere in D3'; the owner has to pass on the address out of band, and an operator
            // reading this log is the only trace that they need to.
            logger.LogInformation(
                "Provisioned a new Fleet Portal account for a {FleetRole} of {FleetId}. "
                + "NOBODY HAS BEEN NOTIFIED — there is no invitation notification anywhere in this build (C058 handoff); "
                + "the Fleet Owner must tell them to sign in at fleet.mageride.lk.",
                fleetRole,
                fleetId);
        }

        return new FleetMember(
            fleetId, user.Id, membership.FleetRole, user.Email, name, IsBlocked: false, membership.CreatedAt);
    }
}
