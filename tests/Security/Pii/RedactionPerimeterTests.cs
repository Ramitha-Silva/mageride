using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MageRide.Contract.Tests.Runtime;
using MageRide.Ocr.Gemini;
using MageRide.Ocr.Redaction;

namespace MageRide.Security.Tests.Pii;

/// <summary>
/// D-36 / ADD §12.5: no document reaches the external model without the in-perimeter redaction
/// pre-pass. Asserted as <b>wiring on the composed service</b>, which is the half that fails
/// silently.
///
/// <para>
/// <b>ocr-svc's own suite proves the pre-pass works; this proves it is still in the way.</b> The
/// two fences D-36 rests on are a type nobody outside <c>RedactionPipeline</c> can construct, and a
/// <see cref="PerimeterGuardHandler"/> on the outbound client. The first cannot be removed without
/// a compiler error. The second is one line in a DI registration, and taking it out changes no test
/// in any per-service suite that does not look for it — while a disarmed redactor, as ocr-svc's own
/// CLAUDE.md puts it, looks exactly like a working one from the outside.
/// </para>
/// </summary>
public sealed class RedactionPerimeterTests
{
    [Fact]
    public async Task The_perimeter_guard_is_on_the_Gemini_client_of_the_composed_service()
    {
        var ocr = ServiceCatalog.All.Single(static service => service.Document == "ocr");
        var application = ServiceComposition.Compose(ocr);

        var factory = application.Services.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(GeminiFieldExtractor.HttpClientName);

        // The client is built exactly as the extractor builds it. Posting an image the ledger never
        // admitted must not reach the network at all — and the address below is one nothing listens
        // on, so a connection error would mean the guard let it past.
        client.BaseAddress = new Uri("http://127.0.0.1:1/", UriKind.Absolute);

        var unredacted = RandomNumberGenerator.GetBytes(4096);
        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { inline_data = new { mime_type = "image/png", data = Convert.ToBase64String(unredacted) } },
                    },
                },
            },
        };

        var violation = await Assert.ThrowsAnyAsync<Exception>(
            () => client.PostAsJsonAsync("/v1beta/models/gemini-flash-3.0:generateContent", payload,
                TestContext.Current.CancellationToken));

        var perimeter = Unwrap(violation);

        Assert.True(
            perimeter is PerimeterViolationException,
            "An image the D-36 pre-pass never produced was allowed onto the Gemini client's wire. The "
            + $"request failed with {violation.GetType().Name}: {violation.Message} — which is a transport "
            + "error, not a refusal. `PerimeterGuardHandler` is no longer on that client's handler chain.");
    }

    [Fact]
    public async Task A_redacted_document_is_allowed_through_the_same_guard()
    {
        // The other direction, so the assertion above is "the guard is armed" rather than "this
        // client is broken". Admitting the hash is what `RedactionPipeline` does on the way out.
        var ledger = new PerimeterLedger();
        var redacted = RandomNumberGenerator.GetBytes(4096);

        ledger.Admit(Convert.ToHexStringLower(SHA256.HashData(redacted)));

        using var guard = new PerimeterGuardHandler(
            ledger, LoggerFactory.Create(static builder => { }).CreateLogger<PerimeterGuardHandler>())
        {
            InnerHandler = new AcceptEverything(),
        };

        using var client = new HttpClient(guard) { BaseAddress = new Uri("http://perimeter.test/") };

        using var response = await client.PostAsJsonAsync(
            "/generate",
            new { contents = new[] { new { data = Convert.ToBase64String(redacted) } } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The innermost exception, which is where a <c>DelegatingHandler</c>'s throw ends up.</summary>
    private static Exception Unwrap(Exception exception)
    {
        var current = exception;

        while (current.InnerException is { } inner)
        {
            current = inner;
        }

        return current;
    }

    private sealed class AcceptEverything : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
