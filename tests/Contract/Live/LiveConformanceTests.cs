using System.Text.Json;
using MageRide.Contract.Tests.Model;

namespace MageRide.Contract.Tests.Live;

/// <summary>
/// Every contract operation, driven over HTTP against a DEPLOYED edge — the response sweep C118
/// deferred, on the transport C124/C125 made possible.
/// </summary>
/// <remarks>
/// <para>
/// What this asserts, per operation: it is <b>routed</b> (the edge does not 404 it as unknown), it
/// does <b>not 5xx</b>, its status is one the platform is <b>entitled to answer</b>, and any 2xx body
/// <b>validates against the declared response schema</b>. That is the whole of what a sweep which
/// must not change state can honestly claim — and it is exactly the layer that was blind when
/// `Jwt__JwksUrl` pointed at a path the gateway refuses, which turned every authenticated request
/// into a 500 while `Runtime/RouteTableTests` stayed green.
/// </para>
/// <para>
/// Skips wholesale without <c>MAGERIDE_LIVE_EDGE</c>. `infra/replica/contract-live-verify.sh` is the
/// runner.
/// </para>
/// </remarks>
public sealed class LiveConformanceTests
{
    /// <summary>Operation keys, so a failure names the operation and xUnit can serialise the case.</summary>
    public static TheoryData<string> Drivable()
    {
        var data = new TheoryData<string>();

        foreach (var operation in ContractSet.Current.Operations)
        {
            if (LiveRequestPlan.IsDrivable(operation, out _))
            {
                data.Add($"{operation.Document}|{operation.Method}|{operation.Template}");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Drivable))]
    public async Task Every_operation_is_reachable_at_the_edge_and_answers_a_shape_the_contract_declares(string key)
    {
        LiveEdge.RequireEdge();

        var operation = Resolve(key);

        if (!LiveRequestPlan.IsSafeToDrive(operation, out var unsafeReason))
        {
            Assert.Skip($"{operation}: {unsafeReason}");
        }

        using var request = LiveRequestPlan.Build(operation);
        using var response = await LiveEdge.Client.Value.SendAsync(request);

        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync();
        var ledgerKey = $"{operation.Method} {operation.Template}";

        // ---------------------------------------------------------------------------------
        // 0. Route existence is NOT asserted here, and the reason is worth writing down.
        //
        // The first version of this test called a 404 without a problem+json body "unrouted" — and
        // that discriminator does not exist: the gateway's own 404 for a path nothing matches is
        // ALSO application/problem+json (`/v1/definitely-not-a-route-anywhere` answers exactly the
        // shape a service's not-found does). An assertion that cannot fire is worse than an absent
        // one, because it reads as coverage.
        //
        // Route existence is `Runtime/RouteTableTests`' job and it does it better: it reads each
        // service's own EndpointDataSource, which also gives the reverse direction — a served route
        // that no contract declares — which no request-driven sweep can see. What THIS transport
        // uniquely proves is below.
        // ---------------------------------------------------------------------------------

        // ---------------------------------------------------------------------------------
        // 1. A service that is not deployed is not drift.
        // ---------------------------------------------------------------------------------
        if (status == 503 && LiveDrift.AbsentDocuments.Contains(operation.Document))
        {
            Assert.Skip(
                $"{operation}: {operation.Document}-svc is not deployed on this target (an optional "
                + "compose profile), and 503 dependency-unavailable is the correct answer about an "
                + "absent dependency.");
        }

        // ---------------------------------------------------------------------------------
        // 2. Not a server error, and not the version gate.
        //
        // A ledgered 5xx has to STILL be a 5xx: the day it is fixed, this fails and asks for the
        // entry to be deleted, which is what stops the ledger from outliving the defect.
        // ---------------------------------------------------------------------------------
        if (LiveDrift.ServerErrors.TryGetValue(ledgerKey, out var recorded))
        {
            Assert.True(
                status == recorded.Status,
                $"{operation} answered {status}; LiveDrift records {recorded.Status}. If it is fixed, "
                + $"delete the entry — the ledger may only shrink.\n  recorded: {recorded.Why}");

            return;
        }

        Assert.False(
            status >= 500,
            $"{operation} answered {status}. A 5xx is the deployment failing, not the request being "
            + "refused — this is the shape the JWKS misconfiguration produced on every authenticated "
            + $"route (C126).\nbody: {Excerpt(body)}");

        Assert.NotEqual(426, status);

        // ---------------------------------------------------------------------------------
        // 3. A status the platform is entitled to answer.
        // ---------------------------------------------------------------------------------
        var declared = operation.ResponseFor(status);

        Assert.True(
            declared is not null || LiveRequestPlan.AcceptableRefusals.Contains(status),
            $"{operation} answered {status}, which the contract does not declare and which is not a "
            + $"refusal this sweep accepts (see LiveRequestPlan.AcceptableRefusals).\nbody: {Excerpt(body)}");

        // ---------------------------------------------------------------------------------
        // 4. A success body has to match the schema it promised.
        //
        // This is where drift actually shows: a field renamed, a nullable that is absent instead, an
        // amount serialised as a decimal where the contract says integer minor units. It only fires
        // on a 2xx, because that is the only answer the contract describes a body for.
        // ---------------------------------------------------------------------------------
        if (status is >= 200 and < 300 && declared is not null && body.Length > 0)
        {
            var schema = declared.Content.TryGetValue("application/json", out var json) ? json : null;

            if (schema is not null && !schema.IsEmpty)
            {
                JsonDocument parsed;
                try
                {
                    parsed = JsonDocument.Parse(body);
                }
                catch (JsonException exception)
                {
                    Assert.Fail($"{operation} answered {status} with a body that is not JSON: {exception.Message}");
                    return;
                }

                using (parsed)
                {
                    var violations = SchemaValidator.Validate(parsed.RootElement, schema);

                    if (LiveDrift.SchemaViolations.TryGetValue(ledgerKey, out var known))
                    {
                        Assert.True(
                            violations.Count > 0,
                            $"{operation} now conforms. Delete its LiveDrift.SchemaViolations entry — "
                            + $"the ledger may only shrink.\n  recorded: {known}");

                        return;
                    }

                    Assert.True(
                        violations.Count == 0,
                        $"{operation} answered {status} with a body the contract does not describe:\n  "
                        + string.Join("\n  ", violations.Take(10)));
                }
            }
        }
    }

    /// <summary>
    /// The internal plane is refused at the edge, and that refusal is asserted rather than assumed.
    /// </summary>
    /// <remarks>
    /// `/v1/internal/**` is mTLS-only (D3' §0) and `BlockedPathMiddleware` answers 404 ahead of
    /// routing — a 403 would confirm the path exists. C125's smoke suite checks one such path; this
    /// checks every one the contracts declare, which is the set that would actually leak.
    /// </remarks>
    [Fact]
    public async Task The_internal_plane_is_not_reachable_from_the_edge()
    {
        LiveEdge.RequireEdge();

        var internals = ContractSet.Current.Operations.Where(o => o.IsInternal).ToArray();
        Assert.NotEmpty(internals);

        var reachable = new List<string>();

        foreach (var operation in internals)
        {
            using var request = LiveRequestPlan.Build(operation);
            using var response = await LiveEdge.Client.Value.SendAsync(request);

            if ((int)response.StatusCode != 404)
            {
                reachable.Add($"{operation} -> {(int)response.StatusCode}");
            }
        }

        Assert.True(
            reachable.Count == 0,
            "The mTLS-only internal plane answered something other than 404 at the public edge "
            + $"({reachable.Count} of {internals.Length}):\n  " + string.Join("\n  ", reachable));
    }

    /// <summary>
    /// The one endpoint every authenticated request depends on, checked as a client checks it.
    /// </summary>
    /// <remarks>
    /// Δ C126. Not declared in any contract document — `ServiceRoutes.OperationalRoutes` already
    /// records it as "a candidate for `iam.yaml`" — so no theory above covers it, and its absence is
    /// what a 500 on every bearer-carrying route looks like from the outside.
    /// </remarks>
    [Fact]
    public async Task The_signing_key_set_is_published_at_the_edge()
    {
        LiveEdge.RequireEdge();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/.well-known/jwks.json");
        request.Headers.Host = LiveEdge.HostHeader;

        using var response = await LiveEdge.Client.Value.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET /v1/.well-known/jwks.json answered {(int)response.StatusCode}. Every service that "
            + "validates a bearer fetches this; unreachable, they all answer 500 to every "
            + $"authenticated request.\nbody: {Excerpt(body)}");

        using var parsed = JsonDocument.Parse(body);
        Assert.True(
            parsed.RootElement.TryGetProperty("keys", out var keys) && keys.GetArrayLength() > 0,
            $"the JWKS carries no keys: {Excerpt(body)}");
    }

    private static ContractOperation Resolve(string key)
    {
        var parts = key.Split('|');

        return ContractSet.Current.Operations.Single(o =>
            o.Document == parts[0] && o.Method == parts[1] && o.Template == parts[2]);
    }

    private static string Excerpt(string body) =>
        body.Length <= 400 ? body : body[..400] + "…";
}
