using System.Security.Claims;
using MageRide.Shared.Auth;

namespace MageRide.AdminBff.Directories;

/// <summary>
/// Whether this caller sees a person's contact details in the clear, and the masks applied when
/// they do not (AL-40/41/42, I-28.6, BR-28.8).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate is URD §2.3's <c>account-management</c> row, held as Write and held
/// platform-wide.</b> The requirement — "PII fields (mobile, email, NIC) render only for roles
/// whose RBAC grant permits them" (URD §2.3 privacy note) — names no cell, so the cell has to be
/// argued. It is the End-user account management row because that is the row about *people as
/// accounts*: KYC status, deactivate/restore. And it is Write-held-unscoped because the row's two
/// ◐ qualifiers say exactly who is bounded — a Verification Officer is <c>◐ verification</c> and a
/// Support CSR is <c>◐ on tickets</c>, and neither qualifier describes browsing the platform's
/// directory. That is the DoD's "a Support/CSR sees masked PII where the matrix says so", read off
/// the matrix rather than hard-coded: an Admin and a Super Admin (✅) see the number, and everybody
/// else — CSR, Verification Officer, Finance (➖ on the row) and Auditor (👁, and URD §2.4's "no
/// write access anywhere") — sees the mask.
/// </para>
/// <para>
/// <b>The ◐ is why the CSR can open the directory at all and still not see the number.</b> The
/// route is gated on Read, which every ◐ cell satisfies; the clear value is gated on the unscoped
/// Write the same cell withholds. One row, two questions, and the mask is what the qualifier means
/// on a surface that cannot express "only the records attached to your tickets".
/// </para>
/// <para>
/// <b>A list is masked for everybody, whatever the caller holds.</b> `admin-bff.yaml`'s own
/// summary — "List responses carry role-masked phone numbers; the clear number requires the audited
/// detail read" — and the field's name (<c>mobileMasked</c>, typed <c>PhoneMasked</c>) make the
/// list a place a clear number never appears. That is what makes the audit claim true: every clear
/// MSISDN this service emits left through a read that wrote a <c>PII_READ</c> row, so an auditor
/// asking who has seen this person's number gets a complete answer from one query.
/// </para>
/// <para>
/// <b>Masking is server-side and there is no unmasked field beside the masked one.</b> A response
/// carrying both and letting the portal choose would put the clear value in the browser, in its
/// cache and in every proxy log on the way — which is the failure the rule exists to prevent
/// (P-02).
/// </para>
/// </remarks>
public interface IPiiPolicy
{
    /// <summary>What <paramref name="principal"/> may read of a person's contact details.</summary>
    PiiView For(ClaimsPrincipal principal);
}

/// <inheritdoc cref="IPiiPolicy"/>
internal sealed class PiiPolicy(IPermissionEvaluator evaluator) : IPiiPolicy
{
    public PiiView For(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var fleet = principal.TryGetFleetScope(out var fleetRole, out var fleetId)
            ? new FleetScope(fleetId, fleetRole)
            : null;

        var permission = evaluator
            .Evaluate(principal.SubjectId() ?? Guid.Empty, [.. principal.Roles()], fleet)
            .For(FeatureAreas.AccountManagement);

        // Satisfies AND unscoped, in that order and both required — the same pair
        // `PlatformWideFeatureHandler` demands of a platform-wide action, because reading every
        // passenger's number is one.
        return new PiiView(
            permission.Satisfies(PermissionGrant.Write) &&
            !permission.RequiresOwnScope(PermissionGrant.Write));
    }
}

/// <summary>
/// One caller's view of the PII on a directory read: the values, or the masks.
/// </summary>
/// <param name="Clear">
/// Whether contact details are rendered as stored. Decided once per request by
/// <see cref="IPiiPolicy"/> and never re-derived at a call site.
/// </param>
public sealed record PiiView(bool Clear)
{
    /// <summary>What a list row shows, and what a caller without the grant sees on a detail.</summary>
    public static readonly PiiView Masked = new(false);

    /// <summary>
    /// An MSISDN as the caller may see it.
    /// </summary>
    /// <remarks>
    /// The masked form is `_shared.yaml`'s <c>PhoneMasked</c> to the character —
    /// <c>+9477*****67</c> — because that example is the contract and a portal rendering a
    /// fixed-width column against it should not have to cope with two spellings.
    /// </remarks>
    public string? Mobile(string? value) => Clear ? value : MaskMsisdn(value);

    /// <summary>An email address as the caller may see it.</summary>
    public string? Email(string? value) => Clear ? value : MaskEmail(value);

    /// <summary>An NIC number as the caller may see it.</summary>
    public string? Nic(string? value) => Clear ? value : MaskNic(value);

    /// <summary>
    /// The <c>PhoneMasked</c> spelling: the country code and network prefix, then the last two
    /// digits.
    /// </summary>
    /// <remarks>
    /// Enough to confirm a number somebody read out and not enough to dial one nobody gave you —
    /// which is what a CSR checking "is this the number that called us" actually needs. A value too
    /// short to mask that way is masked whole rather than partially exposed.
    /// </remarks>
    public static string? MaskMsisdn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= 7
            ? new string('*', trimmed.Length)
            : string.Concat(trimmed[..5], new string('*', trimmed.Length - 7), trimmed[^2..]);
    }

    /// <summary>
    /// The first character of the mailbox, then the domain.
    /// </summary>
    /// <remarks>
    /// The domain survives because it is not the identifier — "they signed in with a Google
    /// account" is what an operator is checking — and because the masked value has to stay a
    /// syntactically valid address: `admin-bff.yaml` types the field <c>format: email</c>, and a
    /// mask that broke the format would make a schema-validating client reject the whole response.
    /// </remarks>
    public static string? MaskEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);

        return at switch
        {
            // No '@' is not an address this service should try to interpret; mask it whole.
            < 1 => new string('*', trimmed.Length),
            _ => string.Concat(trimmed[0], "***", trimmed[at..]),
        };
    }

    /// <summary>
    /// The last two characters only.
    /// </summary>
    /// <remarks>
    /// Harder than a phone number, deliberately. A Sri Lankan NIC's leading digits are the holder's
    /// year of birth and day of year — a prefix is not a hint, it is a date of birth — so nothing of
    /// it is shown except enough to check a value somebody just read out.
    /// </remarks>
    public static string? MaskNic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= 4
            ? new string('*', trimmed.Length)
            : string.Concat(new string('*', trimmed.Length - 2), trimmed[^2..]);
    }
}
