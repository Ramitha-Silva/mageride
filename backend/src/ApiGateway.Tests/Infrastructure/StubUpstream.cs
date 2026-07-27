using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MageRide.ApiGateway.Tests.Infrastructure;

/// <summary>
/// One Kestrel process standing in for every backend service. It echoes what it received so a test
/// can assert what the edge forwarded, and carries a SignalR hub at <c>/hubs/live</c> for the
/// passthrough test.
/// </summary>
internal sealed class StubUpstream : IAsyncDisposable
{
    /// <summary>
    /// Selects a canned behaviour on any path, so a test can exercise the edge's handling of a
    /// backend response without needing a gateway route to a stub-specific path.
    /// </summary>
    public const string BehaviourHeader = "X-Stub-Behaviour";

    /// <summary>Answer <c>409</c> with an <c>application/problem+json</c> body.</summary>
    public const string ProblemBehaviour = "problem";

    /// <summary>Answer <c>204</c> with no body and no content type.</summary>
    public const string NoContentBehaviour = "no-content";

    /// <summary>Sleep long enough to trip a short cluster activity timeout.</summary>
    public const string SlowBehaviour = "slow";

    /// <summary>The exact body <see cref="ProblemBehaviour"/> returns, for a byte-for-byte assertion.</summary>
    public const string ProblemBody =
        """{"type":"https://mageride.lk/errors/active-ride-exists","title":"A non-terminal ride already exists","status":409,"detail":"stub","instance":"/stub","traceId":"00-stub"}""";

    private readonly WebApplication _app;

    private StubUpstream(WebApplication app, string baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    public string BaseAddress { get; }

    /// <summary>Every request the stub saw, in arrival order.</summary>
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public static async Task<StubUpstream> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR();

        var app = builder.Build();
        StubUpstream? stub = null;

        app.MapHub<EchoHub>("/hubs/live");

        app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest || context.Request.Path.StartsWithSegments("/hubs"))
            {
                await next(context);
                return;
            }

            stub!.Requests.Enqueue(RecordedRequest.From(context.Request));

            switch (context.Request.Headers[BehaviourHeader].ToString())
            {
                case ProblemBehaviour:
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    context.Response.ContentType = "application/problem+json";
                    await context.Response.WriteAsync(ProblemBody, Encoding.UTF8);
                    return;

                case NoContentBehaviour:
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;

                case SlowBehaviour:
                    await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    return;

                default:
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        $$"""{"path":"{{context.Request.Path}}","method":"{{context.Request.Method}}"}""",
                        Encoding.UTF8);
                    return;
            }
        });

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        stub = new StubUpstream(app, address);
        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    internal sealed record RecordedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers)
    {
        public static RecordedRequest From(HttpRequest request) => new(
            request.Method,
            request.Path.ToString(),
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Minimal hub: the passthrough test only needs a completed handshake and one round trip.</summary>
    internal sealed class EchoHub : Hub
    {
        public Task<string> Echo(string message) => Task.FromResult("echo:" + message);
    }
}
