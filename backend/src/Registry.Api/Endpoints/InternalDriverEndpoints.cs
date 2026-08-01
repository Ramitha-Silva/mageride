using MageRide.Registry.Vehicles;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Registry.Endpoints;

/// <summary>
/// <c>/v1/internal/drivers</c> — the Verification Officer's decision on a driver's bank &amp;
/// payout profile (AL-58, AL-59), arriving from admin-bff's AL-39 queue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the decision is here and not in admin-bff.</b> That BFF writes
/// <c>registry.driver_profiles.verified_at</c> itself, because an identity verdict is one column
/// and no service exposes a route for it. This is not that: approving a payout profile is BR-31.1's
/// versioning rule — supersede the incumbent, then verify the replacement, in one transaction and
/// in that order, because <c>ux_driver_payout_verified</c> admits exactly one verified row. That
/// invariant belongs to the service that owns the table, and its other half already lives in
/// <see cref="IDriverPayoutProfileService"/>. fleet-svc made the same call for
/// <c>registry.fleet_payout_profiles</c> — the table C028 mirrored column for column — and its
/// whole <c>/v1/internal/fleets/**</c> plane exists for this caller.
/// </para>
/// <para>
/// <b>The ADD says the officer decides this "through the existing AL-39 queue, whose
/// subject-agnostic routes already take a driver id" (§1.18 AL-58).</b> The queue family is indeed
/// reused, but the decision could not be: a driver id already resolves to the <em>identity</em>
/// subject, so <c>POST /v1/admin/verification/{driverId}/approve</c> would have made one button
/// decide two unrelated questions — and rejecting an illegible bank statement would have refused
/// the driver's licence and stopped them driving. Raised as a micro-change-set; see the handoff.
/// </para>
/// <para>
/// Same fence as <see cref="InternalVehicleEndpoints"/>: without <c>Registry:InternalApiKey</c> the
/// group is not mapped at all, so a deployment that forgets it gets 404s rather than an open door.
/// </para>
/// </remarks>
public static class InternalDriverEndpoints
{
    public static IEndpointRouteBuilder MapInternalDriverEndpoints(
        this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var internalDrivers = endpoints.MapGroup("/v1/internal/drivers")
            .WithTags("drivers")
            .AllowAnonymous()
            .AddEndpointFilter(new RegistryInternalApiKeyFilter(apiKey));

        internalDrivers.MapPost("/{driverId:guid}/payout-profile/approve", ApprovePayoutProfileAsync)
            .WithName("approveDriverPayoutProfile");

        internalDrivers.MapPost("/{driverId:guid}/payout-profile/reject", RejectPayoutProfileAsync)
            .WithName("rejectDriverPayoutProfile");

        return endpoints;
    }

    /// <summary>
    /// The approval that lets payout-svc pay this driver, and puts their LankaQR on the pay sheet.
    /// </summary>
    private static async Task<Ok<DriverPayoutDecisionResponse>> ApprovePayoutProfileAsync(
        Guid driverId,
        OfficerDecisionBody? body,
        IDriverPayoutProfileService profiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var decided = await profiles.ApproveAsync(
            driverId, RequireOfficerId(body), cancellationToken);

        return TypedResults.Ok(DriverPayoutDecisionResponse.From(decided));
    }

    private static async Task<Ok<DriverPayoutDecisionResponse>> RejectPayoutProfileAsync(
        Guid driverId,
        OfficerDecisionBody? body,
        IDriverPayoutProfileService profiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var reason = body?.Reason?.Trim();

        if (string.IsNullOrEmpty(reason) || reason.Length > 1000)
        {
            // Shown verbatim to the driver on SCR-DA-022a. A refusal with nothing to read is a
            // screen that says "rejected" and gives them no way to fix it (US-2.15's rule).
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] =
                [
                    "reason is required, must be at most 1000 characters, and is shown verbatim to the driver.",
                ],
            });
        }

        var decided = await profiles.RejectAsync(
            driverId, RequireOfficerId(body), reason, cancellationToken);

        return TypedResults.Ok(DriverPayoutDecisionResponse.From(decided));
    }

    /// <remarks>
    /// The officer's id comes in the body because this plane has no bearer to read it from — the
    /// caller is a service. It is recorded in <c>verified_by</c>, which is the only place this
    /// service keeps who decided; the audit trail proper is admin-bff's <c>audit.events</c> row
    /// (D-35), written on the near side of the hop where the human's token actually is.
    /// </remarks>
    private static Guid RequireOfficerId(OfficerDecisionBody? body)
    {
        if (body?.OfficerId is not { } raw || !Guid.TryParse(raw, out var officerId) || officerId == Guid.Empty)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["officerId"] = ["officerId is required and must be a UUID."],
            });
        }

        return officerId;
    }
}

/// <summary>Who decided, and — on a refusal — why.</summary>
public sealed record OfficerDecisionBody(string? OfficerId, string? Reason);

/// <summary>The version an officer's decision landed on.</summary>
public sealed record DriverPayoutDecisionResponse(
    string ProfileId,
    string DriverId,
    string Bank,
    string Branch,
    string AccountNo,
    string AccountHolderName,
    string? ProofDocId,
    string? LankaqrDocId,
    string Status,
    string? RejectionReason,
    DateTimeOffset? VerifiedAt)
{
    public static DriverPayoutDecisionResponse From(MageRide.Registry.Domain.DriverPayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new DriverPayoutDecisionResponse(
            profile.Id.ToString(),
            profile.DriverId.ToString(),
            profile.Bank,
            profile.Branch,
            profile.AccountNo,
            profile.AccountHolderName,
            profile.ProofUploadId?.ToString(),
            profile.LankaqrUploadId?.ToString(),
            profile.Status,
            profile.RejectionReason,
            profile.VerifiedAt);
    }
}
