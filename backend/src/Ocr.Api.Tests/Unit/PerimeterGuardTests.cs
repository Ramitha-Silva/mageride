using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MageRide.Ocr.Redaction;
using Microsoft.Extensions.Logging.Abstractions;

namespace MageRide.Ocr.Tests.Unit;

/// <summary>
/// The wire check: no image leaves except one the pipeline admitted for the job in hand.
/// </summary>
/// <remarks>
/// <para>
/// Δ MCS-07 — <b>what this guard proves has narrowed, and these tests are unchanged because they
/// never tested the wider claim.</b> The first fence used to be the type: <c>GeminiFieldExtractor</c>
/// took a <c>RedactedDocument</c>, which only the pre-pass could construct, so "admitted" and
/// "redacted" were the same fact. The extractor now takes an <c>OutboundDocument</c>, which may be
/// raw, so admission means only that <c>ExtractionPipeline</c> resolved these bytes for this
/// extraction.
/// </para>
/// <para>
/// That is still the boundary these cases are about, and it still holds: a payload assembled by
/// hand, a retry that re-serialises from a stale buffer, a second provider's field name, an
/// extractor written by somebody who has not read any of this. What it no longer does is answer
/// "was it masked?" — <c>docs.extractions.redaction_applied</c> is where that lives now.
/// </para>
/// </remarks>
public sealed class PerimeterGuardTests
{
    private static readonly byte[] Image = Enumerable.Range(0, 2048).Select(index => (byte)index).ToArray();

    [Fact]
    public async Task An_image_the_pre_pass_produced_is_allowed_through()
    {
        var ledger = new PerimeterLedger();
        ledger.Admit(Hash(Image));

        var (client, terminal) = Build(ledger);

        using var response = await client.PostAsync("http://example.test/v1beta", Payload(Image));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, terminal.Calls);
    }

    [Fact]
    public async Task An_image_the_pre_pass_never_produced_is_refused_before_it_leaves()
    {
        var (client, terminal) = Build(new PerimeterLedger());

        var violation = await Assert.ThrowsAsync<PerimeterViolationException>(
            () => client.PostAsync("http://example.test/v1beta", Payload(Image)));

        Assert.Contains(Hash(Image), violation.Message, StringComparison.Ordinal);

        // The point is not the exception — it is that nothing was sent.
        Assert.Equal(0, terminal.Calls);
    }

    [Fact]
    public async Task Raw_bytes_posted_outside_a_JSON_envelope_are_refused_too()
    {
        // Nothing in this service posts an image/* body to a third party. If something ever did, it
        // would be the raw file, and the JSON inspection above would not see it.
        var (client, terminal) = Build(new PerimeterLedger());

        using var content = new ByteArrayContent(Image);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

        await Assert.ThrowsAsync<PerimeterViolationException>(
            () => client.PostAsync("http://example.test/upload", content));

        Assert.Equal(0, terminal.Calls);
    }

    [Fact]
    public async Task A_request_with_no_image_on_it_is_not_the_guards_business()
    {
        var (client, terminal) = Build(new PerimeterLedger());

        using var content = new StringContent("""{"contents":[{"parts":[{"text":"hello"}]}]}""",
            Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("http://example.test/v1beta", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, terminal.Calls);
    }

    [Fact]
    public void The_ledger_keeps_a_document_admitted_across_a_burst()
    {
        // A window that evicted a document between its redaction and its own send would report a
        // violation on a perfectly compliant call — the one failure mode that would make an
        // operator switch the guard off.
        var ledger = new PerimeterLedger();
        var first = Hash(Image);

        ledger.Admit(first);

        for (var index = 0; index < 512; index++)
        {
            ledger.Admit(Hash(BitConverter.GetBytes(index)));
        }

        Assert.True(ledger.IsAdmitted(first));
    }

    private static (HttpClient Client, CountingHandler Terminal) Build(IPerimeterLedger ledger)
    {
        var terminal = new CountingHandler();

        var guard = new PerimeterGuardHandler(ledger, NullLogger<PerimeterGuardHandler>.Instance)
        {
            InnerHandler = terminal,
        };

        return (new HttpClient(guard), terminal);
    }

    /// <summary>A Gemini <c>generateContent</c> body, shaped exactly as the extractor builds one.</summary>
    private static HttpContent Payload(byte[] image)
    {
        var body = JsonSerializer.Serialize(new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = "read this" },
                        new { inline_data = new { mime_type = "image/png", data = Convert.ToBase64String(image) } },
                    },
                },
            },
        });

        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
