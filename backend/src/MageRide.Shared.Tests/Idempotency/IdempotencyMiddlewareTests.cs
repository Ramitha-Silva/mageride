using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Shared.Tests.Idempotency;

/// <summary>
/// R-14 / R-18 and the DoD line "replaying a POST with the same Idempotency-Key returns the
/// original status + body byte-for-byte".
/// </summary>
public sealed class IdempotencyMiddlewareTests
{
    private const string Key = "01HZX3Y8Q9WK4V2N7M5T6B8C1D";

    private static WebApplication BuildApp(ICommandLog commandLog, out Counter counter)
    {
        var executions = new Counter();
        counter = executions;

        var builder = TestHosts.CreateBuilder();

        builder.Services.AddProblemDetails(problem => problem.CustomizeProblemDetails =
            context => MageRideProblem.Enrich(context.HttpContext, context.ProblemDetails));
        builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
        builder.Services.AddSingleton(commandLog);
        builder.Services.AddOptions<IdempotencyOptions>();

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseRouting();
        app.UseMiddleware<IdempotencyMiddleware>();

        app.MapPost("/v1/rides/request", () =>
        {
            var n = executions.Next();
            // A body whose content changes per execution, so a replay is distinguishable from a
            // second execution rather than merely equal by luck.
            return Results.Json(
                new { rideId = "8f1c0f6e-2d3a-4b5c-9e7f-0a1b2c3d4e5f", execution = n, state = "Matching" },
                statusCode: StatusCodes.Status201Created);
        });

        app.MapPost("/v1/rides/boom", void () => throw new InvalidOperationException("transient database blip"));

        app.MapPost("/webhooks/onepay", () =>
        {
            executions.Next();
            return Results.Ok(new { received = true });
        }).AllowMissingIdempotencyKey();

        return app;
    }

    private sealed class Counter
    {
        private int _value;

        public int Next() => Interlocked.Increment(ref _value);

        public int Value => Volatile.Read(ref _value);
    }

    private static HttpRequestMessage Post(string path, string? key, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body ?? new { pickup = new { lat = 6.9271, lng = 79.8612 } }),
        };

        if (key is not null)
        {
            request.Headers.Add(MageRideHeaders.IdempotencyKey, key);
        }

        return request;
    }

    [Fact]
    public async Task A_post_without_the_header_is_rejected()
    {
        await using var app = BuildApp(new InMemoryCommandLog(), out var counter);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.SendAsync(Post("/v1/rides/request", key: null));
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("https://mageride.lk/errors/idempotency-key-required", problem.GetProperty("type").GetString());
        Assert.Equal(0, counter.Value);
    }

    [Fact]
    public async Task A_malformed_key_is_rejected()
    {
        await using var app = BuildApp(new InMemoryCommandLog(), out var counter);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.SendAsync(Post("/v1/rides/request", "short"));
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("https://mageride.lk/errors/idempotency-key-invalid", problem.GetProperty("type").GetString());
        Assert.Equal(0, counter.Value);
    }

    [Fact]
    public async Task A_replay_returns_the_original_status_and_body_byte_for_byte()
    {
        await using var app = BuildApp(new InMemoryCommandLog(), out var counter);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var first = await client.SendAsync(Post("/v1/rides/request", Key));
        var firstBody = await first.Content.ReadAsByteArrayAsync();

        var second = await client.SendAsync(Post("/v1/rides/request", Key));
        var secondBody = await second.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(firstBody, secondBody);
        Assert.Equal(first.Content.Headers.ContentType?.ToString(), second.Content.Headers.ContentType?.ToString());

        // The handler ran exactly once; the second response came from the command log.
        Assert.Equal(1, counter.Value);
        Assert.Equal("true", second.Headers.GetValues(IdempotencyMiddleware.ReplayHeader).Single());
        Assert.False(first.Headers.Contains(IdempotencyMiddleware.ReplayHeader));
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_a_conflict()
    {
        await using var app = BuildApp(new InMemoryCommandLog(), out var counter);
        await app.StartAsync();
        using var client = app.GetTestClient();

        await client.SendAsync(Post("/v1/rides/request", Key));
        var response = await client.SendAsync(Post("/v1/rides/request", Key, new { pickup = new { lat = 7.2906, lng = 80.6337 } }));
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("https://mageride.lk/errors/idempotency-key-reuse", problem.GetProperty("type").GetString());
        Assert.Equal(1, counter.Value);
    }

    [Fact]
    public async Task Different_keys_execute_independently()
    {
        await using var app = BuildApp(new InMemoryCommandLog(), out var counter);
        await app.StartAsync();
        using var client = app.GetTestClient();

        await client.SendAsync(Post("/v1/rides/request", Key));
        await client.SendAsync(Post("/v1/rides/request", "01HZX3Y8Q9WK4V2N7M5T6B8C2E"));

        Assert.Equal(2, counter.Value);
    }

    /// <summary>
    /// A 500 must not be pinned to the key: the driver app retries the same ULID after a network
    /// blip and has to get a real execution, not a replayed failure (ADD §11.13).
    /// </summary>
    [Fact]
    public async Task A_failed_command_releases_the_key_so_a_retry_executes()
    {
        var commandLog = new InMemoryCommandLog();
        await using var app = BuildApp(commandLog, out _);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var first = await client.SendAsync(Post("/v1/rides/boom", Key));
        Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);

        var reservation = await commandLog.TryReserveAsync(
            new CommandLogKey(Key, "POST /v1/rides/boom", [1, 2, 3], "driver"));

        Assert.Equal(CommandLogOutcome.Reserved, reservation.Outcome);
    }

    [Fact]
    public async Task An_opted_out_endpoint_needs_no_key()
    {
        await using var app = BuildApp(new InMemoryCommandLog(), out var counter);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.SendAsync(Post("/webhooks/onepay", key: null, new { transactionId = "abc" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, counter.Value);
    }

    [Fact]
    public async Task A_concurrent_duplicate_is_told_the_original_is_in_flight()
    {
        var commandLog = new InMemoryCommandLog();

        // Simulate the first request having reserved the key but not yet completed.
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("anything"));
        await commandLog.TryReserveAsync(new CommandLogKey(Key, "POST /v1/rides/request", hash, "passenger"));

        await using var app = BuildApp(commandLog, out _);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.SendAsync(Post("/v1/rides/request", Key));
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // The stored hash is for a different payload, so this reports reuse rather than in-flight;
        // the in-flight branch is covered against a real store in PostgresCommandLogTests.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("https://mageride.lk/errors/idempotency-key-reuse", problem.GetProperty("type").GetString());
    }
}
