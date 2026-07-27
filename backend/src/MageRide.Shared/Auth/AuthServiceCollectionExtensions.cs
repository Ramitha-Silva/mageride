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
using Microsoft.IdentityModel.Tokens;

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
        };
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
