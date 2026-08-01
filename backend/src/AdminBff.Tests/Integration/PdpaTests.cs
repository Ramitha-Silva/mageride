using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.AdminBff.Tests.Integration;

/// <summary>
/// C065's data-rights half: E-06's export and erasure workflow (US-1.8, `pdpa` schema §16).
/// </summary>
/// <remarks>
/// <b>Two of C065's four definition-of-done items are proved here</b> — "an export request produces
/// a downloadable ZIP within the workflow and expires its signed URL" and "an erasure request
/// honours the statutory hold list and leaves the audit subset intact". The second is asserted
/// against <c>iam.users</c>, <c>iam.emergency_contacts</c>, <c>iam.saved_addresses</c>,
/// <c>iam.phone_lookups</c>, <c>iam.sessions</c>, <c>billing.journal_postings</c> and
/// <c>audit.events</c> — what an erasure removes and what it must not touch are equally the claim.
/// </remarks>
[Collection(AdminBffCollection.Name)]
[Trait("Category", "FinancePdpa")]
public sealed class PdpaTests(PostgresFixture postgres)
{
    // ---------------------------------------------------------------------------------------
    // The export (DoD 2)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// DoD: "an export request produces a downloadable ZIP within the workflow and expires its
    /// signed URL".
    /// </summary>
    [Fact]
    public async Task An_export_is_fulfilled_as_a_readable_zip_behind_an_expiring_link()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var subject = await harness.Seed.PdpaSubjectAsync();
        var subjectBearer = harness.Tokens.Issue(subject.UserId, MageRideApps.Passenger, MageRideRoles.Passenger);

        using var accepted = await harness.SendAsync(
            HttpMethod.Post, "/v1/pdpa/export", subjectBearer);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        using var acceptedBody = await harness.ReadJsonAsync(accepted);
        var requestId = acceptedBody.RootElement.GetProperty("requestId").GetGuid();

        Assert.Equal($"/v1/pdpa/{requestId:D}", accepted.Headers.Location?.ToString());

        // The statutory deadline is the column's own 30-day default (migration 1306), not a figure
        // this service invents — so the two places that open a request agree about the clock.
        var dueBy = acceptedBody.RootElement.GetProperty("dueBy").GetDateTimeOffset();

        Assert.InRange(dueBy - DateTimeOffset.UtcNow, TimeSpan.FromDays(29), TimeSpan.FromDays(31));

        var actorId = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        using var fulfilled = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/pdpa/{requestId:D}/fulfill", harness.Tokens.Admin(actorId), new { });

        using var fulfilledBody = await harness.ReadJsonAsync(fulfilled);

        // The ledger and the audit trail are retained by statute, so a complete export of an account
        // that has either is still a FulfilledHold — which is the honest status.
        Assert.Equal("FulfilledHold", fulfilledBody.RootElement.GetProperty("status").GetString());

        var artifact = await harness.Seed.PdpaArtifactAsync(requestId);

        Assert.NotNull(artifact);
        Assert.Equal("export_zip", artifact!.Value.Kind);
        Assert.NotNull(artifact.Value.Sha256);
        Assert.Equal(32, artifact.Value.Sha256!.Length);

        // The subject's own status read carries the download and the instant it dies.
        using var status = await harness.GetAsync($"/v1/pdpa/{requestId:D}", subjectBearer);
        using var statusBody = await harness.ReadJsonAsync(status);

        var downloadUrl = statusBody.RootElement.GetProperty("downloadUrl").GetString();
        var expiresAt = statusBody.RootElement.GetProperty("downloadExpiresAt").GetDateTimeOffset();

        Assert.NotNull(downloadUrl);
        Assert.InRange(expiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(16));
        Assert.Contains("expires=", downloadUrl!, StringComparison.Ordinal);
        Assert.Contains("signature=", downloadUrl, StringComparison.Ordinal);

        // A fresh link on every read, so a subject who was too slow simply asks again.
        using var again = await harness.GetAsync($"/v1/pdpa/{requestId:D}", subjectBearer);
        using var againBody = await harness.ReadJsonAsync(again);

        Assert.True(
            againBody.RootElement.GetProperty("downloadExpiresAt").GetDateTimeOffset() >= expiresAt,
            "The download link is minted per read and must not be a stored value that ages.");

        // The archive itself: openable, self-describing, and about this person.
        var bytes = await ReadArchiveAsync(artifact.Value.StorageUrl);

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        Assert.Contains(zip.Entries, entry => entry.FullName == "README.txt");
        Assert.Contains(zip.Entries, entry => entry.FullName == "manifest.json");
        Assert.Contains(zip.Entries, entry => entry.FullName == "profile.json");
        Assert.Contains(zip.Entries, entry => entry.FullName == "rides.json");

        using var manifest = JsonDocument.Parse(await ReadEntryAsync(zip, "manifest.json"));

        Assert.Equal(subject.UserId, manifest.RootElement.GetProperty("subjectId").GetGuid());
        Assert.Equal(requestId, manifest.RootElement.GetProperty("requestId").GetGuid());

        var profile = await ReadEntryAsync(zip, "profile.json");

        Assert.Contains(subject.Phone, profile, StringComparison.Ordinal);
        Assert.Contains(subject.Name, profile, StringComparison.Ordinal);

        // The paid ride the fixture wrote is in the archive; the export is about the person, not
        // about one table.
        Assert.Contains(subject.RideId.ToString(), await ReadEntryAsync(zip, "rides.json"), StringComparison.Ordinal);
    }

    /// <summary>Two 30-day clocks against one obligation is a 409, not a second row.</summary>
    [Fact]
    public async Task A_second_open_request_of_the_same_kind_is_refused()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var subject = await harness.Seed.PdpaSubjectAsync();
        var bearer = harness.Tokens.Issue(subject.UserId, MageRideApps.Passenger, MageRideRoles.Passenger);

        using var first = await harness.SendAsync(HttpMethod.Post, "/v1/pdpa/export", bearer);
        using var second = await harness.SendAsync(HttpMethod.Post, "/v1/pdpa/export", bearer);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // An erasure is a different obligation with a different clock, so it is not blocked by an
        // open export.
        using var erasure = await harness.SendAsync(HttpMethod.Post, "/v1/pdpa/erasure", bearer);

        Assert.Equal(HttpStatusCode.Accepted, erasure.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // The erasure (DoD 3)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// DoD: "an erasure request honours the statutory hold list and leaves the audit subset intact".
    /// </summary>
    [Fact]
    public async Task An_erasure_anonymises_the_identity_and_retains_what_a_statute_requires()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var subject = await harness.Seed.PdpaSubjectAsync();
        var subjectBearer = harness.Tokens.Issue(subject.UserId, MageRideApps.Passenger, MageRideRoles.Passenger);

        using var accepted = await harness.SendAsync(HttpMethod.Post, "/v1/pdpa/erasure", subjectBearer);
        using var acceptedBody = await harness.ReadJsonAsync(accepted);

        var requestId = acceptedBody.RootElement.GetProperty("requestId").GetGuid();

        var previewed = acceptedBody.RootElement.GetProperty("holdReasons")
            .EnumerateArray().Select(code => code.GetString()).ToArray();

        Assert.Contains("financial-records", previewed);
        Assert.DoesNotContain("active-ride", previewed);
        Assert.DoesNotContain(
            "open-dispute", previewed);

        var actorId = await harness.Seed.InternalUserAsync(MageRideRoles.SuperAdmin);
        var before = await harness.Seed.UserAfterErasureAsync(subject.UserId);

        using var fulfilled = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/pdpa/{requestId:D}/fulfill",
            harness.Tokens.SuperAdmin(actorId),
            new { });

        using var fulfilledBody = await harness.ReadJsonAsync(fulfilled);

        Assert.Equal("FulfilledHold", fulfilledBody.RootElement.GetProperty("status").GetString());
        Assert.Contains(
            "financial-records", fulfilledBody.RootElement.GetProperty("holdReason").GetString()!,
            StringComparison.Ordinal);

        var after = await harness.Seed.UserAfterErasureAsync(subject.UserId);

        // Removed: everything that identifies the person.
        Assert.Null(after.Phone);
        Assert.Null(after.FirstName);
        Assert.Null(after.PhotoUrl);
        Assert.Null(after.EmergencyContactPhone);
        Assert.NotNull(after.AnonymisedAt);
        Assert.Equal(0, after.EmergencyContacts);
        Assert.Equal(0, after.SavedAddresses);
        Assert.Equal(0, after.PhoneLookups);
        Assert.Equal(0, after.LiveSessions);

        // `ck_users_credential` requires one credential column, and both are UNIQUE — so the email
        // becomes a per-account address in RFC 2606's reserved `.invalid` domain rather than being
        // cleared or sharing a placeholder that would block every erasure after the first.
        Assert.EndsWith("@pdpa.invalid", after.Email!, StringComparison.Ordinal);
        Assert.DoesNotContain(subject.Email, after.Email!, StringComparison.Ordinal);

        // Retained: the account row itself, so every reference still resolves — and the two subsets
        // a statute requires.
        Assert.Equal(before.Rides, after.Rides);
        Assert.Equal(before.LedgerPostings, after.LedgerPostings);
        Assert.True(
            after.AuditEvents >= before.AuditEvents,
            "audit.events is append-only and the fulfilment writes to it. An erasure that deleted the "
            + "record of the erasure would leave the platform unable to prove it complied (D-35).");

        var audit = await harness.Seed.AuditRowsAsync(requestId);

        Assert.Contains(audit, row => row.Action == AdminAuditActions.PdpaRequested);

        var fulfilment = Assert.Single(audit, row => row.Action == AdminAuditActions.PdpaFulfilled);

        Assert.Equal(AdminAuditActions.PdpaRequestEntity, fulfilment.EntityType);
        Assert.Equal(actorId, fulfilment.ActorId);
        Assert.Contains("financial-records", fulfilment.After!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A blocking hold refuses the fulfilment rather than recording a partial one: a live operation
    /// lifts on its own, and claiming the obligation was met while a passenger is in a car would be
    /// a false compliance statement.
    /// </summary>
    [Fact]
    public async Task An_erasure_held_by_a_live_ride_is_refused_and_changes_nothing()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var subject = await harness.Seed.PdpaSubjectAsync(withBlockingHold: true);
        var subjectBearer = harness.Tokens.Issue(subject.UserId, MageRideApps.Passenger, MageRideRoles.Passenger);

        using var accepted = await harness.SendAsync(HttpMethod.Post, "/v1/pdpa/erasure", subjectBearer);
        using var acceptedBody = await harness.ReadJsonAsync(accepted);

        var requestId = acceptedBody.RootElement.GetProperty("requestId").GetGuid();

        Assert.Contains(
            "active-ride",
            acceptedBody.RootElement.GetProperty("holdReasons").EnumerateArray().Select(code => code.GetString()));

        using var refused = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/pdpa/{requestId:D}/fulfill",
            harness.Tokens.SuperAdmin(await harness.Seed.InternalUserAsync(MageRideRoles.SuperAdmin)),
            new { });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var after = await harness.Seed.UserAfterErasureAsync(subject.UserId);

        Assert.Null(after.AnonymisedAt);
        Assert.Equal(subject.Phone, after.Phone);
        Assert.Equal(1, after.EmergencyContacts);

        // The refusal changed nothing, so it wrote no audit row either — only successes are audited.
        Assert.DoesNotContain(
            await harness.Seed.AuditRowsAsync(requestId),
            row => row.Action == AdminAuditActions.PdpaFulfilled);
    }

    /// <summary>A refusal carries the reason the subject is shown, and is not a hold.</summary>
    [Fact]
    public async Task A_rejection_records_its_own_reason_and_closes_the_request()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var subject = await harness.Seed.PdpaSubjectAsync();
        var subjectBearer = harness.Tokens.Issue(subject.UserId, MageRideApps.Passenger, MageRideRoles.Passenger);

        using var accepted = await harness.SendAsync(HttpMethod.Post, "/v1/pdpa/erasure", subjectBearer);
        using var acceptedBody = await harness.ReadJsonAsync(accepted);

        var requestId = acceptedBody.RootElement.GetProperty("requestId").GetGuid();
        var bearer = harness.Tokens.Admin(await harness.Seed.InternalUserAsync(MageRideRoles.Admin));

        using var noReason = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/pdpa/{requestId:D}/reject", bearer, new { });

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        using var rejected = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/pdpa/{requestId:D}/reject",
            bearer,
            new { reason = "Identity could not be verified" });

        using var body = await harness.ReadJsonAsync(rejected);

        Assert.Equal("Rejected", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Identity could not be verified", body.RootElement.GetProperty("rejectionReason").GetString());

        // The account is exactly as its owner left it — a refused erasure erases nothing.
        Assert.Null((await harness.Seed.UserAfterErasureAsync(subject.UserId)).AnonymisedAt);

        // And a decided request cannot be decided again: fulfilling an already-rejected erasure
        // would anonymise an account whose owner was told their request was refused.
        using var thenFulfil = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/pdpa/{requestId:D}/fulfill", bearer, new { });

        Assert.Equal(HttpStatusCode.Conflict, thenFulfil.StatusCode);
    }

    /// <summary>An erased passenger's directory row says `deleted`, which nothing could produce before.</summary>
    [Fact]
    public async Task An_erased_passenger_reads_as_deleted_in_the_directory()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var subject = await harness.Seed.PdpaSubjectAsync();
        var subjectBearer = harness.Tokens.Issue(subject.UserId, MageRideApps.Passenger, MageRideRoles.Passenger);

        using var accepted = await harness.SendAsync(HttpMethod.Post, "/v1/pdpa/erasure", subjectBearer);
        using var acceptedBody = await harness.ReadJsonAsync(accepted);

        var adminBearer = harness.Tokens.SuperAdmin(await harness.Seed.InternalUserAsync(MageRideRoles.SuperAdmin));

        using var before = await harness.GetAsync($"/v1/admin/passengers/{subject.UserId:D}", adminBearer);
        using var beforeBody = await harness.ReadJsonAsync(before);

        Assert.Equal("active", beforeBody.RootElement.GetProperty("profile").GetProperty("status").GetString());

        using var fulfilled = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/pdpa/{acceptedBody.RootElement.GetProperty("requestId").GetGuid():D}/fulfill",
            adminBearer,
            new { });

        Assert.True(fulfilled.IsSuccessStatusCode);

        using var after = await harness.GetAsync($"/v1/admin/passengers/{subject.UserId:D}", adminBearer);
        using var afterBody = await harness.ReadJsonAsync(after);

        Assert.Equal("deleted", afterBody.RootElement.GetProperty("profile").GetProperty("status").GetString());
    }

    // ---------------------------------------------------------------------------------------
    // The surface itself
    // ---------------------------------------------------------------------------------------

    /// <summary>Somebody else's request is a 404, never a 403.</summary>
    /// <remarks>
    /// Telling the two apart would make the route an oracle over whether a given id is somebody's
    /// live erasure request — the same house rule wallet-svc states for credit transfers.
    /// </remarks>
    [Fact]
    public async Task Another_subjects_request_is_invisible()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var mine = await harness.Seed.PdpaSubjectAsync();
        var theirs = await harness.Seed.PdpaSubjectAsync();

        using var accepted = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/pdpa/export",
            harness.Tokens.Issue(theirs.UserId, MageRideApps.Passenger, MageRideRoles.Passenger));

        using var acceptedBody = await harness.ReadJsonAsync(accepted);

        using var response = await harness.GetAsync(
            $"/v1/pdpa/{acceptedBody.RootElement.GetProperty("requestId").GetGuid():D}",
            harness.Tokens.Issue(mine.UserId, MageRideApps.Passenger, MageRideRoles.Passenger));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The subject routes are the one family AL-02's prefix fence admits, and they are open to end
    /// users by design — which is the opposite of every other route on this service.
    /// </summary>
    [Fact]
    public async Task The_data_subject_routes_are_reachable_by_an_end_user_and_are_the_only_ones_that_are()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var subject = await harness.Seed.PdpaSubjectAsync();

        using var permitted = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/pdpa/export",
            harness.Tokens.Issue(subject.UserId, MageRideApps.Passenger, MageRideRoles.Passenger));

        Assert.Equal(HttpStatusCode.Accepted, permitted.StatusCode);

        // Everything else on the service stays shut to them (AL-02), including the operator half of
        // this very family.
        using var refused = await harness.GetAsync(
            "/v1/admin/pdpa/queue", harness.Tokens.Driver(subject.UserId));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // And the prefix fence admits exactly these three routes and no more.
        var subjectRoutes = harness.Routes
            .Select(route => route.RoutePattern.RawText!)
            .Where(route => route.StartsWith("/v1/pdpa", StringComparison.Ordinal))
            .Distinct()
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["/v1/pdpa/erasure", "/v1/pdpa/export", "/v1/pdpa/{requestId:guid}"],
            subjectRoutes);
    }

    /// <summary>Unauthenticated is 401 everywhere on the data-subject family too.</summary>
    [Fact]
    public async Task The_data_subject_routes_deny_by_default()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/v1/pdpa/export"),
                     (HttpMethod.Post, "/v1/pdpa/erasure"),
                     (HttpMethod.Get, $"/v1/pdpa/{Guid.CreateVersion7():D}"),
                 })
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await harness.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>The operator queue is open requests by deadline, with an open erasure's live holds.</summary>
    [Fact]
    public async Task The_admin_queue_orders_by_deadline_and_carries_the_live_hold_list()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var subject = await harness.Seed.PdpaSubjectAsync(withBlockingHold: true);

        using var accepted = await harness.SendAsync(
            HttpMethod.Post,
            "/v1/pdpa/erasure",
            harness.Tokens.Issue(subject.UserId, MageRideApps.Passenger, MageRideRoles.Passenger));

        using var acceptedBody = await harness.ReadJsonAsync(accepted);
        var requestId = acceptedBody.RootElement.GetProperty("requestId").GetGuid();

        using var queue = await harness.GetAsync(
            "/v1/admin/pdpa/queue?limit=500",
            harness.Tokens.Admin(await harness.Seed.InternalUserAsync(MageRideRoles.Admin)));

        using var body = await harness.ReadJsonAsync(queue);

        var row = body.RootElement.EnumerateArray()
            .Single(entry => entry.GetProperty("requestId").GetGuid() == requestId);

        Assert.Equal("erasure", row.GetProperty("kind").GetString());
        Assert.Equal(subject.UserId, row.GetProperty("subjectId").GetGuid());

        var holds = row.GetProperty("holds").EnumerateArray().ToArray();

        var blocking = holds.Single(hold => hold.GetProperty("code").GetString() == "active-ride");

        Assert.True(blocking.GetProperty("blocking").GetBoolean());
        Assert.True(blocking.GetProperty("count").GetInt32() >= 1);

        var retained = holds.Single(hold => hold.GetProperty("code").GetString() == "financial-records");

        Assert.False(
            retained.GetProperty("blocking").GetBoolean(),
            "A ledger posting is retained under a statute and bounds the erasure; it does not stop it. "
            + "Treating the two alike would refuse every erasure for ever — every account has a ledger.");
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>Reads the stored archive back, whichever store this deployment got.</summary>
    /// <remarks>
    /// With no <c>Storage:S3:*</c> configured the kernel's composite store writes to the filesystem
    /// and the pointer is a <c>file://</c> URL — which is exactly the fallback path a laptop and this
    /// suite run on, and reading it here is what makes "produces a downloadable ZIP" a claim about
    /// bytes rather than about a row.
    /// </remarks>
    private static async Task<byte[]> ReadArchiveAsync(string storageUrl)
    {
        Assert.StartsWith("file://", storageUrl, StringComparison.Ordinal);

        return await File.ReadAllBytesAsync(new Uri(storageUrl).LocalPath);
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        await using var stream = archive.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return await reader.ReadToEndAsync();
    }
}
