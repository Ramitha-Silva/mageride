using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Authorization;
using MageRide.AdminBff.Directories;
using MageRide.Shared.Storage;
using MageRide.AdminBff.Configuration;
using MageRide.AdminBff.Finance;
using MageRide.AdminBff.Moderation;
using MageRide.AdminBff.Pdpa;
using MageRide.AdminBff.Persistence;
using MageRide.AdminBff.Platform;
using MageRide.AdminBff.Upstream;
using MageRide.AdminBff.Verification;
using MageRide.Analytics;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MageRide.AdminBff;

/// <summary>admin-bff's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class AdminBffServiceCollectionExtensions
{
    public static IServiceCollection AddAdminBffServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AdminBffOptions>()
            .Bind(configuration.GetSection(AdminBffOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // The AL-38 read model, as a library (C061). No endpoints, no authorization, no rollup
        // timer here that the deployment did not ask for — `AddMageRideAnalytics` includes it,
        // because this is the process D7' §4.2 gives the dashboard to.
        services.AddMageRideAnalytics(configuration);

        // Singletons: every repository is stateless over the kernel's connection factory.
        services.TryAddSingleton<IModerationRepository, ModerationRepository>();
        services.TryAddSingleton<IPlatformConfigRepository, PlatformConfigRepository>();
        services.TryAddSingleton<ITrainRepository, TrainRepository>();
        services.TryAddSingleton<IAuditLogRepository, AuditLogRepository>();
        services.TryAddSingleton<IVerificationRepository, VerificationRepository>();
        services.TryAddSingleton<IDirectoryRepository, DirectoryRepository>();
        services.TryAddSingleton<IFinanceRepository, FinanceRepository>();
        services.TryAddSingleton<IPdpaRepository, PdpaRepository>();

        // Singleton: the PII gate is a pure function of the caller's claims over the compiled
        // URD §2.3 matrix — no state, no I/O, and the evaluator it wraps is itself stateless.
        services.TryAddSingleton<IPiiPolicy, PiiPolicy>();

        // Singleton: it holds the signing key and a clock, and minting a link is a pure function of
        // both. A per-request instance would re-read the key on every thumbnail in a grid.
        services.TryAddSingleton<IDocumentLinks, DocumentLinks>();

        // Singleton for the same reason, and a separate one from the document links on purpose: the
        // two sign different payloads for different audiences, and sharing an implementation would
        // let a URL minted for one verify against the other (C065).
        services.TryAddSingleton<IPdpaArtifactLinks, PdpaArtifactLinks>();

        // Δ D-36. admin-bff writes no bytes; it needs the store only to presign a read. Unset
        // `Storage:*` leaves `TryPresign` answering false and `DocumentLinks` on its HMAC fallback.
        services.AddMageRideObjectStore(configuration);

        // Scoped: the audit context is per request by definition, and the services that record into
        // it must share the same instance the interceptor drains.
        services.TryAddScoped<IAdminAuditContext, AdminAuditContext>();
        services.TryAddScoped<IModerationService, ModerationService>();
        services.TryAddScoped<IPlatformConfigService, PlatformConfigService>();
        services.TryAddScoped<ITrainService, TrainService>();
        services.TryAddScoped<IReportQueue, ReportQueue>();
        services.TryAddScoped<ISupportTicketQueue, SupportTicketQueue>();
        services.TryAddScoped<IVerificationService, VerificationService>();

        // Scoped for the same reason the verification service is: it records into the request's
        // own audit context, and a singleton would be recording a PII_READ into somebody else's
        // request.
        services.TryAddScoped<IDirectoryService, DirectoryService>();

        // C065. The two that can change something record into the request's own audit context, so
        // both are scoped; the read service is scoped with them rather than being the one singleton
        // in a family, which is the kind of asymmetry somebody later "tidies up" in the wrong
        // direction.
        services.TryAddScoped<IFinanceService, FinanceService>();
        services.TryAddScoped<IRefundService, RefundService>();
        services.TryAddScoped<IWalletAdjustmentService, WalletAdjustmentService>();
        services.TryAddScoped<IPdpaService, PdpaService>();

        // The filter is resolved per request through the endpoint-filter factory, and holds only
        // options and a logger.
        services.TryAddSingleton<AuditInterceptor>();

        // URD §2.3's ◐ made enforceable where its qualifier names an action this surface does not
        // offer. Enumerable, so it joins the kernel's handlers rather than replacing one.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PlatformWideFeatureHandler>());

        services.AddAdminUpstreams();

        return services;
    }

    /// <summary>
    /// One named <c>HttpClient</c> per upstream, each bounded by its own timeout.
    /// </summary>
    /// <remarks>
    /// Named rather than typed, because the four differ only in where they point and how they
    /// authenticate — and one of them (transit) carries a 200 MB body, so a shared client with one
    /// timeout would either strangle the feed upload or leave a queue read hanging for minutes.
    /// </remarks>
    private static IServiceCollection AddAdminUpstreams(this IServiceCollection services)
    {
        services.TryAddSingleton<IAdminUpstream, AdminUpstream>();

        foreach (var upstream in AdminUpstreams.All)
        {
            services.AddHttpClient(upstream).ConfigureHttpClient((provider, client) =>
            {
                var options = provider
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<AdminBffOptions>>().Value.Upstreams;

                client.Timeout = (upstream switch
                {
                    AdminUpstreams.Safety => options.Safety,
                    AdminUpstreams.Support => options.Support,
                    AdminUpstreams.Content => options.Content,
                    AdminUpstreams.Registry => options.Registry,
                    AdminUpstreams.Fleet => options.Fleet,
                    AdminUpstreams.Wallet => options.Wallet,
                    AdminUpstreams.Fare => options.Fare,
                    _ => options.Transit,
                }).Timeout;
            });
        }

        return services;
    }
}
