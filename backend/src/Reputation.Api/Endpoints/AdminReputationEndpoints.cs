using System.Security.Claims;
using MageRide.Reputation.Counters;
using MageRide.Reputation.Domain;
using MageRide.Reputation.Persistence;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Reputation.Endpoints;

/// <summary>
/// The admin HTTP surface — the two routes D3' declares and the three C033 adds.
/// </summary>
/// <remarks>
/// <para>
/// Every route matches <c>backend/contracts/reputation.yaml</c>, which wins over this file and over
/// the code. The gateway routes <c>/v1/admin/reputation/**</c> and
/// <c>/v1/admin/drivers/{driverId}/level/restore</c> here (C008's <c>gateway-routes.json</c>), which
/// is why the three added routes are nested under <c>/v1/admin/reputation</c> — no gateway change
/// was needed for them.
/// </para>
/// <para>
/// RBAC is deny-by-default (AL-06): each route names the roles it needs. Admin, super-admin and
/// support-CSR can review flags and lift a block — AL-16 names "admin/CSR reinstatement" outright —
/// and the auditor role reads without deciding.
/// </para>
/// </remarks>
public static class AdminReputationEndpoints
{
    public static IEndpointRouteBuilder MapAdminReputationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var admin = endpoints.MapGroup("/v1/admin/reputation").WithTags("reputation");

        admin.MapGet("/flags", ListFlagsAsync)
            .WithName("listFraudFlags")
            .RequireMageRideRole(
                MageRideRoles.Admin, MageRideRoles.SuperAdmin, MageRideRoles.SupportCsr, MageRideRoles.Auditor);

        admin.MapPost("/flags/{flagId}/resolve", ResolveFlagAsync)
            .WithName("resolveFraudFlag")
            .RequireMageRideRole(MageRideRoles.Admin, MageRideRoles.SuperAdmin, MageRideRoles.SupportCsr);

        admin.MapGet("/users/{userId}", GetSubjectAsync)
            .WithName("getReputationSubject")
            .RequireMageRideRole(
                MageRideRoles.Admin, MageRideRoles.SuperAdmin, MageRideRoles.SupportCsr, MageRideRoles.Auditor);

        admin.MapPut("/users/{userId}/block-state", OverrideBlockStateAsync)
            .WithName("overrideBlockState")
            .RequireMageRideRole(MageRideRoles.Admin, MageRideRoles.SuperAdmin, MageRideRoles.SupportCsr);

        // D3' puts the appeal restore under /v1/admin/drivers, not under /v1/admin/reputation, and
        // the gateway has a dedicated route for it. Mapped where the contract says, not where it
        // would be tidier.
        endpoints.MapPost("/v1/admin/drivers/{driverId}/level/restore", RestoreLevelAsync)
            .WithTags("reputation")
            .WithName("restoreDriverLevel")
            .RequireMageRideRole(MageRideRoles.Admin, MageRideRoles.SuperAdmin);

        return endpoints;
    }

    private static async Task<Ok<CursorPage<FraudFlagResponse>>> ListFlagsAsync(
        HttpRequest request,
        string? kind,
        string? status,
        INpgsqlConnectionFactory connections,
        IFraudFlagRepository flags,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(flags);

        if (status is not null && !FraudFlagStatuses.IsResolution(status) && status != FraudFlagStatuses.Open)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["status"] = ["status must be open, dismissed or actioned."],
            });
        }

        var page = PageRequest.FromQuery(request);

        // The cursor is the last row's timestamp. Signing it would add nothing: it carries an
        // ordering position and no entitlement, and the query is already scoped by the RBAC policy
        // on the route rather than by anything the cursor says (CursorCodec's own remark).
        var before = CursorCodec.Unsigned.TryDecodeString(page.Cursor, out var raw)
                     && DateTimeOffset.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts)
            ? ts
            : (DateTimeOffset?)null;

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await flags.ListAsync(
            connection, NullIfBlank(kind), NullIfBlank(status), before, page.OverfetchLimit, cancellationToken);

        var result = CursorPage<FraudFlagRow>.FromOverfetch(
            rows, page.Limit, row => CursorCodec.Unsigned.EncodeString(row.Ts.ToString("O")));

        return TypedResults.Ok(result.Select(row => FraudFlagResponse.From(row)));
    }

    private static async Task<Ok<FraudFlagResponse>> ResolveFlagAsync(
        string flagId,
        ResolveFlagBody? body,
        ClaimsPrincipal user,
        IUnitOfWorkFactory unitOfWorkFactory,
        IFraudFlagRepository flags,
        IAuditEventWriter audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWorkFactory);
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);

        var id = RequestIds.Require(flagId, "flagId");
        var status = body?.Status;

        if (!FraudFlagStatuses.IsResolution(status))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["status"] = ["status must be dismissed or actioned."],
            });
        }

        var actorId = user.RequireSubjectId();
        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var before = await flags.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, id, cancellationToken)
                     ?? throw new MageRideException(MageRideErrors.NotFound, $"No flag '{flagId}'.");

        var resolved = await flags.ResolveAsync(
            unitOfWork.Connection, unitOfWork.Transaction, id, status!, actorId, body?.Note, now, cancellationToken);

        if (resolved is null)
        {
            // The guarded UPDATE matched nothing, which for a row that exists means it is already
            // resolved the other way. Re-resolving with the same verdict does match, and is the
            // no-op the contract describes.
            await unitOfWork.RollbackAsync(cancellationToken);

            throw new MageRideException(
                MageRideErrors.Conflict, $"Flag '{flagId}' is already {before.Status}.");
        }

        await audit.WriteAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            new AuditEntry(
                ReputationAuditActions.FlagResolved,
                EntityType: ReputationAuditActions.FraudFlagEntity,
                EntityId: id,
                ActorId: actorId,
                Before: new { status = before.Status },
                After: new { status = resolved.Status, note = body?.Note }),
            now,
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return TypedResults.Ok(FraudFlagResponse.From(resolved));
    }

    private static async Task<Ok<ReputationSubjectResponse>> GetSubjectAsync(
        string userId,
        IReputationService reputation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reputation);

        var id = RequestIds.Require(userId, "userId");

        var status = await reputation.GetStatusAsync(id, cancellationToken);
        var level = await reputation.GetLevelAsync(id, cancellationToken);

        return TypedResults.Ok(ReputationSubjectResponse.From(status, level));
    }

    private static async Task<Ok<ReputationSubjectResponse>> OverrideBlockStateAsync(
        string userId,
        OverrideBlockStateBody? body,
        ClaimsPrincipal user,
        IReputationService reputation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reputation);

        var id = RequestIds.Require(userId, "userId");

        if (string.IsNullOrWhiteSpace(body?.Reason))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["reason"] = ["reason is required — an override with no reason cannot be audited."],
            });
        }

        var status = await reputation.OverrideAsync(
            id, body.State ?? string.Empty, body.Reason, body.ExpiresAt, user.RequireSubjectId(), cancellationToken);

        var level = await reputation.GetLevelAsync(id, cancellationToken);

        return TypedResults.Ok(ReputationSubjectResponse.From(status, level));
    }

    private static async Task<Ok<LevelResponse>> RestoreLevelAsync(
        string driverId,
        RestoreLevelBody? body,
        ClaimsPrincipal user,
        IReputationService reputation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reputation);

        var id = RequestIds.Require(driverId, "driverId");

        if (body?.Level is not { } level)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["level"] = ["level is required and must be between 1 and 3."],
            });
        }

        if (string.IsNullOrWhiteSpace(body.Reason))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["reason"] = ["reason is required — an appeal outcome with no reason cannot be audited."],
            });
        }

        var restored = await reputation.RestoreLevelAsync(
            id, level, body.Reason, user.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(LevelResponse.From(restored));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
