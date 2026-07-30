using System.Text.RegularExpressions;

namespace MageRide.Content.Domain;

/// <summary>
/// The shape of a notification-template key.
/// </summary>
/// <remarks>
/// <para>
/// <c>ride_offer</c>, <c>package_on_the_way</c>, <c>proxy_ride_link</c>, <c>pickup_confirm_link</c> —
/// lower-case snake_case, which is what server_db_schema.md §20 and D6' I-29.2 seed and what
/// <c>backend/contracts/content.yaml</c> declares as the path parameter's pattern.
/// <c>content.notification_templates.template_key</c> is a bare <c>TEXT</c> with no CHECK, so this is
/// the only thing enforcing it — worth having, because a key with a slash or a space in it is a key
/// notification-svc cannot put in a URL and the failure would surface there.
/// </para>
/// <para>
/// The same pattern is applied as a route constraint, so a malformed key does not reach a handler at
/// all, and again in the publisher, so a caller that bypassed the gateway cannot create one.
/// </para>
/// </remarks>
internal static partial class TemplateKeys
{
    /// <summary>The contract's pattern, for a route constraint (<c>:regex(...)</c>).</summary>
    public const string RoutePattern = @"^[a-z0-9]+(_[a-z0-9]+)*$";

    public static bool IsValid(string? key) => key is not null && Pattern().IsMatch(key);

    [GeneratedRegex(RoutePattern, RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
