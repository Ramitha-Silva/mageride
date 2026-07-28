using System.Net;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Iam.Tests.Integration;

/// <summary><c>GET</c> / <c>PUT /v1/users/me</c> — the profile surface (US-1.5, AL-06, AL-14).</summary>
[Collection<IamCollection>]
public sealed class ProfileTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_fresh_account_reads_back_the_defaults_the_schema_gives_it()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();
        var session = await harness.SignInAsync(phone, "handset");

        var response = await harness.GetAsync("/v1/users/me", session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await IamHarness.ReadJsonAsync(response);

        Assert.Equal(session.UserId, body.GetProperty("userId").GetString());
        Assert.Equal(phone, body.GetProperty("phone").GetString());
        Assert.Equal("passenger", body.GetProperty("role").GetString());
        Assert.Equal(["passenger"], body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        // iam.users defaults: language 'en' (not 'si' — AL-26 makes only the *picker*
        // Sinhala-first) and default_payment_method 'cash' (US-22.4).
        Assert.Equal("en", body.GetProperty("language").GetString());
        Assert.Equal("cash", body.GetProperty("defaultPaymentMethod").GetString());
        Assert.Empty(body.GetProperty("notifPrefs").EnumerateObject());
    }

    [Fact]
    public async Task The_profile_carries_the_union_of_every_granted_role()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");

        await harness.Seed.GrantRoleAsync(Guid.Parse(session.UserId), "fleet_owner");

        var response = await harness.GetAsync("/v1/users/me", session.AccessToken);
        var body = await IamHarness.ReadJsonAsync(response);

        Assert.Equal(
            ["driver", "fleet_owner"],
            body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_patch_changes_only_what_it_names()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        await harness.PutAsync(
            "/v1/users/me",
            new { firstName = "Nimal", photoUrl = "https://cdn.mageride.lk/p/1.jpg" },
            session.AccessToken);

        // Only firstName this time; the photo must survive.
        var second = await harness.PutAsync("/v1/users/me", new { firstName = "Nimal Perera" }, session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var body = await IamHarness.ReadJsonAsync(second);
        Assert.Equal("Nimal Perera", body.GetProperty("firstName").GetString());
        Assert.Equal("https://cdn.mageride.lk/p/1.jpg", body.GetProperty("photoUrl").GetString());
    }

    /// <summary>
    /// The notification-type keys are data, not property names — <c>MageRideJson</c>'s camelCase
    /// dictionary-key policy would rewrite <c>SCHEDULED_REMINDER</c> as <c>sCHEDULED_REMINDER</c>
    /// on the way out and read it back verbatim, corrupting the mute exactly once.
    /// </summary>
    [Fact]
    public async Task Notification_type_keys_survive_the_round_trip_unchanged()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PutAsync(
            "/v1/users/me",
            new { notifPrefs = new Dictionary<string, bool> { ["SCHEDULED_REMINDER"] = false, ["LOW_BALANCE"] = true } },
            session.AccessToken);

        var body = await IamHarness.ReadJsonAsync(response);
        var prefs = body.GetProperty("notifPrefs");

        Assert.False(prefs.GetProperty("SCHEDULED_REMINDER").GetBoolean());
        Assert.True(prefs.GetProperty("LOW_BALANCE").GetBoolean());

        // And at rest, not only on the wire.
        var stored = await harness.Seed.NotificationPreferencesJsonAsync(Guid.Parse(session.UserId));
        Assert.Contains("SCHEDULED_REMINDER", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("sCHEDULED_REMINDER", stored, StringComparison.Ordinal);
    }

    /// <summary>US-10.7 / notification.yaml: the safety-critical types "cannot be muted and are ignored".</summary>
    [Fact]
    public async Task Safety_critical_notifications_cannot_be_muted()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PutAsync(
            "/v1/users/me",
            new
            {
                notifPrefs = new Dictionary<string, bool>
                {
                    ["SOS_TRIGGERED"] = false,
                    ["RIDE_CANCELLED"] = false,
                    ["PROMOTIONS"] = false,
                },
            },
            session.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var prefs = (await IamHarness.ReadJsonAsync(response)).GetProperty("notifPrefs");

        Assert.False(prefs.TryGetProperty("SOS_TRIGGERED", out _));
        Assert.False(prefs.TryGetProperty("RIDE_CANCELLED", out _));
        Assert.False(prefs.GetProperty("PROMOTIONS").GetBoolean());
    }

    [Theory]
    [InlineData("language", "fr")]
    [InlineData("photoUrl", "not-a-url")]
    public async Task A_bad_field_is_a_validation_failure_naming_the_field(string field, string value)
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PutAsync(
            "/v1/users/me", new Dictionary<string, string> { [field] = value }, session.AccessToken);

        var problem = await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.True(problem.Root.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task A_first_name_longer_than_the_contract_allows_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var response = await harness.PutAsync(
            "/v1/users/me", new { firstName = new string('x', 121) }, session.AccessToken);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task The_profile_needs_a_token()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var response = await harness.GetAsync("/v1/users/me");
        await ProblemDocument.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task A_portal_identity_reads_its_email_and_an_empty_phone()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var email = IamSeed.NextEmail("finance");
        const string Password = "correct-horse-battery";

        var userId = await harness.Seed.PortalUserAsync(email, "finance_officer", Password);
        var token = await harness.PortalTokenAsync(email, Password);

        var body = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/users/me", token));

        Assert.Equal(userId.ToString(), body.GetProperty("userId").GetString());
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.Equal(string.Empty, body.GetProperty("phone").GetString());
        Assert.Equal("finance_officer", body.GetProperty("role").GetString());
    }
}
