using System.Globalization;
using System.Text.RegularExpressions;

namespace MageRide.Transit.Geo;

/// <summary>A coordinate read out of a shared link.</summary>
public sealed record ParsedLocation(double Lat, double Lng, string? Label);

/// <summary>
/// Pulls <c>{lat,lng}</c> out of a Google Maps URL (BR-23.4, AL-20).
/// </summary>
/// <remarks>
/// <para>
/// <b>Parsing only — nothing here fetches anything.</b> BR-23.4 lists five shapes and has the app
/// parse them client-side; this is the same parser on the server, because a short link resolves to
/// one of those shapes and the resolver needs to read it. <b>No Google API is called</b>, which is
/// D3''s map hard rule and D6' I-23.1's.
/// </para>
/// <para>
/// <b>Order matters and is the opposite of the obvious one.</b> A <c>/place/</c> URL carries
/// <em>two</em> coordinates: the <c>@</c> viewport centre and, buried in the <c>data=</c>
/// parameter, <c>!3d</c>/<c>!4d</c> — the pin itself. They differ, sometimes by hundreds of metres,
/// because the viewport is framed around the label rather than centred on it. So <c>!3d!4d</c> is
/// tried first and <c>@</c> is the fallback: taking the viewport when the pin is present drops the
/// passenger's marker down the street from the place they shared.
/// </para>
/// </remarks>
public static class MapsLinkParser
{
    private const RegexOptions Options =
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture;

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    /// <summary>The pin inside a <c>/place/</c> URL's <c>data=</c> blob. The most precise shape.</summary>
    private static readonly Regex DataPin =
        new(@"!3d(?<lat>-?\d+(?:\.\d+)?)!4d(?<lng>-?\d+(?:\.\d+)?)", Options, Budget);

    /// <summary>The viewport centre: <c>/@6.9271,79.8612,15z</c>.</summary>
    private static readonly Regex AtCentre =
        new(@"[@/](?<lat>-?\d{1,3}(?:\.\d+)?),(?<lng>-?\d{1,3}(?:\.\d+)?)(?:,[\d.]+[a-z])?", Options, Budget);

    /// <summary>A bare <c>lat,lng</c> pair, for <c>?q=</c> and <c>ll=</c>.</summary>
    private static readonly Regex Pair =
        new(@"^\s*(?<lat>-?\d{1,3}(?:\.\d+)?)\s*,\s*(?<lng>-?\d{1,3}(?:\.\d+)?)\s*$", Options, Budget);

    /// <summary>Query parameters that carry a coordinate, most specific first.</summary>
    private static readonly string[] CoordinateParameters = ["q", "query", "ll", "center", "daddr", "destination"];

    /// <summary>Reads a coordinate out of <paramref name="url"/>, or null if there is none in it.</summary>
    public static ParsedLocation? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var label = Label(uri);

        // 1. The pin in `data=`, when the URL carries one — see the remarks on ordering.
        if (Match(DataPin, uri.OriginalString) is { } pin)
        {
            return pin with { Label = label };
        }

        // 2. An explicit query parameter. `?q=lat,lng` is BR-23.4's first shape.
        foreach (var name in CoordinateParameters)
        {
            var value = QueryValue(uri, name);

            if (value is not null && Match(Pair, value) is { } fromQuery)
            {
                return fromQuery with { Label = label };
            }
        }

        // 3. The `@lat,lng,zoom` viewport, which every /maps and /place URL carries.
        if (Match(AtCentre, uri.AbsolutePath) is { } centre)
        {
            return centre with { Label = label };
        }

        return null;
    }

    /// <summary>
    /// The human name in a <c>/place/Galle+Face+Green/</c> path, for the sheet's pin preview.
    /// </summary>
    /// <remarks>
    /// A convenience, not an address: BR-23.4's Resolved state shows a <em>reverse-geocoded</em>
    /// address beside the pin, and that is query-svc's <c>/v1/geo/reverse</c>. What this gives the
    /// sheet is something to show while that is in flight.
    /// </remarks>
    private static string? Label(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var place = Array.IndexOf(segments, "place");

        if (place < 0 || place + 1 >= segments.Length)
        {
            return null;
        }

        var raw = Uri.UnescapeDataString(segments[place + 1]).Replace('+', ' ').Trim();

        // A `/place/6.9271,79.8612/` URL names a coordinate, not a place; showing "6.9271, 79.8612"
        // as a label under the pin tells the user nothing they cannot already see.
        return raw.Length == 0 || Pair.IsMatch(raw) ? null : raw;
    }

    private static string? QueryValue(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                continue;
            }

            if (pair.AsSpan(0, separator).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            }
        }

        return null;
    }

    private static ParsedLocation? Match(Regex pattern, string input)
    {
        var match = pattern.Match(input);

        if (!match.Success
            || !double.TryParse(match.Groups["lat"].Value, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(match.Groups["lng"].Value, CultureInfo.InvariantCulture, out var lng))
        {
            return null;
        }

        // A zoom level and a coordinate look alike to a regex. Anything outside the globe is not a
        // location, and dropping a pin at it would be worse than saying the link was unreadable.
        return lat is >= -90 and <= 90 && lng is >= -180 and <= 180 ? new ParsedLocation(lat, lng, null) : null;
    }
}
