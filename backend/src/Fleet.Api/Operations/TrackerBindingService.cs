using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MageRide.Fleet.Configuration;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet.Operations;

/// <summary>What US-13.12 binds: an ST-901's IMEI to one of the org's vehicles.</summary>
public sealed record BindTrackerCommand(string? Imei, string? VehicleId, bool AutoStartSession);

/// <summary>What provisioning-svc answered.</summary>
public sealed record TrackerBinding(Guid BindingId, string Imei, Guid VehicleId);

/// <summary>
/// The tracker-binding hand-off to provisioning-svc (US-13.12, T-02).
/// </summary>
/// <remarks>
/// <para>
/// <b>This service mints nothing.</b> A binding is a per-device credential signed by the platform's
/// CA, and the CA is provisioning-svc's — its private key is on that service's volume and nowhere
/// else (C030). What fleet-svc adds is the org scope: the vehicle in the path must be on the
/// caller's roster, checked here, before the request is forwarded.
/// </para>
/// <para>
/// <b>The caller's own bearer is forwarded, never a service credential.</b> provisioning-svc scopes
/// <c>POST /v1/trackers/bind</c> to what the caller may do with the *vehicle* — "owning the
/// vehicle, or running the fleet whose roster carries it (AL-03)" — so passing the operator's token
/// keeps that check where it is and means this hop can grant nothing the operator did not already
/// have. Exactly what subscription-svc's forwarded wallet routes do, for the same reason.
/// </para>
/// <para>
/// <b>An upstream refusal is passed through with its own code.</b> provisioning-svc answers
/// <c>409 imei-duplicate</c> when a live IMEI is claimed a second time — T-08's anti-clone rule,
/// which quarantines both records — and an operator has to see that, not a generic 502. Only a
/// transport failure becomes this service's error.
/// </para>
/// <para>
/// <b><c>autoStartSession</c> is accepted and is not yet armed.</b> AL-32/T-11 make tracker-driven
/// journey auto-start a property of the ingest path (tcp-adapter and trip-state-svc), not of the
/// binding row: provisioning-svc's bind body has no field for it and `prov.tracker_bindings` no
/// column. Sending it would be inventing a contract; dropping it silently would be worse. It is
/// logged when false, and raised in the C059 handoff.
/// </para>
/// </remarks>
public interface ITrackerBindingService
{
    /// <summary>Forwards US-13.12's bind to provisioning-svc under the caller's own bearer.</summary>
    /// <remarks>
    /// <b>Not <c>BindAsync</c>, and the name is load-bearing.</b> Minimal APIs treat any parameter
    /// type carrying a method called <c>BindAsync</c> as custom-bound, so a handler taking this
    /// interface makes <c>RequestDelegateFactory</c> look for
    /// <c>ValueTask&lt;ITrackerBindingService?&gt; BindAsync(HttpContext, ParameterInfo)</c>, find
    /// this instead, and <b>throw while building the route table</b> — fleet-svc does not start at
    /// all. It is invisible until <c>Fleet:ProvisioningBaseUrl</c> is set, because that is what maps
    /// the only route with this parameter; C059's own suite leaves it unset, so the failure first
    /// appeared when C121's fleet configured the hop. The same trap C030 hit with
    /// <c>ITrackerService</c> and records at <c>TrackerService</c>.
    /// </remarks>
    Task<TrackerBinding> BindTrackerAsync(
        Guid fleetId, string bearer, BindTrackerCommand command, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrackerBindingService"/>
internal sealed class TrackerBindingService(
    IHttpClientFactory clients,
    IFleetScopedReader scopedReader,
    IFleetVehicleRepository vehicles,
    ILogger<TrackerBindingService> logger) : ITrackerBindingService
{
    /// <summary>The named client, so the base address and the timeout live in one place.</summary>
    public const string HttpClientName = "provisioning-svc";

    public async Task<TrackerBinding> BindTrackerAsync(
        Guid fleetId, string bearer, BindTrackerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearer);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var imei = command.Imei?.Trim() ?? string.Empty;

        // `^\d{15}$`, the contract's pattern. The Luhn check digit is deliberately not enforced,
        // for C030's reason: D6' §4.1's grey-import GT06/JT808 units report IMEIs that fail it, and
        // refusing one leaves a working tracker unprovisionable with no override.
        if (imei.Length != 15 || !imei.All(char.IsAsciiDigit))
        {
            errors["imei"] = ["imei must be 15 digits."];
        }

        if (!MageRide.Shared.Primitives.Ulids.TryParse(command.VehicleId, out var vehicleId)
            || vehicleId == Guid.Empty)
        {
            errors["vehicleId"] = ["vehicleId is required and must be a ULID or a UUID."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        // The org scope, before anything leaves this process. provisioning-svc would refuse a
        // vehicle that is not the caller's too, but it would do so as a `403 forbidden` about
        // ownership; "that vehicle is not in your fleet" is the answer the Fleet Portal renders.
        _ = await scopedReader.ReadAsync(
            fleetId,
            (connection, transaction) => vehicles.FindAsync(
                connection, transaction, fleetId, vehicleId, cancellationToken),
            cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.VehicleNotFound, "This vehicle is not in the organisation's fleet.");

        if (!command.AutoStartSession)
        {
            logger.LogWarning(
                "Fleet {FleetId} asked for tracker {Imei} to be bound with autoStartSession=false. There is no "
                + "per-binding switch for it anywhere in the tracker plane — AL-32/T-11 make auto-start a property "
                + "of the ingest path — so the request is honoured as a bind and the flag has no effect (C059).",
                fleetId,
                imei);
        }

        var client = clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/trackers/bind")
        {
            Content = JsonContent.Create(
                new ProvisioningBindBody(imei, vehicleId.ToString(), "manual", "x509"),
                options: MageRideJson.Options),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        // A fresh key per attempt: this is one operator pressing Bind once, and provisioning-svc's
        // own replay log is what a genuine client retry would ride on.
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "provisioning-svc could not be reached to bind tracker {Imei}.", imei);

            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "The tracker plane is unavailable, so the ST-901 was not bound. Nothing was changed.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await UpstreamFailureAsync(response, cancellationToken);
            }

            var bound = await response.Content.ReadFromJsonAsync<ProvisioningBindResponse>(
                MageRideJson.Options, cancellationToken);

            if (bound is null || !Guid.TryParse(bound.BindingId, out var bindingId))
            {
                throw new MageRideException(
                    MageRideErrors.DependencyUnavailable,
                    "The tracker plane answered a binding this service could not read.");
            }

            logger.LogInformation(
                "Fleet {FleetId} bound tracker {Imei} to vehicle {VehicleId} (binding {BindingId}).",
                fleetId,
                imei,
                vehicleId,
                bindingId);

            return new TrackerBinding(bindingId, imei, vehicleId);
        }
    }

    /// <summary>
    /// Turns provisioning-svc's RFC 7807 body back into this service's exception, code intact.
    /// </summary>
    /// <remarks>
    /// The <c>type</c> URI's last segment is the registry code (D3' §0), and both services share
    /// that registry — so <c>imei-duplicate</c> arrives here as <c>imei-duplicate</c> and reaches
    /// the portal as the screen T-08 needs. A body that cannot be read at all becomes
    /// <c>dependency-unavailable</c>, because inventing a code the caller would branch on is worse
    /// than admitting the hop failed.
    /// </remarks>
    private async Task<MageRideException> UpstreamFailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        try
        {
            using var problem = JsonDocument.Parse(body);

            if (problem.RootElement.TryGetProperty("type", out var type)
                && type.GetString()?.Split('/')[^1] is { Length: > 0 } code
                && MageRideErrors.TryGet(code, out var known))
            {
                var detail = problem.RootElement.TryGetProperty("detail", out var explanation)
                    ? explanation.GetString()
                    : null;

                return new MageRideException(known, detail);
            }
        }
        catch (JsonException)
        {
            // Not a problem+json body at all — fall through to the generic answer below.
        }

        logger.LogWarning(
            "provisioning-svc answered {Status} to a tracker bind with a body this service could not map: {Body}",
            (int)response.StatusCode,
            body);

        return new MageRideException(
            response.StatusCode == HttpStatusCode.Unauthorized
                ? MageRideErrors.Unauthorized
                : MageRideErrors.DependencyUnavailable,
            "The tracker plane refused the binding.");
    }

    /// <summary>
    /// provisioning-svc's <c>BindTrackerBody</c>, as far as this hop fills it.
    /// </summary>
    /// <remarks>
    /// <c>method: manual</c> because the Fleet Portal types an IMEI rather than scanning a QR or
    /// entering a bind code (T-02's three paths); <c>credentialType: x509</c> because that is what
    /// an MQTT-capable ST-901 needs and what D6' §4.1 lists as the current-firmware path — the same
    /// default provisioning-svc's own bulk upload takes.
    /// </remarks>
    private sealed record ProvisioningBindBody(string Imei, string VehicleId, string Method, string CredentialType);

    private sealed record ProvisioningBindResponse(string? BindingId, string? Imei, string? VehicleId);
}
