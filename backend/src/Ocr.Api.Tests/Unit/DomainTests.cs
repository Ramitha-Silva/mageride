using MageRide.Ocr.Domain;
using MageRide.Ocr.Pipeline;

namespace MageRide.Ocr.Tests.Unit;

/// <summary>
/// The rules that decide what a document said, in the one place they are written down.
/// </summary>
public sealed class PlateNumberTests
{
    [Theory]
    // A plate is its alphanumerics. Every one of these is the same registration painted, written
    // or read differently, and step 4/4 must not refuse a vehicle over a hyphen.
    [InlineData("WP-QA-1234", "WP QA 1234")]
    [InlineData("WP-QA-1234", "wpqa1234")]
    [InlineData("WP-QA-1234", "WP  QA-1234")]
    [InlineData("CAB-4321", "CAB 4321")]
    public void Presentation_never_decides_a_plate_match(string entered, string read) =>
        Assert.True(PlateNumbers.Match(read, entered));

    [Theory]
    // What must never be forgiven: a different character. These are the confusions an OCR engine
    // actually makes, and folding them is how a photograph of somebody else's vehicle passes.
    [InlineData("WP-QA-1234", "WP-QA-1284")]
    [InlineData("WP-QA-1234", "WP-OA-1234")]
    [InlineData("WP-QA-1234", "WP-QA-l234")]
    [InlineData("WP-QA-1234", "WP-QA-123")]
    public void A_different_character_is_a_different_vehicle(string entered, string read) =>
        Assert.False(PlateNumbers.Match(read, entered));

    [Fact]
    public void Two_unreadable_plates_do_not_agree_with_each_other()
    {
        // Otherwise an unreadable photograph and a missing registration verify the step between them.
        Assert.False(PlateNumbers.Match(null, null));
        Assert.False(PlateNumbers.Match("", ""));
        Assert.False(PlateNumbers.Match("WP-QA-1234", null));
    }

    [Theory]
    [InlineData("SRI LANKA WP QA-1234", "WP-QA-1234")]
    [InlineData("noise WPQA1234 noise", "WPQA1234")]
    [InlineData("CAB 4321", "CAB-4321")]
    public void The_plate_is_picked_out_of_the_page(string page, string expected) =>
        Assert.Equal(expected, PlateNumbers.Read(page));

    [Fact]
    public void A_page_with_no_plate_on_it_reads_as_none() =>
        Assert.Null(PlateNumbers.Read("MOTOR INSURANCE CERTIFICATE"));
}

/// <summary>ADD §12.5's "regex-detected ID number".</summary>
public sealed class IdentifierPatternTests
{
    [Theory]
    [InlineData("199012345678")]      // current twelve-digit NIC
    [InlineData("901234567V")]        // pre-2016
    [InlineData("901234567X")]
    [InlineData("901234567 V")]       // as an engine splits it
    public void A_NIC_is_recognised(string value) => Assert.True(IdentifierPatterns.IsIdentifier(value));

    [Theory]
    [InlineData("B1234567")]
    [InlineData("12345678")]
    public void A_licence_number_is_recognised(string value) =>
        Assert.True(IdentifierPatterns.IsIdentifier(value));

    [Theory]
    [InlineData("CEYLINCO")]
    [InlineData("2029-04-30")]
    [InlineData("WP-QA-1234")]
    public void Ordinary_text_is_not_masked(string value) =>
        Assert.False(IdentifierPatterns.IsIdentifier(value));

    [Fact]
    public void The_lettered_licence_number_wins_over_the_bare_digits()
    {
        // A Sri Lankan licence carries both, and the eight leading digits of a NIC are also a
        // licence-shaped run. Preferring the lettered form is what keeps the two fields apart.
        Assert.Equal(
            "B1234567", IdentifierPatterns.FindLicenceNumber("NIC 199012345678 LICENCE NO B1234567"));
    }

    [Fact]
    public void The_NIC_is_found_and_normalised() =>
        Assert.Equal("901234567V", IdentifierPatterns.FindNic("NIC 901234567 v"));
}

/// <summary>The one place a value is shaped, whichever engine produced it.</summary>
public sealed class FieldValueTests
{
    [Theory]
    [InlineData("2029-04-30")]
    [InlineData("30.04.2029")]
    [InlineData("30/04/2029")]
    [InlineData("30-04-2029")]
    [InlineData("30 Apr 2029")]
    [InlineData("Date of Expiry : 30.04.2029")]
    public void Every_way_a_Sri_Lankan_document_prints_an_expiry_normalises_to_ISO(string printed) =>
        Assert.Equal("2029-04-30", FieldValues.NormaliseDate(printed));

    [Fact]
    public void Dates_are_read_day_first()
    {
        // 03/04 is the 3rd of April here and the 4th of March in a month-first reading. Both parse,
        // which is why there is no month-first format in the list at all — "whichever parses" would
        // move an expiry by eleven months on the days it is ambiguous and never say so.
        Assert.Equal("2029-04-03", FieldValues.NormaliseDate("03/04/2029"));
    }

    [Fact]
    public void Text_with_no_date_in_it_yields_none() =>
        Assert.Null(FieldValues.NormaliseDate("EXPIRY: see reverse"));

    [Theory]
    [InlineData("A1 , B ,C1", "A1,B,C1")]
    [InlineData("A1/B/C1", "A1,B,C1")]
    [InlineData("a1 b c1", "A1,B,C1")]
    public void Licence_classes_normalise_to_a_comparable_list(string printed, string expected) =>
        Assert.Equal(expected, FieldValues.NormaliseVehicleClasses(printed));

    [Fact]
    public void Licence_classes_are_stored_verbatim_not_mapped_to_vehicle_types()
    {
        // AL-29 stores what the licence says. No spec in this build maps a Sri Lankan class to a
        // registry.vehicles.vehicle_type, and inventing the mapping here would put an unstated rule
        // between a driver's licence and what they are allowed to drive.
        Assert.Equal("A1,B,C1", FieldValues.NormaliseVehicleClasses("A1,B,C1"));
        Assert.DoesNotContain("three_wheeler", FieldValues.NormaliseVehicleClasses("A1,B,C1")!, StringComparison.Ordinal);
    }
}

/// <summary>C054's third fence: the field-level verdict, and nothing above it.</summary>
public sealed class FieldVerdictTests
{
    private const decimal Threshold = 0.80m;

    [Fact]
    public void A_confident_read_auto_verifies() =>
        Assert.Equal(
            VerifyStatuses.AutoVerified,
            FieldVerdicts.For(DocumentFieldKeys.InsuranceExpiry, "2027-03-31", 0.96m, Threshold));

    [Fact]
    public void A_low_confidence_field_is_pending() =>
        Assert.Equal(
            VerifyStatuses.Pending,
            FieldVerdicts.For(DocumentFieldKeys.InsuranceExpiry, "2027-03-31", 0.42m, Threshold));

    [Fact]
    public void An_unscored_field_is_treated_exactly_like_a_doubtful_one() =>
        Assert.Equal(
            VerifyStatuses.Pending,
            FieldVerdicts.For(DocumentFieldKeys.InsuranceExpiry, "2027-03-31", null, Threshold));

    [Fact]
    public void A_field_that_did_not_extract_is_pending_however_sure_the_engine_was() =>
        Assert.Equal(
            VerifyStatuses.Pending,
            FieldVerdicts.For(DocumentFieldKeys.InsuranceExpiry, null, 0.99m, Threshold));

    [Fact]
    public void A_confident_plate_mismatch_is_still_pending() =>
        Assert.Equal(
            VerifyStatuses.Pending,
            FieldVerdicts.For(DocumentFieldKeys.RegNoMatch, "false", 0.99m, Threshold));

    [Fact]
    public void A_match_read_off_an_illegible_plate_is_pending() =>
        Assert.Equal(
            VerifyStatuses.Pending,
            FieldVerdicts.For(DocumentFieldKeys.RegNoMatch, "true", 0.30m, Threshold));

    [Fact]
    public void A_confident_match_auto_verifies() =>
        Assert.Equal(
            VerifyStatuses.AutoVerified,
            FieldVerdicts.For(DocumentFieldKeys.RegNoMatch, "true", 0.96m, Threshold));

    [Fact]
    public void Judging_a_field_normalises_it_on_the_way_through()
    {
        var judged = FieldVerdicts.Judge(
            new ExtractedField(DocumentFieldKeys.InsuranceExpiry, "31.03.2027", 0.96m), Threshold);

        Assert.Equal("2027-03-31", judged.Value);
        Assert.Equal(VerifyStatuses.AutoVerified, judged.VerifyStatus);
        Assert.Equal(FieldSources.Ai, judged.Source);
    }

    [Fact]
    public void Nothing_this_service_produces_is_ever_manual_or_confirmed()
    {
        // `manual` is a driver typing a value registry-svc collected, and `confirmed` is a
        // Verification Officer's decision (C062). An extractor that could emit either would be
        // able to launder its own guess into a confirmed field.
        var judged = FieldVerdicts.Judge(
            new ExtractedField(DocumentFieldKeys.RevenueNo, "RL8891234", 0.2m, VerifyStatuses.Confirmed,
                FieldSources.Manual),
            Threshold);

        Assert.Equal(FieldSources.Ai, judged.Source);
        Assert.Equal(VerifyStatuses.Pending, judged.VerifyStatus);
    }
}

/// <summary>The vocabulary the two services agree on over the wire.</summary>
public sealed class DocumentVocabularyTests
{
    [Fact]
    public void Every_kind_registry_svc_uploads_has_an_extractor_here()
    {
        // registry.documents.kind's CHECK, spelled out. A kind this service does not know is a
        // 400 from the endpoint, so a drift between the two is loud rather than an empty read.
        string[] registryKinds = ["driving_license", "registration", "permit", "insurance", "revenue_license"];

        Assert.Equal(registryKinds.Order(), DocumentKinds.All.Order());
        Assert.All(registryKinds, kind => Assert.True(DocumentKinds.IsKnown(kind)));
    }

    [Fact]
    public void Every_required_key_is_also_an_accepted_one()
    {
        // Otherwise a required field could never be filled: the extractors only ever emit keys from
        // AcceptedFor, and the pipeline would add the missing required one as pending for ever.
        foreach (var kind in DocumentKinds.All)
        {
            foreach (var side in new string?[] { null, DocumentSides.Front, DocumentSides.Back })
            {
                Assert.All(
                    DocumentFieldKeys.RequiredFor(kind, side),
                    key => Assert.Contains(key, DocumentFieldKeys.AcceptedFor(kind, side)));
            }
        }
    }

    [Fact]
    public void The_licence_reverse_asks_for_the_classes_and_the_front_does_not()
    {
        // A Sri Lankan licence carries its classes on the back (AL-29), so a front scan that was
        // asked for them would come back with a pending field on every driver on the platform.
        Assert.Contains(
            DocumentFieldKeys.AllowedVehicleTypes,
            DocumentFieldKeys.RequiredFor(DocumentKinds.DrivingLicense, DocumentSides.Back));

        Assert.DoesNotContain(
            DocumentFieldKeys.AllowedVehicleTypes,
            DocumentFieldKeys.RequiredFor(DocumentKinds.DrivingLicense, DocumentSides.Front));
    }

    [Fact]
    public void The_NIC_is_accepted_but_never_required()
    {
        // I-25.1: it is masked out of the image before Gemini sees it, so a licence that comes back
        // without one is the normal case — requiring it would send every driver to an officer.
        Assert.Contains(
            DocumentFieldKeys.NicNo, DocumentFieldKeys.AcceptedFor(DocumentKinds.DrivingLicense, null));

        Assert.DoesNotContain(
            DocumentFieldKeys.NicNo, DocumentFieldKeys.RequiredFor(DocumentKinds.DrivingLicense, null));
    }
}
