using MageRide.Iam.Endpoints;
using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Iam;

/// <summary>
/// Composition root for iam-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class IamApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Redis client id.</summary>
    public const string ServiceName = "iam-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // iam-svc owns no outbox and publishes nothing: a sign-in is not a domain event any
            // other service reacts to, and the skeleton has no consumer for one. C026 adds the
            // audit stream (D-35) if the ADD ends up wanting user.* events.
            UseKafka = false,
            UseOutbox = false,
        };

        // Ahead of AddMageRideDefaults so the CommandLog section still wins if an operator sets
        // it, but the iam defaults apply when nobody does. The kernel's defaults describe
        // rides.command_log, which belongs to another bounded context; an auth command targets no
        // aggregate, so there is no ride_id column to write.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "iam";
            commandLog.AggregateIdColumn = null;
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddIamServices(builder.Configuration, builder.Environment);

        var app = builder.Build();

        // Fail fast. All four are singletons that would otherwise be built on the first request
        // that needed them, so a missing Jwt:SigningKeyPem, Otp:PepperKey or
        // Mqtt:SessionTokenSecret — or an SMS template file that did not make it into the image —
        // would surface as a 500 on somebody's sign-in rather than as a deploy that refused to
        // come up.
        _ = app.Services.GetRequiredService<Auth.SigningKeyRing>();
        _ = app.Services.GetRequiredService<Otp.OtpCodes>();
        _ = app.Services.GetRequiredService<Otp.SmsTemplates>();
        _ = app.Services.GetRequiredService<MageRide.Shared.Mqtt.MqttSessionTokenIssuer>();

        app.UseMageRideDefaults(serviceOptions);

        app.MapJwks();
        app.MapAuthEndpoints();

        return app;
    }
}
