using MageRide.Notification.Configuration;
using MageRide.Notification.Messaging;
using MageRide.Notification.Persistence;
using MageRide.Notification.Push;
using MageRide.Notification.Sending;
using MageRide.Notification.Sms;
using MageRide.Notification.Templates;
using MageRide.Notification.Tokens;
using MageRide.Shared.RateLimiting;
using MageRide.Shared.Resilience;

namespace MageRide.Notification;

/// <summary>notification-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Bound to the same `Sms` section iam-svc binds, with the same property names, so one set of
        // environment variables configures both (D7' §4.2 declares the keys once).
        services.AddOptions<SmsOptions>()
            .Bind(configuration.GetSection(SmsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var settings = configuration.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>()
                       ?? new NotificationOptions();

        var sms = configuration.GetSection(SmsOptions.SectionName).Get<SmsOptions>() ?? new SmsOptions();

        // Singletons: each holds a connection factory and a handful of SQL strings.
        services.AddSingleton<INotificationRepository, NotificationRepository>();
        services.AddSingleton<IDeviceTokenRepository, DeviceTokenRepository>();
        services.AddSingleton<IRecipientRepository, RecipientRepository>();
        services.AddSingleton<ILocationRequestLookup, LocationRequestLookup>();
        services.AddSingleton<IShareTokenMinter, ShareTokenMinter>();

        // Δ C066. Beside the minter because it is written in the same handler and for the same
        // recipient: the token opens SCR-WT-002 and this is the code that page shows.
        services.AddSingleton<IDeliveryCodeStore, DeliveryCodeStore>();

        // The rendered-template cache lives in this instance, so the source is a singleton and the
        // purge subscriber below shares it.
        services.AddSingleton<ITemplateSource, ContentTemplateClient>();

        services.AddSingleton<ITokenBucketRateLimiter, RedisTokenBucketRateLimiter>();

        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<INotificationDeliverer, NotificationDeliverer>();

        // Push transports. Registration order is the preference order within a platform; the log
        // transport answers `*` and is only reached when no live channel claims the platform.
        if (string.Equals(settings.PushProvider, NotificationOptions.LiveProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<GoogleAccessTokenSource>();
            services.AddSingleton<IPushChannel, FcmPushChannel>();
            services.AddSingleton<IPushChannel, ApnsPushChannel>();
        }
        else
        {
            services.AddSingleton<IPushChannel, LoggingPushChannel>();
        }

        // SMS gateways. Both concrete gateways are always registered — `IsConfigured` is what decides
        // whether one is used, and SmsSender needs to be able to *see* the secondary to know whether
        // D-33's parallel dispatch is possible at all.
        // The primary is the one the provider names. Registering only the selected gateway — rather
        // than both real ones — is what stops `SmsSender` having to guess which of two configured
        // primaries is meant, and it means an unset token on the OTHER gateway is not a start-up
        // failure for a deployment that does not use it.
        if (string.Equals(sms.Provider, SmsOptions.DevProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ISmsGateway, LoggingSmsGateway>();
        }
        else if (string.Equals(sms.Provider, SmsOptions.FitSmsProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ISmsGateway, FitSmsGateway>();
        }
        else
        {
            services.AddSingleton<ISmsGateway, FitSmsGateway>();
        }

        services.AddSingleton<ISmsGateway, SecondarySmsGateway>();
        services.AddSingleton<ISmsSender, SmsSender>();

        // Scoped: one per consumed message, resolved inside the consumer's own scope.
        services.AddScoped<DispatchEventHandler>();
        services.AddScoped<RideEventHandler>();
        services.AddScoped<WalletEventHandler>();
        services.AddScoped<RegistryEventHandler>();

        AddHttpClients(services, settings, sms);

        return services;
    }

    private static void AddHttpClients(IServiceCollection services, NotificationOptions settings, SmsOptions sms)
    {
        // Named clients through IHttpClientFactory rather than typed clients: every consumer above is
        // a singleton, and a singleton that captures a typed HttpClient pins one message handler for
        // the process's lifetime — which is how a service stops noticing that its dependency moved.
        services.AddHttpClient(ContentTemplateClient.HttpClientName, client =>
            {
                if (!string.IsNullOrWhiteSpace(settings.ContentBaseUrl))
                {
                    client.BaseAddress = new Uri(Slash(settings.ContentBaseUrl));
                }

                client.Timeout = settings.ContentTimeout;
            })
            .AddMageRideResilience();

        services.AddHttpClient(GoogleAccessTokenSource.HttpClientName, client =>
                client.Timeout = TimeSpan.FromSeconds(10))
            .AddMageRideResilience();

        // **No resilience pipeline on the two push channels, deliberately.** D6' §8.3's retry is for
        // an idempotent internal hop; a push is neither, and E-01's whole budget is three seconds —
        // a retry inside the client would spend the window that the SMS fallback is supposed to
        // rescue. Retrying is `comms.notifications`' job, on D-27's schedule, where it is visible.
        services.AddHttpClient(FcmPushChannel.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(Slash(settings.FcmBaseUrl));
            client.Timeout = settings.PushTimeout;
        });

        services.AddHttpClient(ApnsPushChannel.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(Slash(settings.ApnsBaseUrl));
            client.Timeout = settings.PushTimeout;

            // APNs speaks HTTP/2 and nothing else. Pinned rather than negotiated, so a proxy that
            // downgrades the connection fails loudly instead of silently never delivering.
            client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        });

        // Fit SMS. The bearer token is set once here rather than per request: it is a static
        // credential, and a header added in the gateway would be re-added on every retry the
        // resilience pipeline makes.
        services.AddHttpClient(FitSmsGateway.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(Slash(sms.FitSmsBaseUrl));
                client.Timeout = sms.RequestTimeout;
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                if (!string.IsNullOrWhiteSpace(sms.FitSmsApiToken))
                {
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sms.FitSmsApiToken);
                }
            })
            .AddMageRideResilience(new ResilienceOptions { MaxRetryAttempts = sms.MaxAttemptsPerGateway - 1 });

        services.AddHttpClient(SecondarySmsGateway.HttpClientName, client =>
            {
                if (!string.IsNullOrWhiteSpace(sms.SecondaryGateway))
                {
                    client.BaseAddress = new Uri(Slash(sms.SecondaryGateway));
                }

                client.Timeout = sms.RequestTimeout;

                if (!string.IsNullOrWhiteSpace(sms.SecondaryApiKey))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        "Authorization", $"Bearer {sms.SecondaryApiKey}");
                }
            })
            .AddMageRideResilience(new ResilienceOptions { MaxRetryAttempts = sms.MaxAttemptsPerGateway - 1 });
    }

    private static string Slash(string url) => url.EndsWith('/') ? url : url + "/";
}
