using MageRide.Fleet.Configuration;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet.Organisation;

/// <summary>An org's queue detail: the KYC, its current payout version, and the evidence.</summary>
public sealed record FleetVerificationDetail(
    FleetOrganisation Fleet, PayoutProfile? PayoutProfile, IReadOnlyList<PayoutDocument> Documents);

/// <summary>
/// The Verification Officer's decisions, as admin-bff (C062) drives them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The decision lives here; the screen and the audit do not.</b> AL-39 puts
/// <c>GET /v1/admin/verification/queues/fleet-org</c>, <c>…/org/{orgId}</c> and
/// <c>…/{subjectId}/approve|reject</c> on admin-bff, which is RBAC-gated deny-by-default and
/// writes <c>audit.events</c> for every mutation (D-35). Those routes forward to this service —
/// the same split registry-svc uses for vehicle approval and support-svc for the ticket queue. No
/// route here checks a <c>verification_officer</c> role, because it never sees the officer's
/// bearer; admin-bff resolves it and passes the id on the body.
/// </para>
/// <para>
/// <b>Approving an organisation approves its payout profile.</b> admin-bff's own description says
/// so — "for an org this also sets the payout profile to <c>verified</c> (AL-49)" — and AL-49 puts
/// the payout documents in the same <c>documents[]</c> the officer is already reading. One
/// decision, one transaction, both rows.
/// </para>
/// <para>
/// <b>Approve is therefore not once-only.</b> An APPROVED organisation whose owner edited a
/// verified payout profile is back on the queue with a pending version; the officer approves
/// again, the organisation stays APPROVED, and the new version supersedes the incumbent. Rejecting
/// an edit never disturbs the incumbent — BR-31.1's mismatched account-holder name is a reason to
/// refuse the change, not a reason to stop the organisation collecting against details already
/// approved.
/// </para>
/// </remarks>
public interface IFleetVerificationService
{
    Task<IReadOnlyList<FleetQueueRow>> ListQueueAsync(string? status, int? limit, CancellationToken cancellationToken);

    Task<FleetVerificationDetail> ReadAsync(Guid fleetId, CancellationToken cancellationToken);

    Task<VerificationDecision> ApproveAsync(Guid fleetId, Guid officerId, CancellationToken cancellationToken);

    Task<VerificationDecision> RejectAsync(
        Guid fleetId, Guid officerId, string reason, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetVerificationService"/>
internal sealed class FleetVerificationService(
    IUnitOfWorkFactory unitOfWorkFactory,
    INpgsqlConnectionFactory connections,
    IFleetRepository fleets,
    IPayoutProfileRepository profiles,
    IPayoutDocumentRepository documents,
    IOptions<FleetOptions> options,
    ILogger<FleetVerificationService> logger) : IFleetVerificationService
{
    private readonly FleetOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IReadOnlyList<FleetQueueRow>> ListQueueAsync(
        string? status, int? limit, CancellationToken cancellationToken)
    {
        if (status is not null && !FleetStatuses.All.Contains(status))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["status"] = [$"status must be one of {string.Join(", ", FleetStatuses.All.Order(StringComparer.Ordinal))}."],
            });
        }

        // Not fleet-scoped, and cannot be: the queue is the officer's cross-organisation view, and
        // `app.fleet_id` names one org. What guards it is the internal plane's key and, in front of
        // that, admin-bff's deny-by-default RBAC — never this connection.
        await using var connection = await connections.OpenAsync(cancellationToken);

        var capped = Math.Clamp(limit ?? _options.MaxPageSize, 1, _options.MaxPageSize);

        // One more than the cap is deliberately NOT requested: unlike a user-facing list, this one
        // has a cursor-less contract today and an officer who has more than a page of applications
        // has a different problem. The cap is logged when it bites so it is never silent.
        var rows = await fleets.ListQueueAsync(connection, status ?? FleetStatuses.Pending, capped, cancellationToken);

        if (rows.Count == capped)
        {
            logger.LogWarning(
                "The fleet-org verification queue returned its full page of {Limit}; there may be more waiting "
                + "(Fleet:MaxPageSize).",
                capped);
        }

        return rows;
    }

    public async Task<FleetVerificationDetail> ReadAsync(Guid fleetId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var fleet = await fleets.FindAsync(connection, transaction: null, fleetId, cancellationToken)
            ?? throw new MageRideException(FleetErrors.FleetNotFound, "No such fleet organisation.");

        var profile = await profiles.FindCurrentAsync(connection, transaction: null, fleetId, cancellationToken);

        var evidence = profile is null
            ? []
            : await documents.ListForProfileAsync(connection, transaction: null, profile.Id, cancellationToken);

        return new FleetVerificationDetail(fleet, profile, evidence);
    }

    public async Task<VerificationDecision> ApproveAsync(
        Guid fleetId, Guid officerId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var fleet = await fleets.SetStatusAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, FleetStatuses.Approved, null, cancellationToken)
            ?? throw new MageRideException(FleetErrors.FleetNotFound, "No such fleet organisation.");

        var current = await profiles.FindCurrentAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken);

        // Only a pending version is decided on. An org approved with an already-verified profile
        // (nothing edited since) leaves it exactly as it is, so a second Approve does not re-stamp
        // `verified_at` and rewrite who decided it.
        var profile = current is { IsPending: true }
            ? await profiles.VerifyAsync(
                unitOfWork.Connection, unitOfWork.Transaction, fleetId, current.Id, officerId, cancellationToken)
            : current;

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Verification Officer {OfficerId} approved fleet organisation {FleetId}; payout profile is now {Status}.",
            officerId,
            fleetId,
            profile?.Status ?? "absent");

        return new VerificationDecision(fleet, profile);
    }

    public async Task<VerificationDecision> RejectAsync(
        Guid fleetId, Guid officerId, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            // US-2.15's rule, applied to the org subject: the reason is surfaced verbatim to the
            // applicant, and a rejection with nothing to read is one an operator cannot act on.
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] = ["A rejection reason is required and is shown to the applicant."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var fleet = await fleets.SetStatusAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            fleetId,
            FleetStatuses.Rejected,
            reason.Trim(),
            cancellationToken)
            ?? throw new MageRideException(FleetErrors.FleetNotFound, "No such fleet organisation.");

        var current = await profiles.FindCurrentAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken);

        var profile = current is { IsPending: true }
            ? await profiles.RejectAsync(
                unitOfWork.Connection, unitOfWork.Transaction, current.Id, officerId, reason.Trim(), cancellationToken)
            : current;

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Verification Officer {OfficerId} rejected fleet organisation {FleetId}.", officerId, fleetId);

        return new VerificationDecision(fleet, profile);
    }
}
