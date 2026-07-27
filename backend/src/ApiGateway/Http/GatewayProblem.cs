using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Http;

namespace MageRide.ApiGateway.Http;

/// <summary>
/// Writes a gateway-originated error in the one shape the platform emits (D3' §0). Requests the
/// edge rejects — 426, 401 <c>attestation-failed</c>, 429, 404 on a blocked path, 503/504 when a
/// destination is unreachable — must be indistinguishable in shape from an error a service would
/// have produced, or every client needs two error parsers.
/// </summary>
internal static class GatewayProblem
{
    public const string ContentType = "application/problem+json";

    /// <param name="configureResponse">
    /// Runs after the response is reset and before the body is written — the only point at which a
    /// header such as <c>Retry-After</c> survives, since resetting clears the header collection.
    /// </param>
    public static Task WriteAsync(
        HttpContext context,
        ErrorCode error,
        string? detail = null,
        IEnumerable<KeyValuePair<string, object?>>? extensions = null,
        Action<HttpResponse>? configureResponse = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(error);

        if (context.Response.HasStarted)
        {
            // Nothing can be said any more; the caller sees a truncated response, which is the
            // honest outcome. Swallowing it here keeps the pipeline from throwing on top.
            return Task.CompletedTask;
        }

        context.Response.Clear();
        context.Response.StatusCode = error.Status;
        context.Response.ContentType = ContentType;

        configureResponse?.Invoke(context.Response);

        var problem = MageRideProblem.Create(context, error, detail, extensions);

        return context.Response.WriteAsJsonAsync(
            problem, MageRideJson.Options, ContentType, context.RequestAborted);
    }
}
