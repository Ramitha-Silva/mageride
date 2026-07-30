using MageRide.Content.Caching;
using MageRide.Shared.Errors;
using MageRide.Shared.Http.Idempotency;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Content.Endpoints;

/// <summary>
/// <c>POST /v1/internal/content/cache/purge</c> — the invalidation path for a write made elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>One route, and it exists for exactly one dataset.</b> Every write this service makes purges its
/// own caches already. The launch cities are the dataset it serves and does not own: D3' assigns
/// their CRUD to <b>admin-bff</b> (<c>POST/PATCH /v1/admin/config/cities</c>, audited D-35), so an
/// admin activating a city writes <c>config.operating_cities</c> in another service and this one
/// would keep serving the old list until <c>Content:CacheTtl</c> elapsed on every replica. AL-27's
/// promise is "launching a new city needs no app release", and five minutes of the old list is a
/// thin version of it.
/// </para>
/// <para>
/// <b>Nothing calls it yet</b> — admin-bff is C065 — and that is stated rather than papered over: the
/// endpoint is here so the caller has something to call, and the C045 handoff names it under what the
/// next components need. Until then the TTL is the only invalidation for that one dataset.
/// </para>
/// <para>
/// Protected like every other internal family: mTLS by D3' §0, refused at the gateway edge (this one
/// really is under <c>/v1/internal</c>), and guarded by <c>Content:InternalApiKey</c> until C042's
/// mesh identity lands. <b>Without the key the route is not mapped at all</b> — unlike the template
/// read, this is a write, and an unauthenticated cache-drop is a cheap way to make a service query
/// its database on every request.
/// </para>
/// </remarks>
public static class InternalContentEndpoints
{
    public static IEndpointRouteBuilder MapInternalContentEndpoints(
        this IEndpointRouteBuilder endpoints, string internalApiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(internalApiKey);

        // AllowAnonymous because the caller is a service and presents no bearer; the filter is what
        // authenticates it, and the kernel's deny-by-default fallback policy would otherwise 401
        // before the filter ran.
        endpoints.MapPost("/v1/internal/content/cache/purge", PurgeAsync)
            .WithTags("content")
            .AllowAnonymous()
            // `x-idempotency-exempt` in the contract, and the same thing in the pipeline: dropping an
            // already-dropped cache is the same operation, and there is no response to replay. Without
            // this the kernel's middleware would demand a key from a caller whose contract says it
            // does not need one.
            .AllowMissingIdempotencyKey()
            .AddEndpointFilter(new InternalKeyFilter(internalApiKey))
            .WithName("purgeContentCache");

        return endpoints;
    }

    /// <remarks>
    /// <c>202</c> rather than <c>200</c>: the local cache is dropped synchronously, but the whole
    /// point is the purge that travels to the other replicas, and that is a fire-and-forget publish
    /// with no acknowledgement to wait for. Idempotency-exempt for the same reason the contract says —
    /// dropping an already-dropped cache is the same operation.
    /// </remarks>
    private static async Task<Accepted<PurgeCacheResponse>> PurgeAsync(
        PurgeCacheBody? body,
        IContentInvalidator invalidator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invalidator);

        var requested = body?.Datasets;

        if (requested is { Count: > 0 })
        {
            // An unknown dataset name is refused rather than ignored: a caller purging "city"
            // (singular) would be told it worked and would keep serving the old list.
            var unknown = requested
                .Where(dataset => !ContentDatasets.IsKnown(dataset))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (unknown.Length > 0)
            {
                throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["datasets"] =
                    [
                        $"Unknown dataset(s): {string.Join(", ", unknown)}. Valid names are "
                        + $"{string.Join(", ", ContentDatasets.All)}, or omit the field to purge all of them.",
                    ],
                });
            }
        }

        var purged = await invalidator.InvalidateAsync(requested, cancellationToken);

        return TypedResults.Accepted((string?)null, new PurgeCacheResponse(purged));
    }
}
