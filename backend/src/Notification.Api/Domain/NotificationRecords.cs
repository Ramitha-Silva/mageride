using System.Globalization;
using System.Text.Json;
using MageRide.Shared.Http;

namespace MageRide.Notification.Domain;

/// <summary>The three languages D-26 requires, and how a stored value is normalised into one.</summary>
public static class Languages
{
    public const string Sinhala = "si";
    public const string Tamil = "ta";
    public const string English = "en";

    /// <summary>
    /// <c>si-LK</c>, <c>ta_IN</c> and <c>SI</c> all resolve; anything else is English.
    /// </summary>
    /// <remarks>
    /// The same normalisation content-svc applies to <c>?lang=</c>, and it has to be the same: a
    /// device locale that resolved to Sinhala when the app asked for a template and to English when
    /// this service asked for one would render half a screen in each.
    /// </remarks>
    public static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return English;
        }

        var head = value.AsSpan();
        var separator = head.IndexOfAny('-', '_');
        if (separator > 0)
        {
            head = head[..separator];
        }

        return head switch
        {
            _ when head.Equals(Sinhala, StringComparison.OrdinalIgnoreCase) => Sinhala,
            _ when head.Equals(Tamil, StringComparison.OrdinalIgnoreCase) => Tamil,
            _ => English,
        };
    }
}

/// <summary>Who a notification is for, once resolved.</summary>
/// <param name="UserId">
/// <see langword="null"/> for the two recipients with no account: AL-21's unregistered package
/// recipient and AL-45's unregistered proxy rider.
/// </param>
/// <param name="Phone">E.164, or <see langword="null"/> when the recipient is push-only.</param>
/// <param name="Language">Resolved at enqueue, never at send — see migration 1308.</param>
/// <param name="Preferences">
/// <c>iam.users.notif_prefs</c> (US-10.7). Empty for an unregistered recipient, which is why a type
/// they can receive is one <see cref="NotificationTypeSpec.Mutable"/> does not apply to.
/// </param>
public sealed record NotificationRecipient(
    Guid? UserId,
    string? Phone,
    string Language,
    IReadOnlyDictionary<string, bool> Preferences)
{
    public static readonly IReadOnlyDictionary<string, bool> NoPreferences =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>An addressee with no account — a number and nothing else.</summary>
    public static NotificationRecipient Anonymous(string phone, string? language = null) =>
        new(null, phone, Languages.Normalise(language), NoPreferences);

    /// <summary>US-10.7, with the safety-critical types exempt.</summary>
    public bool Accepts(NotificationTypeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (!spec.Mutable || NotificationCatalogue.SafetyCritical.Contains(spec.Type))
        {
            return true;
        }

        // Absent means on. A user who has never opened the settings screen receives everything,
        // which is what a default of `{}` on the column means.
        return !Preferences.TryGetValue(spec.Type, out var enabled) || enabled;
    }
}

/// <summary>One row of <c>comms.notifications</c>, as the workers read it back.</summary>
public sealed record NotificationRow(
    Guid Id,
    string DedupeKey,
    string NotificationType,
    string? TemplateKey,
    string Channel,
    Guid? RecipientUserId,
    string? RecipientPhone,
    string Language,
    string Priority,
    string Payload,
    string Status,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? AckDeadlineAt,
    DateTimeOffset? AckedAt,
    Guid? FallbackOf,
    DateTimeOffset CreatedAt)
{
    /// <summary>The substitution values and client data payload, as a dictionary.</summary>
    public IReadOnlyDictionary<string, string> Values() => PayloadValues.Parse(Payload);
}

/// <summary>
/// The <c>payload</c> column, which is a flat JSON object of strings by the time it is stored.
/// </summary>
/// <remarks>
/// Flat and stringly typed on purpose. It serves two consumers that cannot agree on a richer shape:
/// the <c>{{placeholder}}</c> renderer, which substitutes text, and the FCM <c>data</c> map, whose
/// values <b>must</b> be strings (FCM rejects a nested object outright). Normalising once at the
/// boundary is what stops a number reaching the wire as <c>5.0</c> in one language's message and
/// <c>5</c> in another's.
/// </remarks>
public static class PayloadValues
{
    public static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Flattens a JSON object into strings. Nested values are serialised, not dropped.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = Stringify(property.Value);
            }

            return values;
        }
        catch (JsonException)
        {
            // A payload this service cannot read is a message it cannot render, and the renderer
            // says so with a missing-placeholder failure rather than this method throwing inside a
            // worker loop.
            return Empty;
        }
    }

    public static string Write(IReadOnlyDictionary<string, string>? values) =>
        JsonSerializer.Serialize(values ?? Empty, MageRideJson.StorageOptions);

    /// <summary>Money in minor units as the rupee string a template interpolates (§0).</summary>
    /// <remarks>
    /// Integer arithmetic throughout — the platform stores cents and this is the one place they
    /// become a decimal, at the boundary where a human reads them.
    /// </remarks>
    public static string Rupees(long minor)
    {
        var negative = minor < 0;
        var absolute = negative ? -minor : minor;

        var text = string.Create(
            CultureInfo.InvariantCulture, $"{absolute / 100:N0}.{absolute % 100:D2}");

        return negative ? "-" + text : text;
    }

    private static string Stringify(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };
}

/// <summary>
/// The claim that turns at-least-once event delivery into one message
/// (<c>ux_notifications_dedupe</c>, migration 1308).
/// </summary>
/// <remarks>
/// <para>
/// Shaped <c>{source}:{subject}:{type}</c>. The subject is the <b>fact</b>, not the delivery —
/// <c>offer.created</c> for offer <c>7</c> is one notification however many times Redpanda hands it
/// over — and the type is on the end because one event routinely produces two: <c>ride.accepted</c>
/// tells the passenger and the driver, and a key without the type would let the second one look
/// like a redelivery of the first.
/// </para>
/// <para>
/// The recipient is folded in for the events that fan out to a set of people (a broadcast, a ride
/// with a booker and a rider), which is what <see cref="For(string,string,string,Guid?)"/> does.
/// </para>
/// </remarks>
public static class NotificationDedupe
{
    /// <summary>An event-driven notification: the producing topic, the aggregate, the type.</summary>
    public static string For(string source, string subject, string type, Guid? recipient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        return recipient is { } who
            ? $"{source}:{subject}:{type}:{who}"
            : $"{source}:{subject}:{type}";
    }

    /// <summary>
    /// The E-01 fallback's key, derived from the push it replaces so the pair cannot both exist
    /// twice and a redelivered sweep collides on the row it already wrote.
    /// </summary>
    public static string Fallback(Guid pushNotificationId) => $"fallback:{pushNotificationId}";
}
