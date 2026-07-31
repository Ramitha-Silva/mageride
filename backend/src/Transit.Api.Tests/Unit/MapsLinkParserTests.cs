using MageRide.Transit.Geo;

namespace MageRide.Transit.Tests.Unit;

/// <summary>
/// BR-23.4's five URL shapes, parsed. <b>Nothing here reaches the network.</b>
/// </summary>
public sealed class MapsLinkParserTests
{
    [Fact]
    public void The_q_parameter_shape_resolves()
    {
        var parsed = MapsLinkParser.Parse("https://maps.google.com/?q=6.9271,79.8612");

        Assert.Equal(6.9271, parsed!.Lat, 5);
        Assert.Equal(79.8612, parsed.Lng, 5);
    }

    [Fact]
    public void The_at_viewport_shape_resolves()
    {
        var parsed = MapsLinkParser.Parse("https://www.google.com/maps/@6.9271,79.8612,15z");

        Assert.Equal(6.9271, parsed!.Lat, 5);
        Assert.Equal(79.8612, parsed.Lng, 5);
    }

    [Fact]
    public void The_place_shape_resolves_and_carries_the_place_name()
    {
        var parsed = MapsLinkParser.Parse(
            "https://www.google.com/maps/place/Galle+Face+Green/@6.9271,79.8612,17z");

        Assert.Equal(6.9271, parsed!.Lat, 5);
        Assert.Equal("Galle Face Green", parsed.Label);
    }

    [Fact]
    public void The_pin_in_the_data_blob_wins_over_the_viewport()
    {
        // A /place/ URL carries both, and they are different: the `@` is the viewport framed
        // around the label, the `!3d!4d` is the pin. Taking the viewport drops the passenger's
        // marker down the street from the place they shared.
        var parsed = MapsLinkParser.Parse(
            "https://www.google.com/maps/place/Galle+Face+Green/@6.9200,79.8500,17z/"
            + "data=!3m1!4b1!4m5!3m4!1s0x0:0x0!8m2!3d6.9271!4d79.8612");

        Assert.Equal(6.9271, parsed!.Lat, 5);
        Assert.Equal(79.8612, parsed.Lng, 5);
    }

    [Fact]
    public void The_ll_parameter_shape_resolves()
    {
        var parsed = MapsLinkParser.Parse("https://maps.google.com/maps?ll=6.9271,79.8612&z=15");

        Assert.Equal(6.9271, parsed!.Lat, 5);
        Assert.Equal(79.8612, parsed.Lng, 5);
    }

    [Fact]
    public void A_negative_coordinate_survives()
    {
        var parsed = MapsLinkParser.Parse("https://maps.google.com/?q=-33.8688,151.2093");

        Assert.Equal(-33.8688, parsed!.Lat, 5);
        Assert.Equal(151.2093, parsed.Lng, 5);
    }

    [Fact]
    public void A_short_link_carries_no_coordinate_to_find()
    {
        // Its path is an opaque token — which is exactly why BR-23.4 sends these to the server and
        // parses everything else on the client, and why "parsed nothing" is what makes the resolver
        // follow a redirect rather than a second list of shortener hosts.
        Assert.Null(MapsLinkParser.Parse("https://maps.app.goo.gl/abc123"));
    }

    [Theory]
    [InlineData("https://www.google.com/maps/search/pizza")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData(null)]
    public void A_link_with_no_coordinate_in_it_fails_cleanly(string? url) =>
        Assert.Null(MapsLinkParser.Parse(url));

    [Fact]
    public void A_number_that_is_not_a_coordinate_is_refused()
    {
        // A zoom level and a latitude look alike to a regex. Dropping a pin at latitude 400 would
        // be worse than telling the user the link could not be read.
        Assert.Null(MapsLinkParser.Parse("https://maps.google.com/?q=400,79.8612"));
        Assert.Null(MapsLinkParser.Parse("https://maps.google.com/?q=6.9271,200.5"));
    }

    [Fact]
    public void A_place_segment_that_is_a_coordinate_is_not_offered_as_a_label()
    {
        // Showing "6.9271, 79.8612" under the pin tells the user nothing they cannot already see.
        var parsed = MapsLinkParser.Parse("https://www.google.com/maps/place/6.9271,79.8612/@6.9271,79.8612,17z");

        Assert.Equal(6.9271, parsed!.Lat, 5);
        Assert.Null(parsed.Label);
    }
}
