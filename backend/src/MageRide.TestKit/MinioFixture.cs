using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace MageRide.TestKit;

/// <summary>
/// A throwaway MinIO — D-36's document store, as the dev stack and the replica run it.
/// </summary>
/// <remarks>
/// <para>
/// <c>minio/minio:latest</c>, matching <c>infra/docker-compose.dev.slim.yml</c> and Container 10 of
/// the lightweight production replica. The point of testing against a real MinIO rather than a fake
/// <c>IObjectStore</c> is that almost everything worth asserting about D-36 is S3 behaviour and not
/// ours: whether a presigned URL an unauthenticated client follows actually returns the bytes,
/// whether a lifecycle rule scoped to one prefix is accepted and reads back scoped to that prefix,
/// whether server-side encryption is reported on the object afterwards. A stub would assert that
/// our own test double does what we wrote it to do.
/// </para>
/// <para>
/// Built on the generic container builder rather than a Testcontainers MinIO module so the image
/// tag is the one the compose files pin, and so no extra package is added for one fixture.
/// </para>
/// </remarks>
public sealed class MinioFixture : ContainerFixture
{
    /// <summary>The image the dev stack and the replica run.</summary>
    public const string Image = "minio/minio:latest";

    /// <summary>The dev-stack credentials, matching <c>.env.common.example</c>.</summary>
    public const string AccessKey = "mageride_dev";

    public const string SecretKey = "mageride_dev_secret";

    /// <summary>
    /// The dev KMS key, without which MinIO refuses <b>every</b> encrypted upload.
    /// </summary>
    /// <remarks>
    /// Found by running this fixture: MinIO implements SSE-S3 (<c>AES256</c>) through its KMS, so
    /// an unconfigured MinIO answers "Server side encryption specified but KMS is not configured"
    /// to a plain encrypted PUT — not just to SSE-KMS. Since D-36 requires encryption at rest and
    /// this store always asks for it, a MinIO without this variable cannot store a single document.
    /// <c>MINIO_KMS_SECRET_KEY</c> is the single-key stand-in for a full KES server and is what the
    /// dev stack and the replica set; production uses the provider's own KMS. The value must decode
    /// to <b>exactly 32 bytes</b> — MinIO exits with "invalid key length" on anything else, which
    /// presents as a container that starts and immediately dies.
    /// </remarks>
    public const string KmsKey =
        "mageride-dev-key:bWFnZXJpZGUtZGV2LWttcy1rZXktMzItYnl0ZXMtMDE=";

    private IContainer? _container;

    protected override string Name => "MinIO";

    /// <summary>The S3 endpoint of the running container.</summary>
    public string ServiceUrl => _container is null
        ? throw new InvalidOperationException($"MinIO container is not running: {SkipReason ?? "not started"}")
        : $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9000)}";

    protected override async Task StartAsync()
    {
        _container = new ContainerBuilder(Image)
            .WithEnvironment("MINIO_ROOT_USER", AccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
            .WithEnvironment("MINIO_KMS_SECRET_KEY", KmsKey)
            .WithCommand("server", "/data", "--console-address", ":9001")
            .WithPortBinding(9000, assignRandomHostPort: true)
            // The readiness probe MinIO documents. Waiting on the port alone races the first
            // request against a server that is listening but has not opened its object store.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                request.ForPort(9000).ForPath("/minio/health/live")))
            .Build();

        await _container.StartAsync();
    }

    protected override Task StopAsync() =>
        _container is null ? Task.CompletedTask : _container.DisposeAsync().AsTask();
}

/// <summary>Collection sharing one <see cref="MinioFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class MinioCollection : ICollectionFixture<MinioFixture>
{
    public const string Name = "mageride-minio";
}
