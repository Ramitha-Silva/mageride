using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MageRide.Shared.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Shared.Tests.Auth;

/// <summary>The 15-minute JWKS cache and its refresh behaviour (D-29, D-21, D7' §13).</summary>
public sealed class JwksConfigurationManagerTests
{
    private sealed class CountingHandler(Func<string> body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests;

        public HttpStatusCode Status { get; set; } = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);

            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(body(), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string Jwks(params string[] keyIds)
    {
        var keys = keyIds.Select(kid =>
        {
            using var rsa = RSA.Create(2048);
            var parameters = rsa.ExportParameters(includePrivateParameters: false);

            return new Dictionary<string, string>
            {
                ["kty"] = "RSA",
                ["use"] = "sig",
                ["alg"] = "RS256",
                ["kid"] = kid,
                ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
                ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
            };
        }).ToArray();

        return JsonSerializer.Serialize(new { keys });
    }

    private static (JwksConfigurationManager Manager, CountingHandler Handler) Create(
        FakeTimeProvider clock, Func<string>? body = null, JwtOptions? options = null)
    {
        var handler = new CountingHandler(body ?? (() => Jwks("key-1")));
        var httpClient = new HttpClient(handler);

        var manager = new JwksConfigurationManager(
            httpClient,
            options ?? new JwtOptions { JwksUrl = "https://iam.mageride.lk/.well-known/jwks.json" },
            NullLogger<JwksConfigurationManager>.Instance,
            clock);

        return (manager, handler);
    }

    [Fact]
    public async Task Keys_are_fetched_once_and_served_from_cache()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero));
        var (manager, handler) = Create(clock);

        var first = await manager.GetConfigurationAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(14));
        var second = await manager.GetConfigurationAsync(CancellationToken.None);

        Assert.Single(first.SigningKeys);
        Assert.Same(first, second);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task The_cache_expires_after_15_minutes()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero));
        var (manager, handler) = Create(clock);

        await manager.GetConfigurationAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        await manager.GetConfigurationAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests);
    }

    /// <summary>
    /// How a 90-day signing-key rotation (D7' §13) is picked up before the cache would expire:
    /// the handler asks for a refresh when a token's <c>kid</c> is unknown.
    /// </summary>
    [Fact]
    public async Task Request_refresh_refetches_before_the_cache_expires()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero));
        var (manager, handler) = Create(clock);

        await manager.GetConfigurationAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));

        manager.RequestRefresh();
        clock.Advance(TimeSpan.FromSeconds(31));
        await manager.GetConfigurationAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests);
    }

    /// <summary>A stream of bogus kids must not turn into a fetch storm against iam-svc.</summary>
    [Fact]
    public async Task Forced_refreshes_are_floored_by_the_minimum_interval()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero));
        var (manager, handler) = Create(clock);

        await manager.GetConfigurationAsync(CancellationToken.None);

        for (var i = 0; i < 50; i++)
        {
            manager.RequestRefresh();
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await manager.GetConfigurationAsync(CancellationToken.None);
        }

        Assert.Equal(1, handler.Requests);
    }

    /// <summary>iam-svc being down must not reject every request that was already verifiable.</summary>
    [Fact]
    public async Task A_failed_refresh_keeps_serving_the_cached_keys()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero));
        var (manager, handler) = Create(clock);

        var original = await manager.GetConfigurationAsync(CancellationToken.None);

        handler.Status = HttpStatusCode.ServiceUnavailable;
        clock.Advance(TimeSpan.FromMinutes(20));

        var afterOutage = await manager.GetConfigurationAsync(CancellationToken.None);

        Assert.Same(original, afterOutage);
        Assert.Single(afterOutage.SigningKeys);
    }

    [Fact]
    public async Task The_first_fetch_failing_surfaces_the_error()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero));
        var (manager, handler) = Create(clock);
        handler.Status = HttpStatusCode.InternalServerError;

        await Assert.ThrowsAsync<HttpRequestException>(() => manager.GetConfigurationAsync(CancellationToken.None));
    }

    [Fact]
    public void A_plain_http_jwks_url_is_refused_unless_explicitly_allowed()
    {
        var clock = new FakeTimeProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => Create(
            clock, options: new JwtOptions { JwksUrl = "http://iam-svc:8080/jwks" }));
        Assert.Contains("RequireHttpsMetadata", ex.Message, StringComparison.Ordinal);

        // Local compose runs iam-svc over plain HTTP inside the Docker network.
        var (manager, _) = Create(clock, options: new JwtOptions
        {
            JwksUrl = "http://iam-svc:8080/jwks",
            RequireHttpsMetadata = false,
        });
        Assert.NotNull(manager);
    }

    [Fact]
    public void A_missing_jwks_url_fails_fast()
    {
        Assert.Throws<InvalidOperationException>(() => Create(
            new FakeTimeProvider(), options: new JwtOptions { JwksUrl = "" }));
    }

    [Fact]
    public async Task An_empty_key_set_is_treated_as_a_failure()
    {
        var clock = new FakeTimeProvider();
        var (manager, _) = Create(clock, body: () => """{"keys":[]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetConfigurationAsync(CancellationToken.None));
    }
}
