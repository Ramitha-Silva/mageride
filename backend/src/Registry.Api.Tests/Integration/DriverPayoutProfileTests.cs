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
