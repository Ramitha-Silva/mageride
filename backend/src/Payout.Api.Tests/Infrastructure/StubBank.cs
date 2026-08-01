using System.Collections.Concurrent;
using System.Text.Json;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MageRide.Payout.Tests.Infrastructure;

/// <summary>One instruction the service handed to a bank.</summary>
internal sealed record SubmittedTransfer(Guid Reference, long AmountMinor, string AccountNo);

/// <summary>
/// A bank origination endpoint, as far as payout-svc can tell.
/// </summary>
/// <remarks>
/// <para>
/// <b>The default in this suite is NOT to start one.</b> ADD §1.18 leaves the provider unchosen and
/// the deployed state is "no bank configured" — instructions are raised, debited and rest at
/// <c>PENDING</c> so the liability is visible before a rail exists. That is a definition-of-done
/// item in its own right, so the absence is tested as carefully as the presence.
/// </para>
/// <para>
/// A real socket rather than a stubbed <c>HttpMessageHandler</c>: what is worth asserting is what
/// went over the wire — the amount, the account, and that the reference the bank echoes back is the
/// one the service later settles on.
/// </para>
/// </remarks>
internal sealed class StubBank : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<SubmittedTransfer> _submitted = new();

    private StubBank(WebApplication app)
    {
        _app = app;

        BaseUrl = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public string BaseUrl { get; }

    public IReadOnlyList<SubmittedTransfer> Submitted => [.. _submitted];

    /// <summary>Makes the next submission fail, as a bank that is down or refusing would.</summary>
    public bool RefuseSubmission { get; set; }

    public static async Task<StubBank> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["urls"] = "http://127.0.0.1:0",
        });

        var app = builder.Build();
        var state = new StubBankState();

        app.MapPost("/transfers", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            using var document = JsonDocument.Parse(await reader.ReadToEndAsync());

            var root = document.RootElement;
            var reference = root.GetProperty("reference").GetGuid();

            state.Record(new SubmittedTransfer(
                reference,
                root.GetProperty("amountMinor").GetInt64(),
                root.GetProperty("accountNo").GetString() ?? string.Empty));

            if (state.Refuse)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            // The bank's own reference, which is what the result callback dedupes on (R-19).
            return Results.Json(new { reference = $"CEFTS-{reference:N}"[..24] }, MageRideJson.Options);
        });

        await app.StartAsync();

        var stub = new StubBank(app);
        state.Attach(stub);

        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>Held apart so the route closure does not need the stub that has no address yet.</summary>
    private sealed class StubBankState
    {
        private StubBank? _stub;

        public bool Refuse => _stub?.RefuseSubmission ?? false;

        public void Attach(StubBank stub) => _stub = stub;

        public void Record(SubmittedTransfer transfer) => _stub?._submitted.Enqueue(transfer);
    }
}
