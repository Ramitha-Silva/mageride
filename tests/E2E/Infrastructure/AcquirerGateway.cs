using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MageRide.Shared.Payments;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MageRide.E2E.Infrastructure;

/// <summary>A session the platform asked the acquirer to open (D6' §7.1's create-session call).</summary>
internal sealed record AcquirerSession(
    string OrderId, long AmountMinor, string Currency, string? Reference, DateTimeOffset At);

/// <summary>
/// OnePay, as far as wallet-svc can tell — a real socket speaking D6' §7.1's REST shape, and the
/// party that calls the webhook back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a stub of a MageRide component</b>, which is what the suite's fence is actually about. A
/// scenario must drive the platform through the surface an app, a device or a peer service has; an
/// acquirer sits on the far side of the platform's own egress, and there is no version of this suite
/// that opens a card session at a Sri Lankan payment gateway. The same allowance C121 makes for
/// <c>TrackerDevice</c> and C122 for <see cref="SmsGateway"/>: it must speak the real wire format and
/// be named as what it stands for.
/// </para>
/// <para>
/// <b>It is here because AL-05/AL-57 leave the platform exactly two ways for real money to arrive</b>
/// — an OnePay card session and a LankaQR bank-app hand-off — and both of them credit a wallet on a
/// <em>callback</em> rather than on the initiate. That asymmetry is the single most valuable thing on
/// this surface to test end to end and it cannot be reached at all without something to be the payer's
/// bank: <c>Onepay:BaseUrl</c> unset makes the card rail answer <c>503</c> before a session exists,
/// and a webhook nobody signs is refused by design ("there is no unsigned mode — a wallet-credit
/// endpoint that trusts an unsigned body is a free-money endpoint").
/// </para>
/// <para>
/// <b>Every callback it sends is signed the way a provider signs one</b>: HMAC-SHA256 over the raw
/// bytes, in <c>X-Signature</c>, using the deployment's own secret. The signature is computed over
/// the exact bytes that go on the wire — <see cref="ConfirmAsync"/> serialises once and posts the
/// same array it signed, because a body re-serialised between the two would be signed correctly and
/// verified against something else, which is the bug <c>WebhookSignature</c>'s own remarks describe.
/// </para>
/// <para>
/// <b>What it deliberately does not do is decide anything.</b> It records the session the platform
/// opened and answers the documented success shape; whether a callback is a first delivery, a
/// redelivery under the same <c>providerTransactionId</c>, a different transaction for one session,
/// or an amount that disagrees with what was opened, is the scenario's to choose — those four are
/// distinct R-19 behaviours and a gateway with opinions about them would be testing itself.
/// </para>
/// </remarks>
internal sealed class AcquirerGateway : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<AcquirerSession> _sessions;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };

    private AcquirerGateway(WebApplication app, ConcurrentQueue<AcquirerSession> sessions)
    {
        _app = app;
        _sessions = sessions;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        // wallet-svc appends `sessions` to this — `OnepayGateway.StartAsync` posts to the relative
        // path "sessions", and the client's base address is the configured URL with a trailing slash.
        BaseAddress = address.TrimEnd('/') + "/onepay/";
    }

    /// <summary>What <c>Onepay:BaseUrl</c> is pointed at.</summary>
    public string BaseAddress { get; }

    /// <summary>Every session the platform has opened here, oldest first.</summary>
    public IReadOnlyList<AcquirerSession> Sessions => [.. _sessions];

    public static async Task<AcquirerGateway> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var sessions = new ConcurrentQueue<AcquirerSession>();

        // D6' §7.1's create-session: `{orderId, amountMinor, currency, returnUrl, reference}` in,
        // `{redirectUrl|sessionToken}` out. Both are returned, because the contract admits either and
        // a gateway that returned only one would let a client that read the wrong field pass.
        app.MapPost("/onepay/sessions", async (HttpContext context) =>
        {
            using var document = await JsonDocument.ParseAsync(
                context.Request.Body, cancellationToken: context.RequestAborted);

            var body = document.RootElement;
            var orderId = Text(body, "orderId") ?? string.Empty;

            sessions.Enqueue(new AcquirerSession(
                orderId,
                body.TryGetProperty("amountMinor", out var amount) ? amount.GetInt64() : 0,
                Text(body, "currency") ?? "LKR",
                Text(body, "reference"),
                DateTimeOffset.UtcNow));

            return Results.Ok(new
            {
                redirectUrl = $"https://onepay.test/pay/{orderId}",
                sessionToken = $"ops-{orderId}",
            });
        });

        await app.StartAsync();

        return new AcquirerGateway(app, sessions);
    }

    /// <summary>The session opened for one order, once the platform has opened it.</summary>
    public AcquirerSession SessionFor(string orderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var session = _sessions.FirstOrDefault(candidate => candidate.OrderId == orderId);

        Assert.True(
            session is not null,
            $"The platform never opened an acquirer session for order '{orderId}'. Opened so far: "
            + (_sessions.IsEmpty ? "none at all." : string.Join(", ", _sessions.Select(s => s.OrderId))));

        return session!;
    }

    /// <summary>
    /// The acquirer calls a webhook, signed the way a provider signs one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The body is serialised once and the signature is taken over those exact bytes.</b>
    /// <c>_shared.yaml</c> requires the digest to be verified "before any body parsing" precisely
    /// because a round trip through a serialiser changes whitespace and key order — so a helper that
    /// signed an object and then let <c>HttpClient</c> serialise it again would be signing a
    /// different payload than it sent, and every callback would be refused for a reason that has
    /// nothing to do with the test.
    /// </para>
    /// <para>
    /// The response is returned raw. A redelivery is answered <c>200</c> with the same body as the
    /// first delivery — "that is what stops a provider retrying for ever" — so a scenario that wants
    /// to prove a replay credited nothing has to look at the ledger, not at the status code, and
    /// this signature makes that the obvious thing to do.
    /// </para>
    /// </remarks>
    public async Task<HttpResponseMessage> ConfirmAsync(string callbackUrl, string secret, object body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(body);

        var raw = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, callbackUrl)
        {
            Content = new ByteArrayContent(raw),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(WebhookSignature.HeaderName, WebhookSignature.Compute(raw, secret));

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The same call with a signature the deployment's secret does not produce.
    /// </summary>
    /// <remarks>
    /// The negative half of the rail, and the one worth having: a callback endpoint is reachable by
    /// anyone who finds the URL, so "an unsigned or wrongly-signed body credits nothing" is the
    /// property that stops it being a free-money endpoint. Sent with a real, well-formed HMAC under
    /// the wrong key rather than with a malformed header, because a garbled header would be refused
    /// by the parser before the comparison this is about.
    /// </remarks>
    public Task<HttpResponseMessage> ConfirmWithWrongSecretAsync(string callbackUrl, object body) =>
        ConfirmAsync(callbackUrl, "not-the-secret-this-deployment-configured", body);

    /// <summary>The same call with no <c>X-Signature</c> header at all.</summary>
    public async Task<HttpResponseMessage> ConfirmUnsignedAsync(string callbackUrl, object body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackUrl);
        ArgumentNullException.ThrowIfNull(body);

        using var request = new HttpRequestMessage(HttpMethod.Post, callbackUrl)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions)),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();

        await _app.StopAsync(TimeSpan.FromSeconds(5));
        await _app.DisposeAsync();
    }

    /// <summary>
    /// camelCase, because that is what the platform's own callback DTOs bind.
    /// </summary>
    /// <remarks>
    /// <c>TopupCallbackBody</c> and subscription-svc's equivalent are deserialised with
    /// <c>MageRideJson.Options</c>; a provider sending PascalCase would bind every field to null and
    /// the callback would fail validation rather than settle.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
