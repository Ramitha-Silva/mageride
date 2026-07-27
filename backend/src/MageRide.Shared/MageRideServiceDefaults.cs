using MageRide.Shared.Auth;
using MageRide.Shared.Caching;
using MageRide.Shared.Errors;
using MageRide.Shared.Health;
using MageRide.Shared.Http;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Messaging;
using MageRide.Shared.Observability;
using MageRide.Shared.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MageRide.Shared;

/// <summary>
/// Which pieces of the kernel a service wants. Everything defaults on; a service that owns no
/// outbox or needs no Kafka producer turns those off rather than opting each other piece in.
/// </summary>
public sealed class MageRideServiceOptions
{
    /// <summary>Service name for telemetry and the Postgres/Kafka client id. Required.</summary>
    public required string ServiceName { get; init; }

    public bool UsePostgres { get; init; } = true;

    public bool UseRedis { get; init; } = true;

    public bool UseKafka { get; init; } = true;

    /// <summary>Register the outbox writer and dispatcher. Only ride-svc and dispatch-svc own one.</summary>
    public bool UseOutbox { get; init; }

    /// <summary>Register the Postgres command log behind <see cref="ICommandLog"/> (R-14).</summary>
    public bool UseCommandLog { get; init; } = true;

    public bool UseAuthentication { get; init; } = true;

    public bool UseTelemetry { get; init; } = true;
}

/// <summary>
/// The one call that gives a MageRide service its cross-cutting behaviour: RFC 7807 errors,
/// idempotent replay, Dapper/Npgsql, Redis, Redpanda, RS256 auth, health probes and telemetry.
/// </summary>
public static class MageRideServiceDefaults
{
    /// <summary>Registers the kernel's services. Pair with <see cref="UseMageRideDefaults"/>.</summary>
    public static WebApplicationBuilder AddMageRideDefaults(
        this WebApplicationBuilder builder, MageRideServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        var services = builder.Services;
        var configuration = builder.Configuration;

        services.TryAddSingleton(TimeProvider.System);

        // camelCase System.Text.Json everywhere (D3' §0).
        services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = MageRideJson.Options.PropertyNamingPolicy;
            json.SerializerOptions.DictionaryKeyPolicy = MageRideJson.Options.DictionaryKeyPolicy;
            json.SerializerOptions.DefaultIgnoreCondition = MageRideJson.Options.DefaultIgnoreCondition;
            json.SerializerOptions.PropertyNameCaseInsensitive = MageRideJson.Options.PropertyNameCaseInsensitive;

            foreach (var converter in MageRideJson.Options.Converters)
            {
                json.SerializerOptions.Converters.Add(converter);
            }
        });

        // Every error leaves as application/problem+json with a registry type URI (D3' §0).
        services.AddProblemDetails(problem => problem.CustomizeProblemDetails =
            context => MageRideProblem.Enrich(context.HttpContext, context.ProblemDetails));
        services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

        services.AddHealthChecks();

        if (options.UseTelemetry)
        {
            services.AddMageRideTelemetry(configuration, options.ServiceName);
        }

        if (options.UsePostgres)
        {
            services.AddMageRidePostgres(configuration);
            services.PostConfigure<PostgresOptions>(postgres => postgres.ApplicationName ??= options.ServiceName);

            if (options.UseCommandLog)
            {
                services.AddMageRideCommandLog(configuration);
            }
        }

        if (options.UseRedis)
        {
            services.AddMageRideRedis(configuration);
            services.PostConfigure<RedisOptions>(redis => redis.ClientName ??= options.ServiceName);
        }

        if (options.UseKafka)
        {
            services.AddMageRideKafka(configuration);
            services.PostConfigure<KafkaOptions>(kafka => kafka.ClientId ??= options.ServiceName);
        }

        if (options.UseOutbox)
        {
            services.AddMageRideOutbox(configuration);
        }

        if (options.UseAuthentication)
        {
            services.AddMageRideAuth(configuration);
        }
        else
        {
            services.AddMageRideAuthorization();
        }

        services.AddOptions<IdempotencyOptions>()
            .Bind(configuration.GetSection(IdempotencyOptions.SectionName));

        return builder;
    }

    /// <summary>
    /// Installs the middleware in the order the guarantees require, then maps the health probes
    /// and the metrics endpoint.
    /// </summary>
    public static WebApplication UseMageRideDefaults(this WebApplication app, MageRideServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        // Outermost: an exception anywhere below still leaves as problem+json.
        app.UseExceptionHandler();

        // 404/405/415 from routing get an RFC 7807 body too, not an empty one.
        app.UseStatusCodePages();

        app.UseRouting();

        if (options.UseAuthentication)
        {
            app.UseAuthentication();
        }

        app.UseAuthorization();

        // After authorization, so the command log records the authenticated actor, and inside the
        // exception handler, so a 5xx releases the key instead of pinning a failure to it (R-14).
        if (options.UseCommandLog && options.UsePostgres)
        {
            app.UseMiddleware<IdempotencyMiddleware>();
        }

        app.MapMageRideHealthChecks();

        if (options.UseTelemetry)
        {
            app.MapMageRideMetrics();
        }

        return app;
    }
}
