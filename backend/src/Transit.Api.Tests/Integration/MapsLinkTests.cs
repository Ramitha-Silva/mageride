using System.Diagnostics;
using System.Net;
using MageRide.TestKit;
using MageRide.Transit.Endpoints;
using MageRide.Transit.Tests.Infrastructure;

namespace MageRide.Transit.Tests.Integration;

/// <summary>
/// <b>Definition of done: "all four Google Maps URL shapes (?q=, @lat,lng, /place/...@, short link)
/// resolve or fail cleanly within 3 s."</b>
/// </summary>
/// <remarks>
/// The short-link case goes through a real redirect on a real socket, because the interesting part
/// is the chain being walked by hand with the allowlist re-checked at every hop — and that does not
/// exist above the socket.
/// </remarks>
[Collection(TransitCollection.Name)]
public sealed class MapsLinkTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("https://maps.google.com/?q=6.9271,79.8612")]
    [InlineData("https://www.google.com/maps/@6.9271,79.8612,15z")]
    [InlineData("https://www.google.com/maps/place/Galle+Face+Green/@6.9271,79.8612,17z")]
    public async Task Every_full_URL_shape_resolves(string url)
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var started = Stopwatch.GetTimestamp();

        var parsed = await harness.GetAsync<ParsedLinkResponse>(
            "/v1/geo/parse-maps-link?url=" + Uri.EscapeDataString(url));

        Assert.Equal(6.9271, parsed.Lat, 4);
        Assert.Equal(79.8612, parsed.Lng, 4);
        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_short_link_resolves_by_following_its_redirect()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var url = harness.ShortLink("https://www.google.com/maps/place/Kottawa/@6.8410,79.9653,16z");

        var started = Stopwatch.GetTimestamp();

        var parsed = await harness.GetAsync<ParsedLinkResponse>(
            "/v1/geo/parse-maps-link?url=" + Uri.EscapeDataString(url));

        Assert.Equal(6.8410, parsed.Lat, 4);
        Assert.Equal(79.9653, parsed.Lng, 4);
        Assert.Equal("Kottawa", parsed.Label);
        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_short_link_that_redirects_off_the_allowlist_is_abandoned_mid_chain()
    {
        // The case the allowlist exists for. The FIRST url is one an attacker cannot choose the
        // destination of — the redirect target is — so a check on the first hop alone would be no
        // check at all. `169.254.169.254` is the cluster metadata endpoint.
        await using var harness = await TransitHarness.StartAsync(postgres);

        var url = harness.ShortLink("http://169.254.169.254/latest/meta-data/");

        using var response = await harness.GetAsync(
            "/v1/geo/parse-maps-link?url=" + Uri.EscapeDataString(url));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_url_on_a_host_that_is_not_Google_is_refused_before_any_request()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        using var response = await harness.GetAsync(
            "/v1/geo/parse-maps-link?url=" + Uri.EscapeDataString("http://169.254.169.254/latest/meta-data/"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_host_that_merely_ends_with_an_allowed_one_is_somebody_elses_domain()
    {
        // `evilgoo.gl` ends with `goo.gl`. A suffix match on the string would fetch it.
        await using var harness = await TransitHarness.StartAsync(postgres);

        using var response = await harness.GetAsync(
            "/v1/geo/parse-maps-link?url=" + Uri.EscapeDataString("https://evilgoo.gl/abc"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_link_with_no_coordinate_in_it_fails_cleanly()
    {
        // BR-23.4's Error state: "couldn't read that link — pick on map". A 422, because the
        // request was well-formed and the link is what could not be read.
        await using var harness = await TransitHarness.StartAsync(postgres);

        using var response = await harness.GetAsync(
            "/v1/geo/parse-maps-link?url=" + Uri.EscapeDataString("https://www.google.com/maps/search/pizza"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_short_link_that_leads_nowhere_fails_inside_the_budget()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var started = Stopwatch.GetTimestamp();

        using var response = await harness.GetAsync(
            "/v1/geo/parse-maps-link?url="
            + Uri.EscapeDataString($"{harness.ShortenerBaseUrl}/never-registered"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_missing_url_is_a_400_and_not_a_422()
    {
        // A malformed request and an unreadable link are different failures: one the client can
        // fix by sending a url, the other by picking on the map.
        await using var harness = await TransitHarness.StartAsync(postgres);

        using var response = await harness.GetAsync("/v1/geo/parse-maps-link?url=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        using var response = await harness.GetAsync(
            "/v1/geo/parse-maps-link?url=" + Uri.EscapeDataString("https://maps.google.com/?q=6.9,79.8"),
            authenticated: false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
