using System.Security.Cryptography;
using System.Text;
using MageRide.Dispatch.Levels;
using MageRide.Dispatch.Penalties;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Dispatch.Endpoints;

/// <summary>
/// <c>/v1/internal/**</c> — the two things another service asks dispatch-svc to do.
/// </summary>
/// <remarks>
/// <para>
/// <c>POST /v1/internal/drivers/{driverId}/no-show</c> is D3''s ("scheduler → level−1", US-6A.7).
/// The two penalty routes are <b>Δ C035</b>: D5' §7.1 has fare-svc add the outstanding Rs 50 to the
/// passenger's next completed trip and then mark it settled, and the ledger it settles against
/// lives in this schema — D3' names no route to reach it. Both are recorded as micro-change-sets in
/// the C035 handoff.
/// </para>
/// <para>
/// <b>How they are protected.</b> D3' §0 puts the whole <c>/v1/internal/**</c> family on mTLS and
/// the gateway refuses the prefix at the edge (C008). Until C042's mesh exists the hop carries the
/// shared secret, and <b>without <c>Dispatch:InternalApiKey</c> the routes are not mapped at
/// all</b> — the same shape ride-svc and reputation-svc use, for the same reason.
/// </para>
/// </remarks>
public static class InternalDispatchEndpoints
{
    /// <summary>Carries <c>Dispatch:InternalApiKey</c>. Replaced by the mTLS peer identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapInternalDispatchEndpoints(
        this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // AllowAnonymous because the caller is a service with no bearer to present; the filter is
        // what authenticates it, and the kernel's fallback policy would otherwise 401 every call.
        var internalRoutes = endpoints.MapGroup("/v1/internal")
            .WithTags("driver-level")
            .AllowAnonymous()
            .AddEndpointFilter(new DispatchInternalApiKeyFilter(apiKey));

        internalRoutes.MapPost("/drivers/{driverId}/no-show", ReportNoShowAsync).WithName("reportDriverNoShow");

        internalRoutes.MapGet("/passengers/{passengerId}/penalties", OutstandingPenaltiesAsync)
            .WithName("listOutstandingPenalties");

        internalRoutes.MapPost("/passengers/{passengerId}/penalties/settle", SettlePenaltiesAsync)
            .WithName("settleOutstandingPenalties");

        return endpoints;
    }

    private static async Task<Ok<DriverNoShowResponse>> ReportNoShowAsync(
        string driverId, NoShowBody? body, IDriverLevelService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var subject = ScheduledRideEndpoints.RequireId(driverId, "driverId");

        // `rideId` is optional in the contract. When it is present the (driver, ride) pair is what
        // makes the decrement happen once however many times the report is delivered; when it is
        // absent there is nothing to deduplicate on and the report is counted as given.
        var rideId = body?.RideId is { Length: > 0 } value
            ? ScheduledRideEndpoints.RequireId(value, "rideId")
            : (Guid?)null;

        var level = await service.RecordNoShowAsync(subject, rideId, cancellationToken);

        return TypedResults.Ok(new DriverNoShowResponse(subject, level.Level));
    }

    private static async Task<Ok<PenaltyListResponse>> OutstandingPenaltiesAsync(
        string passengerId, IPenaltyService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var settlement = await service.OutstandingAsync(
            ScheduledRideEndpoints.RequireId(passengerId, "passengerId"), cancellationToken);

        return TypedResults.Ok(PenaltyListResponse.Outstanding(settlement));
    }

    private static async Task<Ok<PenaltySettledResponse>> SettlePenaltiesAsync(
        string passengerId, SettlePenaltiesBody? body, IPenaltyService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var settlement = await service.SettleAsync(
            ScheduledRideEndpoints.RequireId(passengerId, "passengerId"),
            ScheduledRideEndpoints.RequireId(body?.RideId, "rideId"),
            cancellationToken);

        return TypedResults.Ok(PenaltySettledResponse.From(settlement));
    }
}

/// <summary>The body of <c>POST /v1/internal/drivers/{driverId}/no-show</c>.</summary>
public sealed record NoShowBody(string? RideId);

/// <summary>Its 200.</summary>
public sealed record DriverNoShowResponse(Guid DriverId, int Level);

/// <summary>The body of <c>POST /v1/internal/passengers/{passengerId}/penalties/settle</c>.</summary>
public sealed record SettlePenaltiesBody(string? RideId);

/// <summary>The contract's <c>CancellationPenalty</c>.</summary>
public sealed record PenaltyResponse(
    Guid PenaltyId,
    Guid PassengerId,
    Guid OriginalRideId,
    Guid AffectedDriverId,
    long AmountMinor,
    string Currency,
    string Basis,
    string Status,
    Guid? AppliedRideId,
    DateTimeOffset CreatedAt)
{
    public static PenaltyResponse From(Domain.PenaltyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new PenaltyResponse(
            row.Id, row.PassengerId, row.OriginalRideId, row.AffectedDriverId, row.AmountMinor,
            Money.Lkr, row.Basis, row.Status, row.AppliedRideId, row.CreatedAt);
    }
}

/// <summary>The 200 of the outstanding-penalties read.</summary>
public sealed record PenaltyListResponse(IReadOnlyList<PenaltyResponse> Items, long TotalMinor, string Currency)
{
    public static PenaltyListResponse Outstanding(PenaltySettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        return new PenaltyListResponse(
            [.. settlement.Settled.Select(PenaltyResponse.From)], settlement.TotalMinor, Money.Lkr);
    }
}

/// <summary>The 200 of the settle call. Empty on a replay, which is the same answer as "nothing owed".</summary>
public sealed record PenaltySettledResponse(IReadOnlyList<PenaltyResponse> Items, long SettledMinor, string Currency)
{
    public static PenaltySettledResponse From(PenaltySettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        return new PenaltySettledResponse(
            [.. settlement.Settled.Select(PenaltyResponse.From)], settlement.TotalMinor, Money.Lkr);
    }
}

/// <summary>
/// Rejects a call that does not carry <c>Dispatch:InternalApiKey</c>.
/// </summary>
/// <remarks>
/// The answer is <c>404 not-found</c>, matching what the gateway returns for the same prefix
/// (C008): a caller who is not entitled to the internal plane should not be able to map it. The
/// comparison is fixed-time — the key is a secret, and a length-varying compare leaks it a
/// character at a time.
/// </remarks>
internal sealed class DispatchInternalApiKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(apiKey);

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[InternalDispatchEndpoints.ApiKeyHeader].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected)
            ? next(context)
            : throw new MageRideException(MageRideErrors.NotFound, "No such resource.");
    }
}
