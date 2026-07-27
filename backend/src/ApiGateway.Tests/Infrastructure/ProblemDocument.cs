using System.Text.Json;

namespace MageRide.ApiGateway.Tests.Infrastructure;

/// <summary>
/// A parsed RFC 7807 body plus the assertions every gateway-originated error shares (D3' §0).
/// </summary>
internal sealed record ProblemDocument(string ContentType, JsonElement Root)
{
    private const string TypeUriBase = "https://mageride.lk/errors/";

    /// <summary>The stable kebab key out of the <c>type</c> URI.</summary>
    public string Code
    {
        get
        {
            var type = Root.GetProperty("type").GetString() ?? string.Empty;
            Assert.StartsWith(TypeUriBase, type, StringComparison.Ordinal);
            return type[TypeUriBase.Length..];
        }
    }

    public string? GetStringOrNull(string member) =>
        Root.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public bool GetBoolean(string member) => Root.GetProperty(member).GetBoolean();

    public static async Task<ProblemDocument> ReadAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        Assert.Equal("application/problem+json", contentType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        var problem = new ProblemDocument(contentType, document.RootElement.Clone());

        Assert.Equal((int)response.StatusCode, problem.Root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.Root.GetProperty("title").GetString()));

        return problem;
    }
}
