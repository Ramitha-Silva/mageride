using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MageRide.Shared.Errors;

/// <summary>
/// The stable kebab error-code registry (D3' §0 "Errors").
/// <para>
/// Every error response the platform emits carries a <c>type</c> of
/// <c>https://mageride.lk/errors/{code}</c> where <c>{code}</c> is one of these keys. Codes are
/// globally unique across services so a client can branch on the code alone.
/// </para>
/// <para>
/// Codes named by a spec are declared here. A service that needs a code of its own registers it
/// at start-up with <see cref="Register"/> — the registry rejects duplicates, which is what keeps
/// the key space collision-free.
/// </para>
/// </summary>
public static partial class MageRideErrors
{
    /// <summary>Base of the RFC 7807 <c>type</c> URI (D3' §0).</summary>
    public const string TypeUriBase = "https://mageride.lk/errors/";

    // ---------------------------------------------------------------------------------------
    // Cross-cutting — owned by the kernel. Not individually named by a spec; see the C002
    // handoff note in build/progress.md.
    // ---------------------------------------------------------------------------------------

    /// <summary>Request body or query failed validation. Carries an <c>errors</c> extension.</summary>
    public static readonly ErrorCode ValidationFailed = new("validation-failed", 400, "Validation failed");

    /// <summary>Malformed request that is not a field-level validation failure.</summary>
    public static readonly ErrorCode BadRequest = new("bad-request", 400, "Bad request");

    /// <summary>No credential, or a credential that failed validation.</summary>
    public static readonly ErrorCode Unauthorized = new("unauthorized", 401, "Authentication required");

    /// <summary>Authenticated, but the effective role set does not permit this (deny-by-default, AL-06).</summary>
    public static readonly ErrorCode Forbidden = new("forbidden", 403, "Forbidden");

    public static readonly ErrorCode NotFound = new("not-found", 404, "Resource not found");
    public static readonly ErrorCode MethodNotAllowed = new("method-not-allowed", 405, "Method not allowed");
    public static readonly ErrorCode Conflict = new("conflict", 409, "Conflict");
    public static readonly ErrorCode PayloadTooLarge = new("payload-too-large", 413, "Payload too large");
    public static readonly ErrorCode UnsupportedMediaType = new("unsupported-media-type", 415, "Unsupported media type");
    public static readonly ErrorCode InternalError = new("internal-error", 500, "Internal server error");

    /// <summary>A downstream dependency is unavailable or its circuit is open (D6' §8.3).</summary>
    public static readonly ErrorCode DependencyUnavailable = new("dependency-unavailable", 503, "Dependency unavailable");

    public static readonly ErrorCode ServiceUnavailable = new("service-unavailable", 503, "Service unavailable");

    /// <summary>A downstream call exceeded its timeout budget (D6' §8.3).</summary>
    public static readonly ErrorCode UpstreamTimeout = new("upstream-timeout", 504, "Upstream timeout");

    // ---------------------------------------------------------------------------------------
    // Idempotency (R-14, R-18; D3' §0 "Idempotency"). Kernel-owned.
    // ---------------------------------------------------------------------------------------

    /// <summary><c>Idempotency-Key</c> is mandatory on POST mutations (D3' §0).</summary>
    public static readonly ErrorCode IdempotencyKeyRequired = new("idempotency-key-required", 400, "Idempotency-Key header required");

    /// <summary>Key present but not a ULID/UUID-shaped token of at most 128 characters (D3' §0).</summary>
    public static readonly ErrorCode IdempotencyKeyInvalid = new("idempotency-key-invalid", 400, "Idempotency-Key is malformed");

    /// <summary>Key already used for a <em>different</em> request payload — replay is not possible.</summary>
    public static readonly ErrorCode IdempotencyKeyReuse = new("idempotency-key-reuse", 409, "Idempotency-Key reused with a different request");

    /// <summary>The first request under this key has not finished yet; retry after it completes.</summary>
    public static readonly ErrorCode IdempotencyInProgress = new("idempotency-in-progress", 409, "A request with this Idempotency-Key is still in progress");

    // ---------------------------------------------------------------------------------------
    // Gateway edge (D-30, D-31). Enforced by the YARP gateway (C008); declared here so the
    // codes stay in one registry.
    // ---------------------------------------------------------------------------------------

    /// <summary>Play Integrity / App Attest header missing or invalid (D3' §0, D-30).</summary>
    public static readonly ErrorCode AttestationFailed = new("attestation-failed", 401, "App attestation failed");

    /// <summary>Client below the per-platform minimum version (D3' §0, D-31). Body carries
    /// <c>updateUrl</c>, <c>latestVersion</c>, <c>isMandatory</c>.</summary>
    public static readonly ErrorCode UpgradeRequired = new("upgrade-required", 426, "App upgrade required");

    /// <summary>Generic edge/token-bucket rejection (D3' §0, D3' public-bff family).</summary>
    public static readonly ErrorCode RateLimited = new("rate-limited", 429, "Rate limit exceeded");

    // ---------------------------------------------------------------------------------------
    // iam-svc (D3' §iam-svc)
    // ---------------------------------------------------------------------------------------

    public static readonly ErrorCode InvalidPhone = new("invalid-phone", 400, "Phone number is not a valid +94 number");
    public static readonly ErrorCode OtpExpired = new("otp-expired", 400, "OTP has expired");
    public static readonly ErrorCode InvalidOtp = new("invalid-otp", 401, "OTP is incorrect");
    public static readonly ErrorCode UserBlocked = new("user-blocked", 403, "User is blocked");
    public static readonly ErrorCode AuthNotFound = new("auth-not-found", 404, "Auth attempt not found");
    public static readonly ErrorCode DeviceMismatch = new("device-mismatch", 409, "Device does not match the bound device");

    /// <summary>OTP attempt window locked (D3' §0 "423 locked (OTP attempts)"). Kernel-named.</summary>
    public static readonly ErrorCode OtpLocked = new("otp-locked", 423, "Too many OTP attempts; locked");

    /// <summary>&gt;5 per hour or a resend inside the 60 s cooldown (D-32).</summary>
    public static readonly ErrorCode OtpRateLimited = new("otp-rate-limited", 429, "OTP rate limit exceeded");

    // ---------------------------------------------------------------------------------------
    // registry-svc / provisioning-svc (D3')
    // ---------------------------------------------------------------------------------------

    public static readonly ErrorCode InvalidVehicleType = new("invalid-vehicle-type", 400, "Unknown vehicle type");
    public static readonly ErrorCode CsvInvalid = new("csv-invalid", 400, "CSV could not be parsed");
    public static readonly ErrorCode ModeNotAllowed = new("mode-not-allowed", 403, "Operating mode not allowed on this surface");
    public static readonly ErrorCode NotOwner = new("not-owner", 403, "Caller does not own this resource");
    public static readonly ErrorCode VehicleNotApproved = new("vehicle-not-approved", 403, "Vehicle is not approved");
    public static readonly ErrorCode VehicleNotFound = new("vehicle-not-found", 404, "Vehicle not found");
    public static readonly ErrorCode RegistrationExists = new("registration-exists", 409, "An active registration already exists");
    public static readonly ErrorCode ImeiDuplicate = new("imei-duplicate", 409, "IMEI is already bound; both records quarantined");
    public static readonly ErrorCode TooManyRows = new("too-many-rows", 413, "Too many rows in upload");
    public static readonly ErrorCode BulkInProgress = new("bulk-in-progress", 429, "A bulk job is already running");

    // ---------------------------------------------------------------------------------------
    // trip-state-svc / ride-svc / dispatch-svc (D3')
    // ---------------------------------------------------------------------------------------

    public static readonly ErrorCode InvalidFareToken = new("invalid-fare-token", 400, "Fare token is invalid or expired");
    public static readonly ErrorCode IllegalTransition = new("illegal-transition", 400, "Illegal state transition");
    public static readonly ErrorCode PaymentMethodInvalid = new("payment-method-invalid", 402, "Payment method is not usable for this ride");
    public static readonly ErrorCode InsufficientWallet = new("insufficient-wallet", 402, "Wallet balance is insufficient");
    public static readonly ErrorCode BookingDisabled = new("booking-disabled", 403, "Booking is disabled for this account");
    public static readonly ErrorCode NotOnline = new("not-online", 403, "Driver is not online");
    public static readonly ErrorCode NotRideParticipant = new("not-ride-participant", 403, "Caller is not a participant in this ride");
    public static readonly ErrorCode ActiveRideExists = new("active-ride-exists", 409, "A non-terminal ride already exists");
    public static readonly ErrorCode DriverAlreadyLive = new("driver-already-live", 409, "Driver already has a live session");
    public static readonly ErrorCode OfferAlreadyAccepted = new("offer-already-accepted", 409, "Offer was already accepted by another driver");
    public static readonly ErrorCode RideTerminal = new("ride-terminal", 409, "Ride is in a terminal state");
    public static readonly ErrorCode VersionConflict = new("version-conflict", 409, "Optimistic concurrency conflict");
    public static readonly ErrorCode DirectionalLimitReached = new("directional-limit-reached", 409, "Directional Travel daily use limit reached");
    public static readonly ErrorCode OfferExpired = new("offer-expired", 410, "Offer has expired");
    public static readonly ErrorCode LocRequestRateLimited = new("loc-request-rate-limited", 429, "Location-request rate limit exceeded");

    // ---------------------------------------------------------------------------------------
    // fare-svc / wallet-svc / fleet-svc (D3')
    // ---------------------------------------------------------------------------------------

    public static readonly ErrorCode InvalidAmount = new("invalid-amount", 400, "Amount is invalid");
    public static readonly ErrorCode UnserviceableArea = new("unserviceable-area", 400, "Area is not serviceable");
    public static readonly ErrorCode GatewayError = new("gateway-error", 402, "Payment gateway returned an error");
    public static readonly ErrorCode MerchantNotOnboarded = new("merchant-not-onboarded", 402, "Driver has no OnePay merchant account");
    public static readonly ErrorCode PaymentAlreadySettled = new("payment-already-settled", 409, "Payment is already settled");
    public static readonly ErrorCode PayoutProfileNotVerified = new("payout-profile-not-verified", 409, "Fleet payout profile is not verified");
    public static readonly ErrorCode RouteUnavailable = new("route-unavailable", 422, "No route could be computed");

    // ---------------------------------------------------------------------------------------
    // safety-svc / public-bff (D3')
    // ---------------------------------------------------------------------------------------

    public static readonly ErrorCode NoEmergencyContact = new("no-emergency-contact", 400, "No emergency contact on file");
    public static readonly ErrorCode TokenUnknown = new("token-unknown", 404, "Share token is unknown");
    public static readonly ErrorCode TokenExpiredOrRevoked = new("token-expired-or-revoked", 410, "Share token has expired or was revoked");

    // ---------------------------------------------------------------------------------------
    // Registry plumbing
    // ---------------------------------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, ErrorCode> Registry = BuildDeclaredRegistry();

    /// <summary>Every registered code, ordered by key.</summary>
    public static IReadOnlyCollection<ErrorCode> All =>
        Registry.Values.OrderBy(static e => e.Code, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Registers a service-owned code. Idempotent for an identical entry; throws if the same key
    /// is already registered with a different status or title.
    /// </summary>
    public static ErrorCode Register(ErrorCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        Validate(code);

        var existing = Registry.GetOrAdd(code.Code, code);
        if (existing != code)
        {
            throw new InvalidOperationException(
                $"Error code '{code.Code}' is already registered as {existing.Status} \"{existing.Title}\" " +
                $"and cannot be redefined as {code.Status} \"{code.Title}\". Error codes are a public contract.");
        }

        return existing;
    }

    /// <inheritdoc cref="Register(ErrorCode)"/>
    public static ErrorCode Register(string code, int status, string title) =>
        Register(new ErrorCode(code, status, title));

    public static bool TryGet(string code, [NotNullWhen(true)] out ErrorCode? error) =>
        Registry.TryGetValue(code, out error);

    /// <summary>Maps a bare HTTP status to the kernel code services fall back to.</summary>
    public static ErrorCode ForStatus(int status) => status switch
    {
        400 => BadRequest,
        401 => Unauthorized,
        403 => Forbidden,
        404 => NotFound,
        405 => MethodNotAllowed,
        409 => Conflict,
        413 => PayloadTooLarge,
        415 => UnsupportedMediaType,
        426 => UpgradeRequired,
        429 => RateLimited,
        503 => ServiceUnavailable,
        504 => UpstreamTimeout,
        _ => status >= 500 ? InternalError : BadRequest,
    };

    private static ConcurrentDictionary<string, ErrorCode> BuildDeclaredRegistry()
    {
        var registry = new ConcurrentDictionary<string, ErrorCode>(StringComparer.Ordinal);

        foreach (var field in typeof(MageRideErrors).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(ErrorCode) || field.GetValue(null) is not ErrorCode code)
            {
                continue;
            }

            Validate(code);
            if (!registry.TryAdd(code.Code, code))
            {
                throw new InvalidOperationException($"Duplicate error code '{code.Code}' declared in {nameof(MageRideErrors)}.");
            }
        }

        return registry;
    }

    private static void Validate(ErrorCode code)
    {
        if (!KebabCase().IsMatch(code.Code))
        {
            throw new ArgumentException(
                $"Error code '{code.Code}' is not a stable kebab key (lower-case a-z0-9 separated by single hyphens).",
                nameof(code));
        }

        if (code.Status is < 400 or > 599)
        {
            throw new ArgumentException($"Error code '{code.Code}' maps to status {code.Status}; only 4xx/5xx are error codes.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(code.Title))
        {
            throw new ArgumentException($"Error code '{code.Code}' has no title.", nameof(code));
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KebabCase();
}
