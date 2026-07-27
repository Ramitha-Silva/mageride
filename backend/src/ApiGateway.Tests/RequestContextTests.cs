using System.Diagnostics;
using MageRide.ApiGateway.Http;
using MageRide.ApiGateway.Tests.Infrastructure;

namespace MageRide.ApiGateway.Tests;

/// <summary>Request-id and traceparent propagation across the edge.</summary>
public sealed class RequestContextTests : IAsyncLifetime
{
    private GatewayHarness _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await GatewayHarness.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task A_request_without_an_id_is_given_one_and_told_about_it()
    {
        using var response = await _gateway.Client.GetAsync("/v1/users/me");

        var echoed = Assert.Single(response.Headers.GetValues(RequestContextMiddleware.HeaderName));
        Assert.False(string.IsNullOrWhiteSpace(echoed));

        var forwarded = LastUpstreamRequest();
        Assert.Equal(echoed, forwarded.Headers[RequestContextMiddleware.HeaderName]);
    }

    [Fact]
    public async Task A_caller_supplied_id_is_kept_end_to_end()
    {
        const string id = "01JZZZ-support-ticket-4821";

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/users/me");
        request.Headers.Add(RequestContextMiddleware.HeaderName, id);

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(id, response.Headers.GetValues(RequestContextMiddleware.HeaderName).First());
        Assert.Equal(id, LastUpstreamRequest().Headers[RequestContextMiddleware.HeaderName]);
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("new\tline")]
    [InlineData("semi;colon")]
    public async Task An_unsafe_id_is_replaced_rather_than_echoed(string hostile)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/users/me");
        request.Headers.TryAddWithoutValidation(RequestContextMiddleware.HeaderName, hostile);

        using var response = await _gateway.Client.SendAsync(request);

        var echoed = response.Headers.GetValues(RequestContextMiddleware.HeaderName).First();
        Assert.NotEqual(hostile, echoed);
        Assert.False(string.IsNullOrWhiteSpace(echoed));
    }

    [Fact]
    public async Task An_overlong_id_is_replaced()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/users/me");
        request.Headers.TryAddWithoutValidation(RequestContextMiddleware.HeaderName, new string('a', 500));

        using var response = await _gateway.Client.SendAsync(request);

        Assert.True(response.Headers.GetValues(RequestContextMiddleware.HeaderName).First().Length < 500);
    }

    [Fact]
    public async Task The_trace_continues_across_the_edge_with_the_gateways_own_span()
    {
        using var caller = new Activity("client-request").Start();
        var traceId = caller.TraceId.ToHexString();

        using var _ = await _gateway.Client.GetAsync("/v1/users/me");

        var forwarded = LastUpstreamRequest();
        Assert.True(forwarded.Headers.TryGetValue("traceparent", out var traceparent),
            "The backend received no traceparent, so a trace stops at the edge.");

        // Same trace, different span: the backend parents to the gateway's hop rather than to the
        // client's, so the edge does not vanish from the trace (D6' §8.1 propagation).
        Assert.StartsWith($"00-{traceId}-", traceparent, StringComparison.Ordinal);
        Assert.DoesNotContain(caller.SpanId.ToHexString(), traceparent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edge_rejection_still_carries_a_request_id()
    {
        // The response the exception handler / gate writes must be traceable too — those are the
        // ones a support ticket is opened about.
        using var response = await _gateway.Client.GetAsync("/v1/internal/notify/send");

        Assert.True(response.Headers.Contains(RequestContextMiddleware.HeaderName));
    }

    private StubUpstream.RecordedRequest LastUpstreamRequest()
    {
        Assert.True(_gateway.Upstream.Requests.TryPeek(out _), "The stub upstream saw no requests.");
        return _gateway.Upstream.Requests.Last();
    }
}
