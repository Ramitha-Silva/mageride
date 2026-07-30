using System.Globalization;
using System.Text;
using System.Text.Json;
using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Observability;
using Microsoft.Extensions.Options;

namespace MageRide.TcpAdapter.Ingest;

/// <summary>
/// Reports an ACC transition to trip-state-svc, which is what auto-starts and auto-ends a
/// tracker-equipped Mode A/B session (AL-32, US-3.22/3.23).
/// </summary>
/// <remarks>
/// <c>POST /v1/internal/sessions/ignition</c> has existed since C031 and has had no caller:
/// <c>trip-state.yaml</c> says in as many words that "the tracker plane decodes ACC out of a
/// GT06/JT808 frame (<c>tcp-adapter</c>, C043) and had nowhere to say so". This is that caller. It is
/// <b>not</b> in C043's deliverable list — recorded in the handoff — but a landed endpoint with no
/// caller means a fleet's buses never start a journey, and the decode was going to happen here either
/// way: the ACC line is a bit in a status byte only this service parses.
/// </remarks>
public interface IIgnitionReporter
{
    /// <summary>Reports one transition. Never throws, and never retried.</summary>
    Task ReportAsync(Guid vehicleId, bool on, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IIgnitionReporter"/>
public sealed class TripStateIgnitionReporter(
    IHttpClientFactory clients,
    IOptions<AdapterOptions> options,
    ILogger<TripStateIgnitionReporter> logger) : IIgnitionReporter
{
    /// <summary>
    /// Named client so C042 can swap the handler for an mTLS one in one place. Resolved per call —
    /// see <see cref="Identity.TrackerDirectory.HttpClientName"/> for why a singleton must not hold one.
    /// </summary>
    public const string HttpClientName = "trip-state-svc-ignition";

    /// <summary>trip-state-svc's interim guard on <c>/v1/internal/**</c>.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    private readonly AdapterOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task ReportAsync(Guid vehicleId, bool on, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.TripStateBaseUrl))
        {
            return;
        }

        var state = on ? "on" : "off";

        var payload = JsonSerializer.Serialize(new
        {
            vehicleId = vehicleId.ToString(),
            state,
            at = at.ToString("O", CultureInfo.InvariantCulture),
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/internal/sessions/ignition")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(_options.TripStateInternalApiKey))
        {
            request.Headers.Add(ApiKeyHeader, _options.TripStateInternalApiKey);
        }

        // Derived from the transition rather than random: a device that re-sends the same status frame
        // — which GT06 units do on every heartbeat — must not open a second session, and the kernel's
        // command log (R-14) is what makes the repeat a replay.
        request.Headers.Add(
            "Idempotency-Key",
            $"ignition-{vehicleId}-{state}-{at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.TripStateTimeout);

            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "trip-state-svc answered {Status} to the ignition-{State} report for vehicle {VehicleId}",
                    (int)response.StatusCode, state, vehicleId);

                AdapterDiagnostics.IgnitionReports.Add(1, AdapterDiagnostics.Tag("outcome", "rejected"));
                return;
            }

            // 202, and the outcome is informational: whether the report opened a session, closed one
            // or did nothing is that service's decision, and `declined` is not a failure to retry.
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            AdapterDiagnostics.IgnitionReports.Add(1, AdapterDiagnostics.Tag("outcome", ReadOutcome(body)));
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                             && !cancellationToken.IsCancellationRequested)
        {
            // Not retried. The next status frame carries the same ACC state and the transition is
            // re-derived from it, so a missed report costs a session start until the next heartbeat
            // rather than for ever — and a retry queue here would replay an ignition an hour later.
            logger.LogWarning(
                exception, "Could not report ignition-{State} for vehicle {VehicleId}", state, vehicleId);

            AdapterDiagnostics.IgnitionReports.Add(1, AdapterDiagnostics.Tag("outcome", "unreachable"));
        }
    }

    private static string ReadOutcome(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.TryGetProperty("outcome", out var outcome)
                   && outcome.ValueKind == JsonValueKind.String
                ? outcome.GetString() ?? "unknown"
                : "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }
}
