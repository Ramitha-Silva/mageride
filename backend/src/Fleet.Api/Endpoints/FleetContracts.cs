using System.Text.RegularExpressions;
using MageRide.Fleet.Domain;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;

namespace MageRide.Fleet.Endpoints;

// =================================================================================================
// Wire shapes. Names and casing come from backend/contracts/fleet.yaml, which is normative — the
// kernel serialises camelCase and omits nulls, so an optional member absent from the JSON is the
// contract's optional member being absent.
// =================================================================================================

/// <summary><c>POST /v1/fleets</c> (US-13.A7).</summary>
public sealed record RegisterFleetBody(
    string? Name, string? RegistrationNo, string? ContactPhone, string? ContactEmail, string? Address);

/// <summary><c>fleet.yaml#/components/schemas/Fleet</c>.</summary>
public sealed record FleetResponse(
    string FleetId,
    string Name,
    string? RegistrationNo,
    string? ContactPhone,
    string? ContactEmail,
    string? Address,
    string Status,
    string? RejectionReason,
    DateTimeOffset CreatedAt)
{
    public static FleetResponse From(FleetOrganisation fleet)
    {
        ArgumentNullException.ThrowIfNull(fleet);

        return new FleetResponse(
            fleet.Id.ToString(),
            fleet.Name,
            fleet.BusinessReg,
            fleet.ContactPhone,
            fleet.ContactEmail,
            fleet.Address,
            fleet.Status,
            fleet.RejectionReason,
            fleet.CreatedAt);
    }
}

/// <summary><c>POST /v1/fleets/{fleetId}/members</c> (US-13.A5).</summary>
public sealed record AddFleetMemberBody(string? Email, string? Name, string? FleetRole);

/// <summary>The 201 body <c>addFleetMember</c> declares.</summary>
/// <remarks>
/// <c>memberId</c> is the <c>iam.users.id</c> of the person provisioned. <c>iam.fleet_members</c>
/// has a composite primary key and no surrogate id, and the user id is what every other surface —
/// the token's <c>sub</c>, an assignment, an audit row — already names them by.
/// </remarks>
public sealed record FleetMemberResponse(string MemberId, string? Email, string? Name, string FleetRole)
{
    public static FleetMemberResponse From(FleetMember member)
    {
        ArgumentNullException.ThrowIfNull(member);

        return new FleetMemberResponse(member.UserId.ToString(), member.Email, member.Name, member.FleetRole);
    }
}

/// <summary>The org's team. Δ C058 — <c>fleet.yaml</c> has the POST and no way to read the result.</summary>
public sealed record FleetMembersResponse(IReadOnlyList<FleetMemberResponse> Items);

/// <summary><c>PUT /v1/fleets/{fleetId}/payout-profile</c> (AL-49).</summary>
public sealed record PayoutProfileBody(
    string? Bank, string? Branch, string? AccountNo, string? AccountHolderName);

/// <summary><c>fleet.yaml#/components/schemas/PayoutProfile</c>.</summary>
public sealed record PayoutProfileResponse(
    string Bank,
    string Branch,
    string AccountNo,
    string AccountHolderName,
    string? LankaqrDocId,
    string? ProofDocId,
    string Status,
    string? RejectionReason,
    DateTimeOffset? VerifiedAt)
{
    public static PayoutProfileResponse From(PayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new PayoutProfileResponse(
            profile.Bank,
            profile.Branch,
            profile.AccountNo,
            profile.AccountHolderName,
            profile.LankaqrUploadId?.ToString(),
            profile.ProofUploadId?.ToString(),
            profile.Status,
            profile.RejectionReason,
            profile.VerifiedAt);
    }
}

/// <summary>The 201 body <c>uploadPayoutProfileDocument</c> declares.</summary>
public sealed record PayoutDocumentResponse(string DocId, string Kind);

/// <summary><c>PUT /v1/fleets/{fleetId}/vehicles/{vehicleId}/classification</c> (AL-24 item 16b).</summary>
public sealed record ClassificationBody(string? ModeBBilling, long? DefaultMonthlyFareMinor);

/// <summary><c>fleet.yaml#/components/schemas/FleetVehicle</c>.</summary>
/// <remarks>
/// <c>docsStatus</c> is AL-50's verdict — <c>docs_pending</c> until every slot the vehicle's mode
/// requires is verified. It is <b>derived, never stored</b> (see <c>VehicleDocumentSlots</c>), and
/// it is absent on the one response that has no reason to have read the documents:
/// <c>PUT …/classification</c> changes the Service payment setting and nothing about the paperwork,
/// so answering it with a stale-by-a-transaction verdict would be worse than answering without one.
/// </remarks>
public sealed record FleetVehicleResponse(
    string VehicleId,
    string RegistrationNumber,
    string VehicleType,
    string Mode,
    string Status,
    string? DocsStatus,
    string? ModeBBilling,
    long? DefaultMonthlyFareMinor,
    string? Currency)
{
    /// <summary>D3' §0: every money field is LKR integer minor units.</summary>
    private const string Lkr = "LKR";

    public static FleetVehicleResponse From(FleetVehicle vehicle, string? docsStatus = null)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        return new FleetVehicleResponse(
            vehicle.VehicleId.ToString(),
            vehicle.RegistrationNumber,
            vehicle.VehicleType,
            vehicle.Mode,
            vehicle.Status,
            docsStatus,
            vehicle.ModeBBilling,
            vehicle.DefaultMonthlyFareMinor,
            // Only when there is an amount to denominate. A currency beside a null fare is a fact
            // about nothing.
            vehicle.DefaultMonthlyFareMinor is null ? null : Lkr);
    }
}

// -------------------------------------------------------------------------------------------------
// Internal plane (admin-bff, C062)
// -------------------------------------------------------------------------------------------------

/// <summary>One row of the fleet-org verification queue (AL-39).</summary>
public sealed record FleetQueueRowResponse(
    string FleetId,
    string Name,
    string? RegistrationNo,
    string? ContactPhone,
    string Status,
    string? PayoutProfileStatus,
    int DocumentCount,
    DateTimeOffset CreatedAt)
{
    public static FleetQueueRowResponse From(FleetQueueRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new FleetQueueRowResponse(
            row.FleetId.ToString(),
            row.Name,
            row.BusinessReg,
            row.ContactPhone,
            row.Status,
            row.PayoutProfileStatus,
            row.DocumentCount,
            row.CreatedAt);
    }
}

/// <summary>The queue page.</summary>
public sealed record FleetQueueResponse(IReadOnlyList<FleetQueueRowResponse> Items);

/// <summary>One attached document, as <c>admin-bff.yaml</c>'s <c>DocumentRef</c> shapes it.</summary>
public sealed record VerificationDocumentResponse(string DocId, string Kind, DateTimeOffset CreatedAt);

/// <summary>
/// <c>GET /v1/admin/verification/org/{orgId}</c>'s payload, as this service supplies it.
/// </summary>
/// <remarks>
/// <c>kyc</c> is <c>additionalProperties: true</c> in <c>admin-bff.yaml</c> — deliberately open,
/// because what an officer must read differs by subject type. What fleet-svc puts in it is the
/// organisation itself; admin-bff adds the signed document URLs (US-24.8), which need a signing key
/// this service does not hold.
/// </remarks>
public sealed record FleetVerificationResponse(
    FleetResponse Kyc,
    string? PayoutProfileStatus,
    PayoutProfileResponse? PayoutProfile,
    IReadOnlyList<VerificationDocumentResponse> Documents);

/// <summary>Who decided, forwarded by admin-bff from the officer's own bearer.</summary>
public sealed record VerificationDecisionBody(string? OfficerId, string? Reason);

/// <summary>What a decision produced.</summary>
public sealed record VerificationDecisionResponse(FleetResponse Fleet, PayoutProfileResponse? PayoutProfile)
{
    public static VerificationDecisionResponse From(VerificationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new VerificationDecisionResponse(
            FleetResponse.From(decision.Fleet),
            decision.PayoutProfile is null ? null : PayoutProfileResponse.From(decision.PayoutProfile));
    }
}

// -------------------------------------------------------------------------------------------------
// Parsing
// -------------------------------------------------------------------------------------------------

/// <summary>Parses the identifiers D3' types as <c>Ulid</c> ("ULID or UUID, rendered canonically").</summary>
/// <remarks>
/// The same twelve lines wallet-svc, reputation-svc and subscription-svc carry. Per service rather
/// than in the kernel because each names its own fields in the error, which is what makes a 400
/// actionable.
/// </remarks>
internal static class RequestIds
{
    public static Guid Require(string? value, string field) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [$"{field} is required and must be a ULID or a UUID."],
            });
}

/// <summary>Validates the fields <c>fleet.yaml</c> constrains, and nothing it does not.</summary>
internal static partial class FleetRequests
{
    /// <summary>Trims, bounds and refuses. Collected so one 400 carries every problem at once.</summary>
    public static string RequireText(
        Dictionary<string, string[]> errors, string? value, string field, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            errors[field] = [$"{field} is required."];
            return string.Empty;
        }

        if (trimmed.Length > maxLength)
        {
            errors[field] = [$"{field} is at most {maxLength} characters."];
        }

        return trimmed;
    }

    public static string? OptionalText(
        Dictionary<string, string[]> errors, string? value, string field, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            errors[field] = [$"{field} is at most {maxLength} characters."];
        }

        return trimmed;
    }

    /// <summary><c>_shared.yaml#/schemas/PhoneE164</c> — <c>^\+947\d{8}$</c>.</summary>
    public static string RequirePhone(Dictionary<string, string[]> errors, string? value, string field)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var trimmed = value?.Trim() ?? string.Empty;

        if (!SriLankanMobile().IsMatch(trimmed))
        {
            errors[field] = [$"{field} must be a Sri Lankan mobile number in E.164, +947XXXXXXXX."];
        }

        return trimmed;
    }

    /// <summary>
    /// An address good enough to be a sign-in identity.
    /// </summary>
    /// <remarks>
    /// Deliberately not RFC 5322. The address becomes an <c>iam.users.email</c> row and a Fleet
    /// Portal credential (AL-07); what matters is that it is one token, has an <c>@</c> with
    /// something either side, and a dot in the domain. A stricter grammar rejects addresses that
    /// work, and a looser one creates accounts nobody can sign in to.
    /// </remarks>
    public static string RequireEmail(Dictionary<string, string[]> errors, string? value, string field)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var trimmed = value?.Trim() ?? string.Empty;

        if (trimmed.Length is 0 or > 320 || !PlausibleEmail().IsMatch(trimmed))
        {
            errors[field] = [$"{field} must be an email address."];
        }

        return trimmed;
    }

    [GeneratedRegex(@"^\+947\d{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex SriLankanMobile();

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PlausibleEmail();
}
