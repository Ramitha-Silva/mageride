using MageRide.Shared.Caching;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace MageRide.Shared.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// RS256 bearer authentication against iam-svc's JWKS, plus deny-by-default authorization
    /// (D3' §0 "Auth"; D-29, D-21, AL-06).
    /// </summary>
    public static IServiceCollection AddMageRideAuth(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(nameof(JwksConfigurationManager));

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<JwtOptions>>().Value;
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(JwksConfigurationManager));
            return new JwksConfigurationManager(
                httpClient,
                options,
                sp.GetRequiredService<ILogger<JwksConfigurationManager>>(),
                sp.GetRequiredService<TimeProvider>());
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>, JwksConfigurationManager>(ConfigureJwtBearer);

        services.AddMageRideAuthorization();

        return services;
    }

    /// <summary>
    /// Deny-by-default authorization plus the per-role and fleet-scope policies (AL-06, AL-03).
    /// Called by <see cref="AddMageRideAuth"/>; call it directly only when a service supplies its
    /// own authentication scheme.
    /// </summary>
    public static IServiceCollection AddMageRideAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAuthorizationHandler, FleetRoleHandler>();

        // URD §2.3 itself (AL-06). Pure and stateless — the matrix is compiled in and the evaluator
        // holds no per-request state — so a singleton, and exactly one of them: two registrations
        // would be two opinions about one table. `TryAdd` so a service that registered its own
        // before C062 promoted these into the kernel is not given a second.
        services.TryAddSingleton<IPermissionEvaluator, PermissionEvaluator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAuthorizationHandler, FeatureAuthorizationHandler>());

        var builder = services.AddAuthorizationBuilder();

        // Deny-by-default: an endpoint that says nothing still requires an authenticated caller.
        // Public surfaces (health, /v1/config/cities, the token-authenticated public-bff family)
        // opt out explicitly with AllowAnonymous.
        builder.SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        foreach (var role in MageRideRoles.All)
        {
            builder.AddPolicy(MageRidePolicies.Role(role), policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(MageRideClaims.Role, role));
        }

        builder.AddPolicy(MageRidePolicies.InternalStaff, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(MageRideClaims.Role, MageRideRoles.Internal));

        foreach (var fleetRole in FleetRoles.All)
        {
            builder.AddPolicy(MageRidePolicies.FleetRole(fleetRole), policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new FleetRoleRequirement(fleetRole)));
        }

        return services;
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions options, IOptions<JwtOptions> jwtOptions, JwksConfigurationManager jwks)
    {
        var jwt = jwtOptions.Value;

        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = jwt.RequireHttpsMetadata;

        // 15-minute JWKS cache with refresh-on-unknown-kid (D-21).
        options.ConfigurationManager = jwks;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            // RS256 only. Leaving the algorithm open would accept an HS256 token forged with a
            // public key as the HMAC secret.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],

            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = jwt.ClockSkew,

            ValidateIssuer = !string.IsNullOrWhiteSpace(jwt.Issuer),
            ValidIssuer = jwt.Issuer,

            ValidateAudience = jwt.Audiences.Count > 0,
            ValidAudiences = jwt.Audiences,

            NameClaimType = MageRideClaims.Subject,
            RoleClaimType = MageRideClaims.Role,
        };

        // Authentication and authorization failures must leave as problem+json like everything
        // else (D3' §0); the stock handler writes an empty body with a WWW-Authenticate header.
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await WriteProblemAsync(context.HttpContext, MageRideErrors.Unauthorized,
                    string.IsNullOrEmpty(context.ErrorDescription) ? null : context.ErrorDescription);
            },
            OnForbidden = context => WriteProblemAsync(context.HttpContext, MageRideErrors.Forbidden, null),

            // Δ MCS-30 — AL-08's displacement, enforced on the request rather than at the refresh.
            OnTokenValidated = RefuseRevokedSessionAsync,
        };
    }

    /// <summary>
    /// Refuses a token whose session has been revoked by a sign-in on another device (AL-08).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this, revocation only bit at the next refresh.</b> iam-svc revokes the old
    /// session inside the same transaction that opens the new one, and drops its Redis mirror — but
    /// nothing outside iam-svc reads session state, so the displaced device's access token stayed
    /// valid until it expired. Up to thirty minutes, during which a phone the account had just
    /// been signed out of could still accept rides.
    /// </para>
    /// <para>
    /// <b>Absence of a tombstone is not evidence of anything, and this fails OPEN.</b> Redis is
    /// best-effort across this platform — registry-svc's own rule is that an outage "degrades
    /// coordination rather than refusing requests" — so a missing key, an unreachable server or a
    /// restarted one all leave the request alone. Only a key that is *present* rejects, and a
    /// present key can only have been written by a revocation. The alternative, treating absence as
    /// revocation, would sign every driver on the platform out of a Redis restart.
    /// </para>
    /// <para>
    /// The cost is one string GET per authenticated request against the same Redis every rate-limit
    /// decision already reaches.
    /// </para>
    /// </remarks>
    private static async Task RefuseRevokedSessionAsync(TokenValidatedContext context)
    {
        var jti = context.Principal?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;

        // No `jti` means a token this check cannot speak about — the internal service tokens, for
        // one, which carry no session at all. Not this middleware's business to refuse.
        if (string.IsNullOrEmpty(jti))
        {
            return;
        }

        var redis = context.HttpContext.RequestServices.GetService<IConnectionMultiplexer>();

        if (redis is null)
        {
            return;
        }

        bool revoked;
        try
        {
            revoked = await redis.GetDatabase().KeyExistsAsync(RedisKeys.RevokedSession(jti));
        }
        catch (RedisException)
        {
            // Fail open. See the remarks: a Redis outage must not be an outage of the platform.
            return;
        }

        if (!revoked)
        {
            return;
        }

        // `Fail` rather than writing the response here: it takes the request out of the
        // authenticated path and leaves `OnChallenge` to render it, which is the one place this
        // file writes a problem document.
        context.Fail("device-revoked");

        await WriteProblemAsync(
            context.HttpContext,
            MageRideErrors.DeviceRevoked,
            "This account was signed in on another device.");
    }

    private static async Task WriteProblemAsync(HttpContext context, ErrorCode error, string? detail)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = error.Status;
        context.Response.ContentType = "application/problem+json";

        var problem = MageRideProblem.Create(context, error, detail);
        await context.Response.WriteAsJsonAsync(problem, MageRideJson.Options, "application/problem+json", context.RequestAborted);
    }
}
