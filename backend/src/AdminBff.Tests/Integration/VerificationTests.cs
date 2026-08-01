using System.Net;
using System.Text.Json;
using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.AdminBff.Tests.Integration;

/// <summary>
/// C063's definition of done: approval is refused while a flagged field is unconfirmed, every
/// document view writes exactly one <c>DOC_VIEW</c> row, a rejection reaches the applicant with its
/// reason and re-enters the queue on re-upload, and approving a fleet org makes its payout profile
/// the one <c>payTo</c> reads (AL-39, AL-29, AL-30, AL-49, D-35).
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven at the socket by a real Verification Officer token.</b> The gate, the D-35 interceptor
/// and the problem+json handler are all in the path, so a claim proved here is proved about the
/// service rather than about a service object.
/// </para>
/// <para>
/// <b>The two forwarded planes write real rows.</b> registry-svc's recompute and fleet-svc's
/// approval are stubbed by <c>StubInternalPlanes</c>, which performs each service's own
/// transaction against the same Postgres — so "the payout profile is verified" is asserted against
/// <c>registry.fleet_payout_profiles</c> and not against a canned reply.
/// </para>
/// </remarks>
[Trait("Category", "Verification")]
[Collection(AdminBffCollection.Name)]
public sealed class VerificationTests(PostgresFixture postgres)
{
    // ---------------------------------------------------------------------------------------
    // The queues — AL-27's fence
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// AL-27: "the Verification Officer sees only PENDING items — auto-verified documents never
    /// enter the queue."
    /// </summary>
    [Fact]
    public async Task Only_a_flagged_submission_reaches_the_driving_licence_queue()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (driverId, _, _) = await harness.Seed.DriverAwaitingLicenceAsync();
        var officer = await OfficerAsync(harness);

        var row = Assert.Single(
            await QueuedDriversAsync(harness, officer),
            item => Guid.Parse(item.GetProperty("driverId").GetString()!) == driverId);

        Assert.Equal("PENDING", row.GetProperty("status").GetString());

        // The licence number extracted at 0.96 and is auto_verified, so it is not a flagged field:
        // the officer is shown the question, not the whole document.
        Assert.Equal(
            ["nic_no"],
            row.GetProperty("flaggedFields").EnumerateArray().Select(field => field.GetString()!).ToArray());

        using (var confirm = await harness.SendAsync(
            HttpMethod.Put, $"/v1/admin/verification/{driverId:D}/fields/nic_no", officer, new { }))
        {
            confirm.EnsureSuccessStatusCode();
        }

        // AL-27's fence, stated as the query it is: membership is "has a pending field", so a
        // submission with nothing left to decide cannot appear whatever its status.
        Assert.DoesNotContain(
            await QueuedDriversAsync(harness, officer),
            item => Guid.Parse(item.GetProperty("driverId").GetString()!) == driverId);
    }

    [Fact]
    public async Task The_vehicle_queue_carries_the_flagged_field_and_answers_a_search()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, vehicleId, _) = await harness.Seed.VehicleAwaitingReviewAsync();
        var (_, plate) = await PlateAsync(harness, vehicleId);
        var officer = await OfficerAsync(harness);

        using var response = await harness.GetAsync(
            $"/v1/admin/verification/queues/vehicle-registration?search={plate}", officer);

        using var body = await harness.ReadJsonAsync(response);

        var row = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray().ToArray());

        Assert.Equal(vehicleId, Guid.Parse(row.GetProperty("vehicleId").GetString()!));
        Assert.Equal(plate, row.GetProperty("regNo").GetString());
        Assert.Equal("PENDING", row.GetProperty("status").GetString());
        Assert.Equal(
            ["insurance_expiry"],
            row.GetProperty("flaggedFields").EnumerateArray().Select(field => field.GetString()!).ToArray());

        // The status filter is SCR-AP-003's, and it filters on the subject's own status.
        using var approvedOnly = await harness.GetAsync(
            $"/v1/admin/verification/queues/vehicle-registration?search={plate}&status=APPROVED", officer);

        using var empty = await harness.ReadJsonAsync(approvedOnly);

        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task An_unknown_status_filter_is_a_400_naming_the_field()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        using var response = await harness.GetAsync(
            "/v1/admin/verification/queues/driving-license?status=MAYBE", await OfficerAsync(harness));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("status", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // The detail — SCR-AP-003a
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_detail_carries_the_fields_the_documents_and_the_per_step_breakdown()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, vehicleId, docId) = await harness.Seed.VehicleAwaitingReviewAsync();

        using var response = await harness.GetAsync(
            $"/v1/admin/verification/{vehicleId:D}", await OfficerAsync(harness));

        using var body = await harness.ReadJsonAsync(response);
        var root = body.RootElement;

        Assert.Equal("vehicle", root.GetProperty("subject").GetProperty("type").GetString());
        Assert.False(root.GetProperty("approvable").GetBoolean());

        var field = Assert.Single(root.GetProperty("fields").EnumerateArray().ToArray());
        Assert.Equal("insurance_expiry", field.GetProperty("key").GetString());
        Assert.Equal("ai", field.GetProperty("source").GetString());
        Assert.Equal("pending", field.GetProperty("verifyStatus").GetString());
        Assert.Equal(0.410m, field.GetProperty("confidence").GetDecimal());

        var document = Assert.Single(root.GetProperty("documents").EnumerateArray().ToArray());
        Assert.Equal(docId, Guid.Parse(document.GetProperty("docId").GetString()!));
        Assert.Equal("insurance", document.GetProperty("kind").GetString());

        // AL-43's provenance: this scan came out of the gallery, which is the fraud signal
        // SCR-AP-003a sorts on.
        Assert.Equal("gallery", document.GetProperty("capturedVia").GetString());

        // Δ C063: both links are the audited viewer, so no fetch of a document escapes DOC_VIEW.
        Assert.Equal($"/v1/admin/documents/{docId:D}?variant=thumb", document.GetProperty("thumbUrl").GetString());
        Assert.Equal($"/v1/admin/documents/{docId:D}?variant=full", document.GetProperty("fullUrl").GetString());

        var steps = root.GetProperty("steps").EnumerateArray()
            .ToDictionary(step => step.GetProperty("step").GetString()!, step => step.GetProperty("status").GetString());

        Assert.Equal(["details", "insurance", "revenue", "photos"], steps.Keys);
        Assert.Equal("PENDING_REVIEW", steps["insurance"]);
        Assert.Equal("VERIFIED", steps["details"]);
    }

    [Fact]
    public async Task An_id_that_names_nothing_is_a_404()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        using var response = await harness.GetAsync(
            $"/v1/admin/verification/{Guid.CreateVersion7():D}", await OfficerAsync(harness));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // Confirm and edit-and-confirm — US-2.4a / US-2.10a
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Confirming_a_field_as_read_keeps_its_provenance_and_unlocks_approval()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, vehicleId, docId) = await harness.Seed.VehicleAwaitingReviewAsync();
        var officerId = await harness.Seed.InternalUserAsync(MageRideRoles.VerificationOfficer);

        using var response = await harness.SendAsync(
            HttpMethod.Put,
            $"/v1/admin/verification/{vehicleId:D}/fields/insurance_expiry",
            harness.Tokens.Internal(officerId, MageRideRoles.VerificationOfficer),
            new { });

        using var body = await harness.ReadJsonAsync(response);

        Assert.True(body.RootElement.GetProperty("approvable").GetBoolean());
        Assert.Equal("VERIFIED", body.RootElement.GetProperty("stepStatus").GetString());
        Assert.Equal("confirmed", body.RootElement.GetProperty("field").GetProperty("verifyStatus").GetString());

        var stored = await harness.Seed.FieldAsync(docId, "insurance_expiry");

        Assert.NotNull(stored);
        Assert.Equal("confirmed", stored.VerifyStatus);
        Assert.Equal("2027-05-01", stored.FieldValue);

        // Confirming as read is not an edit: what ocr-svc produced, and how sure it was, survive.
        Assert.Equal("ai", stored.Source);
        Assert.Equal(0.410m, stored.Confidence);
        Assert.Equal(officerId, stored.ConfirmedBy);
        Assert.NotNull(stored.ConfirmedAt);

        var audits = await harness.Seed.AuditRowsAsync(vehicleId);
        var confirmed = Assert.Single(audits, row => row.Action == AdminAuditActions.FieldConfirmed);

        Assert.Equal(officerId, confirmed.ActorId);
        Assert.Equal("vehicle", confirmed.EntityType);
        Assert.Contains("insurance_expiry", confirmed.After!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_and_confirming_replaces_the_value_and_drops_the_confidence()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (driverId, docId, _) = await harness.Seed.DriverAwaitingLicenceAsync();

        using var response = await harness.SendAsync(
            HttpMethod.Put,
            $"/v1/admin/verification/{driverId:D}/fields/nic_no",
            await OfficerAsync(harness),
            new { value = "199012345679" });

        using var body = await harness.ReadJsonAsync(response);

        Assert.Equal("199012345679", body.RootElement.GetProperty("field").GetProperty("value").GetString());
        Assert.True(body.RootElement.GetProperty("approvable").GetBoolean());

        var stored = await harness.Seed.FieldAsync(docId, "nic_no");

        Assert.NotNull(stored);
        Assert.Equal("199012345679", stored.FieldValue);
        Assert.Equal("confirmed", stored.VerifyStatus);

        // The value is no longer what anything read, so it carries no score —
        // ck_document_fields_manual_confidence refuses one.
        Assert.Equal("manual", stored.Source);
        Assert.Null(stored.Confidence);
    }

    [Fact]
    public async Task A_field_that_was_never_extracted_is_a_404()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (driverId, _, _) = await harness.Seed.DriverAwaitingLicenceAsync();

        using var response = await harness.SendAsync(
            HttpMethod.Put,
            $"/v1/admin/verification/{driverId:D}/fields/blood_group",
            await OfficerAsync(harness),
            new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // Approve — US-2.10a's gate
    // ---------------------------------------------------------------------------------------

    /// <summary>DoD: "approving with an unconfirmed flagged field is refused."</summary>
    [Fact]
    public async Task Approving_with_an_unconfirmed_flagged_field_is_refused()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, vehicleId, _) = await harness.Seed.VehicleAwaitingReviewAsync();

        using var response = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/verification/{vehicleId:D}/approve", await OfficerAsync(harness));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var (status, _) = await harness.Seed.VehicleVerdictAsync(vehicleId);
        Assert.Equal("PENDING", status);

        // A refusal changed nothing, so it wrote nothing: only successes are audited (D-35).
        Assert.Empty(await harness.Seed.AuditRowsAsync(vehicleId));
    }

    [Fact]
    public async Task A_vehicle_whose_every_field_is_confirmed_is_approved()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, vehicleId, _) = await harness.Seed.VehicleAwaitingReviewAsync();
        var officer = await OfficerAsync(harness);

        using (var confirm = await harness.SendAsync(
            HttpMethod.Put, $"/v1/admin/verification/{vehicleId:D}/fields/insurance_expiry", officer, new { }))
        {
            confirm.EnsureSuccessStatusCode();
        }

        using var response = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/verification/{vehicleId:D}/approve", officer);

        using var body = await harness.ReadJsonAsync(response);

        Assert.Equal("APPROVED", body.RootElement.GetProperty("status").GetString());

        // D-11 has no merchant id to bind: registry-svc requires one and nothing onboards it.
        Assert.False(body.RootElement.GetProperty("merchantBound").GetBoolean());

        var (status, _) = await harness.Seed.VehicleVerdictAsync(vehicleId);
        Assert.Equal("APPROVED", status);

        Assert.Single(
            await harness.Seed.AuditRowsAsync(vehicleId),
            row => row.Action == AdminAuditActions.VerificationApproved);
    }

    // ---------------------------------------------------------------------------------------
    // Reject — US-2.15
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// DoD: "a rejected item returns to the driver with the reason and re-enters the queue on
    /// re-upload."
    /// </summary>
    [Fact]
    public async Task A_rejection_reaches_the_driver_and_the_resubmission_returns_to_the_queue()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, vehicleId, _) = await harness.Seed.VehicleAwaitingReviewAsync();
        var officer = await OfficerAsync(harness);

        using (var confirm = await harness.SendAsync(
            HttpMethod.Put, $"/v1/admin/verification/{vehicleId:D}/fields/insurance_expiry", officer, new { }))
        {
            confirm.EnsureSuccessStatusCode();
        }

        using (var reject = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/verification/{vehicleId:D}/reject",
            officer,
            new { reason = "The insurance certificate is for a different vehicle." }))
        {
            reject.EnsureSuccessStatusCode();
        }

        // What the driver reads at GET /v1/vehicles/{id}/status — registry-svc serves this column
        // and does not write it.
        var (status, reason) = await harness.Seed.VehicleVerdictAsync(vehicleId);

        Assert.Equal("REJECTED", status);
        Assert.Equal("The insurance certificate is for a different vehicle.", reason);

        // With nothing pending the vehicle has left the queue.
        Assert.DoesNotContain(await QueuedVehiclesAsync(harness, officer), id => id == vehicleId);

        // The driver re-uploads: registry-svc writes a fresh pending field, and that alone puts the
        // vehicle back in front of an officer.
        await harness.Seed.ReuploadInsuranceAsync(vehicleId);

        Assert.Contains(await QueuedVehiclesAsync(harness, officer), id => id == vehicleId);

        // ...and the officer can now decide it again, which means withdrawing their own refusal —
        // its own audited fact, because registry-svc will not auto-approve a REJECTED vehicle.
        using (var confirm = await harness.SendAsync(
            HttpMethod.Put, $"/v1/admin/verification/{vehicleId:D}/fields/insurance_expiry", officer, new { }))
        {
            confirm.EnsureSuccessStatusCode();
        }

        using var approve = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/verification/{vehicleId:D}/approve", officer);

        approve.EnsureSuccessStatusCode();

        var audits = await harness.Seed.AuditRowsAsync(vehicleId);

        Assert.Single(audits, row => row.Action == AdminAuditActions.VerificationRejected);
        Assert.Single(audits, row => row.Action == AdminAuditActions.VerificationReopened);
        Assert.Single(audits, row => row.Action == AdminAuditActions.VerificationApproved);

        var (finalStatus, finalReason) = await harness.Seed.VehicleVerdictAsync(vehicleId);

        Assert.Equal("APPROVED", finalStatus);
        Assert.Null(finalReason);
    }

    [Fact]
    public async Task A_rejection_with_no_reason_is_refused()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, vehicleId, _) = await harness.Seed.VehicleAwaitingReviewAsync();

        using var response = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/verification/{vehicleId:D}/reject",
            await OfficerAsync(harness),
            new { reason = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_driver_identity_submission_is_approved_and_can_be_refused()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (driverId, _, _) = await harness.Seed.DriverAwaitingLicenceAsync();
        var officer = await OfficerAsync(harness);

        using (var confirm = await harness.SendAsync(
            HttpMethod.Put, $"/v1/admin/verification/{driverId:D}/fields/nic_no", officer, new { }))
        {
            confirm.EnsureSuccessStatusCode();
        }

        using (var approve = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/verification/{driverId:D}/approve", officer))
        {
            approve.EnsureSuccessStatusCode();
        }

        var approved = await harness.Seed.DriverVerdictAsync(driverId);

        Assert.NotNull(approved.VerifiedAt);
        Assert.Null(approved.RejectionReason);

        using (var reject = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/verification/{driverId:D}/reject",
            officer,
            new { reason = "The NIC does not match the licence." }))
        {
            reject.EnsureSuccessStatusCode();
        }

        var rejected = await harness.Seed.DriverVerdictAsync(driverId);

        // Migration 0315's column: the two answers are never both on the row at once.
        Assert.Null(rejected.VerifiedAt);
        Assert.Equal("The NIC does not match the licence.", rejected.RejectionReason);
    }

    // ---------------------------------------------------------------------------------------
    // The viewer — AL-39, US-24.8
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// DoD: "every document view produces exactly one DOC_VIEW audit row with the actor and doc id."
    /// </summary>
    [Fact]
    public async Task Every_document_view_writes_exactly_one_DOC_VIEW_row_and_redirects_to_a_signed_url()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, _, docId) = await harness.Seed.VehicleAwaitingReviewAsync();
        var officerId = await harness.Seed.InternalUserAsync(MageRideRoles.VerificationOfficer);
        var officer = harness.Tokens.Internal(officerId, MageRideRoles.VerificationOfficer);

        using var response = await harness.GetAsync($"/v1/admin/documents/{docId:D}", officer);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        var location = response.Headers.Location!.ToString();

        Assert.StartsWith("https://docs.mageride.test/", location, StringComparison.Ordinal);
        Assert.Contains("expires=", location, StringComparison.Ordinal);
        Assert.Contains("signature=", location, StringComparison.Ordinal);

        var rows = await harness.Seed.AuditRowsAsync(docId);
        var view = Assert.Single(rows);

        Assert.Equal(AdminAuditActions.DocumentViewed, view.Action);
        Assert.Equal("document", view.EntityType);
        Assert.Equal(docId, view.EntityId);
        Assert.Equal(officerId, view.ActorId);
        Assert.Contains("insurance", view.After!, StringComparison.Ordinal);

        // One row per view, not one row ever: opening the lightbox again is another look at
        // somebody's document and is recorded as one.
        using var second = await harness.GetAsync($"/v1/admin/documents/{docId:D}", officer);
        Assert.Equal(HttpStatusCode.Found, second.StatusCode);

        Assert.Equal(2, (await harness.Seed.AuditRowsAsync(docId)).Count);
    }

    [Fact]
    public async Task A_document_id_that_names_nothing_is_a_404_and_records_no_view()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var docId = Guid.CreateVersion7();

        using var response = await harness.GetAsync($"/v1/admin/documents/{docId:D}", await OfficerAsync(harness));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await harness.Seed.AuditRowsAsync(docId));
    }

    // ---------------------------------------------------------------------------------------
    // The fleet org — AL-49
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// DoD: "approving a fleet org's payout profile makes <c>payTo</c> available to
    /// subscription-svc."
    /// </summary>
    [Fact]
    public async Task Approving_a_fleet_org_verifies_the_payout_profile_the_pay_sheet_reads()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (orgId, _, _, proofUploadId) = await harness.Seed.FleetOrgAwaitingKycAsync();
        var officer = await OfficerAsync(harness);

        using (var queue = await harness.GetAsync("/v1/admin/verification/queues/fleet-org?limit=100", officer))
        {
            using var page = await harness.ReadJsonAsync(queue);

            var row = Assert.Single(
                page.RootElement.GetProperty("items").EnumerateArray().ToArray(),
                item => Guid.Parse(item.GetProperty("orgId").GetString()!) == orgId);

            Assert.Equal("complete", row.GetProperty("kycStatus").GetString());
            Assert.Equal("pending_verification", row.GetProperty("payoutProfileStatus").GetString());

            // Counted here from registry.fleet_vehicles — the field is on admin-bff.yaml's
            // OrgQueueRow and fleet-svc's internal row does not carry it.
            Assert.Equal(1, row.GetProperty("vehicleCount").GetInt32());
        }

        using (var detail = await harness.GetAsync($"/v1/admin/verification/org/{orgId:D}", officer))
        {
            using var body = await harness.ReadJsonAsync(detail);
            var root = body.RootElement;

            Assert.Equal("Ruhunu Transport (Pvt) Ltd", root.GetProperty("kyc").GetProperty("name").GetString());
            Assert.Equal("+94112345678", root.GetProperty("kyc").GetProperty("contactPhone").GetString());
            Assert.Equal("pending_verification", root.GetProperty("payoutProfileStatus").GetString());
            Assert.Equal(
                "Bank of Ceylon",
                root.GetProperty("kyc").GetProperty("payoutProfile").GetProperty("bank").GetString());

            // AL-49's evidence, carrying links this service minted because fleet-svc holds no key.
            var document = Assert.Single(root.GetProperty("documents").EnumerateArray().ToArray());

            Assert.Equal(proofUploadId, Guid.Parse(document.GetProperty("docId").GetString()!));
            Assert.Equal("bank_statement", document.GetProperty("kind").GetString());
            Assert.Equal(
                $"/v1/admin/documents/{proofUploadId:D}?variant=full", document.GetProperty("fullUrl").GetString());
        }

        using (var approve = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/verification/{orgId:D}/approve", officer))
        {
            using var body = await harness.ReadJsonAsync(approve);
            Assert.Equal("APPROVED", body.RootElement.GetProperty("status").GetString());
        }

        // Exactly what subscription-svc's pay sheet reads (C050: WHERE status = 'verified'), and
        // exactly one of them — ux_payout_profile_verified admits no more (BR-31.1).
        Assert.Equal(["verified"], await harness.Seed.PayoutProfileStatusesAsync(orgId));

        var forwarded = harness.Upstream.Last($"/v1/internal/fleets/{orgId:D}/approve");

        Assert.Equal(StubUpstream.InternalKey, forwarded.InternalKey);
        Assert.Contains("officerId", forwarded.Body, StringComparison.Ordinal);

        var audit = Assert.Single(await harness.Seed.AuditRowsAsync(orgId));

        Assert.Equal(AdminAuditActions.VerificationApproved, audit.Action);
        Assert.Equal("fleet_org", audit.EntityType);
        Assert.Contains("verified", audit.After!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_payout_document_opens_in_the_same_audited_viewer()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (_, _, _, proofUploadId) = await harness.Seed.FleetOrgAwaitingKycAsync();

        using var response = await harness.GetAsync(
            $"/v1/admin/documents/{proofUploadId:D}", await OfficerAsync(harness));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        // A payout document has no registry.documents row at all — it is a docs.uploads row, and
        // the viewer resolves either.
        var view = Assert.Single(await harness.Seed.AuditRowsAsync(proofUploadId));

        Assert.Equal(AdminAuditActions.DocumentViewed, view.Action);
        Assert.Contains("docs.uploads", view.After!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------

    private static async Task<string> OfficerAsync(AdminBffHarness harness) =>
        harness.Tokens.Internal(
            await harness.Seed.InternalUserAsync(MageRideRoles.VerificationOfficer),
            MageRideRoles.VerificationOfficer);

    private static async Task<(Guid VehicleId, string Plate)> PlateAsync(AdminBffHarness harness, Guid vehicleId) =>
        (vehicleId, await harness.Seed.RegistrationNumberAsync(vehicleId));

    private static async Task<IReadOnlyList<JsonElement>> QueuedDriversAsync(
        AdminBffHarness harness, string officer)
    {
        using var response = await harness.GetAsync(
            "/v1/admin/verification/queues/driving-license?limit=100", officer);

        using var body = await harness.ReadJsonAsync(response);

        // Cloned: the JsonDocument is disposed on the way out of this method and the elements would
        // otherwise be reading freed memory.
        return [.. body.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone())];
    }

    private static async Task<IReadOnlyList<Guid>> QueuedVehiclesAsync(AdminBffHarness harness, string officer)
    {
        using var response = await harness.GetAsync(
            "/v1/admin/verification/queues/vehicle-registration?limit=100", officer);

        using var body = await harness.ReadJsonAsync(response);

        return
        [
            .. body.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => Guid.Parse(item.GetProperty("vehicleId").GetString()!)),
        ];
    }
}
