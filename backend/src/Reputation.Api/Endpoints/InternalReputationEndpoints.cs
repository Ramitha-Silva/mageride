using System.Net;
using System.Security.Cryptography;
using System.Text;
using MageRide.Reputation.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Reputation.Endpoints;

/// <summary>
/// <c>/v1/internal/reputation</c> — the E-07 clustering input.
/// </summary>
/// <remarks>
/// <para>
/// One route, and it exists because <b>nothing in the platform records a client address</b>. E-07
/// asks for "IP/ASN clustering" and the only <c>INET</c> column in the schema belongs to a tracker
/// (<c>prov.tracker_bindings.remote_addr</c>). See <c>db/migrations/0805</c>.
/// </para>
/// <para>
/// The caller is whoever terminates the client connection and can see the real address — the API
/// gateway (C008) or iam-svc (C020). <b>Neither calls it yet</b>, so the clustering detector runs
/// against an empty table and simply does not fire; that is stated rather than papered over,
/// because a detector with no input reads like a detector that found nothing.
/// </para>
/// <para>
/// Protected like ride-svc's internal family: mTLS by D3' §0, refused at the gateway edge, and
/// guarded by a shared secret until C042. Without <c>Reputation:InternalApiKey</c> it is not mapped.
/// </para>
/// </remarks>
public static class InternalReputationEndpoints
{
    /// <summary>Carries <c>Reputation:InternalApiKey</c>. Replaced by the mesh peer identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapInternalReputationEndpoints(
        this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // AllowAnonymous because the caller is a service and has no bearer to present; the filter
        // is what actually authenticates it, and the kernel's fallback policy would otherwise 401.
        var internalGroup = endpoints.MapGroup("/v1/internal/reputation")
            .WithTags("reputation")
            .AllowAnonymous()
            .AddEndpointFilter(new InternalApiKeyFilter(apiKey));

        internalGroup.MapPost("/observations", RecordObservationAsync).WithName("recordNetworkObservation");

        return endpoints;
    }

    private static async Task<Accepted> RecordObservationAsync(
        NetworkObservationBody? body,
        INpgsqlConnectionFactory connections,
        IDetectionRepository detection,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(clock);

        var userId = RequestIds.Require(body?.UserId, "userId");

        if (!IPAddress.TryParse(body?.Ip, out var ip))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["ip"] = ["ip is required and must be an IPv4 or IPv6 literal."],
            });
        }

        await using var connection = await connections.OpenAsync(cancellationToken);

        await detection.RecordObservationAsync(
            connection,
            transaction: null,
            userId,
            RequestIds.Optional(body?.RideId),
            ip,
            body?.Asn,
            body?.UserAgent,
            clock.GetUtcNow(),
            cancellationToken);

        // 202, not 201: the row is evidence for a detector that runs later, and there is no
        // resource for the caller to go and read.
        return TypedResults.Accepted((string?)null);
    }
}

/// <summary>
/// Rejects a call that does not carry the configured internal key. The HTTP twin of
/// <see cref="Grpc.InternalKeyInterceptor"/>.
/// </summary>
/// <remarks>
/// Answers <c>404 not-found</c>, matching what the gateway returns for the same prefix (C008): a
/// caller who is not entitled to the internal plane should not be able to map it. Fixed-time
/// comparison — a length-varying compare leaks the key a character at a time.
/// </remarks>
internal sealed class InternalApiKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(apiKey);

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[InternalReputationEndpoints.ApiKeyHeader].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected)
            ? next(context)
            : throw new MageRideException(MageRideErrors.NotFound, "No such resource.");
    }
}
