using MageRide.ApiGateway.Versioning;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// Version ordering for D-31. The floor comparison is only as trustworthy as this: a mis-ordered
/// pair either locks out a current build or lets an unsupported one through.
/// </summary>
public sealed class ClientVersionTests
{
    [Theory]
    [InlineData("1.4.0")]
    [InlineData("1.4.0+318")]
    [InlineData("1.4.0-rc.1")]
    [InlineData("1.4.0-rc.1+318")]
    [InlineData("1.4.0+exp.sha.5114f85")]
    [InlineData("0.0.1")]
    [InlineData("10.20.30")]
    public void Every_shape_the_contract_allows_parses(string value) =>
        Assert.True(ClientVersion.TryParse(value, out _), value);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.4")]
    [InlineData("1.4.0.2")]
    [InlineData("v1.4.0")]
    [InlineData("one.four.zero")]
    [InlineData("-1.4.0")]
    public void A_shape_the_contract_forbids_does_not_parse(string? value) =>
        Assert.False(ClientVersion.TryParse(value, out _), value ?? "(null)");

    [Theory]
    [InlineData("1.4.0", "1.4.1")]
    [InlineData("1.4.0", "1.5.0")]
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("1.4.0+1", "1.4.0+2")]
    // Semver: a pre-release precedes the release it leads to, so an rc must not satisfy the floor.
    [InlineData("1.4.0-rc.1", "1.4.0")]
    public void Ordering_follows_semver(string lower, string higher)
    {
        Assert.True(ClientVersion.TryParse(lower, out var left));
        Assert.True(ClientVersion.TryParse(higher, out var right));

        Assert.True(left < right, $"{lower} should sort below {higher}");
        Assert.True(right > left);
        Assert.False(left >= right);
    }

    [Fact]
    public void Equal_versions_compare_equal()
    {
        Assert.True(ClientVersion.TryParse("1.4.0+7", out var left));
        Assert.True(ClientVersion.TryParse("1.4.0+7", out var right));

        Assert.True(left <= right);
        Assert.True(left >= right);
        Assert.Equal(left, right);
    }

    [Theory]
    // hard floor 1.4.0, soft floor 1.6.0, latest 1.6.2
    [InlineData("1.3.9", true, true)]
    [InlineData("1.4.0-rc.9", true, true)]
    [InlineData("1.4.0", true, false)]
    [InlineData("1.6.0", false, false)]
    [InlineData("2.0.0", false, false)]
    [InlineData("garbage", true, true)]
    public void Evaluate_reports_the_floor_verdict(string current, bool updateRequired, bool isMandatory)
    {
        var floor = new PlatformVersionFloor
        {
            MinimumVersion = "1.4.0",
            RecommendedVersion = "1.6.0",
            LatestVersion = "1.6.2",
            UpdateUrl = "https://play.google.com/store/apps/details?id=lk.mageride.driver",
        };

        var verdict = VersionFloorService.Evaluate(floor, current);

        Assert.Equal(updateRequired, verdict.UpdateRequired);
        Assert.Equal(isMandatory, verdict.IsMandatory);
        Assert.Equal("1.6.2", verdict.LatestVersion);
    }

    [Fact]
    public void A_missing_recommended_version_falls_back_to_latest()
    {
        var floor = new PlatformVersionFloor
        {
            MinimumVersion = "1.0.0",
            RecommendedVersion = null,
            LatestVersion = "1.6.2",
            UpdateUrl = "https://play.google.com/store/apps/details?id=lk.mageride.driver",
        };

        Assert.True(VersionFloorService.Evaluate(floor, "1.5.0").UpdateRequired);
        Assert.False(VersionFloorService.Evaluate(floor, "1.6.2").UpdateRequired);
    }
}
