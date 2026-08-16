using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using MageRide.Contract.Tests.Model;

namespace MageRide.Contract.Tests.Live;

/// <summary>
/// How one contract operation becomes one request it is SAFE to send to a running deployment.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole problem is that this suite drives a real platform.</b> A sweep that sent a plausible
/// body to every mutating operation would cancel rides, suspend drivers, file PDPA erasures, fan out
/// SOS alerts to real gateways and inject payment state — on a replica today, and on whatever
/// somebody points the variables at tomorrow. So no request here is allowed to change state, and
/// that is achieved by construction rather than by care:
/// </para>
/// <list type="bullet">
/// <item><b>Reads go through as reads.</b> A GET with an id that cannot exist answers 404, which is
/// a contract-declared response and proves the route, the auth and the handler all ran.</item>
/// <item><b>POSTs are refused before the handler.</b> The C002 kernel demands
/// <c>Idempotency-Key</c> on POST mutations, so omitting it is a deterministic
/// <c>400 idempotency-key-required</c> from the middleware — measured across ~180 POSTs, not one
/// reached business logic.</item>
/// <item><b>PUT, PATCH and DELETE are only driven when the path carries an id</b>, so the absent id
/// makes them a no-op. The kernel does <em>not</em> gate those verbs, and the first run of this sweep
/// proved it the hard way — see <see cref="IsSafeToDrive"/>.</item>
/// <item><b>Anything that could act anyway is named and excluded</b>, with the reason, below. That
/// list is not complete by inspection: two PDPA operations were added to it only after the sweep had
/// filed the obligations itself. Treat it as a ledger of what has been thought about, and add to it
/// before pointing this at anything whose state you care about.</item>
/// </list>
/// <para>
/// This is why the sweep asserts <em>reachability and shape</em> rather than behaviour: behaviour is
/// `tests/E2E`'s, where a scenario owns the state it creates and tears down.
/// </para>
/// </remarks>
internal static class LiveRequestPlan
{
    /// <summary>
    /// A UUID no row will ever carry, so every id-shaped path parameter resolves to "not found"
    /// rather than to somebody's ride. Deliberately not <c>Guid.Empty</c>, which several services
    /// treat as "unset" and validate differently.
    /// </summary>
    private const string AbsentId = "ffffffff-0000-4000-8000-ffffffffffff";

    /// <summary>
    /// Operations this transport must never send, and why. Each one is dangerous or meaningless
    /// rather than merely awkward — an entry here is a decision, not a workaround for a failure.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Excluded =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DELETE /v1/users/me"] =
                "files a PDPA erasure request against the caller — the sweep's own operator account "
                + "(US-1.8, E-06). A second request while one is open is a 409, so it cannot even be "
                + "retried away.",
            ["POST /v1/auth/logout"] =
                "revokes the bearer every later operation in the sweep depends on.",
            ["POST /v1/auth/refresh"] =
                "rotates the refresh family and single-uses the token (D-29); the sweep would be "
                + "invalidating a session it did not issue.",
            ["POST /v1/sos"] =
                "D-33 fans out to two SMS gateways and a support queue inside five seconds. A "
                + "synthetic SOS is indistinguishable from a real one at the receiving end.",
            ["POST /v1/sos/{sosId}/cancel"] =
                "the other half of the SOS pair; cancelling an alert nobody raised is still an "
                + "operator-visible event.",
            ["POST /v1/auth/otp/request"] =
                "sends an SMS and spends money (D-32 fails closed on the rate bucket, which is the "
                + "control that protects the bill).",
            ["POST /v1/auth/otp/resend"] = "as above.",

            // Δ FOUND BY THIS SWEEP DOING IT. The first run filed both a PDPA erasure AND a PDPA
            // export against its own operator account — 30-day statutory clocks, visible in
            // audit.events as PDPA_REQUESTED, rows in pdpa.requests. `DELETE /v1/users/me` was
            // excluded; these two were missed, and they need no Idempotency-Key, so nothing stopped
            // them. The erasure is the dangerous one: fulfilment soft-anonymises the account (E-06),
            // which on this replica is the only internal account there is.
            ["POST /v1/pdpa/export"] =
                "files a real PDPA data-export obligation with a 30-day fulfilment clock (US-1.8, "
                + "E-06). Nothing about it is undone by the request failing later.",
            ["POST /v1/pdpa/erasure"] =
                "files a real PDPA ERASURE obligation. C065's fulfilment soft-anonymises the "
                + "subject; on a replica whose only internal account is the sweep's own operator, "
                + "that is the credential this suite runs on.",
        };

    /// <summary>
    /// Writes with no path parameter are not driven at all — the rule that closes the hole the first
    /// run walked through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original design claimed writes were "refused before the handler" because the C002 kernel
    /// demands <c>Idempotency-Key</c> on POST mutations. That is true of POST — ~180 of them were
    /// driven and not one returned 2xx — and <b>not true of PUT and PATCH</b>, which the kernel does
    /// not gate. Three of them answered 200: <c>PUT /v1/users/me</c>,
    /// <c>PUT /v1/admin/dispatch/directional-config</c> and <c>PUT /v1/admin/drivers/level-config</c>.
    /// An empty body bound to "no fields supplied", so the two configuration rows were rewritten with
    /// the values they already held — no semantic change, but only because they happened to be at
    /// their migration defaults. On a tuned deployment that PUT is a config wipe.
    /// </para>
    /// <para>
    /// So a write is only driven when its template carries a path parameter, because then the absent
    /// id makes the operation a no-op before any state is touched. A write addressed at "me" or at a
    /// singleton config row has nothing to miss, and is left alone.
    /// </para>
    /// </remarks>
    public static bool IsSafeToDrive(ContractOperation operation, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var isPut = operation.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
                    || operation.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)
                    || operation.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);

        if (isPut && !operation.Template.Contains('{', StringComparison.Ordinal))
        {
            reason =
                $"a {operation.Method.ToUpperInvariant()} with no path parameter has no absent id to "
                + "make it a no-op, and the kernel's Idempotency-Key requirement covers POST only — "
                + "so driving it would WRITE. Reachability for these is RouteTableTests' to prove.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Whether an operation is one this transport can reach at all. `/v1/internal/**` is mTLS-only
    /// and refused ahead of routing by the gateway (D3' §0) — that refusal is asserted separately
    /// rather than pretended to be conformance.
    /// </summary>
    public static bool IsDrivable(ContractOperation operation, out string? reason)
    {
        if (Excluded.TryGetValue($"{operation.Method} {operation.Template}", out var excluded))
        {
            reason = excluded;
            return false;
        }

        if (operation.IsInternal)
        {
            reason = "on the mTLS-only internal plane, which the edge refuses by design (D3' §0).";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>Builds the request for an operation, and says what answer would prove it healthy.</summary>
    public static HttpRequestMessage Build(ContractOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var path = Path(operation);
        var request = new HttpRequestMessage(new HttpMethod(operation.Method.ToUpperInvariant()), path);

        request.Headers.Host = LiveEdge.HostHeader;

        if (LiveEdge.Token is { } token && !operation.IsPublic)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // D-31's gate answers 426 to a client that declares nothing, on every route that is not
        // exempt. Declaring a current client is what a phone does, and what keeps the sweep talking
        // about the operation rather than about the gate.
        request.Headers.TryAddWithoutValidation("X-Client-Version", "99.0.0");
        request.Headers.TryAddWithoutValidation("X-Client-Platform", "android");

        if (IsWrite(operation.Method))
        {
            // NO Idempotency-Key, on purpose — see the class remark. An empty JSON object rather
            // than no body at all, so a service that binds before it checks headers still fails on
            // validation instead of on a missing content type.
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>
    /// The statuses that mean "this operation is deployed and behaving", beyond the ones the
    /// contract declares for it.
    /// </summary>
    /// <remarks>
    /// A sweep that demanded a declared status and nothing else would be asserting that it had
    /// constructed valid business state for 382 operations, which it has not and must not. What it
    /// can demand is that the platform REFUSED it for a reason the platform is entitled to give:
    /// <list type="bullet">
    /// <item><b>400 / 422</b> — the body or the missing Idempotency-Key was judged. The route, the
    /// bearer and the middleware all worked.</item>
    /// <item><b>401 / 403</b> — AL-06 deny-by-default in the service. The sweep's operator holds
    /// `admin`, which is deliberately not entitled to a driver's or a passenger's surface.</item>
    /// <item><b>404</b> — the id could not exist. Distinguished from a gateway 404 by the body: a
    /// routed refusal is problem+json, an unrouted path is not (see the test).</item>
    /// <item><b>409</b> — a state conflict, e.g. an idempotency replay or a feed already active.</item>
    /// <item><b>415</b> — the operation wanted multipart and got JSON.</item>
    /// </list>
    /// <b>5xx is never acceptable</b>, and 426 is not either: both mean the deployment, not the
    /// request, is wrong.
    /// </remarks>
    public static readonly IReadOnlySet<int> AcceptableRefusals =
        new HashSet<int> { 400, 401, 403, 404, 405, 409, 415, 422, 429 };

    private static bool IsWrite(string method) =>
        method.Equals("POST", StringComparison.OrdinalIgnoreCase)
        || method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
        || method.Equals("PATCH", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The template with every path parameter filled, and required query parameters supplied.
    /// </summary>
    /// <remarks>
    /// Values are chosen by the parameter's declared schema rather than by its name: a contract that
    /// renames <c>rideId</c> should not change what this sends. The one exception is a token-shaped
    /// segment on the public tracking surface, where an id-shaped value would be rejected by length
    /// before the route was proven.
    /// </remarks>
    private static string Path(ContractOperation operation)
    {
        var path = operation.Template;

        foreach (var parameter in operation.Parameters.Where(p => p.In == ParameterLocation.Path))
        {
            path = path.Replace($"{{{parameter.Name}}}", Sample(parameter), StringComparison.Ordinal);
        }

        // A template the contract declared no parameter for still has to resolve to something, or
        // the request would carry a literal brace and prove nothing.
        while (path.IndexOf('{', StringComparison.Ordinal) is var open and >= 0)
        {
            var close = path.IndexOf('}', open);
            if (close < 0)
            {
                break;
            }

            path = path[..open] + AbsentId + path[(close + 1)..];
        }

        var required = operation.Parameters
            .Where(p => p.In == ParameterLocation.Query && p.Required)
            .Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(Sample(p))}")
            .ToArray();

        return required.Length == 0 ? path : $"{path}?{string.Join('&', required)}";
    }

    private static string Sample(ContractParameter parameter)
    {
        var schema = parameter.Schema;
        var format = schema.Format;
        var types = schema.Types;

        if (schema.Enum.Count > 0)
        {
            return schema.Enum[0];
        }

        if (string.Equals(format, "uuid", StringComparison.OrdinalIgnoreCase))
        {
            return AbsentId;
        }

        if (string.Equals(format, "date", StringComparison.OrdinalIgnoreCase))
        {
            return "2026-08-11";
        }

        if (string.Equals(format, "date-time", StringComparison.OrdinalIgnoreCase))
        {
            return "2026-08-11T00:00:00Z";
        }

        if (types.Contains("integer") || types.Contains("number"))
        {
            // A coordinate parameter is the common numeric one on this surface, and 0,0 is in the
            // Gulf of Guinea — outside every validator's bounding box, which would make a 400 the
            // answer to a question about routing. Colombo instead.
            return parameter.Name.Contains("lat", StringComparison.OrdinalIgnoreCase)
                ? "6.9271"
                : parameter.Name.Contains("lng", StringComparison.OrdinalIgnoreCase)
                  || parameter.Name.Contains("lon", StringComparison.OrdinalIgnoreCase)
                    ? "79.8612"
                    : 1.ToString(CultureInfo.InvariantCulture);
        }

        if (types.Contains("boolean"))
        {
            return "false";
        }

        // A share/track token is opaque and length-checked before anything is looked up.
        return parameter.Name.Contains("token", StringComparison.OrdinalIgnoreCase)
            ? "mr1.0000000000000000000000000.0000000000000000000000000"
            : AbsentId;
    }
}
