using System.Net.Http.Headers;
using MageRide.Iam.Auth;
using MageRide.Iam.Configuration;
using MageRide.Iam.Domain;
using MageRide.Iam.Mqtt;
using MageRide.Iam.Otp;
using MageRide.Iam.Persistence;
using MageRide.Iam.Profiles;
using MageRide.Iam.Rbac;
using MageRide.Iam.Sessions;
using MageRide.Iam.SignIn;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Resilience;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MageRide.Iam;

/// <summary>Everything iam-svc owns on top of the shared kernel.</summary>
public static class IamServiceCollectionExtensions
{
    public static IServiceCollection AddIamServices(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<OtpOptions>()
            .Bind(configuration.GetSection(OtpOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        AddSmsOptions(services, configuration, environment);

        services.AddOptions<TokenOptions>()
            .Bind(configuration.GetSection(TokenOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AuthPolicyOptions>()
            .Bind(configuration.GetSection(AuthPolicyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<OidcOptions>()
            .Bind(configuration.GetSection(OidcOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<IamMqttOptions>()
            .Bind(configuration.GetSection(IamMqttOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // The session-token issuer and the broker settings behind POST /v1/auth/mqtt-token. iam-svc
        // is one of the few services with any business holding the secret that mints one (E-02) —
        // D6' §3.2 eventually moves that to provisioning-svc (C030) and only the signature changes.
        services.AddMageRideMqtt(configuration);

        services.AddSingleton<SigningKeyRing>();
        services.AddSingleton<RefreshTokenCodec>();
        services.AddSingleton<OtpCodes>();
        services.AddSingleton<SmsTemplates>();
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<PhoneHasher>();
        services.AddSingleton<InternalAccessPolicy>();
        services.AddSingleton<IAccessTokenIssuer, AccessTokenIssuer>();

        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IDeviceRepository, DeviceRepository>();
        services.AddSingleton<ISessionRepository, SessionRepository>();
        services.AddSingleton<IOtpAttemptRepository, OtpAttemptRepository>();
        services.AddSingleton<ICredentialRepository, CredentialRepository>();
        services.AddSingleton<IPublisherRepository, PublisherRepository>();

        // C027 — the profile data plane.
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<ISavedAddressRepository, SavedAddressRepository>();
        services.AddSingleton<IEmergencyContactRepository, EmergencyContactRepository>();
        services.AddSingleton<IRoleGrantRepository, RoleGrantRepository>();
        services.AddSingleton<IPdpaRequestRepository, PdpaRequestRepository>();
        services.AddSingleton<IPhoneLookupRepository, PhoneLookupRepository>();
        services.AddSingleton<IBootstrapRepository, BootstrapRepository>();

        // The evaluator and the handler are the kernel's since C062 promoted the matrix out of this
        // service; `AddMageRideAuthorization` registers both. Nothing to add here — and nothing to
        // re-register, because a second `IPermissionEvaluator` would be a second opinion about
        // URD §2.3.

        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IOtpService, OtpService>();
        services.AddScoped<IPortalSignInService, PortalSignInService>();
        services.AddScoped<IMqttTokenService, MqttTokenService>();

        services.AddScoped<IProfileService, ProfileService>();
        services.AddScoped<ISavedAddressService, SavedAddressService>();
        services.AddScoped<IEmergencyContactService, EmergencyContactService>();
        services.AddScoped<IBootstrapService, BootstrapService>();
        services.AddScoped<IUserLookupService, UserLookupService>();
        services.AddScoped<IRoleAdminService, RoleAdminService>();

        AddOidc(services);
        AddOtpSender(services, configuration);

        // iam-svc validates the tokens it just signed. Resolving them from its own JWKS over HTTP
        // would make the service depend on itself to answer a request and would break the moment
        // it is reached under a host name other than the one Jwt:JwksUrl names. PostConfigure so
        // this runs after the kernel's AddMageRideAuth has built the options.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .PostConfigure<SigningKeyRing>((bearer, keys) =>
            {
                bearer.ConfigurationManager = null;
                bearer.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, keyId, _) => keys.Resolve(keyId);
            });

        return services;
    }

    /// <summary>
    /// Binds and vets <c>Sms</c>. Every rule runs at host start rather than on the first OTP:
    /// a service that boots healthy and then cannot deliver a code is a service nobody can sign
    /// in to, discovered by a user rather than by a deploy.
    /// </summary>
    private static void AddSmsOptions(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<SmsOptions>()
            .Bind(configuration.GetSection(SmsOptions.SectionName))
            .Validate(
                static sms => IsProvider(sms, SmsOptions.DevProvider)
                              || IsProvider(sms, SmsOptions.NotifyLkProvider)
                              || IsProvider(sms, SmsOptions.FitSmsProvider),
                $"Sms:Provider must be '{SmsOptions.DevProvider}', '{SmsOptions.NotifyLkProvider}' or " +
                $"'{SmsOptions.FitSmsProvider}'.")
            .Validate(
                static sms => !IsProvider(sms, SmsOptions.NotifyLkProvider)
                              || (!string.IsNullOrWhiteSpace(sms.NotifyLkApiKey)
                                  && !string.IsNullOrWhiteSpace(sms.NotifyLkUserId)),
                "Sms:Provider=notifylk needs Sms:NotifyLkApiKey and Sms:NotifyLkUserId (D7' §4.2). Without them " +
                "every OTP is refused by the gateway and nobody can sign in.")
            .Validate(
                static sms => !IsProvider(sms, SmsOptions.FitSmsProvider)
                              || !string.IsNullOrWhiteSpace(sms.FitSmsApiToken),
                "Sms:Provider=fitsms needs Sms:FitSmsApiToken (D7' §4.2) — their whole '{id}|{secret}' string. " +
                "Without it every OTP is refused by the gateway and nobody can sign in.")
            .Validate(
                static sms => !IsProvider(sms, SmsOptions.FitSmsProvider)
                              || Uri.TryCreate(sms.FitSmsBaseUrl, UriKind.Absolute, out _),
                "Sms:FitSmsBaseUrl must be an absolute URL (default https://app.fitsms.lk/api/v4/).")
            .Validate(
                // Their limit is on an alphanumeric mask; a mask that is a telephone number is a
                // longer string they accept, so the length is only checked when it is not one.
                static sms => !IsProvider(sms, SmsOptions.FitSmsProvider)
                              || sms.FitSmsSenderId.All(char.IsAsciiDigit)
                              || sms.FitSmsSenderId.Length <= 11,
                "Sms:FitSmsSenderId is an alphanumeric sender mask, which Fit SMS caps at 11 characters.")
            .Validate(
                static sms => string.IsNullOrWhiteSpace(sms.SecondaryGateway)
                              || Uri.TryCreate(sms.SecondaryGateway, UriKind.Absolute, out _),
                "Sms:SecondaryGateway must be an absolute URL (D6' §7.3 Dialog/Mobitel fallback).")
            .Validate(
                sms => !IsProvider(sms, SmsOptions.DevProvider)
                       || environment.IsDevelopment()
                       || sms.AllowDevSenderOutsideDevelopment,
                $"Sms:Provider={SmsOptions.DevProvider} writes live OTPs into the log. Outside Development it has " +
                "to be asked for explicitly with Sms:AllowDevSenderOutsideDevelopment=true.")
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    /// <summary>
    /// The SMS transport, its per-gateway retry (D6' §8.3) and the secondary-gateway fallback
    /// (D6' §7.3).
    /// </summary>
    /// <remarks>
    /// <c>TryAdd</c> on <see cref="IOtpSender"/>: a host that already chose a transport keeps it,
    /// which is how the test suite substitutes a capturing sender for the whole pipeline.
    /// </remarks>
    private static void AddOtpSender(IServiceCollection services, IConfiguration configuration)
    {
        // The resilience pipeline is built once, at registration, so the retry budget is read
        // here rather than from the validated IOptions the request path uses. Same section, same
        // value; a bad one is still caught by ValidateOnStart before a request reaches either.
        var sms = configuration.GetSection(SmsOptions.SectionName).Get<SmsOptions>() ?? new SmsOptions();

        // D6' §7.3: "Retry: 2 attempts" — the first plus one more, then the fallback gateway takes
        // over. A third try against a gateway that has refused twice only delays the OTP the user
        // is waiting for.
        var perGateway = new ResilienceOptions
        {
            MaxRetryAttempts = Math.Max(0, sms.MaxAttemptsPerGateway - 1),
            AttemptTimeout = sms.RequestTimeout,
        };

        services.AddHttpClient(NotifyLkOtpSender.HttpClientName)
            .ConfigureHttpClient(static (provider, client) =>
            {
                var sms = provider.GetRequiredService<IOptions<SmsOptions>>().Value;

                // A relative "send" resolves against a base that ends in '/'; without the slash
                // Uri would drop the last path segment and post to the wrong endpoint.
                var baseUrl = sms.NotifyLkBaseUrl.EndsWith('/') ? sms.NotifyLkBaseUrl : sms.NotifyLkBaseUrl + "/";
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = sms.RequestTimeout;
            })
            .AddMageRideResilience(perGateway);

        services.AddHttpClient(FitSmsOtpSender.HttpClientName)
            .ConfigureHttpClient(static (provider, client) =>
            {
                var sms = provider.GetRequiredService<IOptions<SmsOptions>>().Value;

                // A relative "sms/send" resolves against a base that ends in '/'; without the slash
                // Uri would drop the last path segment and post to /api/sms/send.
                var baseUrl = sms.FitSmsBaseUrl.EndsWith('/') ? sms.FitSmsBaseUrl : sms.FitSmsBaseUrl + "/";
                client.BaseAddress = new Uri(baseUrl);

                if (!string.IsNullOrWhiteSpace(sms.FitSmsApiToken))
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sms.FitSmsApiToken);
                }

                // Their documentation lists `Accept: application/json` as a required header, and
                // the send path reads the body to tell an accepted send from a refused one.
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                client.Timeout = sms.RequestTimeout;
            })
            .AddMageRideResilience(perGateway);

        services.AddHttpClient(SecondaryGatewayOtpSender.HttpClientName)
            .ConfigureHttpClient(static (provider, client) =>
            {
                var sms = provider.GetRequiredService<IOptions<SmsOptions>>().Value;

                if (!string.IsNullOrWhiteSpace(sms.SecondaryGateway))
                {
                    client.BaseAddress = new Uri(sms.SecondaryGateway);
                }

                if (!string.IsNullOrWhiteSpace(sms.SecondaryApiKey))
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sms.SecondaryApiKey);
                }

                client.Timeout = sms.RequestTimeout;
            })
            .AddMageRideResilience(perGateway);

        services.TryAddSingleton<IOtpSender>(static provider =>
        {
            var sms = provider.GetRequiredService<IOptions<SmsOptions>>().Value;

            IOtpSender primary;

            if (IsProvider(sms, SmsOptions.FitSmsProvider))
            {
                primary = ActivatorUtilities.CreateInstance<FitSmsOtpSender>(provider);
            }
            else if (IsProvider(sms, SmsOptions.NotifyLkProvider))
            {
                primary = ActivatorUtilities.CreateInstance<NotifyLkOtpSender>(provider);
            }
            else
            {
                return ActivatorUtilities.CreateInstance<DevLoggingOtpSender>(provider);
            }

            if (string.IsNullOrWhiteSpace(sms.SecondaryGateway))
            {
                return primary;
            }

            return ActivatorUtilities.CreateInstance<FallbackOtpSender>(
                provider, primary, ActivatorUtilities.CreateInstance<SecondaryGatewayOtpSender>(provider));
        });
    }

    /// <summary>Google and Apple sign-in (AL-07).</summary>
    private static void AddOidc(IServiceCollection services)
    {
        services.AddHttpClient(HttpOidcKeySource.HttpClientName)
            .ConfigureHttpClient(static client => client.Timeout = TimeSpan.FromSeconds(10))
            .AddMageRideResilience();

        // Deliberately no retry pipeline. An authorization code is single-use: a retry after a
        // response we did not see spends the code and returns invalid_grant, turning a blip into
        // a definite failure. One attempt, one timeout, and the operator signs in again.
        services.AddHttpClient(GoogleAuthCodeExchange.HttpClientName)
            .ConfigureHttpClient(static client => client.Timeout = TimeSpan.FromSeconds(15));

        services.TryAddSingleton<IOidcKeySource, HttpOidcKeySource>();
        services.TryAddSingleton<IOidcTokenVerifier, OidcTokenVerifier>();
        services.TryAddSingleton<IGoogleAuthCodeExchange, GoogleAuthCodeExchange>();
    }

    private static bool IsProvider(SmsOptions options, string provider) =>
        string.Equals(options.Provider, provider, StringComparison.OrdinalIgnoreCase);
}
