using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MageRide.Shared.Fares;

public static class FaresServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="FareEstimateTokenCodec"/> for the two services on either side of a
    /// <c>fareEstimateToken</c>: fare-svc, which issues one, and ride-svc, which refuses a booking
    /// without a valid one (<c>400 invalid-fare-token</c>).
    /// </summary>
    public static IServiceCollection AddMageRideFareTokens(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FareEstimateTokenOptions>()
            .Bind(configuration.GetSection(FareEstimateTokenOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<FareEstimateTokenCodec>();

        return services;
    }
}
