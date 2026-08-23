using System.Globalization;
using MageRide.Registry.Configuration;
using MageRide.Registry.Onboarding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MageRide.Registry.Tests.Domain;

/// <summary>
/// The signed profile-photo link (Δ MCS-25).
/// </summary>
/// <remarks>
/// This is the whole of the authorisation on <c>getDriverProfilePhoto</c>: the route is anonymous
/// because the caller is an image loader, so what stops one driver reading another's photograph is
/// that the signature does not verify. Every property that claim rests on is asserted here.
/// </remarks>
public sealed class DriverPhotoLinkTests
{
    private static readonly Guid Driver = Guid.Parse("00000000-0000-4000-8000-00000000d001");
    private static readonly Guid OtherDriver = Guid.Parse("00000000-0000-4000-8000-00000000d002");

    private static readonly DateTimeOffset Now = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

    private const string Photo = "s3://mageride-docs/retained/registry/driver-photo/d001/one";
    private const string Replacement = "s3://mageride-docs/retained/registry/driver-photo/d001/two";

    private static (DriverPhotoLinks Links, FakeTimeProvider Clock) Build(
        string? key = "a-key-both-replicas-share", TimeSpan? ttl = null)
    {
        var clock = new FakeTimeProvider(Now);

        var options = Options.Create(new RegistryOptions
        {
            ProfilePhotoLinkSigningKey = key,
            ProfilePhotoLinkTtl = ttl ?? TimeSpan.FromMinutes(15),
        });

        return (new DriverPhotoLinks(options, clock, NullLogger<DriverPhotoLinks>.Instance), clock);
    }

    /// <summary>The happy path, and the shape the app resolves against its gateway origin.</summary>
    [Fact]
    public void A_minted_link_names_the_driver_and_verifies()
    {
        var (links, _) = Build();

        var url = links.Create(Driver, Photo);

        Assert.StartsWith($"/v1/drivers/{Driver}/profile-photo?", url, StringComparison.Ordinal);

        var (version, expires, signature) = Parse(url);

        Assert.True(links.Verify(Driver, Photo, version, expires, signature));
    }

    /// <summary>
    /// The property the whole route rests on: a link is for one driver.
    /// </summary>
    /// <remarks>
    /// Without this, a driver who read their own profile would hold a link they could edit into
    /// anybody's — and the route is anonymous, so there is no second check behind it.
    /// </remarks>
    [Fact]
    public void A_link_for_one_driver_does_not_verify_for_another()
    {
        var (links, _) = Build();

        var (version, expires, signature) = Parse(links.Create(Driver, Photo));

        Assert.False(links.Verify(OtherDriver, Photo, version, expires, signature));
    }

    /// <summary>Past the TTL it stops working, which is what makes a leaked link worthless.</summary>
    [Fact]
    public void An_expired_link_no_longer_verifies()
    {
        var (links, clock) = Build(ttl: TimeSpan.FromMinutes(15));

        var (version, expires, signature) = Parse(links.Create(Driver, Photo));

        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True(links.Verify(Driver, Photo, version, expires, signature));

        // Over the line, not merely at it.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(links.Verify(Driver, Photo, version, expires, signature));
    }

    /// <summary>
    /// Moving the deadline out does not buy more time, because the deadline is signed.
    /// </summary>
    [Fact]
    public void Extending_the_expiry_invalidates_the_signature()
    {
        var (links, clock) = Build(ttl: TimeSpan.FromMinutes(15));

        var (version, expires, signature) = Parse(links.Create(Driver, Photo));

        clock.Advance(TimeSpan.FromHours(1));

        var later = (Now + TimeSpan.FromHours(2)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        Assert.False(links.Verify(Driver, Photo, version, later, signature));
    }

    /// <summary>A signature that is not hex, or not the right one, is refused rather than thrown on.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-hex")]
    [InlineData("00")]
    [InlineData("deadbeef")]
    public void A_malformed_or_wrong_signature_is_refused(string signature)
    {
        var (links, _) = Build();

        var (version, expires, _) = Parse(links.Create(Driver, Photo));

        Assert.False(links.Verify(Driver, Photo, version, expires, signature));
    }

    /// <summary>A missing half of the pair is refused, not treated as absent-therefore-fine.</summary>
    [Fact]
    public void A_link_with_no_expiry_or_no_signature_is_refused()
    {
        var (links, _) = Build();

        var (version, expires, signature) = Parse(links.Create(Driver, Photo));

        Assert.False(links.Verify(Driver, Photo, version, null, signature));
        Assert.False(links.Verify(Driver, Photo, version, expires, null));
        Assert.False(links.Verify(Driver, Photo, version, null, null));
    }

    /// <summary>
    /// An expiry that is not a number is refused — including the negative and hex forms
    /// <c>long.TryParse</c> would otherwise accept.
    /// </summary>
    [Theory]
    [InlineData("tomorrow")]
    [InlineData("-1")]
    [InlineData("+1893456000")]
    [InlineData("1893456000.5")]
    public void A_malformed_expiry_is_refused(string expires)
    {
        var (links, _) = Build();

        var (version, _, signature) = Parse(links.Create(Driver, Photo));

        Assert.False(links.Verify(Driver, Photo, version, expires, signature));
    }

    /// <summary>
    /// Two instances configured with the same key agree, which is the whole reason the key is
    /// configuration rather than something generated at start-up.
    /// </summary>
    /// <remarks>
    /// The failure this rules out is a link minted by replica A and followed on replica B, which a
    /// driver sees as an avatar that loads on one refresh and not the next.
    /// </remarks>
    [Fact]
    public void A_link_minted_by_one_instance_verifies_on_another_with_the_same_key()
    {
        var (minting, _) = Build();
        var (verifying, _) = Build();

        var (version, expires, signature) = Parse(minting.Create(Driver, Photo));

        Assert.True(verifying.Verify(Driver, Photo, version, expires, signature));
    }

    /// <summary>And two instances that generated their own keys do not, which is why it warns.</summary>
    [Fact]
    public void An_unconfigured_key_does_not_verify_across_instances()
    {
        var (minting, _) = Build(key: null);
        var (verifying, _) = Build(key: null);

        var (version, expires, signature) = Parse(minting.Create(Driver, Photo));

        Assert.True(minting.Verify(Driver, Photo, version, expires, signature));
        Assert.False(verifying.Verify(Driver, Photo, version, expires, signature));
    }

    /// <summary>
    /// Replacing the photograph changes the URL, which is what makes every cache in the path let
    /// go of the old one — the loader's, the OS's, a CDN's.
    /// </summary>
    [Fact]
    public void Replacing_the_photo_changes_the_link()
    {
        var (links, _) = Build();

        Assert.NotEqual(links.Create(Driver, Photo), links.Create(Driver, Replacement));
    }

    /// <summary>
    /// And a link minted for the old photograph stops working, rather than quietly serving the new
    /// one. "Here is my avatar" must not be a URL that outlives every picture it was issued for.
    /// </summary>
    [Fact]
    public void A_link_for_a_replaced_photo_no_longer_verifies()
    {
        var (links, _) = Build();

        var (version, expires, signature) = Parse(links.Create(Driver, Photo));

        Assert.True(links.Verify(Driver, Photo, version, expires, signature));
        Assert.False(links.Verify(Driver, Replacement, version, expires, signature));
    }

    /// <summary>The version is signed, so it cannot be swapped for the current one.</summary>
    [Fact]
    public void A_version_swapped_by_hand_invalidates_the_signature()
    {
        var (links, _) = Build();

        var (_, expires, signature) = Parse(links.Create(Driver, Photo));
        var (current, _, _) = Parse(links.Create(Driver, Replacement));

        Assert.False(links.Verify(Driver, Replacement, current, expires, signature));
    }

    /// <summary>It is opaque: the object key must not be readable out of the URL.</summary>
    [Fact]
    public void The_version_does_not_leak_the_object_key()
    {
        var (links, _) = Build();

        var url = links.Create(Driver, Photo);

        Assert.DoesNotContain("mageride-docs", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("driver-photo", url, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Version, string Expires, string Signature) Parse(string url)
    {
        var query = url[(url.IndexOf('?', StringComparison.Ordinal) + 1)..].Split('&');

        return (Value(query, "v"), Value(query, "expires"), Value(query, "signature"));

        static string Value(string[] pairs, string name) =>
            pairs.Single(pair => pair.StartsWith($"{name}=", StringComparison.Ordinal))[(name.Length + 1)..];
    }
}
