using System.Net;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// <c>/v1/me/prefs/*</c> — language (D-26, AL-26), default payment method (AL-14, US-22.4) and
/// the launch city (AL-27, US-1.3a).
/// </summary>
[Collection<IamCollection>]
public sealed class PreferenceTests(PostgresFixture postgres, RedisFixture redis)
{
    [Theory]
    [InlineData("si")]
    [InlineData("ta")]
    [InlineData("en")]
    public async Task Each_of_the_three_languages_can_be_set(string language)
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PutAsync("/v1/me/prefs/language", new { language }, session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(language, (await IamHarness.ReadJsonAsync(response)).GetProperty("language").GetString());

        var profile = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/users/me", session.AccessToken));
        Assert.Equal(language, profile.GetProperty("language").GetString());
    }

    [Fact]
    public async Task A_language_outside_si_ta_en_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PutAsync("/v1/me/prefs/language", new { language = "hi" }, session.AccessToken);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("lankaqr")]
    [InlineData("onepay")]
    public async Task Each_of_the_three_payment_methods_can_be_set(string method)
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PutAsync(
            "/v1/me/prefs/payment-method", new { defaultPaymentMethod = method }, session.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/users/me", session.AccessToken));
        Assert.Equal(method, profile.GetProperty("defaultPaymentMethod").GetString());
    }

    /// <summary>
    /// <c>scan_driver_qr</c> (AL-22) is a settlement choice made during a ride, not a stored
    /// preference, and <c>cod</c> is package-only. Neither belongs on this route.
    /// </summary>
    [Theory]
    [InlineData("scan_driver_qr")]
    [InlineData("cod")]
    public async Task A_settlement_only_method_is_not_a_stored_preference(string method)
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PutAsync(
            "/v1/me/prefs/payment-method", new { defaultPaymentMethod = method }, session.AccessToken);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task A_launch_city_can_be_chosen_from_the_seeded_set()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PutAsync(
            "/v1/me/prefs/operating-city", new { operatingCityCode = "kandy" }, session.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/users/me", session.AccessToken));
        Assert.Equal("kandy", profile.GetProperty("operatingCityCode").GetString());
    }

    [Fact]
    public async Task A_city_the_platform_does_not_operate_in_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PutAsync(
            "/v1/me/prefs/operating-city", new { operatingCityCode = "jaffna" }, session.AccessToken);

        // 400, not a foreign-key 500: config.operating_cities has no Jaffna row today.
        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task Every_preference_route_needs_a_token()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        foreach (var (path, body) in new (string, object)[]
                 {
                     ("/v1/me/prefs/language", new { language = "si" }),
                     ("/v1/me/prefs/payment-method", new { defaultPaymentMethod = "cash" }),
                     ("/v1/me/prefs/operating-city", new { operatingCityCode = "colombo" }),
                 })
        {
            await ProblemDocument.AssertAsync(
                await harness.PutAsync(path, body), HttpStatusCode.Unauthorized, "unauthorized");
        }
    }
}
