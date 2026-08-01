using MageRide.Ocr.Configuration;
using MageRide.Ocr.Endpoints;
using MageRide.Shared.Storage;
using MageRide.Ocr.Gemini;
using MageRide.Ocr.Ocr;
using MageRide.Ocr.Persistence;
using MageRide.Ocr.Pipeline;
using MageRide.Ocr.Queue;
using MageRide.Ocr.Redaction;
using MageRide.Ocr.Storage;
using MageRide.Shared.Resilience;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MageRide.Ocr;

/// <summary>ocr-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class OcrServiceCollectionExtensions
{
    public static IServiceCollection AddOcrServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OcrOptions>()
            .Bind(configuration.GetSection(OcrOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.TesseractConfidenceCeiling < options.ConfidenceThreshold,
                "Ocr:TesseractConfidenceCeiling must be below Ocr:ConfidenceThreshold. Above it, a Gemini outage "
                + "would auto-verify fields the on-prem fallback found by keyword match — and AL-27 approves a "
                + "vehicle on those fields with no human involvement at all.")
            .ValidateOnStart();

        // Singletons, and each for its own reason: the ledger is process-wide by definition, the
        // cascade is a 900 KB model nobody wants to reload per page, and the Tesseract engine caches
        // one availability probe.
        services.TryAddSingleton<IPerimeterLedger, PerimeterLedger>();
        services.TryAddSingleton<IImageEditor, OpenCvImageEditor>();
        services.TryAddSingleton<IFaceDetector, OpenCvFaceDetector>();
        services.TryAddSingleton<IRedactionPipeline, RedactionPipeline>();
        services.TryAddSingleton<IOcrEngine, TesseractCliOcrEngine>();
        services.TryAddSingleton<TesseractFieldExtractor>();
        services.TryAddSingleton<GeminiFieldExtractor>();
        services.TryAddSingleton<IRawDocumentStore, FileSystemRawDocumentStore>();

        // Δ D-36. `Ocr:Storage:Root` stays the filesystem fallback's root, so a deployment that has
        // not set `Storage:*` reads exactly where it read before.
        services.AddMageRideObjectStore(
            configuration, ObjectBucket.Documents, configuration["Ocr:Storage:Root"]);

        // Scoped: it takes the repository, which takes the connection factory. One document, one
        // scope — see ExtractionWorker.
        services.TryAddScoped<IExtractionRepository, ExtractionRepository>();
        services.TryAddScoped<IExtractionPipeline, ExtractionPipeline>();

        services.TryAddSingleton<ExtractionQueue>();
        services.TryAddSingleton<IExtractionDispatcher, ExtractionDispatcher>();
        services.AddHostedService<ExtractionWorker>();

        services.AddGeminiClient(configuration);
        services.AddDocumentStorageClient();

        services.AddHealthChecks()
            .AddCheck<RedactionHealthCheck>("d36-redaction", tags: ["ready"]);

        return services;
    }

    /// <summary>
    /// The one outbound client that reaches a third party, with the perimeter guard on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The guard is registered as the outermost handler, before the resilience pipeline.</b> That
    /// ordering is load-bearing: a retry re-sends the same content, and a guard placed inside the
    /// pipeline would inspect the first attempt and wave the rest through. Outermost, every attempt
    /// passes through it.
    /// </para>
    /// <para>
    /// D6' §8.3's retry/breaker: 3 attempts with jittered backoff, a breaker per dependency. The
    /// per-attempt timeout is <c>Ocr:Gemini:Timeout</c>; the whole document's is the worker's.
    /// </para>
    /// </remarks>
    private static IServiceCollection AddGeminiClient(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTransient<PerimeterGuardHandler>();

        // Read here rather than through IOptions: the resilience pipeline is built once, at
        // registration, and its budgets are not reloadable. The same values are validated on
        // OcrOptions, which is what an operator reads.
        var section = configuration.GetSection(OcrOptions.SectionName).GetSection(nameof(OcrOptions.Gemini));
        var attemptTimeout = section.GetValue(nameof(OcrOptions.GeminiOptions.Timeout), TimeSpan.FromSeconds(20));
        var attempts = Math.Clamp(section.GetValue(nameof(OcrOptions.GeminiOptions.Attempts), 3), 1, 5);

        services.AddHttpClient(GeminiFieldExtractor.HttpClientName)
            .ConfigureHttpClient((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<OcrOptions>>().Value;

                if (!string.IsNullOrWhiteSpace(options.Gemini.BaseUrl))
                {
                    client.BaseAddress = new Uri(options.Gemini.BaseUrl, UriKind.Absolute);
                }

                // The pipeline below owns the per-attempt timeout; HttpClient's own is the backstop
                // over the whole retry sequence, so it must be longer than the sum or it would
                // cancel the second attempt as the first one's budget expires.
                client.Timeout = TimeSpan.FromTicks(options.Gemini.Timeout.Ticks * attempts)
                    + TimeSpan.FromSeconds(10);
            })
            .AddHttpMessageHandler<PerimeterGuardHandler>()
            .AddMageRideResilience(new ResilienceOptions
            {
                // D6' §8.3's "3 attempts", counted the way the kernel counts them — retries after
                // the first. The rest of the pipeline (backoff curve, jitter band, breaker window)
                // is the spec's and is not this service's to restate.
                MaxRetryAttempts = attempts - 1,
                AttemptTimeout = attemptTimeout,
            });

        return services;
    }

    /// <summary>The optional object-storage reader. Off unless <c>AllowHttpSources</c> is on.</summary>
    private static IServiceCollection AddDocumentStorageClient(this IServiceCollection services)
    {
        services.AddHttpClient(FileSystemRawDocumentStore.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(15));

        return services;
    }
}
