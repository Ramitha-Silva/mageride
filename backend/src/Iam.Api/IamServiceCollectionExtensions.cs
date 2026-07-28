using MageRide.Iam.Auth;
using MageRide.Iam.Configuration;
using MageRide.Iam.Otp;
using MageRide.Iam.Persistence;
using MageRide.Iam.Sessions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<SigningKeyRing>();
        services.AddSingleton<RefreshTokenCodec>();
        services.AddSingleton<OtpCodes>();
        services.AddSingleton<IAccessTokenIssuer, AccessTokenIssuer>();

        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IDeviceRepository, DeviceRepository>();
        services.AddSingleton<ISessionRepository, SessionRepository>();
        services.AddSingleton<IOtpAttemptRepository, OtpAttemptRepository>();

        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IOtpService, OtpService>();

        // TryAdd: a host that already chose a transport keeps it. The options validation above is
        // what rejects an unimplemented or unsafe provider, so only the dev sender is reachable.
        services.TryAddSingleton<IOtpSender>(sp => ActivatorUtilities.CreateInstance<DevLoggingOtpSender>(sp));

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
    /// Binds and vets <c>Sms</c>. All three rules run at host start rather than on the first OTP:
    /// a service that boots healthy and then cannot deliver a code is a service nobody can sign
    /// in to, discovered by a user rather than by a deploy.
    /// </summary>
    private static void AddSmsOptions(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<SmsOptions>()
            .Bind(configuration.GetSection(SmsOptions.SectionName))
            .Validate(
                static sms => IsProvider(sms, SmsOptions.DevProvider) || IsProvider(sms, SmsOptions.NotifyLkProvider),
                $"Sms:Provider must be '{SmsOptions.DevProvider}' or '{SmsOptions.NotifyLkProvider}'.")
            .Validate(
                static sms => !IsProvider(sms, SmsOptions.NotifyLkProvider),
                "Sms:Provider=notifylk is not implemented in C020 (ws-iam-minimal). The Notify.lk gateway, its " +
                "Si/Ta/En templates (D-26) and the D-33 secondary gateway land with C026/C051.")
            .Validate(
                sms => !IsProvider(sms, SmsOptions.DevProvider)
                       || environment.IsDevelopment()
                       || sms.AllowDevSenderOutsideDevelopment,
                $"Sms:Provider={SmsOptions.DevProvider} writes live OTPs into the log. Outside Development it has " +
                "to be asked for explicitly with Sms:AllowDevSenderOutsideDevelopment=true.")
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    private static bool IsProvider(SmsOptions options, string provider) =>
        string.Equals(options.Provider, provider, StringComparison.OrdinalIgnoreCase);
}
