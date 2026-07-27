using System.Net;
using MageRide.ApiGateway.Http;
using MageRide.ApiGateway.Tests.Infrastructure;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// "problem+json error passthrough": an error a service produced must arrive at the client
/// untouched, and an error the edge produces must be indistinguishable in shape from one.
/// </summary>
public sealed class ErrorPassthroughTests
{
    [Fact]
    public async Task A_service_problem_document_arrives_byte_for_byte()
    {
        await using var gateway = await GatewayHarness.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rides/01JZ/cancel");
        request.Headers.Add(StubUpstream.BehaviourHeader, StubUpstream.ProblemBehaviour);

        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(StubUpstream.ProblemBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_bodyless_service_response_is_not_rewritten()
    {
        // UseStatusCodePages is in the pipeline for the gateway's own 404s; it must not invent a
        // body for a proxied 204.
        await using var gateway = await GatewayHarness.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/me/saved-addresses/01JZ");
        request.Headers.Add(StubUpstream.BehaviourHeader, StubUpstream.NoContentBehaviour);

        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_unreachable_service_becomes_503_dependency_unavailable()
    {
        await using var gateway = await GatewayHarness.StartAsync(new Dictionary<string, string?>
        {
            // Port 1 refuses immediately, which is what a dead pod looks like to the forwarder.
            ["ReverseProxy:Clusters:support-svc:Destinations:primary:Address"] = "http://127.0.0.1:1/",
        });

        using var response = await gateway.Client.GetAsync("/v1/support/faq");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var problem = await ProblemDocument.ReadAsync(response);
        Assert.Equal("dependency-unavailable", problem.Code);
    }

    [Fact]
    public async Task A_service_that_never_answers_becomes_504_upstream_timeout()
    {
        await using var gateway = await GatewayHarness.StartAsync(new Dictionary<string, string?>
        {
            ["ReverseProxy:Clusters:support-svc:HttpRequest:ActivityTimeout"] = "00:00:01",
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/support/faq");
        request.Headers.Add(StubUpstream.BehaviourHeader, StubUpstream.SlowBehaviour);

        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);

        var problem = await ProblemDocument.ReadAsync(response);
        Assert.Equal("upstream-timeout", problem.Code);
    }

    [Fact]
    public async Task An_unrouted_path_is_a_problem_document_not_an_empty_404()
    {
        await using var gateway = await GatewayHarness.StartAsync();

        using var response = await gateway.Client.GetAsync("/v1/no-such-family/thing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(GatewayTransforms.UpstreamHeaderName));

        var problem = await ProblemDocument.ReadAsync(response);
        Assert.Equal("not-found", problem.Code);
    }
}
