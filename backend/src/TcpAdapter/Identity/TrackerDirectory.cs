using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MageRide.Shared.Caching;
using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Observability;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.TcpAdapter.Identity;

/// <summary>Why a device was let in, or was not.</summary>
public enum AuthOutcome
{
    /// <summary>Bound, ACTIVE, and — where one was presented — holding a credential that verified.</summary>
    Authorised,

    /// <summary>The identity on the wire is not a 15-digit IMEI, so nothing could ever have bound it.</summary>
    MalformedIdentity,

    /// <summary>No binding. A device nobody provisioned, or one whose binding was released.</summary>
    NotBound,

    /// <summary>The binding is REVOKED (T-12).</summary>
    Revoked,

    /// <summary>The binding is QUARANTINED — two devices claimed this IMEI (T-08).</summary>
    Quarantined,

    /// <summary>A credential was presented and it is forged, expired, or issued to another device.</summary>
    BadCredential,

    /// <summary>No credential was presented and <c>Adapter:RequireCredential</c> is on.</summary>
    CredentialRequired,

    /// <summary>Neither the cache nor provisioning-svc could answer. Refused, deliberately.</summary>
    Unavailable,
}

/// <summary>The verdict on one device's connect.</summary>
/// <param name="Outcome">Why.</param>
/// <param name="VehicleId">The vehicle its samples publish under. <see cref="Guid.Empty"/> unless authorised.</param>
/// <param name="CredentialSerial">
/// The serial the device's credential names, when it presented one. Carried past the verdict because
/// <c>validate</c> takes it as anti-clone evidence and because the T-12 revocation signal matches on it.
/// </param>
/// <param name="Detail">One line for the log.</param>
public sealed record TrackerAuthorisation(
    AuthOutcome Outcome, Guid VehicleId = default, string? CredentialSerial = null, string? Detail = null)
{
    /// <summary>Whether the device may publish.</summary>
    public bool IsAuthorised => Outcome == AuthOutcome.Authorised && VehicleId != Guid.Empty;
}

/// <summary>
/// Resolves a device identity to a vehicle, and says so again every few minutes (T-01, T-03, T-12).
/// </summary>
public interface ITrackerDirectory
{
    /// <summary>
    /// Authenticates one device.
    /// </summary>
    /// <param name="identity">The digits the protocol frame presented.</param>
    /// <param name="credential">A credential from the frame, where the protocol carries one.</param>
    /// <param name="peer">The device's address, passed on as audit evidence.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<TrackerAuthorisation> AuthenticateAsync(
        string? identity, string? credential, IPAddress? peer, CancellationToken cancellationToken);

    /// <summary>
    /// Reports one IMEI seen on two live sockets at once — T-08's adapter half.
    /// </summary>
    /// <remarks>
    /// C030's fence: at <c>bind</c> a clone arrives with two identities and is decidable there; at
    /// the adapter it presents a <i>copy</i> of the genuine credential and what tells them apart is
    /// two live sockets holding one identity, which only this service can see. So it reports, and
    /// provisioning-svc adjudicates.
    /// </remarks>
    Task ReportCloneAsync(string imei, string detail, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrackerDirectory"/>
/// <remarks>
/// <para>
/// <b>Two sources, in this order and for a reason.</b> <c>imei:{imei}</c> is read first: present means
/// ACTIVE (C030's rule, and there is no cached "revoked"), so a hit is the whole answer and a fleet of
/// buses keeps publishing through a provisioning-svc restart. A miss goes to
/// <c>GET /v1/internal/trackers/{imei}/validate</c>, which is authoritative and primes the cache as a
/// side effect.
/// </para>
/// <para>
/// <b>A presented credential always goes to <c>validate</c>.</b> The cache holds one value per IMEI
/// and cannot evaluate the anti-clone rule; the serial has to reach the service that records
/// sightings, so the fast path is skipped whenever there is one.
/// </para>
/// <para>
/// <b>Unresolvable means refused.</b> Not "allowed pending confirmation" — an adapter that admitted
/// devices while it could not check them would publish positions for revoked trackers for as long as
/// the outage lasted, and the failure direction C030 chose deliberately is the other one.
/// </para>
/// </remarks>
public sealed class TrackerDirectory(
    IHttpClientFactory clients,
    IConnectionMultiplexer redis,
    PskCredentials credentials,
    IOptions<AdapterOptions> options,
    TimeProvider clock,
    ILogger<TrackerDirectory> logger) : ITrackerDirectory
{
    /// <summary>
    /// Named client so C042 can swap the handler for an mTLS one in one place.
    /// </summary>
    /// <remarks>
    /// Taken from the factory per call rather than injected as an <see cref="HttpClient"/>. This service
    /// is a singleton — every session on the pod shares it — and a singleton holding one typed client
    /// holds one <see cref="HttpMessageHandler"/> for the life of the process, which never picks up a
    /// changed DNS answer. In a cluster where provisioning-svc's address moves on a rollout that is a
    /// pod that refuses every device until it is restarted.
    /// </remarks>
    public const string HttpClientName = "provisioning-svc-trackers";

    /// <summary>provisioning-svc's interim guard on <c>/v1/internal/**</c> (D3' §0).</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    /// <summary>The query parameter <c>validate</c> takes the presented credential's serial in.</summary>
    public const string CredentialSerialQuery = "credentialSerial";

    /// <summary><c>prov.tracker_bindings.state</c> values. Spelled here — this project holds no reference to C030.</summary>
    private const string StateRevoked = "REVOKED";

    private const string StateQuarantined = "QUARANTINED";

    private readonly AdapterOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Whether a string is a well-formed IMEI — <c>provisioning.yaml</c>'s <c>^\d{15}$</c>.</summary>
    /// <remarks>
    /// The Luhn check digit is deliberately not enforced, matching C030: D6' §4.1's grey-import
    /// GT06/JT808 units report IMEIs that fail it, and refusing one leaves a working tracker
    /// unprovisionable with no override.
    /// </remarks>
    public static bool IsImei(string? value) =>
        value is { Length: 15 } && value.All(char.IsAsciiDigit);

    public async Task<TrackerAuthorisation> AuthenticateAsync(
        string? identity, string? credential, IPAddress? peer, CancellationToken cancellationToken)
    {
        if (!IsImei(identity))
        {
            // Includes the JT/T 808-2013 case: a six-byte BCD terminal phone number is twelve digits
            // and an IMEI is fifteen, so such a device presents an identity no binding can carry.
            // Named rather than folded into NotBound, because the two need different fixes.
            return new TrackerAuthorisation(
                AuthOutcome.MalformedIdentity,
                Detail: $"'{Redact(identity)}' is not a 15-digit IMEI");
        }

        var imei = identity!;
        string? serial = null;

        if (credential is not null)
        {
            var verified = credentials.TryRead(credential, imei, clock.GetUtcNow(), out var read);
            serial = string.IsNullOrEmpty(read) ? null : read;

            if (!verified && credentials.CanVerify && PskCredentials.LooksLikeToken(credential))
            {
                // A token that is shaped right and does not verify is the case this check exists for:
                // forged, expired, or lifted off another device. Refused without a network call.
                return new TrackerAuthorisation(
                    AuthOutcome.BadCredential, CredentialSerial: serial, Detail: "PSK signature did not verify");
            }
        }
        else if (_options.RequireCredential)
        {
            return new TrackerAuthorisation(
                AuthOutcome.CredentialRequired, Detail: "no credential presented and Adapter:RequireCredential is on");
        }

        if (serial is null && await ResolveFromCacheAsync(imei) is { } cached)
        {
            return new TrackerAuthorisation(AuthOutcome.Authorised, cached, Detail: "imei cache");
        }

        return await ValidateAsync(imei, serial, peer, cancellationToken);
    }

    public async Task ReportCloneAsync(string imei, string detail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imei);

        if (!_options.ReportDuplicateSockets || string.IsNullOrWhiteSpace(_options.ProvisioningBaseUrl))
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            reportedBy = _options.ServiceName,
            detail,
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/internal/trackers/{Uri.EscapeDataString(imei)}/quarantine")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        AddApiKey(request);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ProvisioningTimeout);

            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, timeout.Token);

            // 204 whether or not there was an ACTIVE binding to hold — a second report of the same
            // clone is not an error (C030). Anything else is worth a line, because a clone that went
            // unreported keeps publishing.
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "provisioning-svc answered {Status} to the T-08 clone report for IMEI {Imei}",
                    (int)response.StatusCode, imei);

                return;
            }

            AdapterDiagnostics.ClonesReported.Add(1);
            logger.LogWarning("Reported IMEI {Imei} to provisioning-svc as a possible clone: {Detail}", imei, detail);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Could not report IMEI {Imei} as a possible clone; both sockets stay open and the " +
                "binding is not held", imei);
        }
    }

    private async Task<Guid?> ResolveFromCacheAsync(string imei)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(RedisKeys.Imei(imei));

            return value.IsNullOrEmpty || !Guid.TryParse(value.ToString(), out var vehicleId) || vehicleId == Guid.Empty
                ? null
                : vehicleId;
        }
        catch (RedisException exception)
        {
            // A miss and an outage are the same answer here — go and ask provisioning-svc.
            logger.LogWarning(exception, "IMEI cache read failed for {Imei}; falling back to validate", imei);
            return null;
        }
    }

    private async Task<TrackerAuthorisation> ValidateAsync(
        string imei, string? serial, IPAddress? peer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ProvisioningBaseUrl))
        {
            return new TrackerAuthorisation(
                AuthOutcome.Unavailable,
                CredentialSerial: serial,
                Detail: "Adapter:ProvisioningBaseUrl is not configured");
        }

        var path = $"/v1/internal/trackers/{Uri.EscapeDataString(imei)}/validate";

        if (serial is not null)
        {
            path += $"?{CredentialSerialQuery}={Uri.EscapeDataString(serial)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        AddApiKey(request);

        if (peer is not null)
        {
            // The adapter terminated the device's socket and calls provisioning-svc over its own, so
            // the far end sees this process's address. C030 reads the first X-Forwarded-For entry as
            // the device — evidence in an audit trail, never an authorisation input.
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", peer.ToString());
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ProvisioningTimeout);

            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                // 404 included, and it is the likeliest one: C030 does not map
                // /v1/internal/trackers/** at all unless Provisioning:InternalApiKey is set, so a
                // missing key at that end looks exactly like this.
                logger.LogError(
                    "provisioning-svc answered {Status} to validate for IMEI {Imei}; refusing the device",
                    (int)response.StatusCode, imei);

                return new TrackerAuthorisation(
                    AuthOutcome.Unavailable,
                    CredentialSerial: serial,
                    Detail: $"validate answered {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            return Interpret(body, serial);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "provisioning-svc could not be reached to validate IMEI {Imei}", imei);

            return new TrackerAuthorisation(
                AuthOutcome.Unavailable, CredentialSerial: serial, Detail: "validate unreachable");
        }
    }

    private TrackerAuthorisation Interpret(string body, string? serial)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var valid = root.TryGetProperty("valid", out var validElement)
                        && validElement.ValueKind == JsonValueKind.True;

            var state = root.TryGetProperty("state", out var stateElement) && stateElement.ValueKind == JsonValueKind.String
                ? stateElement.GetString()
                : null;

            if (!valid)
            {
                // provisioning-svc answers 200 with a verdict for every IMEI, including one it has
                // never heard of — the state distinguishes "nobody bound this" from "held".
                return new TrackerAuthorisation(
                    state switch
                    {
                        StateRevoked => AuthOutcome.Revoked,
                        StateQuarantined => AuthOutcome.Quarantined,
                        _ => AuthOutcome.NotBound,
                    },
                    CredentialSerial: serial,
                    Detail: state is null ? "no binding" : $"binding is {state}");
            }

            if (!root.TryGetProperty("vehicleId", out var vehicleElement)
                || vehicleElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(vehicleElement.GetString(), out var vehicleId)
                || vehicleId == Guid.Empty)
            {
                // A "valid" verdict naming no vehicle cannot produce a topic to publish on, so it is
                // treated as unreadable rather than as an authorised device.
                return new TrackerAuthorisation(
                    AuthOutcome.Unavailable, CredentialSerial: serial, Detail: "validate named no vehicle");
            }

            return new TrackerAuthorisation(AuthOutcome.Authorised, vehicleId, serial, "validate");
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "provisioning-svc's validate answer could not be read");

            return new TrackerAuthorisation(
                AuthOutcome.Unavailable, CredentialSerial: serial, Detail: "validate answer unreadable");
        }
    }

    private void AddApiKey(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.ProvisioningInternalApiKey))
        {
            request.Headers.Add(ApiKeyHeader, _options.ProvisioningInternalApiKey);
        }
    }

    /// <summary>
    /// What a rejected identity is allowed to put in a log line.
    /// </summary>
    /// <remarks>
    /// The bytes came off an unauthenticated socket, so they are attacker-controlled: a raw echo
    /// would let anybody write newlines and ANSI escapes into the operator's log. Digits and a length
    /// are all that is needed to tell a mis-provisioned device from a port scanner.
    /// </remarks>
    private static string Redact(string? identity)
    {
        if (string.IsNullOrEmpty(identity))
        {
            return "(empty)";
        }

        var digits = new string(identity.Where(char.IsAsciiDigit).Take(20).ToArray());

        return digits.Length == identity.Length
            ? digits
            : $"{digits}(+{(identity.Length - digits.Length).ToString(CultureInfo.InvariantCulture)} non-digit)";
    }
}
