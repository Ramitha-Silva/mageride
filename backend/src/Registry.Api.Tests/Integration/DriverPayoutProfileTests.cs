using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using MageRide.Registry.Endpoints;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// The driver's bank &amp; payout profile (AL-58, AL-59) — what replaced D-11's merchant binding.
/// </summary>
/// <remarks>
/// <para>
/// OnePay supports one merchant account per merchant, so the per-driver sub-account D-11 assumed
/// never existed. Where a driver's money goes is this table, a Verification Officer approves it
/// through admin-bff's AL-39 queue, and payout-svc sweeps against the single verified row.
/// </para>
/// <para>
/// Every rule below is fleet-svc's, mirrored deliberately: the platform must not hold a payee's
/// bank details in two shapes, and BR-31.1's versioning is the part that protects somebody's money.
/// </para>
/// </remarks>
[Collection<RegistryCollection>]
public sealed class DriverPayoutProfileTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_driver_with_no_profile_is_told_so_rather_than_given_an_empty_one()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        using var response = await harness.GetAsync(
            "/v1/drivers/payout-profile", harness.Tokens.Driver(driverId));

        // Not an empty 200: "no account on file" is why payout-svc will never sweep them, and the
        // SCR-DA-022a screen says so. An empty shape would look like a saved profile.
        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "payout-profile-not-found");
    }

    [Fact]
    public async Task Submitting_bank_details_creates_a_pending_version()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var saved = await Put(harness, bearer, "Bank of Ceylon", "Kollupitiya", "0071234567", "Nimal Perera");

        Assert.Equal("pending_verification", saved.Status);
        Assert.Equal("0071234567", saved.AccountNo);
        Assert.Null(saved.VerifiedAt);

        using var reread = await harness.GetAsync("/v1/drivers/payout-profile", bearer);
        var read = (await reread.Content.ReadFromJsonAsync<DriverPayoutProfileResponse>(
            MageRide.Shared.Http.MageRideJson.Options))!;

        Assert.Equal(saved.AccountNo, read.AccountNo);
    }

    /// <summary>
    /// BR-31.1's cheap half: a correction to a version nobody has decided on updates in place.
    /// </summary>
    [Fact]
    public async Task Correcting_a_pending_version_does_not_queue_a_second_application()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        await Put(harness, bearer, "Bank of Ceylon", "Kollupitya", "0071234567", "Nimal Perera");
        var fixedUp = await Put(harness, bearer, "Bank of Ceylon", "Kollupitiya", "0071234567", "Nimal Perera");

        Assert.Equal("Kollupitiya", fixedUp.Branch);

        // One row, not two: inserting per keystroke would put a second application for one driver on
        // the officer's queue for every digit fixed.
        Assert.Equal(1, await VersionsAsync(harness, driverId));
    }

    /// <summary>
    /// BR-31.1's expensive half: an edit to a <b>verified</b> profile inserts, and the incumbent
    /// keeps being paid.
    /// </summary>
    [Fact]
    public async Task Editing_a_verified_profile_leaves_the_incumbent_verified_and_payable()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        await Put(harness, bearer, "Bank of Ceylon", "Kollupitiya", "0071234567", "Nimal Perera");
        await VerifyAsync(harness, driverId);

        var edited = await Put(harness, bearer, "Sampath Bank", "Nugegoda", "0079999999", "Nimal Perera");

        Assert.Equal("pending_verification", edited.Status);
        Assert.Equal(2, await VersionsAsync(harness, driverId));

        // The account payout-svc sweeps to is still the one an officer approved — a driver who
        // mistypes on Friday is still paid on Sunday. `ux_driver_payout_verified` admits exactly one.
        await using var connection = await harness.OpenAsync();

        var verified = await connection.QuerySingleAsync<string>(
            """
            SELECT account_no FROM registry.driver_payout_profiles
             WHERE driver_id = @Id AND status = 'verified';
            """,
            new { Id = driverId });

        Assert.Equal("0071234567", verified);
    }

    [Fact]
    public async Task Bank_details_are_required_and_are_bounded()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        using var response = await harness.PutAsync(
            "/v1/drivers/payout-profile",
            new { bank = "Bank of Ceylon", branch = "  ", accountNo = "0071234567", accountHolderName = "Nimal" },
            harness.Tokens.Driver(driverId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A driver writes their own account and there is no route by which they could write another's.</summary>
    [Fact]
    public async Task The_profile_is_always_the_callers_own()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var alice = await harness.CreateDriverAsync();
        var bob = await harness.CreateDriverAsync();

        await Put(harness, harness.Tokens.Driver(alice), "Bank of Ceylon", "Kollupitiya", "0071111111", "Alice");
        await Put(harness, harness.Tokens.Driver(bob), "Sampath Bank", "Nugegoda", "0072222222", "Bob");

        // The subject comes from the token and the path carries no id, so the two cannot cross.
        using var read = await harness.GetAsync("/v1/drivers/payout-profile", harness.Tokens.Driver(alice));
        var mine = (await read.Content.ReadFromJsonAsync<DriverPayoutProfileResponse>(
            MageRide.Shared.Http.MageRideJson.Options))!;

        Assert.Equal("0071111111", mine.AccountNo);
        Assert.Equal(1, await VersionsAsync(harness, alice));
        Assert.Equal(1, await VersionsAsync(harness, bob));
    }

    /// <summary>
    /// The evidence an officer decides on. AL-59's LankaQR slot is the same route with a different
    /// <c>kind</c> — a driver's own QR is now theirs, not a MageRide merchant id (AL-57).
    /// </summary>
    [Fact]
    public async Task An_uploaded_document_attaches_to_the_pending_version()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        await Put(harness, bearer, "Bank of Ceylon", "Kollupitiya", "0071234567", "Nimal Perera");

        var attached = await UploadAsync(harness, bearer, "bank_statement", [1, 2, 3, 4]);

        Assert.NotNull(attached.ProofDocId);
        Assert.Null(attached.LankaqrDocId);

        // The row lands in docs.uploads like every other document on the platform, so NFR-28's
        // retention deadline sweeps it and admin-bff resolves it by the same id.
        await using var connection = await harness.OpenAsync();

        var kind = await connection.QuerySingleAsync<string>(
            "SELECT kind FROM docs.uploads WHERE id = @Id;", new { Id = Guid.Parse(attached.ProofDocId!) });

        Assert.Equal("bank_statement", kind);

        var withQr = await UploadAsync(harness, bearer, "lankaqr_code", [9, 9]);

        // Two independent slots: a driver's LankaQR does not displace their proof of account.
        Assert.Equal(attached.ProofDocId, withQr.ProofDocId);
        Assert.NotNull(withQr.LankaqrDocId);
        Assert.Equal(1, await VersionsAsync(harness, driverId));
    }

    /// <summary>
    /// NFR-28 expires the evidence and must never touch the payment QR (Δ D-36).
    /// </summary>
    /// <remarks>
    /// <b>This is the assertion that keeps drivers getting paid.</b> AL-59 makes a driver's own
    /// bank-app LankaQR what a passenger scans to pay them on <em>every ride</em> — live payment
    /// infrastructure, not a document somebody checked once. Before D-36 was wired every payout
    /// document was stamped with a 90-day deadline including the QR; nothing swept it, so the bug
    /// was invisible. Wiring a real bucket lifecycle rule would have made it start deleting
    /// drivers' QR codes 90 days after upload, one driver at a time, with nothing to see.
    /// </remarks>
    [Fact]
    public async Task The_bank_statement_expires_and_the_lankaqr_never_does()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        await Put(harness, bearer, "Bank of Ceylon", "Kollupitiya", "0071234567", "Nimal Perera");

        var withProof = await UploadAsync(harness, bearer, "bank_statement", [1, 2, 3]);
        var withQr = await UploadAsync(harness, bearer, "lankaqr_code", [4, 5, 6]);

        await using var connection = await harness.OpenAsync();

        var proofDeadline = await connection.QuerySingleAsync<DateTimeOffset?>(
            "SELECT auto_delete_at FROM docs.uploads WHERE id = @Id;",
            new { Id = Guid.Parse(withProof.ProofDocId!) });

        var qrDeadline = await connection.QuerySingleAsync<DateTimeOffset?>(
            "SELECT auto_delete_at FROM docs.uploads WHERE id = @Id;",
            new { Id = Guid.Parse(withQr.LankaqrDocId!) });

        Assert.NotNull(proofDeadline);
        Assert.Null(qrDeadline);

        // And the same split in the object key, which is what the bucket's lifecycle rule matches
        // on — `ephemeral/` is expired, `retained/` is not.
        var storage = await connection.QueryAsync<string>(
            "SELECT storage_url FROM docs.uploads WHERE owner_id = @Id ORDER BY created_at;",
            new { Id = driverId });

        var urls = storage.ToArray();

        Assert.Contains("/ephemeral/", urls[0], StringComparison.Ordinal);
        Assert.Contains("/retained/", urls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_with_no_account_to_attach_it_to_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        using var response = await PostDocumentAsync(
            harness, harness.Tokens.Driver(driverId), "bank_statement", [1, 2, 3]);

        // Evidence of nothing. The bank details come first so the officer has an account to check
        // the statement against.
        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "payout-profile-not-found");
    }

    [Fact]
    public async Task An_unknown_document_kind_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        await Put(harness, bearer, "Bank of Ceylon", "Kollupitiya", "0071234567", "Nimal Perera");

        using var response = await PostDocumentAsync(harness, bearer, "selfie", [1, 2, 3]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<DriverPayoutProfileResponse> UploadAsync(
        RegistryHarness harness, string bearer, string kind, byte[] bytes)
    {
        using var response = await PostDocumentAsync(harness, bearer, kind, bytes);

        Assert.True(
            response.IsSuccessStatusCode,
            $"POST payout document returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        // The 201 carries the document, not the profile (registry.yaml). Read the profile back so the
        // assertion is on what the officer's queue and the driver's screen will actually load.
        using var read = await harness.GetAsync("/v1/drivers/payout-profile", bearer);

        return (await read.Content.ReadFromJsonAsync<DriverPayoutProfileResponse>(
            MageRide.Shared.Http.MageRideJson.Options))!;
    }

    private static async Task<HttpResponseMessage> PostDocumentAsync(
        RegistryHarness harness, string bearer, string kind, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        form.Add(new StringContent(kind), "kind");
        form.Add(file, "file", "proof.jpg");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/drivers/payout-profile/documents")
        {
            Content = form,
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return await harness.Client.SendAsync(request);
    }

    // ---------------------------------------------------------------------------------------
    // The Verification Officer's decision (AL-58) — /v1/internal/drivers/**, called by admin-bff.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The approval that lets payout-svc pay the driver, and the ordering the index demands.
    /// </summary>
    [Fact]
    public async Task Approving_an_edit_supersedes_the_incumbent_and_verifies_the_replacement()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var officerId = await OfficerAsync(harness);

        await Put(harness, bearer, "Bank of Ceylon", "Kollupitiya", "0071234567", "Nimal Perera");
        await ApproveAsync(harness, driverId, officerId);

        // The edit BR-31.1 exists for: a driver changing banks.
        await Put(harness, bearer, "Sampath Bank", "Nugegoda", "0079999999", "Nimal Perera");

        var decided = await ApproveAsync(harness, driverId, officerId);

        Assert.Equal("verified", decided.Status);
        Assert.Equal("0079999999", decided.AccountNo);
        Assert.NotNull(decided.VerifiedAt);

        // Supersede THEN verify. The reverse order fails on ux_driver_payout_verified, which admits
        // one verified row per driver — migration 0316 says so on the index's own comment rather
        // than leaving it to a 23505.
        Assert.Equal(["verified", "superseded"], await StatusesAsync(harness, driverId));
    }

    /// <summary>A second Approve must not rewrite the record of when a decision was made.</summary>
    [Fact]
    public async Task Approving_twice_decides_once_and_re_stamps_nothing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var officerId = await OfficerAsync(harness);

        await Put(harness, harness.Tokens.Driver(driverId), "Bank of Ceylon", "Kollupitiya", "0071234567", "N P");

        var first = await ApproveAsync(harness, driverId, officerId);
        var second = await ApproveAsync(harness, driverId, await OfficerAsync(harness));

        // Same verdict, same instant, and — crucially — a different officer pressing it changes
        // neither. `verified_by` goes on naming whoever actually decided.
        Assert.Equal(first.VerifiedAt, second.VerifiedAt);
        Assert.Equal(["verified"], await StatusesAsync(harness, driverId));

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            officerId,
            await connection.QuerySingleAsync<Guid>(
                "SELECT verified_by FROM registry.driver_payout_profiles WHERE driver_id = @Id;",
                new { Id = driverId }));
    }

    /// <summary>
    /// The rule that is about somebody's wages: refusing an edit never stops the money already flowing.
    /// </summary>
    [Fact]
    public async Task Rejecting_an_edit_leaves_the_incumbent_verified_and_payable()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var officerId = await OfficerAsync(harness);

        await Put(harness, bearer, "Bank of Ceylon", "Kollupitiya", "0071234567", "Nimal Perera");
        await ApproveAsync(harness, driverId, officerId);

        await Put(harness, bearer, "Sampath Bank", "Nugegoda", "0079999999", "Someone Else");

        using var response = await harness.PostInternalAsync(
            $"/v1/internal/drivers/{driverId:D}/payout-profile/reject",
            new { officerId = officerId.ToString(), reason = "Account holder name does not match the NIC." });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        Assert.Equal(["rejected", "verified"], await StatusesAsync(harness, driverId));

        // Sunday's sweep still goes to the account an officer approved.
        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            "0071234567",
            await connection.QuerySingleAsync<string>(
                """
                SELECT account_no FROM registry.driver_payout_profiles
                 WHERE driver_id = @Id AND status = 'verified';
                """,
                new { Id = driverId }));
    }

    [Fact]
    public async Task A_decided_version_cannot_be_rejected_and_a_reason_is_mandatory()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var officerId = await OfficerAsync(harness);

        await Put(harness, harness.Tokens.Driver(driverId), "Bank of Ceylon", "Kollupitiya", "0071234567", "N P");

        using var noReason = await harness.PostInternalAsync(
            $"/v1/internal/drivers/{driverId:D}/payout-profile/reject",
            new { officerId = officerId.ToString(), reason = "   " });

        // Shown verbatim on SCR-DA-022a. "Rejected" with nothing to read leaves a driver unable to
        // fix the one thing standing between them and their money.
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        await ApproveAsync(harness, driverId, officerId);

        using var afterDecision = await harness.PostInternalAsync(
            $"/v1/internal/drivers/{driverId:D}/payout-profile/reject",
            new { officerId = officerId.ToString(), reason = "changed my mind" });

        // Not a no-op like a second Approve: writing a refusal onto an approved version would stop
        // a driver's payouts by a mis-click.
        Assert.Equal(HttpStatusCode.Conflict, afterDecision.StatusCode);
    }

    [Fact]
    public async Task There_is_nothing_to_decide_for_a_driver_who_never_submitted_one()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        using var response = await harness.PostInternalAsync(
            $"/v1/internal/drivers/{driverId:D}/payout-profile/approve",
            new { officerId = (await OfficerAsync(harness)).ToString() });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "payout-profile-not-found");
    }

    /// <summary>The decision plane is service-to-service and carries no bearer to fall back on.</summary>
    [Fact]
    public async Task The_decision_plane_refuses_a_caller_with_no_internal_key()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        await Put(harness, harness.Tokens.Driver(driverId), "Bank of Ceylon", "Kollupitiya", "0071234567", "N P");

        using var response = await harness.PostInternalAsync(
            $"/v1/internal/drivers/{driverId:D}/payout-profile/approve",
            new { officerId = (await OfficerAsync(harness)).ToString() },
            apiKey: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(["pending_verification"], await StatusesAsync(harness, driverId));
    }

    /// <summary>
    /// A real Verification Officer. <c>verified_by</c> is an FK onto <c>iam.users</c>, which is the
    /// point — a column naming who authorised money to be sent somewhere must name somebody who
    /// exists. In production the id is the officer's own token subject, forwarded by admin-bff.
    /// </summary>
    private static async Task<Guid> OfficerAsync(RegistryHarness harness)
    {
        var officerId = Guid.CreateVersion7();

        await using var connection = await harness.OpenAsync();

        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, email, role) VALUES (@Id, @Email, 'verification_officer');",
            new { Id = officerId, Email = $"{officerId:N}@officer.test" });

        return officerId;
    }

    private static async Task<DriverPayoutDecisionResponse> ApproveAsync(
        RegistryHarness harness, Guid driverId, Guid officerId)
    {
        using var response = await harness.PostInternalAsync(
            $"/v1/internal/drivers/{driverId:D}/payout-profile/approve",
            new { officerId = officerId.ToString() });

        Assert.True(
            response.IsSuccessStatusCode,
            $"approve returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<DriverPayoutDecisionResponse>(
            MageRide.Shared.Http.MageRideJson.Options))!;
    }

    /// <summary>Every version, newest first — where BR-31.1 is actually visible.</summary>
    private static async Task<IReadOnlyList<string>> StatusesAsync(RegistryHarness harness, Guid driverId)
    {
        await using var connection = await harness.OpenAsync();

        var rows = await connection.QueryAsync<string>(
            """
            SELECT status FROM registry.driver_payout_profiles
             WHERE driver_id = @Id ORDER BY created_at DESC;
            """,
            new { Id = driverId });

        return [.. rows];
    }

    private static async Task<DriverPayoutProfileResponse> Put(
        RegistryHarness harness, string bearer, string bank, string branch, string accountNo, string holder)
    {
        using var response = await harness.PutAsync(
            "/v1/drivers/payout-profile",
            new { bank, branch, accountNo, accountHolderName = holder },
            bearer);

        Assert.True(
            response.IsSuccessStatusCode,
            $"PUT payout-profile returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<DriverPayoutProfileResponse>(
            MageRide.Shared.Http.MageRideJson.Options))!;
    }

    /// <summary>What the Verification Officer's approval does, without standing admin-bff up.</summary>
    private static async Task VerifyAsync(RegistryHarness harness, Guid driverId)
    {
        await using var connection = await harness.OpenAsync();

        await connection.ExecuteAsync(
            """
            UPDATE registry.driver_payout_profiles
               SET status = 'verified', verified_at = now()
             WHERE driver_id = @Id AND status = 'pending_verification';
            """,
            new { Id = driverId });
    }

    private static async Task<int> VersionsAsync(RegistryHarness harness, Guid driverId)
    {
        await using var connection = await harness.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM registry.driver_payout_profiles WHERE driver_id = @Id;",
            new { Id = driverId });
    }
}
