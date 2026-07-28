using System.Net;
using System.Text.Json;

namespace MageRide.Ride.Tests.Infrastructure;

/// <summary>
/// A parsed RFC 7807 body plus the assertions every MageRide error shares (D3' §0).
/// </summary>
internal sealed record ProblemDocument(JsonElement Root)
{
    private const string TypeUriBase = "https://mageride.lk/errors/";

    /// <summary>The stable kebab key out of the <c>type</c> URI — what a client branches on.</summary>
    public string Code
    {
        get
        {
            var type = Root.GetProperty("type").GetString() ?? string.Empty;
            Assert.StartsWith(TypeUriBase, type, StringComparison.Ordinal);
            return type[TypeUriBase.Length..];
        }
    }

    /// <summary>Reads the body and asserts the response is a problem+json with the expected status and code.</summary>
    public static async Task<ProblemDocument> AssertAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(body);
        var problem = new ProblemDocument(document.RootElement.Clone());

        Assert.Equal(status, response.StatusCode);
        Assert.Equal((int)status, problem.Root.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.Code);
        Assert.False(string.IsNullOrWhiteSpace(problem.Root.GetProperty("title").GetString()));

        return problem;
    }
}
