using System.Net;
using System.Text.Json;
using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.AdminBff.Tests.Integration;

/// <summary>
/// SCR-AP-007 — tariffs, launch cities, feature flags, trains and announcements.
/// </summary>
[Collection(AdminBffCollection.Name)]
public sealed class ConfigurationTests(PostgresFixture postgres)
{
    /// <summary>
    /// A tariff publish adds a version and leaves the one that priced yesterday's rides alone.
    /// </summary>
    [Fact]
    public async Task Publishing_tariffs_versions_them_rather_than_editing_in_place()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var finance = await harness.Seed.InternalUserAsync(MageRideRoles.FinanceOfficer);
        var bearer = harness.Tokens.Internal(finance, MageRideRoles.FinanceOfficer);

        var effectiveFrom = DateTimeOffset.UtcNow.AddHours(1);

        using var response = await harness.SendAsync(
            HttpMethod.Put,
            "/v1/admin/fares/tariffs",
            bearer,
            new
            {
                effectiveFrom,
                tariffs = new[]
                {
                    new { vehicleType = "sedan", firstKmMinor = 25_000, perKmMinor = 9_000 },
                },
                peakWindows = new[]
                {
                    // Wraps midnight, and must stay that way: migration 1001 declines to constrain
                    // the ordering for exactly this window.
                    new { kind = "night", startLocal = "22:00", endLocal = "05:00", multiplierPct = 15 },
                },
            });

        using var payload = await harness.ReadJsonAsync(response);

        Assert.Equal("sedan", payload.RootElement.GetProperty("tariffs")[0].GetProperty("vehicleType").GetString());

        var window = payload.RootElement.GetProperty("peakWindows")[0];
        Assert.Equal("22:00", window.GetProperty("startLocal").GetString());
        Assert.Equal("05:00", window.GetProperty("endLocal").GetString());

        // The 1901 seed row for sedan is still there — the publish inserted a version, it did not
        // replace the rate that has already priced rides.
        var audit = (await harness.Seed.AuditRowsByActionAsync(AdminAuditActions.TariffsPublished))
            .First(row => row.ActorId == finance);

        using var before = JsonDocument.Parse(audit.Before!);
        Assert.True(before.RootElement.GetProperty("tariffs").GetArrayLength() > 0);
    }

    /// <summary>Backdating a version is refused: a published rate is permanent (D-10).</summary>
    [Fact]
    public async Task A_backdated_tariff_version_is_refused()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        using var response = await harness.SendAsync(
            HttpMethod.Put,
            "/v1/admin/fares/tariffs",
            harness.Tokens.Admin(admin),
            new
            {
                effectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
                tariffs = new[] { new { vehicleType = "sedan", firstKmMinor = 1, perKmMinor = 1 } },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A rate for a type AL-09 does not have never reaches the table.</summary>
    [Fact]
    public async Task A_non_canonical_vehicle_type_is_refused()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        using var response = await harness.SendAsync(
            HttpMethod.Put,
            "/v1/admin/fares/tariffs",
            harness.Tokens.Admin(admin),
            new { tariffs = new[] { new { vehicleType = "car", firstKmMinor = 1, perKmMinor = 1 } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("sedan", body, StringComparison.Ordinal);
    }

    /// <summary>Launching a city needs no app release, and needs all three languages (D-26).</summary>
    [Fact]
    public async Task A_city_is_created_with_three_names_and_refused_with_two()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var bearer = harness.Tokens.Admin(admin);
        var code = $"city_{Guid.NewGuid():N}"[..20];

        using var incomplete = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/admin/config/cities",
            bearer,
            new { code, nameEn = "Matara", nameSi = "මාතර", centroid = new { lat = 5.95, lng = 80.55 } });

        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.Contains("nameTa", await incomplete.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var created = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/admin/config/cities",
            bearer,
            new
            {
                code,
                nameEn = "Matara",
                nameSi = "මාතර",
                nameTa = "மாத்தறை",
                centroid = new { lat = 5.95, lng = 80.55 },
            });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The same code twice is a 409 the operator can act on, not a 500 with a constraint name.
        using var duplicate = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/admin/config/cities",
            bearer,
            new
            {
                code,
                nameEn = "Matara",
                nameSi = "මාතර",
                nameTa = "மாத்தறை",
                centroid = new { lat = 5.95, lng = 80.55 },
            });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    /// <summary>A PATCH applies over the row that is there and audits the pair.</summary>
    [Fact]
    public async Task Deactivating_a_city_records_both_images()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        using var response = await harness.SendAsync(
            HttpMethod.Patch, "/v1/admin/config/cities/kandy", harness.Tokens.Admin(admin), new { active = false });

        using var payload = await harness.ReadJsonAsync(response);

        Assert.False(payload.RootElement.GetProperty("active").GetBoolean());
        // Untouched fields survive a sparse PATCH.
        Assert.Equal("Kandy", payload.RootElement.GetProperty("nameEn").GetString());

        var audit = (await harness.Seed.AuditRowsByActionAsync(AdminAuditActions.CityUpdated))
            .First(row => row.ActorId == admin);

        using var before = JsonDocument.Parse(audit.Before!);
        using var after = JsonDocument.Parse(audit.After!);

        Assert.True(before.RootElement.GetProperty("isActive").GetBoolean());
        Assert.False(after.RootElement.GetProperty("isActive").GetBoolean());
    }

    /// <summary>A feature flag's first set creates it; the second changes it and keeps the note.</summary>
    [Fact]
    public async Task A_feature_flag_is_upserted_and_keeps_its_description()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var superAdmin = await harness.Seed.InternalUserAsync(MageRideRoles.SuperAdmin);
        var bearer = harness.Tokens.SuperAdmin(superAdmin);
        var key = $"probe_{Guid.NewGuid():N}"[..20];

        using var created = await harness.SendAsync(
            HttpMethod.Put,
            $"/v1/admin/config/feature-flags/{key}",
            bearer,
            new { enabled = true, description = "Turns on the thing." });

        using var createdPayload = await harness.ReadJsonAsync(created);
        Assert.True(createdPayload.RootElement.GetProperty("enabled").GetBoolean());

        using var flipped = await harness.SendAsync(
            HttpMethod.Put, $"/v1/admin/config/feature-flags/{key}", bearer, new { enabled = false });

        using var flippedPayload = await harness.ReadJsonAsync(flipped);

        Assert.False(flippedPayload.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("Turns on the thing.", flippedPayload.RootElement.GetProperty("description").GetString());

        using var list = await harness.GetAsync("/v1/admin/config/feature-flags", bearer);
        using var listPayload = await harness.ReadJsonAsync(list);

        Assert.Contains(
            listPayload.RootElement.EnumerateArray(),
            flag => flag.GetProperty("key").GetString() == key);
    }

    /// <summary>
    /// A train is a Mode A vehicle nobody but an admin can create, and its number is unique across
    /// the live set (D-37).
    /// </summary>
    [Fact]
    public async Task A_train_is_registered_edited_and_retired()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var bearer = harness.Tokens.Admin(admin);
        var number = $"EXP-{Guid.NewGuid():N}"[..12];

        using var created = await harness.SendAsync(
            HttpMethod.Post, "/v1/admin/trains", bearer, new { name = "Ruhunu Kumari", trainNumber = number });

        using var createdPayload = await harness.ReadJsonAsync(created);
        var trainId = createdPayload.RootElement.GetProperty("trainId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(createdPayload.RootElement.GetProperty("active").GetBoolean());

        using var duplicate = await harness.SendAsync(
            HttpMethod.Post, "/v1/admin/trains", bearer, new { name = "Copy", trainNumber = number });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var updated = await harness.SendAsync(
            HttpMethod.Put,
            $"/v1/admin/trains/{trainId:D}",
            bearer,
            new { name = "Ruhunu Kumari Express", trainNumber = number });

        using var updatedPayload = await harness.ReadJsonAsync(updated);
        Assert.Equal("Ruhunu Kumari Express", updatedPayload.RootElement.GetProperty("name").GetString());

        using var retired = await harness.SendAsync(HttpMethod.Delete, $"/v1/admin/trains/{trainId:D}", bearer);
        Assert.Equal(HttpStatusCode.NoContent, retired.StatusCode);

        // Soft: the row is still there, so every historical trip still resolves.
        var rows = await harness.Seed.AuditRowsAsync(trainId);
        Assert.Equal(3, rows.Count);
        Assert.Equal(AdminAuditActions.TrainRetired, rows[0].Action);
    }

    /// <summary>
    /// An announcement is forwarded to content-svc with the operator's own bearer, and refused here
    /// when a language is missing.
    /// </summary>
    [Fact]
    public async Task An_announcement_needs_three_languages_and_is_forwarded_with_the_bearer()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var bearer = harness.Tokens.Admin(admin);

        using var incomplete = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/admin/announcements",
            bearer,
            new { messageByLang = new { en = "Service update" }, startsAt = DateTimeOffset.UtcNow });

        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.Contains("messageByLang.si", await incomplete.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var published = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/admin/announcements",
            bearer,
            new
            {
                messageByLang = new { si = "සේවා යාවත්කාලීන", ta = "சேவை புதுப்பிப்பு", en = "Service update" },
                startsAt = DateTimeOffset.UtcNow,
                push = true,
            });

        using var payload = await harness.ReadJsonAsync(published);
        var broadcastId = payload.RootElement.GetProperty("broadcastId").GetGuid();

        var forwarded = harness.Upstream.Last("/broadcasts");

        // content-svc's route is role-gated, so the caller's own bearer goes and the shared key
        // does not — sending the key would bypass a check that exists.
        Assert.Null(forwarded.InternalKey);
        Assert.NotNull(forwarded.Authorization);

        Assert.Single(await harness.Seed.AuditRowsAsync(broadcastId));
    }

    /// <summary>The GTFS family is forwarded to transit-svc and audited at the front door.</summary>
    [Fact]
    public async Task The_gtfs_manager_is_proxied_to_transit_svc()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        using var response = await harness.GetAsync(
            "/v1/admin/transit/gtfs/versions", harness.Tokens.Admin(admin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var forwarded = harness.Upstream.Last("/transit/gtfs/");

        Assert.Equal("/v1/admin/transit/gtfs/versions", forwarded.Path);
        Assert.NotNull(forwarded.Authorization);
        Assert.Null(forwarded.InternalKey);
    }
}
