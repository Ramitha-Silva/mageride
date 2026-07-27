using System.Net;
using MageRide.ApiGateway.Http;
using MageRide.ApiGateway.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// DoD: "a SignalR handshake completes through the gateway in an integration test".
/// <para>
/// Both ends are real Kestrel sockets, so the negotiate POST and the WebSocket upgrade to
/// <c>/hubs/live</c> travel through YARP exactly as they do in production (D6' §8.2
/// <c>/hubs/live → fanout-svc (WSS)</c>).
/// </para>
/// </summary>
public sealed class SignalRPassthroughTests : IAsyncLifetime
{
    private GatewayHarness _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await GatewayHarness.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task A_websocket_hub_connection_completes_through_the_gateway()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(_gateway.BaseAddress), "/hubs/live"),
                options => options.Transports = HttpTransportType.WebSockets)
            .Build();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await connection.StartAsync(cancellation.Token);

        Assert.Equal(HubConnectionState.Connected, connection.State);

        // A round trip after the handshake proves the upgraded connection is proxied both ways,
        // not merely established.
        var reply = await connection.InvokeAsync<string>("Echo", "live", cancellation.Token);
        Assert.Equal("echo:live", reply);

        await connection.StopAsync(cancellation.Token);
    }

    [Fact]
    public async Task Long_polling_also_completes_through_the_gateway()
    {
        // The fallback transport when a corporate proxy blocks upgrades. It exercises a different
        // path through YARP — a long-lived streamed GET rather than an upgrade — so it is worth
        // proving separately.
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(_gateway.BaseAddress), "/hubs/live"),
                options => options.Transports = HttpTransportType.LongPolling)
            .Build();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await connection.StartAsync(cancellation.Token);
        Assert.Equal(HubConnectionState.Connected, connection.State);

        var reply = await connection.InvokeAsync<string>("Echo", "poll", cancellation.Token);
        Assert.Equal("echo:poll", reply);

        await connection.StopAsync(cancellation.Token);
    }

    [Fact]
    public async Task The_negotiate_request_routes_to_the_fanout_cluster()
    {
        using var response = await _gateway.Client.PostAsync("/hubs/live/negotiate?negotiateVersion=1", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fanout-svc", response.Headers.GetValues(GatewayTransforms.UpstreamHeaderName).First());
    }
}
