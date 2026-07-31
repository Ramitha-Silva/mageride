using System.Collections.Concurrent;
using System.Globalization;
using MageRide.Notification.Domain;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MageRide.Notification.Tests.Infrastructure;

/// <summary>One template, in all three languages, as content-svc would serve it.</summary>
internal sealed record StubTemplate(string? Title, string Body);

/// <summary>
/// content-svc, as far as notification-svc can tell — a real socket serving
/// <c>GET /v1/content/templates/{key}?lang=</c>.
/// </summary>
/// <remarks>
/// <b>A stub rather than the real service, and the reason is what is being asserted.</b> "Every
/// notification body is rendered in the recipient's language" is a claim about *this* service
/// choosing a language and substituting values — content-svc's own suite already proves it serves
/// three. What the stub adds that a fake <c>ITemplateSource</c> would not is the HTTP boundary: the
/// internal key header, the 404 for an unknown key, and the fact that a template arrives over a
/// network that can be slow.
/// </remarks>
internal sealed class ContentStub : IAsyncDisposable
{
    public const string InternalApiKey = "c051-content-internal-key-not-a-secret";

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, Dictionary<string, StubTemplate>> _templates;
    private readonly ConcurrentQueue<string> _requests;

    private ContentStub(
        WebApplication app,
        ConcurrentDictionary<string, Dictionary<string, StubTemplate>> templates,
        ConcurrentQueue<string> requests)
    {
        _app = app;
        _templates = templates;
        _requests = requests;

        BaseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public string BaseAddress { get; }

    /// <summary>Every <c>{key}|{lang}</c> that was asked for, in order.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];

    /// <summary>Replaces or adds a key. All three languages, or 1307's trigger would have refused it.</summary>
    public void Publish(string key, StubTemplate si, StubTemplate ta, StubTemplate en) =>
        _templates[key] = new Dictionary<string, StubTemplate>(StringComparer.Ordinal)
        {
            [Languages.Sinhala] = si,
            [Languages.Tamil] = ta,
            [Languages.English] = en,
        };

    public static async Task<ContentStub> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var templates = new ConcurrentDictionary<string, Dictionary<string, StubTemplate>>(StringComparer.Ordinal);
        var requests = new ConcurrentQueue<string>();

        Seed(templates);

        app.MapGet("/v1/content/templates/{key}", (string key, string? lang, HttpContext context) =>
        {
            var language = Languages.Normalise(lang);
            requests.Enqueue($"{key}|{language}");

            if (!templates.TryGetValue(key, out var byLanguage))
            {
                // content-svc's rule: an unknown key is a 404, not a new template.
                return Results.NotFound();
            }

            var template = byLanguage[language];

            return Results.Ok(new
            {
                key,
                language,
                version = 1,
                title = template.Title,
                body = template.Body,
                placeholders = Placeholders(template),
            });
        });

        await app.StartAsync();

        return new ContentStub(app, templates, requests);
    }

    /// <summary>
    /// The migration-1904 keys these tests exercise, in all three languages.
    /// </summary>
    /// <remarks>
    /// The bodies are deliberately distinguishable per language and per key: the language assertion
    /// is "the recipient got theirs", which only means something if the three differ.
    /// </remarks>
    private static void Seed(ConcurrentDictionary<string, Dictionary<string, StubTemplate>> templates)
    {
        Add(templates, "driver_assigned",
            new StubTemplate("රියදුරු පැමිණෙමින්", "රියදුරෙකු ඔබේ ගමන පිළිගෙන ඇත."),
            new StubTemplate("ஓட்டுநர் வருகிறார்", "ஒரு ஓட்டுநர் உங்கள் பயணத்தை ஏற்றுக்கொண்டார்."),
            new StubTemplate("Driver on the way", "A driver has accepted your ride."));

        Add(templates, "ride_offer_sms",
            new StubTemplate(null, "නව MageRide ඉල්ලීමක් — රු. {{fare}}, කි.මී. {{distance}}."),
            new StubTemplate(null, "புதிய MageRide கோரிக்கை — ரூ. {{fare}}, {{distance}} கி.மீ."),
            new StubTemplate(null, "New MageRide request — Rs {{fare}}, pickup {{distance}} km away."));

        Add(templates, "sos_alert",
            new StubTemplate(null, "MageRide හදිසි අවස්ථාව: {{name}}. ස්ථානය: {{link}}"),
            new StubTemplate(null, "MageRide அவசரநிலை: {{name}}. இருப்பிடம்: {{link}}"),
            new StubTemplate(null, "MageRide emergency: {{name}} has raised an SOS. Live location: {{link}}"));

        Add(templates, "package_on_the_way",
            new StubTemplate(null, "ඔබේ පාර්සලය මාර්ගයේ ය. මෙතැනින්: {{link}}"),
            new StubTemplate(null, "உங்கள் பொதி வழியில் உள்ளது: {{link}}"),
            new StubTemplate(null, "Your package is on the way. Track it here: {{link}}"));

        Add(templates, "driver_arrived",
            new StubTemplate("රියදුරු පැමිණ ඇත", "ඔබේ රියදුරු ආරම්භක ස්ථානයේ රැඳී සිටී."),
            new StubTemplate("ஓட்டுநர் வந்துவிட்டார்", "உங்கள் ஓட்டுநர் காத்திருக்கிறார்."),
            new StubTemplate("Driver has arrived", "Your driver is waiting at the pickup point."));

        Add(templates, "package_delivered",
            new StubTemplate("පාර්සලය භාර දී ඇත", "ඔබේ පාර්සලය භාර දී ඇත."),
            new StubTemplate("பொதி வழங்கப்பட்டது", "உங்கள் பொதி வழங்கப்பட்டுவிட்டது."),
            new StubTemplate("Package delivered", "Your package has been delivered."));

        Add(templates, "proxy_ride_link",
            new StubTemplate(null, "ඔබ වෙනුවෙන් ගමනක් වෙන්කර ඇත: {{link}}"),
            new StubTemplate(null, "உங்களுக்காக ஒரு பயணம் முன்பதிவு செய்யப்பட்டுள்ளது: {{link}}"),
            new StubTemplate(null, "A ride has been booked for you. Follow it here: {{link}}"));

        Add(templates, "package_picked_up",
            new StubTemplate("📦 පාර්සලය මාර්ගයේ ය", "ඔබේ පාර්සලය රැගෙන ගොස් ඇත."),
            new StubTemplate("📦 பொதி வழியில்", "உங்கள் பொதி எடுக்கப்பட்டது."),
            new StubTemplate("📦 Package on the way", "Your package has been picked up and is on the way."));

        Add(templates, "pickup_confirm_link",
            new StubTemplate(null, "ඔබේ ගමන් ආරම්භක ස්ථානය තහවුරු කරන්න: {{link}}"),
            new StubTemplate(null, "உங்கள் புறப்படும் இடத்தை உறுதிப்படுத்தவும்: {{link}}"),
            new StubTemplate(null, "Confirm your pickup location: {{link}}"));

        Add(templates, "low_balance",
            new StubTemplate("ශේෂය අඩුයි", "ඔබේ ශේෂය රු. {{balance}}ය."),
            new StubTemplate("இருப்பு குறைவு", "உங்கள் இருப்பு ரூ. {{balance}}."),
            new StubTemplate("Low wallet balance", "Your wallet balance is Rs {{balance}}."));

        Add(templates, "ride_cancelled",
            new StubTemplate("ගමන අවලංගුයි", "මෙම ගමන අවලංගු කර ඇත."),
            new StubTemplate("பயணம் ரத்து", "இந்தப் பயணம் ரத்து செய்யப்பட்டது."),
            new StubTemplate("Ride cancelled", "This ride has been cancelled."));
    }

    private static void Add(
        ConcurrentDictionary<string, Dictionary<string, StubTemplate>> templates,
        string key,
        StubTemplate si,
        StubTemplate ta,
        StubTemplate en) =>
        templates[key] = new Dictionary<string, StubTemplate>(StringComparer.Ordinal)
        {
            [Languages.Sinhala] = si,
            [Languages.Tamil] = ta,
            [Languages.English] = en,
        };

    private static IReadOnlyList<string> Placeholders(StubTemplate template) =>
        MageRide.Notification.Templates.TemplateRenderer.PlaceholdersOf(template.Body);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(5));
            await _app.DisposeAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"warning: could not stop the content stub: {exception.Message}");
        }
    }
}

/// <summary>One SMS a gateway was asked to send.</summary>
internal sealed record SentSms(string Gateway, string To, string Message, DateTimeOffset At);

/// <summary>
/// An SMS gateway on a real socket.
/// </summary>
/// <remarks>
/// <b>Two of these run at once, and that is the point of D-33.</b> "SOS reaches both gateways in
/// parallel, p99 ≤ 5 s" is a claim about two sockets being written to concurrently and the first
/// answer winning — a fake <c>ISmsGateway</c> would prove only that the code calls what it calls.
/// The <see cref="Delay"/> knob is what makes the race observable: set the primary slow and the
/// secondary fast, and the parallel send must still land inside the budget.
/// </remarks>
internal sealed class SmsGatewayStub : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<SentSms> _sent;

    private SmsGatewayStub(WebApplication app, ConcurrentQueue<SentSms> sent, string name)
    {
        _app = app;
        _sent = sent;
        Name = name;

        BaseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public string Name { get; }

    public string BaseAddress { get; }

    /// <summary>Everything this gateway was asked to deliver, in order.</summary>
    public IReadOnlyList<SentSms> Sent => [.. _sent];

    /// <summary>Artificial latency before answering. Models a gateway having a bad minute.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>When set, the gateway refuses everything — Notify.lk's 200-with-an-error shape.</summary>
    public bool Refuse { get; set; }

    /// <summary>Notify.lk's REST shape: form POST to <c>/send</c>, 200 with a status member.</summary>
    public static Task<SmsGatewayStub> StartPrimaryAsync() => StartAsync("primary", notifyLk: true);

    /// <summary>The generic JSON POST the secondary gateway takes (D6' §7.3, shape unspecified).</summary>
    public static Task<SmsGatewayStub> StartSecondaryAsync() => StartAsync("secondary", notifyLk: false);

    private static async Task<SmsGatewayStub> StartAsync(string name, bool notifyLk)
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
        SmsGatewayStub? stub = null;

        if (notifyLk)
        {
            app.MapPost("/api/v1/send", async (HttpContext context) =>
            {
                if (stub!.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(stub.Delay);
                }

                var form = await context.Request.ReadFormAsync();

                if (stub.Refuse)
                {
                    // Their real failure shape: HTTP 200 with an error body, which is exactly the
                    // trap NotifyLkSmsGateway.IsAccepted exists for.
                    return Results.Ok(new { status = "error", message = "insufficient balance" });
                }

                sent.Enqueue(new SentSms(
                    name, form["to"].ToString(), form["message"].ToString(), DateTimeOffset.UtcNow));

                return Results.Ok(new { status = "success", data = new { message_id = sent.Count } });
            });
        }
        else
        {
            app.MapPost("/", async (SecondaryBody body) =>
            {
                if (stub!.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(stub.Delay);
                }

                if (stub.Refuse)
                {
                    return Results.StatusCode(502);
                }

                sent.Enqueue(new SentSms(name, body.To ?? string.Empty, body.Message ?? string.Empty, DateTimeOffset.UtcNow));

                return Results.Ok(new { accepted = true });
            });
        }

        await app.StartAsync();

        stub = new SmsGatewayStub(app, sent, name);
        return stub;
    }

    /// <summary>How long the slowest send took, for the D-33 percentile.</summary>
    public static double Percentile(IReadOnlyList<double> samples, double percentile)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var ordered = samples.OrderBy(static value => value).ToArray();
        var rank = (int)Math.Ceiling(percentile / 100 * ordered.Length) - 1;

        return ordered[Math.Clamp(rank, 0, ordered.Length - 1)];
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Name} @ {BaseAddress}");

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(5));
            await _app.DisposeAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"warning: could not stop the {Name} SMS stub: {exception.Message}");
        }
    }

    private sealed record SecondaryBody(string? To, string? From, string? Message);
}
