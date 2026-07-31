using System.Net;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Endpoints;
using MageRide.Fleet.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Fleet.Tests.Integration;

/// <summary>
/// AL-49 / BR-31.1 — the versioned bank profile, and the definition-of-done claim that editing a
/// verified one re-enters pending <b>while Paid subscriptions keep collecting against the last
/// verified snapshot</b>.
/// </summary>
[Collection<FleetCollection>]
public sealed class PayoutProfileTests(PostgresFixture postgres)
{
    private static readonly object FirstDraft = new
    {
        bank = "Bank of Ceylon",
        branch = "Nugegoda",
        accountNo = "0071234567",
        accountHolderName = "Ruhunu Express (Pvt) Ltd",
    };

    [Fact]
    public async Task An_organisation_with_no_profile_has_nothing_to_read()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        using var response = await harness.GetAsync($"/v1/fleets/{fleet.FleetId}/payout-profile", fleet.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("payout-profile-not-found", problem.Code);
    }

    /// <summary>
    /// The profile can be submitted while the organisation is still PENDING — deliberately.
    /// </summary>
    /// <remarks>
    /// AL-49 puts the payout documents in the same <c>documents[]</c> the Verification Officer
    /// reads before approving the org. A gate here would mean the officer had to approve an
    /// organisation before seeing the evidence they are meant to approve it on.
    /// </remarks>
    [Fact]
    public async Task A_pending_organisation_can_submit_its_bank_details()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        var profile = await harness.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile", FirstDraft, fleet.OwnerBearer);

        Assert.Equal(PayoutProfileStatuses.PendingVerification, profile.Status);
        Assert.Equal("0071234567", profile.AccountNo);
        Assert.Null(profile.VerifiedAt);
    }

    /// <summary>
    /// Correcting a profile nobody has decided on yet rewrites it — it does not fork.
    /// </summary>
    /// <remarks>
    /// Nothing is collecting against a pending version, so a version per keystroke would put a
    /// second application for one organisation on the officer's queue for every digit fixed. A
    /// version marks a verification decision.
    /// </remarks>
    [Fact]
    public async Task Correcting_a_pending_profile_updates_it_in_place()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        await harness.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile", FirstDraft, fleet.OwnerBearer);

        var corrected = await harness.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile",
            new
            {
                bank = "Bank of Ceylon",
                branch = "Nugegoda Super Grade",
                accountNo = "0071234567",
                accountHolderName = "Ruhunu Express (Pvt) Ltd",
            },
            fleet.OwnerBearer);

        Assert.Equal("Nugegoda Super Grade", corrected.Branch);

        var versions = await harness.PayoutVersionsAsync(fleet.FleetId);
        Assert.Single(versions);
    }

    /// <summary>
    /// The definition-of-done item: an edit re-enters pending and the money keeps going where an
    /// officer said it should.
    /// </summary>
    [Fact]
    public async Task Editing_a_verified_profile_forks_a_pending_version_and_the_pay_sheet_keeps_the_old_account()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        await harness.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile", FirstDraft, fleet.OwnerBearer);

        await harness.ApproveAsync(fleet.FleetId);

        var verified = await harness.GetAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile", fleet.OwnerBearer);

        Assert.Equal(PayoutProfileStatuses.Verified, verified.Status);
        Assert.NotNull(verified.VerifiedAt);

        // The owner retypes their account number.
        var edited = await harness.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile",
            new
            {
                bank = "Sampath Bank",
                branch = "Maharagama",
                accountNo = "1049876543",
                accountHolderName = "Ruhunu Express (Pvt) Ltd",
            },
            fleet.OwnerBearer);

        // What the owner sees: their edit, awaiting an officer.
        Assert.Equal(PayoutProfileStatuses.PendingVerification, edited.Status);
        Assert.Equal("1049876543", edited.AccountNo);

        // What the rows say: two versions, the incumbent still verified.
        var versions = await harness.PayoutVersionsAsync(fleet.FleetId);

        Assert.Equal(2, versions.Count);
        Assert.Equal(PayoutProfileStatuses.Verified, versions[0].Status);
        Assert.Equal("0071234567", versions[0].AccountNo);
        Assert.Equal(PayoutProfileStatuses.PendingVerification, versions[1].Status);

        // What matters: the passenger pay sheet still renders the account an officer approved.
        // This is subscription-svc's own query, verbatim (C050).
        var payTo = await harness.PaySheetPayToAsync(fleet.FleetId);

        Assert.NotNull(payTo);
        Assert.Equal("Bank of Ceylon", payTo.Value.Bank);
        Assert.Equal("0071234567", payTo.Value.AccountNo);

        // And once the officer approves the edit, the incumbent is superseded — one verified row
        // per organisation is what ux_payout_profile_verified admits.
        await harness.ApproveAsync(fleet.FleetId);

        var afterSecondApproval = await harness.PayoutVersionsAsync(fleet.FleetId);

        Assert.Equal(PayoutProfileStatuses.Superseded, afterSecondApproval[0].Status);
        Assert.Equal(PayoutProfileStatuses.Verified, afterSecondApproval[1].Status);

        var newPayTo = await harness.PaySheetPayToAsync(fleet.FleetId);

        Assert.NotNull(newPayTo);
        Assert.Equal("1049876543", newPayTo.Value.AccountNo);
    }

    /// <summary>
    /// A rejected edit leaves the incumbent alone.
    /// </summary>
    /// <remarks>
    /// BR-31.1's mismatched account-holder name is a reason to refuse the <em>change</em>, not a
    /// reason to stop an organisation collecting against details already approved.
    /// </remarks>
    [Fact]
    public async Task Rejecting_an_edit_does_not_disturb_the_verified_snapshot()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        await harness.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile", FirstDraft, fleet.OwnerBearer);
        await harness.ApproveAsync(fleet.FleetId);

        await harness.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile",
            new
            {
                bank = "Sampath Bank",
                branch = "Maharagama",
                accountNo = "1049876543",
                accountHolderName = "Somebody Else",
            },
            fleet.OwnerBearer);

        var officerId = await harness.CreateUserAsync("verification_officer");

        var decision = await harness.InternalAsync<VerificationDecisionResponse>(
            HttpMethod.Post,
            $"/v1/internal/fleets/{fleet.FleetId}/reject",
            new { officerId = officerId.ToString(), reason = "The account holder name does not match the organisation." });

        Assert.Equal(PayoutProfileStatuses.Rejected, decision.PayoutProfile?.Status);

        var payTo = await harness.PaySheetPayToAsync(fleet.FleetId);

        Assert.NotNull(payTo);
        Assert.Equal("0071234567", payTo.Value.AccountNo);
    }

    [Fact]
    public async Task A_document_lands_in_docs_uploads_and_attaches_to_the_profile()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        await harness.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile", FirstDraft, fleet.OwnerBearer);

        using var statement = await harness.UploadPayoutDocumentAsync(
            fleet.FleetId, fleet.OwnerBearer, PayoutDocumentKinds.BankStatement, [1, 2, 3, 4, 5]);

        var stored = await FleetHarness.OkAsync<PayoutDocumentResponse>(statement, "upload bank_statement");

        using var qr = await harness.UploadPayoutDocumentAsync(
            fleet.FleetId, fleet.OwnerBearer, PayoutDocumentKinds.LankaQrCode, [6, 7, 8]);

        var qrDoc = await FleetHarness.OkAsync<PayoutDocumentResponse>(qr, "upload lankaqr_code");

        var profile = await harness.GetAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile", fleet.OwnerBearer);

        Assert.Equal(stored.DocId, profile.ProofDocId);
        Assert.Equal(qrDoc.DocId, profile.LankaqrDocId);

        // NFR-28's deadline is on the row, measured from the created_at Postgres stamped.
        await using var connection = await harness.OpenAsync();

        var deadlineIsSet = await Dapper.SqlMapper.ExecuteScalarAsync<bool>(
            connection,
            """
            SELECT auto_delete_at > created_at AND kind = 'bank_statement'
              FROM docs.uploads WHERE id = @Id;
            """,
            new { Id = Guid.Parse(stored.DocId) });

        Assert.True(deadlineIsSet);

        // And the officer sees both, on the queue detail admin-bff forwards.
        var detail = await harness.InternalAsync<FleetVerificationResponse>(
            HttpMethod.Get, $"/v1/internal/fleets/{fleet.FleetId}");

        Assert.Equal(2, detail.Documents.Count);
        Assert.Contains(detail.Documents, document => document.Kind == PayoutDocumentKinds.LankaQrCode);
    }

    /// <summary>
    /// A document is an edit too (BR-31.1: "any edit re-enters <c>pending_verification</c>").
    /// </summary>
    [Fact]
    public async Task Replacing_evidence_behind_a_verified_profile_forks_a_pending_version()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        await harness.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile", FirstDraft, fleet.OwnerBearer);

        using var first = await harness.UploadPayoutDocumentAsync(
            fleet.FleetId, fleet.OwnerBearer, PayoutDocumentKinds.LankaQrCode, [1, 1, 1]);
        var firstQr = await FleetHarness.OkAsync<PayoutDocumentResponse>(first, "first lankaqr_code");

        await harness.ApproveAsync(fleet.FleetId);

        using var replacement = await harness.UploadPayoutDocumentAsync(
            fleet.FleetId, fleet.OwnerBearer, PayoutDocumentKinds.LankaQrCode, [2, 2, 2]);
        var newQr = await FleetHarness.OkAsync<PayoutDocumentResponse>(replacement, "replacement lankaqr_code");

        Assert.NotEqual(firstQr.DocId, newQr.DocId);

        var versions = await harness.PayoutVersionsAsync(fleet.FleetId);

        Assert.Equal(2, versions.Count);
        Assert.Equal(PayoutProfileStatuses.Verified, versions[0].Status);
        Assert.Equal(PayoutProfileStatuses.PendingVerification, versions[1].Status);

        var current = await harness.GetAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile", fleet.OwnerBearer);

        Assert.Equal(newQr.DocId, current.LankaqrDocId);
    }

    [Fact]
    public async Task A_document_needs_a_profile_and_a_known_kind()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        using var noProfile = await harness.UploadPayoutDocumentAsync(
            fleet.FleetId, fleet.OwnerBearer, PayoutDocumentKinds.BankStatement, [1, 2, 3]);

        var missing = await FleetHarness.ProblemAsync(noProfile);
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Equal("payout-profile-not-found", missing.Code);

        await harness.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile", FirstDraft, fleet.OwnerBearer);

        using var wrongKind = await harness.UploadPayoutDocumentAsync(
            fleet.FleetId, fleet.OwnerBearer, "driving_license", [1, 2, 3]);

        var refused = await FleetHarness.ProblemAsync(wrongKind);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("validation-failed", refused.Code);
    }

    [Fact]
    public async Task A_document_larger_than_the_ceiling_is_refused()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        await harness.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile", FirstDraft, fleet.OwnerBearer);

        // 64 KiB is the configured floor for Fleet:DocumentMaxBytes; the harness lowers it so the
        // suite does not have to push 8 MiB through a socket to prove a bound.
        await using var small = await FleetHarness.StartAsync(
            postgres, new Dictionary<string, string?> { ["Fleet:DocumentMaxBytes"] = "65536" });

        var tiny = await small.CreateFleetAsync();

        await small.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{tiny.FleetId}/payout-profile", FirstDraft, tiny.OwnerBearer);

        using var response = await small.UploadPayoutDocumentAsync(
            tiny.FleetId, tiny.OwnerBearer, PayoutDocumentKinds.BankStatement, new byte[70_000]);

        var problem = await FleetHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, problem.Status);
        Assert.Equal("payload-too-large", problem.Code);
    }
}
