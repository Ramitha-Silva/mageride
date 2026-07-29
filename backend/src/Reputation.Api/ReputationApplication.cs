using System.Net;
using MageRide.Reputation.Configuration;
using MageRide.Reputation.Endpoints;
using MageRide.Reputation.Grpc;
using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Reputation;

/// <summary>
/// Composition root for reputation-svc. Lives here rather than in <c>Program.cs</c> so the test
/// suite drives the same pipeline the process runs.
/// </summary>
public static class ReputationApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Kafka client id.</summary>
    public const string ServiceName = "reputation-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // reputation.outbox (migration 0803) carries fraud.suspected and
            // reputation.block_state_changed; the kernel's defaults describe rides.outbox, so all
            // three Outbox settings are overridden below.
            UseOutbox = true,

            // The D-04 gate is read once per candidate per offer round — the busiest read in the
            // platform that is not a position sample. The DoD's 20 ms p95 is measured against a
            // warm cache and Postgres is the fallback, not the hot path.
            UseRedis = true,
        };

        ConfigureDefaults(builder);

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddReputationServices(builder.Configuration);

        ConfigureEndpoints(builder);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        var settings = app.Services.GetRequiredService<IOptions<ReputationOptions>>().Value;

        app.MapAdminReputationEndpoints();

        // AllowAnonymous for the same reason ride-svc's /v1/internal family has it: the caller is a
        // service, not a user, so there is no bearer to present and the kernel's deny-by-default
        // fallback policy would answer every call `Unauthenticated`. InternalKeyInterceptor is what
        // actually authenticates the hop until the mesh lands (C042).
        //
        // AllowMissingIdempotencyKey because a gRPC call is an HTTP POST and the kernel demands the
        // header on every one (D3' §0) — a caller that cannot set it gets `400
        // idempotency-key-required`, which reaches the client as an unreadable "Bad gRPC response".
        // The RPCs carry their own dedupe key instead: reputation.intake_log claims every fact
        // before it moves a counter, which is a stronger guarantee than the header's, since it also
        // covers a retry that regenerated it.
        app.MapGrpcService<ReputationGrpcService>()
            .AllowAnonymous()
            .AllowMissingIdempotencyKey();

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalReputationEndpoints(settings.InternalApiKey);
        }
        else
        {
            // Two consequences, and the second is the one that matters: without the key the gRPC
            // service answers any caller that can reach the port. That is tolerable on one dev host
            // and is not tolerable anywhere else, so it is said loudly rather than defaulted
            // quietly.
            app.Logger.LogWarning(
                "Reputation:InternalApiKey is not configured: /v1/internal/reputation/** is unmapped (the E-07 " +
                "IP/ASN clustering input has no intake) and reputation.v1 gRPC accepts any in-cluster caller.");
        }

        StartWorkers(app, settings);

        return app;
    }

    private static void ConfigureDefaults(WebApplicationBuilder builder)
    {
        var reputation = builder.Configuration.GetSection(ReputationOptions.SectionName);

        // Registered ahead of AddMageRideDefaults so an operator's own section still wins, and the
        // reputation defaults apply when nobody sets one — the shape dispatch-svc uses. The
        // kernel's defaults describe rides.outbox / ride_outbox / ride.events, which belong to
        // another bounded context: pointing this service at them would publish fraud.suspected onto
        // ride-svc's topic and wake ride-svc's dispatcher.
        builder.Services.Configure<OutboxOptions>(outbox =>
        {
            outbox.Schema = "reputation";
            outbox.Channel = "reputation_outbox";
            outbox.Topic = EventTopics.ReputationEvents;
        });

        // migration 0803 (c) — the fourth per-service command log (iam 0104, registry 0307,
        // dispatch 0710). AggregateIdColumn is null because an admin decision targets a user or a
        // flag and there is no single column that would hold either; the kernel's PostgresCommandLog
        // omits the column when it is null.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "reputation";
            commandLog.AggregateIdColumn = null;
        });

        ConfigureListeners(builder, reputation);
    }

    /// <summary>
    /// Binds the two endpoints this service needs: HTTP/1.1 for the admin routes, and a separate
    /// HTTP/2-only one for <c>reputation.v1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>They cannot be the same socket.</b> Cleartext HTTP has no ALPN, so Kestrel cannot
    /// negotiate between HTTP/1.1 and HTTP/2 on one port — an endpoint that serves the admin routes
    /// answers a gRPC client's HTTP/2 preface with <c>GOAWAY HTTP_1_1_REQUIRED</c>. That is why
    /// D7' §4.2 gives reputation-svc a <c>Grpc__ListenPort</c> and why this is the one service in
    /// the platform that binds its own listeners rather than taking <c>ASPNETCORE_URLS</c> as-is.
    /// </para>
    /// <para>
    /// <c>urls</c> / <c>ASPNETCORE_URLS</c> is still honoured — it decides the HTTP endpoints, and
    /// the gRPC endpoint is added beside them on the same host. Calling <c>Listen</c> at all means
    /// Kestrel ignores the URL set, so it is parsed here rather than left to the host.
    /// </para>
    /// </remarks>
    private static void ConfigureListeners(WebApplicationBuilder builder, IConfigurationSection reputation)
    {
        var configured = builder.Configuration["urls"] ?? builder.Configuration["ASPNETCORE_URLS"];

        var httpAddresses = string.IsNullOrWhiteSpace(configured)
            ? [$"http://0.0.0.0:{reputation.GetValue("HttpListenPort", 5000)}"]
            : configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var grpcPort = reputation.GetValue("GrpcListenPort", 5005);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            var host = IPAddress.Any;

            foreach (var address in httpAddresses)
            {
                var binding = BindingAddress.Parse(address);
                host = ResolveHost(binding.Host);

                kestrel.Listen(host, binding.Port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
            }

            // Second, and the order is load-bearing: IServerAddressesFeature reports bound
            // addresses in Listen order, which is how a caller (and the test harness) tells the
            // gRPC endpoint from the HTTP one when both were given port 0.
            kestrel.Listen(host, grpcPort, endpoint => endpoint.Protocols = HttpProtocols.Http2);
        });
    }

    private static IPAddress ResolveHost(string host) => host switch
    {
        "localhost" => IPAddress.Loopback,
        "*" or "+" => IPAddress.Any,
        _ => IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Any,
    };

    private static void ConfigureEndpoints(WebApplicationBuilder builder)
    {
        var reputation = builder.Configuration.GetSection(ReputationOptions.SectionName);

        if (reputation.GetValue("ConsumerEnabled", true))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<Messaging.RideEventConsumer>());
        }

        if (reputation.GetValue("ExpiryWorkerEnabled", true))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<Workers.BlockStateExpiryWorker>());
        }

        if (reputation.GetValue("DetectorEnabled", true))
        {
            builder.Services.AddHostedService(
                services => services.GetRequiredService<Workers.CollusionDetectorWorker>());
        }
    }

    private static void StartWorkers(WebApplication app, ReputationOptions settings)
    {
        if (!settings.ConsumerEnabled)
        {
            // D6' §2.1 lists reputation-svc as a consumer of ride.events and ride-svc calls no gRPC
            // report (C032 publishes and does not call), so with the consumer off **nothing counts
            // anything**: no cancellation is tallied, no report threshold is ever reached, and
            // every block status answers OK forever. That is invisible from the outside — the gate
            // works, it just always opens — so it is said here.
            app.Logger.LogWarning(
                "Reputation:ConsumerEnabled is off: ride.events is not consumed, so no counter moves in this " +
                "process and every block status stays OK (D6' §2.1).");
        }

        if (!settings.DetectorEnabled)
        {
            app.Logger.LogWarning("Reputation:DetectorEnabled is off: no E-07 signal is raised in this process.");
        }
    }
}
