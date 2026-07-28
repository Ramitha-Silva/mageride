using System.Globalization;
using MageRide.Iam.Auth;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace MageRide.Iam.Endpoints;

/// <summary>
/// <c>GET /.well-known/jwks.json</c> — the public half of the signing key, for every service,
/// the gateway and EMQX (D-29, D-21).
/// </summary>
/// <remarks>
/// Not in <c>backend/contracts/iam.yaml</c>: the contracts describe the <c>/v1</c> product API and
/// Spectral enforces that prefix. This is infrastructure, at the well-known path RFC 8615 reserves
/// and the path <c>Jwt__JwksUrl</c> already points every service at (D7' §4.1).
/// </remarks>
public static class JwksEndpoints
{
    /// <summary>The well-known path, as <c>Jwt__JwksUrl</c> spells it.</summary>
    public const string Path = "/.well-known/jwks.json";

    public static IEndpointRouteBuilder MapJwks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(Path, (HttpContext context, SigningKeyRing keys, IOptions<JwtOptions> jwt) =>
            {
                // D-21 has every consumer cache the key set for 15 minutes; say so on the wire too,
                // so an intermediary caches for the same window rather than a guess of its own.
                var seconds = (int)jwt.Value.JwksCacheDuration.TotalSeconds;
                context.Response.Headers.CacheControl =
                    string.Create(CultureInfo.InvariantCulture, $"public, max-age={seconds}");
                context.Response.Headers[HeaderNames.ETag] = $"\"{keys.KeyId}\"";

                return TypedResults.Ok(JsonWebKeySetDocument.From(keys));
            })
            .AllowAnonymous()
            .WithName("jwks")
            .WithTags("auth");

        return endpoints;
    }
}
