using MageRide.AdminBff;
using MageRide.Content;
using MageRide.Dispatch;
using MageRide.Fare;
using MageRide.Fleet;
using MageRide.FleetBilling;
using MageRide.FleetHealth;
using MageRide.Iam;
using MageRide.Notification;
using MageRide.Ocr;
using MageRide.Payout;
using MageRide.Provisioning;
using MageRide.PublicBff;
using MageRide.Query;
using MageRide.Registry;
using MageRide.Reputation;
using MageRide.Ride;
using MageRide.Safety;
using MageRide.Subscriptions;
using MageRide.Support;
using MageRide.Transit;
using MageRide.TripState;
using MageRide.Voip;
using MageRide.Wallet;

namespace MageRide.Contract.Tests.Runtime;

/// <summary>Composes one service's real pipeline — the same call its <c>Program.cs</c> makes.</summary>
internal delegate WebApplication ServiceComposer(WebApplicationOptions options, Action<WebApplicationBuilder> configure);

/// <summary>
/// One contract document and the service that answers it.
/// </summary>
/// <param name="Document">Contract file stem — the key everything in this suite joins on.</param>
/// <param name="Settings">
/// What this service needs beyond the common set to reach a built pipeline. Transcribed from the
/// service's own integration harness, which is the only place the required-options set is written
/// down: a service that validates its options at start-up refuses to compose without them, and a
/// contract test that could not compose a service could not read its route table.
/// </param>
internal sealed record ServiceDefinition(
    string Document,
    ServiceComposer Compose,
    IReadOnlyDictionary<string, string?> Settings);

/// <summary>
/// Every service that owns a document in <c>backend/contracts/</c>, and how to compose it.
///
/// <para>
/// <b>The join is the contract file name.</b> Twenty-four documents have a service; the
/// twenty-fifth, <c>version-check.yaml</c>, is served by the edge itself (C008 — there is no
/// `version-check-svc` cluster) and is covered by `ApiGateway.Tests` rather than composed here,
/// which `ContractCoverageTests` states rather than leaves to be noticed.
/// </para>
///
/// <para>
/// <b>Nothing here is a fake.</b> Each entry calls the service's own <c>XApplication.Build</c> — the
/// composition root <c>Program.cs</c> calls — so the route table this suite reads is the route table
/// the container serves, endpoint filters and authorization metadata included.
/// </para>
/// </summary>
internal static class ServiceCatalog
{
    /// <summary>Long enough for every option validator that checks a key's length.</summary>
    private const string Secret = "c118-contract-suite-not-a-secret-0123456789abcdef";

    /// <summary>An address nothing listens on. Reached only if a test drives a real upstream call.</summary>
    /// <remarks>
    /// <b>Every optional upstream is set, and that is the point of setting them.</b> Several services
    /// map a proxy route only when its upstream is configured — fleet-svc's Epic 23 Mode B family and
    /// its tracker-bind proxy are behind `if (!string.IsNullOrWhiteSpace(settings.SubscriptionBaseUrl))`
    /// and `ProvisioningBaseUrl` — so a suite that left them unset would read a route table with ten
    /// operations missing from it and report the platform as drifted. The contract declares those
    /// operations unconditionally, so the table they are compared against has to be the fully
    /// configured one.
    /// </remarks>
    private const string BlackHole = "http://127.0.0.1:1";

    public static IReadOnlyList<ServiceDefinition> All { get; } = Build();

    /// <summary>
    /// Documents this catalog deliberately does not compose, and why.
    /// </summary>
    /// <remarks>
    /// Named rather than absent: a coverage report whose denominator quietly shrinks is a coverage
    /// report. <c>ContractCoverageTests</c> asserts this set has not grown.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> NotComposed { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version-check"] =
                "Served by the API gateway itself rather than by a service (C008): there is no "
                + "`version-check-svc` cluster, and `ApiGateway.Tests/VersionGateTests` owns it.",
        };

    /// <summary>The settings every service needs, whatever else it needs.</summary>
    public static Dictionary<string, string?> CommonSettings(string postgres, string redis, string issuer) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["ConnectionStrings:Redis"] = redis,

            // A consumer of iam-svc's key, never a holder of it: the harness hands the bearer
            // handler the public half directly (see `ServiceFleet`), so no JWKS is fetched.
            ["Jwt:JwksUrl"] = $"{BlackHole}/.well-known/jwks.json",
            ["Jwt:Issuer"] = issuer,
            ["Jwt:RequireHttpsMetadata"] = "false",

            // The broker address has to be *present* — the kernel validates the producer's options
            // at start-up — and is never dialled, because every dispatcher and consumer below is
            // off. A contract suite asserts an HTTP surface; a background worker that woke up would
            // only add flakiness to it.
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = "false",

            ["urls"] = "http://127.0.0.1:0",
            ["Otel:PrometheusEnabled"] = "false",
        };

    private static IReadOnlyList<ServiceDefinition> Build()
    {
        var root = Path.Combine(Path.GetTempPath(), "mageride-c118", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);

        string Storage(string name)
        {
            var path = Path.Combine(root, name);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        return
        [
            new ServiceDefinition("admin-bff", AdminBffApplication.Build, new Dictionary<string, string?>
            {
                ["AdminBff:Documents:SigningKey"] = Secret,
                ["AdminBff:Documents:PublicBaseUrl"] = BlackHole,
                ["AdminBff:Audit:PublishToTopic"] = "false",
                ["Analytics:RollupEnabled"] = "false",
                ["AdminBff:Upstreams:Content:BaseUrl"] = BlackHole,
                ["AdminBff:Upstreams:Fare:BaseUrl"] = BlackHole,
                ["AdminBff:Upstreams:Fleet:BaseUrl"] = BlackHole,
                ["AdminBff:Upstreams:Fleet:InternalApiKey"] = Secret,
                ["AdminBff:Upstreams:Registry:BaseUrl"] = BlackHole,
                ["AdminBff:Upstreams:Registry:InternalApiKey"] = Secret,
                ["AdminBff:Upstreams:Safety:BaseUrl"] = BlackHole,
                ["AdminBff:Upstreams:Safety:InternalApiKey"] = Secret,
                ["AdminBff:Upstreams:Support:BaseUrl"] = BlackHole,
                ["AdminBff:Upstreams:Support:InternalApiKey"] = Secret,
                ["AdminBff:Upstreams:Transit:BaseUrl"] = BlackHole,
                ["AdminBff:Upstreams:Wallet:BaseUrl"] = BlackHole,
                ["AdminBff:Upstreams:Wallet:InternalApiKey"] = Secret,
            }),

            new ServiceDefinition("content", ContentApplication.Build, new Dictionary<string, string?>
            {
                ["Content:AssetBaseUrl"] = BlackHole,
                ["Content:InternalApiKey"] = Secret,
            }),

            new ServiceDefinition("dispatch", DispatchApplication.Build, new Dictionary<string, string?>
            {
                ["Dispatch:InternalApiKey"] = Secret,
                ["Dispatch:RideServiceBaseUrl"] = BlackHole,
                ["Dispatch:RideServiceInternalKey"] = Secret,
                ["Dispatch:ReputationGrpcAddress"] = BlackHole,
                ["Dispatch:ReputationInternalKey"] = Secret,
                ["Dispatch:ConsumerEnabled"] = "false",
                ["Dispatch:PositionConsumerEnabled"] = "false",
                ["Dispatch:ExpiryWorkerEnabled"] = "false",
                ["Dispatch:LevelWorkerEnabled"] = "false",
                ["Dispatch:ScheduledWorkerEnabled"] = "false",
                ["Dispatch:DispatchTimerWorkerEnabled"] = "false",
                ["Dispatch:KeyspaceNotificationsEnabled"] = "false",
                ["Fare:EstimateTokenKey"] = Secret,
                ["Ride:InternalApiKey"] = Secret,
            }),

            new ServiceDefinition("fare", FareApplication.Build, new Dictionary<string, string?>
            {
                ["Fare:DispatchBaseUrl"] = BlackHole,
                ["Fare:EstimateTokenKey"] = Secret,
                ["Fare:InternalApiKey"] = Secret,
                ["Fare:RideBaseUrl"] = BlackHole,
                ["Fare:RideInternalApiKey"] = Secret,
                ["Fare:WalletBaseUrl"] = BlackHole,
                ["Fare:WalletInternalApiKey"] = Secret,
                ["Fare:PenaltySettlementEnabled"] = "false",
                ["Fare:QrNudgeEnabled"] = "false",
            }),

            new ServiceDefinition("fleet", FleetApplication.Build, new Dictionary<string, string?>
            {
                ["Fleet:SubscriptionBaseUrl"] = BlackHole,
                // `Fleet:ProvisioningBaseUrl` is deliberately NOT set, and `RouteDrift` records why:
                // setting it maps `POST /v1/fleets/{fleetId}/trackers/bind`, whose handler takes
                // `ITrackerBindingService` without `[FromServices]` — and that interface has an
                // instance `BindAsync`, which minimal APIs read as a custom binder and refuse. The
                // route cannot be mapped; fleet-svc throws while composing. Every existing harness
                // leaves the setting unset too, which is why nothing has caught it.
                ["Fleet:NotificationBaseUrl"] = BlackHole,
                ["Fleet:OcrBaseUrl"] = BlackHole,
                ["Fleet:InternalApiKey"] = Secret,
                ["Fleet:DocumentRoot"] = Storage("fleet-documents"),
            }),

            new ServiceDefinition("fleet-billing", FleetBillingApplication.Build, new Dictionary<string, string?>
            {
                ["FleetBilling:NotificationBaseUrl"] = BlackHole,
                ["FleetBilling:OnepayBaseUrl"] = BlackHole,
                ["FleetBilling:InternalApiKey"] = Secret,
                ["FleetBilling:InvoicingEnabled"] = "false",
                ["FleetBilling:WalletBaseUrl"] = BlackHole,
                ["FleetBilling:WalletInternalApiKey"] = Secret,
                ["Wallet:InternalApiKey"] = Secret,
                ["Onepay:WebhookSecret"] = Secret,
                ["ComBankIpg:WebhookSecret"] = Secret,
                ["LankaQr:MerchantId"] = "C118TEST",
                ["LankaQr:DeepLinkTemplate"] = "lankaqr://pay?ref={reference}",
            }),

            new ServiceDefinition("fleet-health", FleetHealthApplication.Build, new Dictionary<string, string?>
            {
                ["Health:AlertsEnabled"] = "false",
                ["Health:SweepEnabled"] = "false",
                ["Health:PingConsumerEnabled"] = "false",
                ["Health:ProvisioningConsumerEnabled"] = "false",
                ["Health:DevicePlaneEnabled"] = "false",
                ["Mqtt:Host"] = "127.0.0.1",
                ["Mqtt:Port"] = "1",
                ["Mqtt:SessionTokenSecret"] = Secret,
            }),

            new ServiceDefinition("iam", IamApplication.Build, new Dictionary<string, string?>
            {
                ["Auth:InternalApiKey"] = Secret,
                ["Auth:PhoneHashKey"] = Secret,
                // iam-svc validates this on composition: `AuthPolicyOptions` requires
                // 100 000–5 000 000. The floor, because this suite hashes no password it cares
                // about and composes twenty-four pipelines.
                ["Auth:PasswordIterations"] = "100000",
                ["Otp:PepperKey"] = Secret,
                ["Mqtt:SessionTokenSecret"] = Secret,
            }),

            new ServiceDefinition("notification", NotificationApplication.Build, new Dictionary<string, string?>
            {
                ["Notification:InternalApiKey"] = Secret,
                ["Notification:ContentBaseUrl"] = BlackHole,
                ["Notification:ContentInternalApiKey"] = Secret,
                ["Notification:WebTrackBaseUrl"] = "https://passenger.mageride.test",
                ["Notification:ConsumersEnabled"] = "false",
                ["Notification:DeliveryEnabled"] = "false",
                ["Notification:OfferAckSweepEnabled"] = "false",
                ["Notification:RetentionSweepEnabled"] = "false",
                ["Sms:Provider"] = "notifylk",
                ["Sms:NotifyLkBaseUrl"] = BlackHole,
                ["Sms:NotifyLkApiKey"] = Secret,
                ["Sms:NotifyLkUserId"] = "c118",
                ["Sms:SecondaryGateway"] = BlackHole,
                ["Sms:SecondaryApiKey"] = Secret,
            }),

            new ServiceDefinition("ocr", OcrApplication.Build, new Dictionary<string, string?>
            {
                ["Ocr:InternalApiKey"] = Secret,
                ["Ocr:Gemini:BaseUrl"] = BlackHole,
                ["Ocr:Gemini:ApiKey"] = Secret,
                ["Ocr:Storage:Root"] = Storage("ocr-storage"),
                ["Ocr:Tesseract:WorkRoot"] = Storage("ocr-work"),
            }),

            new ServiceDefinition("payout", PayoutApplication.Build, new Dictionary<string, string?>
            {
                ["Payout:Enabled"] = "false",
                ["Payout:InternalApiKey"] = Secret,
                ["Payout:BankBaseUrl"] = BlackHole,
                ["Payout:WalletBaseUrl"] = BlackHole,
                ["Payout:WalletInternalApiKey"] = Secret,
                ["Wallet:InternalApiKey"] = Secret,
            }),

            new ServiceDefinition("provisioning", ProvisioningApplication.Build, new Dictionary<string, string?>
            {
                ["Provisioning:InternalApiKey"] = Secret,
                ["Provisioning:ErrorReportSigningKey"] = Secret,
                ["Provisioning:BulkMintEnabled"] = "false",
                ["Provisioning:RotationEnabled"] = "false",
                ["StepCa:RootKeyPath"] = Path.Combine(Storage("step-ca"), "root.key"),
            }),

            new ServiceDefinition("public-bff", PublicBffApplication.Build, new Dictionary<string, string?>
            {
                ["PublicBff:Ride:BaseUrl"] = BlackHole,
                ["PublicBff:Ride:InternalApiKey"] = Secret,
                ["PublicBff:Safety:BaseUrl"] = BlackHole,
                ["PublicBff:Safety:InternalApiKey"] = Secret,
            }),

            new ServiceDefinition("query", QueryApplication.Build, new Dictionary<string, string?>
            {
                ["Query:FareBaseUrl"] = BlackHole,
                ["Query:TransitBaseUrl"] = BlackHole,
                ["Query:NominatimBaseUrl"] = BlackHole,
                ["Query:InternalApiKey"] = Secret,
                ["Query:GrpcListenPort"] = "0",
            }),

            new ServiceDefinition("registry", RegistryApplication.Build, new Dictionary<string, string?>
            {
                ["Registry:OcrBaseUrl"] = BlackHole,
                ["Registry:InternalApiKey"] = Secret,
                ["Registry:DocumentExpiryEnabled"] = "false",
            }),

            new ServiceDefinition("reputation", ReputationApplication.Build, new Dictionary<string, string?>
            {
                ["Reputation:InternalApiKey"] = Secret,
                ["Reputation:GrpcListenPort"] = "0",
                ["Reputation:ConsumerEnabled"] = "false",
                ["Reputation:DetectorEnabled"] = "false",
                ["Reputation:ExpiryWorkerEnabled"] = "false",
            }),

            new ServiceDefinition("ride", RideApplication.Build, new Dictionary<string, string?>
            {
                ["Ride:IamBaseUrl"] = BlackHole,
                ["Ride:InternalApiKey"] = Secret,
                ["Ride:PhoneHashKey"] = Secret,
                ["Ride:OtpPepper"] = Secret,
                ["Ride:TimersEnabled"] = "false",
                ["Ride:StuckStateMetricsEnabled"] = "false",
                ["Ride:ProofPhotoRoot"] = Storage("ride-proof"),
                ["Fare:EstimateTokenKey"] = Secret,
            }),

            new ServiceDefinition("safety", SafetyApplication.Build, new Dictionary<string, string?>
            {
                ["Safety:InternalApiKey"] = Secret,
                ["Safety:NotificationBaseUrl"] = BlackHole,
                ["Safety:NotificationInternalApiKey"] = Secret,
                ["Safety:ShareBaseUrl"] = "https://track.mageride.test",
                ["Safety:ReputationReportingEnabled"] = "false",
            }),

            new ServiceDefinition("subscription", SubscriptionApplication.Build, new Dictionary<string, string?>
            {
                ["Subscription:InternalApiKey"] = Secret,
                ["Subscription:ModeBBillingEnabled"] = "false",
                ["Subscription:FileLinkSigningKey"] = Secret,
                ["Subscription:SlipRoot"] = Storage("subscription-slips"),
                ["Subscription:WalletBaseUrl"] = BlackHole,
                ["Subscription:WalletInternalApiKey"] = Secret,
                ["Subscription:OnepayWebhookSecret"] = Secret,
                ["Subscription:LankaQrWebhookSecret"] = Secret,
                ["Wallet:InternalApiKey"] = Secret,
            }),

            new ServiceDefinition("support", SupportApplication.Build, new Dictionary<string, string?>
            {
                ["Support:InternalApiKey"] = Secret,
                ["Support:FileLinkSigningKey"] = Secret,
                ["Support:ScreenshotRoot"] = Storage("support-screenshots"),
            }),

            new ServiceDefinition("transit", TransitApplication.Build, new Dictionary<string, string?>
            {
                ["Transit:PublicBaseUrl"] = BlackHole,
                ["Transit:Gtfs:StorageRoot"] = Storage("gtfs"),
                ["Transit:Gtfs:DownloadSigningKey"] = Secret,
            }),

            new ServiceDefinition("trip-state", TripStateApplication.Build, new Dictionary<string, string?>
            {
                ["TripState:InternalApiKey"] = Secret,
                ["TripState:PositionConsumerEnabled"] = "false",
                ["TripState:SweepEnabled"] = "false",
            }),

            new ServiceDefinition("voip", VoipApplication.Build, new Dictionary<string, string?>
            {
                ["Voip:LiveKit:ApiUrl"] = BlackHole,
                ["Voip:LiveKit:WsUrl"] = "ws://127.0.0.1:1",
                ["Voip:LiveKit:ApiKey"] = Secret,
                ["Voip:LiveKit:ApiSecret"] = Secret,
                ["Voip:RoomTeardownEnabled"] = "false",
            }),

            new ServiceDefinition("wallet", WalletApplication.Build, new Dictionary<string, string?>
            {
                ["Wallet:OnepayBaseUrl"] = BlackHole,
                ["Wallet:InternalApiKey"] = Secret,
                ["Onepay:WebhookSecret"] = Secret,
                ["ComBankIpg:WebhookSecret"] = Secret,
                ["LankaQr:MerchantId"] = "C118TEST",
                ["LankaQr:DeepLinkTemplate"] = "lankaqr://pay?ref={reference}",
            }),
        ];
    }
}
