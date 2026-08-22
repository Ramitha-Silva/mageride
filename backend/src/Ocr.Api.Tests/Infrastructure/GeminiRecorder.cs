using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MageRide.Ocr.Domain;
using MageRide.Ocr.Gemini;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MageRide.Ocr.Tests.Infrastructure;

/// <summary>One request the model received, decomposed into the parts a test asserts on.</summary>
/// <param name="Image">The inline image, decoded. This is the evidence for the D-36 test.</param>
public sealed record RecordedCall(string Path, string? ApiKey, string Prompt, byte[] Image, string Body)
{
    /// <summary>Hex sha256 of the bytes that actually left the perimeter.</summary>
    public string ImageSha256 { get; } = Convert.ToHexStringLower(SHA256.HashData(Image));
}

/// <summary>
/// The network recorder the definition of done asks for: a real HTTP server on a real socket,
/// standing in for Gemini, keeping every request it was sent.
/// </summary>
/// <remarks>
/// <para>
/// <b>A server, not a message handler.</b> The claim under test is that nothing unredacted leaves
/// this process, and a stubbed <c>HttpMessageHandler</c> would assert it one layer above the wire —
/// on the far side of the perimeter guard, the resilience pipeline and the serialiser, which are
/// three of the places a defect could put the wrong bytes on it. This captures what came out of the
/// socket.
/// </para>
/// <para>
/// <b>It never answers from a fixture file.</b> Each test says what the model "read", so the
/// low-confidence and plate-mismatch cases are inputs rather than photographs the repository has to
/// carry and hope stay ambiguous.
/// </para>
/// </remarks>
internal sealed class GeminiRecorder : IAsyncDisposable
{
    private readonly WebApplication _app;

    private GeminiRecorder(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    /// <summary>Everything the model was sent, in order.</summary>
    public ConcurrentQueue<RecordedCall> Calls { get; } = new();

    public string BaseUrl { get; }

    /// <summary>What the next call answers. Null means the default clean read.</summary>
    public Func<RecordedCall, (HttpStatusCode Status, string Body)>? Responder { get; set; }

    /// <summary>Makes the model unavailable — D6' §8.3's "OCR down → Tesseract".</summary>
    public void Fail(HttpStatusCode status = HttpStatusCode.ServiceUnavailable) =>
        Responder = _ => (status, """{"error":{"message":"unavailable"}}""");

    /// <summary>Answers with these fields, whatever the document was.</summary>
    public void Answer(params (string Key, string? Value, decimal? Confidence)[] fields) =>
        Responder = call => (HttpStatusCode.OK, Envelope(fields, DocumentTypeFor(call.Prompt)));

    /// <summary>
    /// Answers with these fields while identifying the document as <paramref name="documentType"/>
    /// (Δ MCS-21).
    /// </summary>
    /// <remarks>
    /// The double answers for ANY model and used to answer for any document too — it echoed back
    /// whatever the prompt asked for, so it agreed with the caller's claim by construction. That
    /// made a type MISMATCH unreproducible: the one case the feature exists for was the one case
    /// no test could express.
    /// </remarks>
    public void AnswerAs(string documentType, params (string Key, string? Value, decimal? Confidence)[] fields) =>
        Responder = _ => (HttpStatusCode.OK, Envelope(fields, documentType));

    public static async Task<GeminiRecorder> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        GeminiRecorder? recorder = null;

        app.MapPost("/v1beta/models/{model}:generateContent", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);

            var body = await reader.ReadToEndAsync();
            var call = Decompose(context, body);

            recorder!.Calls.Enqueue(call);

            var (status, payload) = recorder.Responder?.Invoke(call) ?? (HttpStatusCode.OK, CleanRead(call));

            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsync(payload);
        });

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        recorder = new GeminiRecorder(app, address);

        return recorder;
    }

    private static RecordedCall Decompose(HttpContext context, string body)
    {
        using var document = JsonDocument.Parse(body);

        var parts = document.RootElement
            .GetProperty("contents")[0]
            .GetProperty("parts");

        var prompt = string.Empty;
        var image = Array.Empty<byte>();

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text))
            {
                prompt = text.GetString() ?? string.Empty;
            }

            if (part.TryGetProperty("inline_data", out var inline))
            {
                image = Convert.FromBase64String(inline.GetProperty("data").GetString()!);
            }
        }

        return new RecordedCall(
            context.Request.Path,
            context.Request.Headers[GeminiFieldExtractor.ApiKeyHeader].ToString(),
            prompt,
            image,
            body);
    }

    /// <summary>
    /// The default: every key the prompt asked for, read confidently, with plausible values.
    /// </summary>
    /// <remarks>
    /// Derived from the prompt rather than from the document, because the prompt is what names the
    /// keys — and a stub that agreed with the document by construction would not notice
    /// <see cref="GeminiPrompts"/> asking for a key nothing consumes.
    /// </remarks>
    private static string CleanRead(RecordedCall call)
    {
        var fields = new List<(string Key, string? Value, decimal? Confidence)>();

        foreach (var line in call.Prompt.Split('\n'))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf(" — ", StringComparison.Ordinal);

            if (separator <= 0 || !line.StartsWith("  ", StringComparison.Ordinal))
            {
                continue;
            }

            var key = trimmed[..separator];

            fields.Add((key, ValueFor(key), 0.96m));
        }

        return Envelope([.. fields], DocumentTypeFor(call.Prompt));
    }

    /// <summary>
    /// The document type a clean read reports: whatever the prompt says the caller expects
    /// (Δ MCS-21).
    /// </summary>
    /// <remarks>
    /// Read off the prompt rather than hard-coded, so the default stays "the model agrees" for
    /// every kind without this double carrying its own copy of the kind list. A mismatch is asked
    /// for explicitly through <see cref="AnswerAs"/>, which is the only way a test should get one.
    /// </remarks>
    private static string DocumentTypeFor(string prompt)
    {
        if (prompt.Contains("driving licence", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentTypes.DrivingLicence;
        }

        if (prompt.Contains("insurance certificate", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentTypes.Insurance;
        }

        if (prompt.Contains("revenue licence", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentTypes.RevenueLicence;
        }

        return prompt.Contains("photograph", StringComparison.OrdinalIgnoreCase)
            ? DocumentTypes.VehiclePhoto
            : DocumentTypes.Unclear;
    }

    private static string? ValueFor(string key) => key switch
    {
        "licence_no" => DocumentFixtures.LicenceNumber,
        "licence_expiry" => DocumentFixtures.LicenceExpiry,
        // Redacted out of the image before it got here, and the prompt says to answer null for
        // anything behind a black rectangle. The stub obeys its own instructions.
        "nic_no" => null,
        "allowed_vehicle_types" => "A1,B,C1",
        "insurance_expiry" => DocumentFixtures.InsuranceExpiry,
        "insurance_policy_no" => "4477112",
        "insurer" => "Ceylinco",
        "revenue_no" => DocumentFixtures.RevenueNumber,
        "revenue_expiry" => DocumentFixtures.RevenueExpiry,
        "plate_text" => DocumentFixtures.Plate,
        "reg_no_match" => null,
        "permit_no" => DocumentFixtures.PermitNumber,
        "permit_route" => "138",
        "permit_expiry" => "2027-12-31",
        _ => null,
    };

    private static string Envelope(
        (string Key, string? Value, decimal? Confidence)[] fields, string documentType)
    {
        var payload = new
        {
            document_type = documentType,
            fields = fields.Select(field => new { key = field.Key, value = field.Value, confidence = field.Confidence }),
        };

        var text = JsonSerializer.Serialize(payload);

        return JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new { content = new { parts = new[] { new { text } } }, finishReason = "STOP" },
            },
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
