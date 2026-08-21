using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using MageRide.Ocr.Endpoints;
using MageRide.Shared.Http;
using MageRide.TestKit;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Npgsql;

namespace MageRide.Ocr.Tests.Infrastructure;

/// <summary>A <c>docs.extractions</c> row, as this suite reads one back.</summary>
public sealed record ExtractionRow(
    Guid Id,
    Guid UploadId,
    string DocType,
    string? Extracted,
    decimal? Confidence,
    string Status,
    bool RedactionApplied,
    string? Engine,
    string? RawSha256,
    string? RedactedSha256,
    string? RedactionPolicyVersion,
    string? RedactionPassVersion,
    short? FacesBlurred,
    short? IdentifiersMasked);

/// <summary>
/// A running ocr-svc on a real socket, against a real Postgres and a recording Gemini.
/// </summary>
/// <remarks>
/// Built through <see cref="OcrApplication.Build"/>, so the pipeline under test — the internal-key
/// filter, the problem+json handler, the options validation, the worker pool and the perimeter
/// guard on the outbound client — is the one the process runs.
/// </remarks>
internal sealed class OcrHarness : IAsyncDisposable
{
    /// <summary>The interim shared secret the internal plane demands until the mesh lands.</summary>
    public const string InternalApiKey = "c054-ocr-internal-key-not-a-secret";

    /// <summary>Asserted against on the recorded call, so it is a constant.</summary>
    public const string GeminiApiKey = "c054-gemini-key-not-a-secret";

    /// <summary>The synthetic driver every fixture upload is owned by.</summary>
    public static readonly Guid OwnerId = new("c0000054-0000-0000-0000-000000000001");

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;
    private readonly string _storageRoot;

    private OcrHarness(WebApplication app, PostgresFixture postgres, GeminiRecorder gemini, string storageRoot)
    {
        _app = app;
        _postgres = postgres;
        _storageRoot = storageRoot;

        Gemini = gemini;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        Client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(120) };
    }

    public HttpClient Client { get; }

    public GeminiRecorder Gemini { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<OcrHarness> StartAsync(
        PostgresFixture postgres,
        IDictionary<string, string?>? settings = null,
        bool withInternalPlane = true)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();
        await ResetAsync(postgres);

        var gemini = await GeminiRecorder.StartAsync();

        // One directory per harness, so two tests in the shared collection cannot see each other's
        // documents and a leftover file cannot make a later assertion pass.
        var storageRoot = Path.Combine(Path.GetTempPath(), "mageride-c054", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(storageRoot);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer.
            ["Postgres:PgBouncerTransactionMode"] = "false",

            ["Ocr:InternalApiKey"] = withInternalPlane ? InternalApiKey : null,
            ["Ocr:Storage:Root"] = storageRoot,
            ["Ocr:Tesseract:WorkRoot"] = Path.Combine(storageRoot, "work"),
            ["Ocr:Gemini:BaseUrl"] = gemini.BaseUrl,
            ["Ocr:Gemini:ApiKey"] = GeminiApiKey,
            // The recorder answers immediately; a long budget would only slow a failure down.
            ["Ocr:Gemini:Timeout"] = "00:00:10",
            ["Ocr:Queue:JobTimeout"] = "00:01:00",

            ["urls"] = "http://127.0.0.1:0",
            // One /metrics endpoint per harness would collide across concurrently running tests.
            ["Otel:PrometheusEnabled"] = "false",
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var app = OcrApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace.
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(overrides);
            });

        await app.StartAsync();

        return new OcrHarness(app, postgres, gemini, storageRoot);
    }

    // -----------------------------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a document to the store and its <c>docs.uploads</c> row, and returns both.
    /// </summary>
    /// <param name="autoDeleteAt">
    /// Left null on purpose by most callers: NFR-28's deadline is one of the things this service is
    /// asserted to stamp on a row that arrived without one.
    /// </param>
    public async Task<(Guid UploadId, string StorageUrl, byte[] Raw)> UploadAsync(
        byte[] bytes, string kind, DateTimeOffset? autoDeleteAt = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var name = $"{Guid.NewGuid():N}.png";

        await File.WriteAllBytesAsync(Path.Combine(_storageRoot, name), bytes);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);

        var uploadId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO docs.uploads (owner_id, storage_url, sha256, kind, captured_via, auto_delete_at)
            VALUES (@OwnerId, @StorageUrl, @Sha256, @Kind, 'camera_dragcrop', @AutoDeleteAt)
            RETURNING id;
            """,
            new
            {
                OwnerId,
                StorageUrl = name,
                Sha256 = SHA256.HashData(bytes),
                Kind = kind,
                AutoDeleteAt = autoDeleteAt,
            });

        return (uploadId, name, bytes);
    }

    // -----------------------------------------------------------------------------------------
    // HTTP
    // -----------------------------------------------------------------------------------------

    /// <summary>Calls the extraction route with the internal key.</summary>
    public async Task<HttpResponseMessage> ExtractAsync(object body, string? apiKey = InternalApiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/internal/ocr/extractions")
        {
            Content = JsonContent.Create(body, options: MageRideJson.Options),
        };

        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(ExtractionEndpoints.ApiKeyHeader, apiKey);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>Extracts one document and returns the parsed answer, failing on a non-200.</summary>
    public async Task<ExtractionResponse> ExtractAsync(
        Guid uploadId, string storageUrl, string kind, string? side = null, string? registrationNumber = null)
    {
        using var response = await ExtractAsync(new
        {
            uploadId,
            storageUrl,
            kind,
            side,
            registrationNumber,
        });

        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"POST /v1/internal/ocr/extractions answered {(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<ExtractionResponse>(body, MageRideJson.Options)!;
    }

    // -----------------------------------------------------------------------------------------
    // Rows
    // -----------------------------------------------------------------------------------------

    public async Task<ExtractionRow?> ExtractionRowAsync(Guid uploadId)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);

        return await connection.QuerySingleOrDefaultAsync<ExtractionRow>(
            """
            SELECT id, upload_id, doc_type, extracted::text AS extracted, confidence, status,
                   redaction_applied, engine, raw_sha256, redacted_sha256,
                   redaction_policy_version, redaction_pass_version, faces_blurred, identifiers_masked
              FROM docs.extractions
             WHERE upload_id = @UploadId
             ORDER BY created_at DESC
             LIMIT 1;
            """,
            new { UploadId = uploadId });
    }

    public async Task<int> ExtractionCountAsync(Guid uploadId)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM docs.extractions WHERE upload_id = @UploadId;", new { UploadId = uploadId });
    }

    public async Task<DateTimeOffset?> AutoDeleteAtAsync(Guid uploadId)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);

        return await connection.ExecuteScalarAsync<DateTimeOffset?>(
            "SELECT auto_delete_at FROM docs.uploads WHERE id = @UploadId;", new { UploadId = uploadId });
    }

    /// <summary>Runs arbitrary SQL, for the constraint assertions the migration is judged on.</summary>
    public async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);

        await connection.ExecuteAsync(sql, parameters);
    }

    /// <summary>
    /// One scalar, for asserting on the schema itself rather than on a row (Δ MCS-07).
    /// </summary>
    /// <remarks>
    /// A dropped CHECK has to be asserted BY NAME against <c>pg_constraint</c>. "The insert
    /// succeeded" is not the same claim: 1310 added that constraint <c>NOT VALID</c>, which still
    /// rejects new rows, so an insert that works proves the constraint is gone only if you already
    /// know it was never <c>VALIDATE</c>d.
    /// </remarks>
    public async Task<T?> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);

        return await connection.ExecuteScalarAsync<T>(sql, parameters);
    }

    private static async Task ResetAsync(PostgresFixture postgres)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);

        await connection.ExecuteAsync(
            """
            DELETE FROM docs.extractions;
            DELETE FROM docs.uploads;
            DELETE FROM iam.users WHERE id = @OwnerId;

            INSERT INTO iam.users (id, phone, role)
            VALUES (@OwnerId, '+94770000054', 'driver')
            ON CONFLICT (id) DO NOTHING;
            """,
            new { OwnerId });
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        await _app.StopAsync();
        await _app.DisposeAsync();
        await Gemini.DisposeAsync();

        try
        {
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a suite over.
        }
    }
}
