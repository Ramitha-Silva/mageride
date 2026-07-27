using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace MageRide.Shared.Http;

/// <summary>
/// A client app version as sent in <c>X-App-Version</c> (D-31): <c>major.minor.patch</c> with an
/// optional <c>+build</c> suffix, e.g. <c>1.4.2+318</c>.
/// </summary>
/// <remarks>
/// The gate itself lives at the YARP gateway (C008); this is only the parse-and-compare the
/// gateway and any service reading the header share, so both agree on ordering.
/// </remarks>
public readonly record struct AppVersion(int Major, int Minor, int Patch, int Build) : IComparable<AppVersion>
{
    public static bool TryParse(string? value, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();

        var build = 0;
        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            var buildSpan = span[(plus + 1)..];
            if (!int.TryParse(buildSpan, NumberStyles.None, CultureInfo.InvariantCulture, out build))
            {
                return false;
            }

            span = span[..plus];
        }

        Span<int> parts = [0, 0, 0];
        var index = 0;
        var start = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i != span.Length && span[i] != '.')
            {
                continue;
            }

            if (index == parts.Length ||
                !int.TryParse(span[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out var segment))
            {
                return false;
            }

            parts[index++] = segment;
            start = i + 1;
        }

        version = new AppVersion(parts[0], parts[1], parts[2], build);
        return true;
    }

    public static AppVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a valid X-App-Version (expected major.minor.patch[+build]).");

    public int CompareTo(AppVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        return result != 0 ? result : Build.CompareTo(other.Build);
    }

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        Build == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}+{Build}");
}

/// <summary>Reads the D-31 headers off a request.</summary>
public static class AppVersionHeaderExtensions
{
    public static bool TryGetAppVersion(this Microsoft.AspNetCore.Http.HttpRequest request, out AppVersion version)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AppVersion.TryParse(request.Headers[MageRideHeaders.AppVersion], out version);
    }

    public static bool TryGetClientPlatform(this Microsoft.AspNetCore.Http.HttpRequest request, [NotNullWhen(true)] out string? platform)
    {
        ArgumentNullException.ThrowIfNull(request);

        var value = request.Headers[MageRideHeaders.Platform].ToString();
        platform = value.ToLowerInvariant() switch
        {
            ClientPlatforms.Android => ClientPlatforms.Android,
            ClientPlatforms.Ios => ClientPlatforms.Ios,
            _ => null,
        };

        return platform is not null;
    }
}
