using System.Net;
using MageRide.ApiGateway.Attestation;
using Microsoft.AspNetCore.Authorization;
using MageRide.ApiGateway.Configuration;
using MageRide.ApiGateway.Http;
using MageRide.ApiGateway.RateLimiting;
using MageRide.ApiGateway.Versioning;
using MageRide.Shared.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MageRide.ApiGateway;

/// <summary>Registers everything the edge owns: the proxy, the two gates and the limiter.</summary>
internal static class GatewayServiceCollectionExtensions
{
    /// <summary>Configuration section holding the YARP route and cluster tables.</summary>
    public const string ReverseProxySection = "ReverseProxy";

    public static WebApplicationBuilder AddMageRideGateway(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var configuration = builder.Configuration;

        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        services.AddOptions<GatewayOptions>()
            .Bind(configuration.GetSection(GatewayOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<VersionGateOptions>()
            .Bind(configuration.GetSection(VersionGateOptions.SectionName))
            .Validate(ValidateFloors, "Gateway:VersionGate has a floor that is not a valid version.")
            .ValidateOnStart();

        services.AddOptions<AttestationOptions>()
            .Bind(configuration.GetSection(AttestationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<GatewayRateLimitOptions>()
            .Bind(configuration.GetSection(GatewayRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The kernel's deny-by-default fallback policy (AL-06) is right for a service and wrong for
        // the edge. The gateway registers no authentication scheme, so the fallback's challenge
        // throws "Unable to find the required 'IAuthenticationService'" — turning every unmatched
        // path, and every route someone forgets to mark anonymous, into a 500 instead of a 404.
        // Authorization stays where it can be decided: in the service that owns the resource.
        services.Configure<AuthorizationOptions>(options => options.FallbackPolicy = null);

        services.AddSingleton<VersionFloorService>();
        services.AddSingleton<AttestationPolicy>();

        AddAttestationVerifiers(services);
        AddStateStore(services, configuration);
        ConfigureForwardedHeaders(services, configuration);

        services.AddReverseProxy()
            .LoadFromConfig(configuration.GetSection(ReverseProxySection))
            .AddTransforms(context => GatewayTransforms.Configure(
                context, context.Services.GetRequiredService<IOptionsMonitor<GatewayOptions>>()));

        return builder;
    }

    private static void AddAttestationVerifiers(IServiceCollection services)
    {
        services.AddHttpClient(PlayIntegrityVerifier.HttpClientName);

        services.AddSingleton<IAttestationVerifier, PlayIntegrityVerifier>();
        services.AddSingleton<IAttestationVerifier, AppAttestVerifier>();
    }

    private static void AddStateStore(IServiceCollection services, IConfiguration configuration)
    {
        var store = configuration.GetValue(
            $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.StateStore)}", GatewayStateStore.Redis);

        if (store == GatewayStateStore.Redis)
        {
            // AddMageRideRedis (called by AddMageRideDefaults) has already registered the shared
            // IConnectionMultiplexer and the Redis token-bucket limiter.
            services.AddSingleton<IAttestedKeyStore, RedisAttestedKeyStore>();
            return;
        }

        // Registered plainly, not with TryAdd: when Redis is off the kernel registered nothing, and
        // a later duplicate would still resolve to the last registration.
        services.AddSingleton<ITokenBucketRateLimiter, InMemoryTokenBucketRateLimiter>();
        services.AddSingleton<InMemoryAttestedKeyStore>();
        services.AddSingleton<IAttestedKeyStore>(sp => sp.GetRequiredService<InMemoryAttestedKeyStore>());
    }

    /// <summary>
    /// Trusts <c>X-Forwarded-For</c>/<c>-Proto</c> only from the hops named in configuration.
    /// </summary>
    /// <remarks>
    /// HAProxy terminates TLS in front of the gateway (D7' §2.1), so without this the edge sees
    /// HAProxy's address as every caller's and the rate-limit buckets collapse into one. Trusting
    /// the header unconditionally would be worse: any client could then forge its own address and
    /// pick a fresh bucket per request. C009 / C125 must set
    /// <c>Gateway__ForwardedHeaders__KnownProxies__0</c> to the HAProxy address.
    /// </remarks>
    private static void ConfigureForwardedHeaders(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection($"{GatewayOptions.SectionName}:ForwardedHeaders");
        var knownProxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        var knownNetworks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
        var forwardLimit = section.GetValue("ForwardLimit", 1);

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = forwardLimit;

            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in knownProxies)
            {
                if (IPAddress.TryParse(proxy, out var address))
                {
                    options.KnownProxies.Add(address);
                }
            }

            foreach (var network in knownNetworks)
            {
                if (System.Net.IPNetwork.TryParse(network, out var parsed))
                {
                    options.KnownIPNetworks.Add(parsed);
                }
            }
        });
    }

    private static bool ValidateFloors(VersionGateOptions options)
    {
        foreach (var floor in options.Platforms.Values)
        {
            if (!ClientVersion.TryParse(floor.MinimumVersion, out _)
                || !ClientVersion.TryParse(floor.LatestVersion, out _)
                || (floor.RecommendedVersion is not null && !ClientVersion.TryParse(floor.RecommendedVersion, out _))
                || !Uri.TryCreate(floor.UpdateUrl, UriKind.Absolute, out _))
            {
                return false;
            }
        }

        return true;
    }
}
