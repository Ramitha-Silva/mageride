using System.Net;
using System.Text.Json;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using MageRide.Shared.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Shared.Tests.Errors;

/// <summary>
/// The DoD line "every error response validates as application/problem+json with a stable type
/// URI" (D3' §0), exercised over real HTTP.
/// </summary>
public sealed class ProblemDetailsPipelineTests
{
    private static WebApplication BuildApp()
    {
        var builder = TestHosts.CreateBuilder();

        builder.Services.AddProblemDetails(problem => problem.CustomizeProblemDetails =
            context => MageRideProblem.Enrich(context.HttpContext, context.ProblemDetails));
        builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.MapGet("/known", void () => throw new MageRideException(MageRideErrors.OfferExpired, "The 15 s offer window closed."));
        app.MapGet("/unexpected", void () => throw new InvalidOperationException("connection string leaked in here"));
        app.MapGet("/validation", void () => throw new MageRideValidationException(
            new Dictionary<string, string[]> { ["phone"] = ["must be a +94 number"] }));
        app.MapGet("/result", () => MageRideResults.Problem(MageRideErrors.VersionConflict, "Ride version 4 expected."));

        return app;
    }

    [Fact]
    public async Task A_registry_exception_becomes_problem_json_with_its_type_uri()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri("/known", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("https://mageride.lk/errors/offer-expired", problem.GetProperty("type").GetString());
        Assert.Equal(410, problem.GetProperty("status").GetInt32());
        Assert.Equal("/known", problem.GetProperty("instance").GetString());
        Assert.Equal("The 15 s offer window closed.", problem.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_an_opaque_internal_error()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri("/unexpected", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("https://mageride.lk/errors/internal-error",
            JsonDocument.Parse(body).RootElement.GetProperty("type").GetString());

        // The exception message must not reach the client outside Development.
        Assert.DoesNotContain("connection string leaked", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_validation_failure_carries_the_errors_map()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri("/validation", UriKind.Relative));
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("https://mageride.lk/errors/validation-failed", problem.GetProperty("type").GetString());
        Assert.Equal("must be a +94 number",
            problem.GetProperty("errors").GetProperty("phone")[0].GetString());
    }

    [Fact]
    public async Task MageRideResults_problem_is_enriched_with_instance_and_trace_id()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri("/result", UriKind.Relative));
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("https://mageride.lk/errors/version-conflict", problem.GetProperty("type").GetString());
        Assert.Equal("/result", problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    /// <summary>
    /// A route the framework rejects before any handler runs still has to carry a registry type
    /// URI, or a client would see two different error shapes from the same service.
    /// </summary>
    [Fact]
    public async Task An_unmatched_route_still_returns_a_registry_type_uri()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri("/no-such-route", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("https://mageride.lk/errors/not-found", problem.GetProperty("type").GetString());
    }
}
