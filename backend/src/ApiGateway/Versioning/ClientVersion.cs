using System.Diagnostics.CodeAnalysis;
using MageRide.Shared.Http;

namespace MageRide.ApiGateway.Versioning;

/// <summary>
/// A client build as the contracts spell it: <c>major.minor.patch</c> with an optional
/// <c>-prerelease</c> and/or <c>+build</c> suffix
/// (<c>version-check.yaml</c> <c>^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$</c>).
/// </summary>
/// <remarks>
/// <see cref="AppVersion"/> in the shared kernel reads <c>major.minor.patch[+build]</c>. Play's
/// internal-testing and TestFlight builds also carry a <c>-rc.1</c> style pre-release tag, and
/// semver orders a pre-release <em>below</em> the release it precedes — so <c>1.6.0-rc.1</c> must
/// not satisfy a floor of <c>1.6.0</c>. That one rule is the whole reason this type exists; the
/// numeric comparison is still <see cref="AppVersion"/>'s, so the gateway and every service order
/// versions identically.
/// </remarks>
internal readonly record struct ClientVersion(AppVersion Release, bool IsPreRelease) : IComparable<ClientVersion>
{
    public static bool TryParse(string? value, [NotNullWhen(true)] out ClientVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.AsSpan().Trim();

        // Build metadata first: a '-' only starts a pre-release tag when it precedes any '+'.
        var plus = text.IndexOf('+');
        var build = plus < 0 ? default : text[(plus + 1)..];
        var head = plus < 0 ? text : text[..plus];

        var dash = head.IndexOf('-');
        var isPreRelease = dash >= 0;
        var core = isPreRelease ? head[..dash] : head;

        if (!IsThreeNumericSegments(core))
        {
            // The contract's shape is major.minor.patch; the shared kernel's parser would fill a
            // missing segment with zero and quietly accept "1.4" as 1.4.0.
            return false;
        }

        // Semver ignores build metadata for precedence, but an app build code is exactly what
        // distinguishes two shipped builds of the same version, so a numeric one is kept and a
        // non-numeric one (which AppVersion cannot represent) is dropped.
        var comparable = build.Length > 0 && IsAllDigits(build)
            ? string.Concat(core, "+", build)
            : core.ToString();

        if (!AppVersion.TryParse(comparable, out var release))
        {
            return false;
        }

        version = new ClientVersion(release, isPreRelease);
        return true;
    }

    private static bool IsThreeNumericSegments(ReadOnlySpan<char> core)
    {
        var segments = 0;
        var digitsInSegment = 0;

        foreach (var c in core)
        {
            if (c == '.')
            {
                if (digitsInSegment == 0)
                {
                    return false;
                }

                segments++;
                digitsInSegment = 0;
                continue;
            }

            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            digitsInSegment++;
        }

        return segments == 2 && digitsInSegment > 0;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    public static ClientVersion Zero => new(default, false);

    public int CompareTo(ClientVersion other)
    {
        var result = Release.CompareTo(other.Release);
        if (result != 0)
        {
            return result;
        }

        return IsPreRelease == other.IsPreRelease ? 0 : IsPreRelease ? -1 : 1;
    }

    public static bool operator <(ClientVersion left, ClientVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ClientVersion left, ClientVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ClientVersion left, ClientVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ClientVersion left, ClientVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => IsPreRelease ? $"{Release}-pre" : Release.ToString();
}
