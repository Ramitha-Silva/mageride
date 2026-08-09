using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MageRide.E2E.Infrastructure;

/// <summary>One message a handset was actually sent.</summary>
/// <param name="Phone">National digits, as Notify.lk's <c>to</c> carries them (no leading <c>+</c>).</param>
internal sealed record SentSms(string Phone, string Body, DateTimeOffset At);

/// <summary>
/// Notify.lk, as far as notification-svc can tell — a real socket speaking D6' §7.3's REST shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not a stub of a MageRide component, and the distinction is the whole reason C122 is
/// allowed one.</b> The suite's rule is that a scenario drives the platform through the surface an
/// app, a device or a peer service has; it says nothing about the third parties on the other side of
/// the platform's own egress, because there is no version of this suite that dials a real Sri Lankan
/// SMS gateway. The same choice C121 made for a tracker: <c>TrackerDevice</c> writes the bytes
/// firmware would write, and this answers the way Notify.lk answers.
/// </para>
/// <para>
/// <b>What it buys is the only honest way to reach SCR-WT-002 and SCR-WT-003.</b> AL-44/AL-45 make a
/// share token mint-and-SMS — notification-svc's <c>MintedLink</c> has no <c>Token</c> member, there
/// is no response shape in that assembly that could carry one out, and the fence is asserted in its
/// own suite. So a scenario that read the token out of <c>safety.trip_share_tokens</c> would be
/// reaching past the credential's only delivery path and asserting about a page nobody could have
/// opened. Here the token arrives the way the recipient's does: in a message, addressed to their
/// number, containing a link the platform composed from a template it fetched from content-svc.
/// </para>
/// <para>
/// Notify.lk's real failure shape is <b>HTTP 200 with <c>{"status":"error"}</c></b>, which is what
/// <c>NotifyLkSmsGateway.IsAccepted</c> exists for; this answers the success shape, so a scenario
/// that never receives a message is looking at a platform that never sent one rather than at a
/// gateway that refused.
/// </para>
/// </remarks>
internal sealed class SmsGateway : IAsyncDisposable
{
    /// <summary>The <c>{{link}}</c> in an AL-44 template body, wherever the template puts it.</summary>
    /// <remarks>
    /// Deliberately anchored on <see cref="ProxyPackageFleet.WebTrackBaseUrl"/> rather than on "the
    /// last word of the message": D6' I-29.2's bodies differ per key and per language, and a scenario
    /// that pulled the trailing token out of a Sinhala string would break on a template edit rather
    /// than on a regression.
    /// </remarks>
    private static readonly Regex TrackLink = new(
        @"https?://[^\s]*?/track\?token=(?<token>[A-Za-z0-9_\-%]+)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<SentSms> _sent;

    private SmsGateway(WebApplication app, ConcurrentQueue<SentSms> sent)
    {
        _app = app;
        _sent = sent;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        // notification-svc appends `send` to this, the way Notify.lk's own base URL ends.
        BaseAddress = address.TrimEnd('/') + "/api/v1/";
    }

    /// <summary>What <c>Sms:NotifyLkBaseUrl</c> is pointed at.</summary>
    public string BaseAddress { get; }

    public static async Task<SmsGateway> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var sent = new ConcurrentQueue<SentSms>();

        app.MapPost("/api/v1/send", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();

            sent.Enqueue(new SentSms(
                form["to"].ToString(), form["message"].ToString(), DateTimeOffset.UtcNow));

            return Results.Ok(new { status = "success", data = new { message_id = sent.Count } });
        });

        await app.StartAsync();

        return new SmsGateway(app, sent);
    }

    /// <summary>
    /// Waits for a message addressed to <paramref name="phone"/> and returns it.
    /// </summary>
    /// <remarks>
    /// A wait rather than a read, because everything between the act and the message is real: the
    /// ride-svc transaction commits, its LISTEN/NOTIFY dispatcher publishes to Redpanda,
    /// notification-svc's consumer picks the event up, mints a token, renders a body against
    /// content-svc over HTTP, and enqueues a row that a delivery worker sweeps on its own cadence.
    /// </remarks>
    public async Task<SentSms> AwaitSmsAsync(string phone, TimeSpan? within = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);

        var wanted = phone.TrimStart('+');
        var timeout = within ?? TimeSpan.FromSeconds(60);
        var deadline = DateTimeOffset.UtcNow + timeout;

        do
        {
            if (_sent.FirstOrDefault(sms => sms.Phone == wanted) is { } message)
            {
                return message;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail(
            $"Nothing was SMSed to {wanted} within {timeout.TotalSeconds:F0}s. The gateway was handed: "
            + (_sent.IsEmpty ? "nothing at all." : string.Join("; ", _sent.Select(sms => sms.Phone))));

        return null!;
    }

    /// <summary>
    /// True when nothing has been sent to <paramref name="phone"/> after a fair chance to be.
    /// </summary>
    /// <remarks>
    /// The delay is not politeness either: "no SMS was sent" asserted immediately is indistinguishable
    /// from "the consumer has not run yet", and the assertion that matters — an unregistered recipient
    /// with an app is pushed to rather than SMSed — is exactly of that shape.
    /// </remarks>
    public async Task<bool> NothingSentToAsync(string phone, TimeSpan settle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);

        await Task.Delay(settle, TestContext.Current.CancellationToken);

        return !_sent.Any(sms => sms.Phone == phone.TrimStart('+'));
    }

    /// <summary>
    /// The share token out of a message body — the one way a scenario is allowed to learn one.
    /// </summary>
    public static string TokenIn(SentSms message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var match = TrackLink.Match(message.Body);

        Assert.True(
            match.Success,
            $"The SMS to {message.Phone} carries no {{link}} a browser could open: '{message.Body}'. "
            + "Without it the recipient has no way onto SCR-WT-002/003 at all (AL-44).");

        return Uri.UnescapeDataString(match.Groups["token"].Value);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TimeSpan.FromSeconds(5));
        await _app.DisposeAsync();
    }
}
