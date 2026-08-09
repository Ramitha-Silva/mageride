using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MageRide.Shared.Http;

namespace MageRide.E2E.Infrastructure;

/// <summary>What one of the six SCR-WT pages answered, before anything is asserted about it.</summary>
/// <param name="Body">
/// The raw JSON text, kept beside the parsed element on purpose. The half of AL-44 that matters is
/// what is <em>absent</em> — a plate, a coordinate, a booker's number — and a test that only looked
/// at deserialised members would say nothing about a field the shape has no property for.
/// </param>
internal sealed record WebPage(HttpStatusCode Status, string Body)
{
    public JsonElement Json
    {
        get
        {
            using var document = JsonDocument.Parse(Body);
            return document.RootElement.Clone();
        }
    }

    /// <summary>The RFC 7807 <c>type</c> slug, for the refusals.</summary>
    public string ProblemCode
    {
        get
        {
            var type = Json.TryGetProperty("type", out var value) ? value.GetString() ?? string.Empty : string.Empty;

            return type[(type.LastIndexOf('/') + 1)..];
        }
    }

    /// <summary>True when <paramref name="text"/> appears anywhere in the response at all.</summary>
    /// <remarks>
    /// Case-insensitive and over the whole body rather than over a named member: the assertions this
    /// serves are of the form "the driver's plate is not on this page" and "the booker's number is
    /// nowhere in it", and naming the member would be assuming the leak's shape.
    /// </remarks>
    public bool Mentions(string text) =>
        !string.IsNullOrEmpty(text) && Body.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A browser at <c>passenger.mageride.lk</c>, holding nothing but a token out of an SMS.
/// </summary>
/// <remarks>
/// <para>
/// <b>No bearer, no cookie, no session, and no way to acquire one.</b> public-bff registers no
/// authentication scheme at all — that is its first fence — so this client sends exactly what a
/// browser opening an SMS link sends: a GET or a POST at <c>/public/track/{token}</c>, with the
/// token in the path as the whole credential (AL-44, P-09).
/// </para>
/// <para>
/// <b>It sends no <c>Idempotency-Key</c> either</b>, and that is deliberate rather than lazy. D3'
/// §0 makes the header mandatory on a POST and <c>public-bff.yaml</c> repeats it, but the page is a
/// browser form that does not mint one — so public-bff derives the key from the business fact
/// instead (<c>pickup:{verb}:{token}</c>, <c>sos:{window}:{token}</c>). A client here that helpfully
/// supplied a header would be testing the branch a real visitor never takes.
/// </para>
/// <para>
/// <c>X-Forwarded-For</c> is set because <c>PublicBff:TrustForwardedFor</c> is on by default and
/// every request in production arrives through the C008 gateway: without it every scenario in the
/// suite shares one per-IP bucket, which is the loopback address.
/// </para>
/// </remarks>
internal sealed class WebSubview(string baseAddress) : IDisposable
{
    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri(baseAddress),
        Timeout = TimeSpan.FromSeconds(30),
    };

    private int _visitors;

    /// <summary>SCR-WT-001 → 002 / 003 / 004: the snapshot the token's scope decides.</summary>
    public Task<WebPage> OpenAsync(string token) => SendAsync(HttpMethod.Get, $"/public/track/{token}", null);

    /// <summary>The poll fallback of the live feed (<c>?since=</c>), which is one evaluation of the diff.</summary>
    public Task<WebPage> PollAsync(string token, string? since = "") =>
        SendAsync(HttpMethod.Get, $"/public/track/{token}/live?since={Uri.EscapeDataString(since ?? string.Empty)}", null);

    /// <summary>SCR-WT-003's Share (AL-45).</summary>
    public Task<WebPage> ConfirmPickupAsync(string token, double lat, double lng, double? accuracy = 15) =>
        SendAsync(
            HttpMethod.Post, $"/public/track/{token}/pickup/confirm", new { lat, lng, accuracy });

    /// <summary>
    /// SCR-WT-003's Decline.
    /// </summary>
    /// <param name="body">
    /// Normally none. A scenario passes one to prove P-02 the hard way: the handler takes no body
    /// parameter, the ride client sends no content and ride-svc's statement has no
    /// <c>resolved_geo</c> in its <c>SET</c> list, so a coordinate posted here has three components
    /// to survive and survives none of them.
    /// </param>
    public Task<WebPage> DeclinePickupAsync(string token, object? body = null) =>
        SendAsync(HttpMethod.Post, $"/public/track/{token}/pickup/decline", body);

    /// <summary>SCR-WT-004's panic button (US-25.5, D-33).</summary>
    public Task<WebPage> SosAsync(string token, double lat, double lng) =>
        SendAsync(HttpMethod.Post, $"/public/track/{token}/sos", new { lat, lng, accuracy = 20d });

    /// <summary>SCR-WT-005's Download receipt (US-25.6).</summary>
    public Task<WebPage> ReceiptAsync(string token) =>
        SendAsync(HttpMethod.Get, $"/public/track/{token}/receipt", null);

    private async Task<WebPage> SendAsync(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        // A fresh address per visitor. The per-IP bucket is 600/min and shared across the whole
        // family, so a suite that arrived from one address would eventually be rate-limited by
        // itself — which is a real limit doing its job on a load nobody real produces.
        request.Headers.Add("X-Forwarded-For", $"203.0.113.{Interlocked.Increment(ref _visitors) % 250}");

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        return new WebPage(
            response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    public void Dispose() => _client.Dispose();
}
