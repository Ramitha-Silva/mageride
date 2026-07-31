using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.Shared.Http.Idempotency;
using MageRide.TestKit;
using MageRide.Transit.Endpoints;
using MageRide.Transit.Tests.Infrastructure;

namespace MageRide.Transit.Tests.Integration;

/// <summary>
/// US-28.2 and US-28.3 — the atomic swap, version history, rollback and the signed download.
/// </summary>
/// <remarks>
/// <b>Definition of done:</b> "activation swaps live tables atomically and transit-svc serves the
/// new routes within 60 s" and "re-activating an archived version restores it and archives the
/// current one". Both are asserted through the running service's own
/// <c>GET /v1/transit/options</c>, not by reading the tables — what BR-32.2 promises is that
/// passengers see the new dataset, and only the endpoint can say whether they do.
/// </remarks>
[Trait("Category", "Gtfs")]
[Collection(TransitCollection.Name)]
public sealed class GtfsActivationTests(PostgresFixture postgres)
{
    private const string FortToKottawa =
        "/v1/transit/options?fromLat=6.9344&fromLng=79.8428&toLat=6.8410&toLng=79.9653";

    /// <summary>A second route and halt, so "which feed is live" is answerable from the wire.</summary>
    private static byte[] FeedWithExpressRoute(string feedVersion) =>
        GtfsZipBuilder.Valid(feedVersion)
            .Append("stops.txt", "PLW,Piliyandala,6.8016,79.9219")
            .Append("routes.txt", "R255,SLTB,255,Kottawa - Piliyandala,3")
            .Append("trips.txt", "R255,WEEKDAY,T255-1,Piliyandala,0,")
            .Append("stop_times.txt", "T255-1,08:00:00,08:00:00,KTW,1")
            .Append("stop_times.txt", "T255-1,08:20:00,08:20:00,PLW,2")
            .Build();

    [Fact]
    public async Task Activation_swaps_the_live_tables_and_transit_svc_serves_the_new_routes()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (adminId, bearer) = await harness.AdminAsync();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(
            GtfsZipBuilder.Valid("2026-12-01").Build(), bearer);

        using var response = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}/activate", bearer);

        var activated = await TransitHarness.JsonAsync(response);

        Assert.Equal("active", activated.GetProperty("status").GetString());
        Assert.Equal("2026-12-01", activated.GetProperty("feedInfoVersion").GetString());

        // The bound D6' I-32.1 sets, and the reason activation fires NOTIFY inside its own
        // transaction: the running process reloads without a restart.
        var options = await harness.WaitForAsync<TransitOptionsResponse>(
            FortToKottawa,
            result => result.FeedVersion == "2026-12-01",
            TimeSpan.FromSeconds(60));

        Assert.Equal(TransitEndpoints.CoverageActive, options.Coverage);

        var direct = Assert.Single(options.Options, option => option.Kind == "direct");
        var leg = Assert.Single(direct.Legs);

        Assert.Equal("138", leg.RouteShortName);
        Assert.Equal("FORT", leg.BoardStopId);
        Assert.Equal("KTW", leg.AlightStopId);

        // C056 asked for this by name: trips.txt's trip_headsign into transit.gtfs_trips
        // (migration 1406). Without the importer mapping it, this is "Colombo Fort - Kottawa" —
        // the route_long_name fallback — and the two directions of route 138 are the same card.
        Assert.Equal("Kottawa", leg.Headsign);

        // The shape came through the temp-table hop as real geography, not as (0, 0).
        Assert.False(string.IsNullOrEmpty(leg.Shape));

        await AssertAuditedAsync(postgres, feedVersionId, "GTFS_FEED_ACTIVATED", adminId);
    }

    [Fact]
    public async Task Re_activating_an_archived_version_restores_it_and_archives_the_current_one()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var first = await harness.UploadAndAwaitVerdictAsync(GtfsZipBuilder.Valid("2027-01-01").Build(), bearer);

        await ActivateAsync(harness, first, bearer);
        await harness.WaitForAsync<TransitOptionsResponse>(FortToKottawa, feed => feed.FeedVersion == "2027-01-01");

        // A feed that carries a route the first one does not, so "which is live" is a question the
        // wire answers rather than one the database answers.
        var second = await harness.UploadAndAwaitVerdictAsync(FeedWithExpressRoute("2027-02-01"), bearer, "v2.zip");

        await ActivateAsync(harness, second, bearer);
        await harness.WaitForAsync<TransitOptionsResponse>(FortToKottawa, feed => feed.FeedVersion == "2027-02-01");

        using (var expressWhileLive = await harness.GetAsync("/v1/transit/routes/R255"))
        {
            Assert.Equal(HttpStatusCode.OK, expressWhileLive.StatusCode);
        }

        Assert.Equal("archived", await StatusOfAsync(harness, first, bearer));

        // The rollback: BR-32.3 is the same endpoint with the same guarantees.
        var rolledBack = await ActivateAsync(harness, first, bearer);

        Assert.Equal("active", rolledBack.GetProperty("status").GetString());

        // `archived_at` is cleared, or ck_gtfs_feed_versions_activated would have refused the row.
        Assert.Equal(JsonValueKind.Null, rolledBack.GetProperty("archivedAt").ValueKind);

        Assert.Equal("archived", await StatusOfAsync(harness, second, bearer));

        await harness.WaitForAsync<TransitOptionsResponse>(FortToKottawa, feed => feed.FeedVersion == "2027-01-01");

        using var expressAfterRollback = await harness.GetAsync("/v1/transit/routes/R255");

        Assert.Equal(HttpStatusCode.NotFound, expressAfterRollback.StatusCode);
    }

    [Fact]
    public async Task The_swap_leaves_each_schema_carrying_its_own_index_names()
    {
        // The C005 decision `contracts/transit.yaml` records: the two sides carry deliberately
        // different index names, and a swap that moved the tables but not the names would leave a
        // database where migration 1404's `CREATE INDEX IF NOT EXISTS ix_staging_…` matches
        // nothing and builds a second index on every re-run. Asserted after TWO activations,
        // because a rename that only worked in one direction would pass after one.
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var first = await harness.UploadAndAwaitVerdictAsync(GtfsZipBuilder.Valid("2030-01-01").Build(), bearer, "i1.zip");
        var second = await harness.UploadAndAwaitVerdictAsync(FeedWithExpressRoute("2030-02-01"), bearer, "i2.zip");

        await ActivateAsync(harness, first, bearer);
        await ActivateAsync(harness, second, bearer);

        await using var connection = await postgres.OpenAsync();

        var live = await connection.QueryAsync<string>(
            "SELECT indexname FROM pg_indexes WHERE schemaname='transit' AND indexname LIKE 'ix_%';");

        var staging = await connection.QueryAsync<string>(
            "SELECT indexname FROM pg_indexes WHERE schemaname='transit_staging' AND indexname LIKE 'ix_%';");

        Assert.Contains("ix_gtfs_stops_geo", live);
        Assert.Contains("ix_gtfs_trips_route", live);
        Assert.Contains("ix_gtfs_stop_times_stop", live);
        Assert.DoesNotContain(live, name => name.StartsWith("ix_staging_", StringComparison.Ordinal));

        Assert.Contains("ix_staging_gtfs_stops_geo", staging);
        Assert.Contains("ix_staging_gtfs_trips_route", staging);
        Assert.Contains("ix_staging_gtfs_stop_times_stop", staging);

        // And the swap left staging holding the feed it replaced, not the one that went live —
        // which is what makes the next activation's TRUNCATE the only thing standing between two
        // feeds.
        var stagingRoutes = await connection.QueryAsync<string>(
            "SELECT route_id FROM transit_staging.gtfs_routes ORDER BY route_id;");

        Assert.Equal(["R138"], stagingRoutes);
    }

    [Fact]
    public async Task Activating_the_live_feed_again_is_409_feed_already_active()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(
            GtfsZipBuilder.Valid("2027-03-01").Build(), bearer);

        await ActivateAsync(harness, feedVersionId, bearer);

        using var again = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}/activate", bearer);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var problem = await TransitHarness.ProblemAsync(again);

        Assert.EndsWith("feed-already-active", problem.GetProperty("type").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replayed_activation_answers_the_first_response_and_swaps_once()
    {
        // BR-32.2 says activation is "idempotent on `Idempotency-Key`", and SCR-AP-016 puts it
        // behind a confirm dialog somebody can double-click.
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(
            GtfsZipBuilder.Valid("2027-04-01").Build(), bearer);

        var key = Guid.NewGuid().ToString("N");
        var path = $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}/activate";

        using var first = await harness.SendAsync(HttpMethod.Post, path, bearer, key);
        var firstBody = await first.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var replay = await harness.SendAsync(HttpMethod.Post, path, bearer, key);
        var replayBody = await replay.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstBody, replayBody);
        Assert.Equal("true", replay.Headers.GetValues(IdempotencyMiddleware.ReplayHeader).Single());

        // Replayed, not re-executed: a second swap would have written a second audit row and
        // reloaded a feed nobody asked to change.
        await using var connection = await postgres.OpenAsync();

        var activations = await connection.ExecuteScalarAsync<long>(
            """
            SELECT count(*) FROM audit.events
             WHERE action = 'GTFS_FEED_ACTIVATED' AND entity_id = @FeedVersionId;
            """,
            new { FeedVersionId = feedVersionId });

        Assert.Equal(1, activations);
    }

    [Fact]
    public async Task A_failed_activation_leaves_the_previous_feed_live_and_untouched()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var live = await harness.UploadAndAwaitVerdictAsync(GtfsZipBuilder.Valid("2027-05-01").Build(), bearer);

        await ActivateAsync(harness, live, bearer);
        await harness.WaitForAsync<TransitOptionsResponse>(FortToKottawa, feed => feed.FeedVersion == "2027-05-01");

        var doomed = await harness.UploadAndAwaitVerdictAsync(FeedWithExpressRoute("2027-06-01"), bearer, "doomed.zip");

        // The version row says `validated`; the bytes behind it are gone. Activation gets as far
        // as opening the archive and no further — which is the point: the live tables are never
        // touched before the swap.
        harness.LoseStoredZip(doomed);

        using var response = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/transit/gtfs/uploads/{doomed:D}/activate", bearer);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        Assert.Equal("active", await StatusOfAsync(harness, live, bearer));
        Assert.Equal("validated", await StatusOfAsync(harness, doomed, bearer));

        var options = await harness.GetAsync<TransitOptionsResponse>(FortToKottawa);

        Assert.Equal("2027-05-01", options.FeedVersion);
        Assert.NotEmpty(options.Options);

        // The failed candidate's own route never reached the live tables.
        using var express = await harness.GetAsync("/v1/transit/routes/R255");

        Assert.Equal(HttpStatusCode.NotFound, express.StatusCode);
    }

    [Fact]
    public async Task The_version_history_lists_every_upload_newest_first_with_its_status()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (adminId, bearer) = await harness.AdminAsync();

        var older = await harness.UploadAndAwaitVerdictAsync(GtfsZipBuilder.Valid("2027-07-01").Build(), bearer, "a.zip");
        var newer = await harness.UploadAndAwaitVerdictAsync(FeedWithExpressRoute("2027-08-01"), bearer, "b.zip");

        await ActivateAsync(harness, newer, bearer);

        var page = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, "/v1/admin/transit/gtfs/versions?limit=100", bearer));

        var items = page.GetProperty("items").EnumerateArray().ToArray();

        var newestFirst = items
            .Select(item => item.GetProperty("uploadedAt").GetDateTimeOffset())
            .ToArray();

        Assert.Equal(newestFirst.OrderByDescending(at => at), newestFirst);

        var active = items.Single(item => item.GetProperty("feedVersionId").GetGuid() == newer);

        Assert.Equal("active", active.GetProperty("status").GetString());
        Assert.Equal("b.zip", active.GetProperty("fileName").GetString());
        Assert.Equal(adminId, active.GetProperty("uploadedBy").GetGuid());
        Assert.Equal(64, active.GetProperty("sha256").GetString()!.Length);
        Assert.Equal(2, active.GetProperty("counts").GetProperty("routes").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, active.GetProperty("activatedAt").ValueKind);

        Assert.Equal("validated", items.Single(item => item.GetProperty("feedVersionId").GetGuid() == older)
            .GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_history_pages_and_the_cursor_does_not_drop_a_version()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var uploaded = new List<Guid>();

        for (var index = 0; index < 3; index++)
        {
            uploaded.Add(await harness.UploadAndAwaitVerdictAsync(
                GtfsZipBuilder.Valid($"2028-0{index + 1}-01").Build(), bearer, $"page-{index}.zip"));
        }

        var seen = new List<Guid>();
        string? cursor = null;

        do
        {
            var query = cursor is null ? "?limit=1" : $"?limit=1&cursor={Uri.EscapeDataString(cursor)}";

            var page = await TransitHarness.JsonAsync(
                await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/versions{query}", bearer));

            seen.AddRange(page.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("feedVersionId").GetGuid()));

            cursor = page.GetProperty("cursor").ValueKind == JsonValueKind.Null
                ? null
                : page.GetProperty("cursor").GetString();
        }
        while (cursor is not null);

        Assert.All(uploaded, id => Assert.Contains(id, seen));
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task The_download_redirects_to_a_signed_url_that_serves_the_original_zip()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var zip = GtfsZipBuilder.Valid("2028-09-01").Build();
        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(zip, bearer, "original.zip");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/admin/transit/gtfs/versions/{feedVersionId:D}/download");

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);

        using var redirect = await harness.Unredirected.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);

        var location = redirect.Headers.Location!;

        // The signature is the credential, so the signed URL carries no bearer — which is the whole
        // reason it exists: a browser following a 302 does not send one.
        using var download = await harness.Unredirected.GetAsync(location);

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(zip, await download.Content.ReadAsByteArrayAsync());

        // A tampered signature is refused, not served.
        var tampered = new UriBuilder(location)
        {
            Query = location.Query.TrimStart('?').Replace("sig=", "sig=x", StringComparison.Ordinal),
        }.Uri;

        using var refused = await harness.Unredirected.GetAsync(tampered);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // …and so is a link with no signature at all.
        using var unsigned = await harness.Unredirected.GetAsync(
            $"/v1/admin/transit/gtfs/objects/{feedVersionId:D}");

        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    [Fact]
    public async Task Stable_id_warnings_name_ids_that_disappeared_since_the_active_feed()
    {
        // BR-32.1, as AL-56 redefined it: compared against the currently active feed version, with
        // no external convention document in the loop.
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var live = await harness.UploadAndAwaitVerdictAsync(FeedWithExpressRoute("2029-01-01"), bearer, "wide.zip");

        await ActivateAsync(harness, live, bearer);
        await harness.WaitForAsync<TransitOptionsResponse>(FortToKottawa, feed => feed.FeedVersion == "2029-01-01");

        // The next feed drops route 255 and the halt only it served.
        var narrower = await harness.UploadAndAwaitVerdictAsync(
            GtfsZipBuilder.Valid("2029-02-01").Build(), bearer, "narrow.zip");

        var status = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{narrower:D}", bearer));

        // A warning, never an error: routes do stop running, and BR-32.1 lets warnings through.
        Assert.Equal("validated", status.GetProperty("status").GetString());

        var warnings = status.GetProperty("warnings").EnumerateArray()
            .Select(warning => warning.GetString()!)
            .ToArray();

        Assert.Contains(warnings, warning => warning.Contains("R255", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("PLW", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_mutation_on_this_surface_is_audited()
    {
        // D-35. The interceptor C062 puts in front of these routes records "an admin called this";
        // these rows record what changed, and only the second survives a rename.
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (adminId, bearer) = await harness.AdminAsync();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(
            GtfsZipBuilder.Valid("2029-03-01").Build(), bearer, "audited.zip");

        await AssertAuditedAsync(postgres, feedVersionId, "GTFS_FEED_UPLOADED", adminId);

        // Validation is a queued job, so it has no actor — `audit.events.actor_id` is nullable for
        // exactly this, and the person who caused it is on the upload row.
        await AssertAuditedAsync(postgres, feedVersionId, "GTFS_FEED_VALIDATED", expectedActor: null);

        await ActivateAsync(harness, feedVersionId, bearer);

        await AssertAuditedAsync(postgres, feedVersionId, "GTFS_FEED_ACTIVATED", adminId);
    }

    // -----------------------------------------------------------------------------------------

    private static async Task<JsonElement> ActivateAsync(TransitHarness harness, Guid feedVersionId, string bearer)
    {
        using var response = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}/activate", bearer);

        return await TransitHarness.JsonAsync(response);
    }

    private static async Task<string> StatusOfAsync(TransitHarness harness, Guid feedVersionId, string bearer)
    {
        var status = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}", bearer));

        return status.GetProperty("status").GetString()!;
    }

    private static async Task AssertAuditedAsync(
        PostgresFixture postgres, Guid feedVersionId, string action, Guid? expectedActor)
    {
        await using var connection = await postgres.OpenAsync();

        var rows = await connection.QueryAsync<(Guid? ActorId, string EntityType)>(
            """
            SELECT actor_id, entity_type FROM audit.events
             WHERE action = @Action AND entity_id = @FeedVersionId;
            """,
            new { Action = action, FeedVersionId = feedVersionId });

        var audited = rows.ToArray();

        Assert.NotEmpty(audited);
        Assert.All(audited, row => Assert.Equal("gtfs_feed", row.EntityType));
        Assert.All(audited, row => Assert.Equal(expectedActor, row.ActorId));
    }
}
