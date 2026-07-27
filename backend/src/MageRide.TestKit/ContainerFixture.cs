namespace MageRide.TestKit;

/// <summary>
/// Shared behaviour for the throwaway-container fixtures: start on collection setup, record
/// why the container could not start rather than throwing, tear down on collection teardown.
/// </summary>
/// <remarks>
/// <para>
/// Skip-not-fail is deliberate and matches C002's convention. A developer without a Docker
/// daemon still gets the whole unit-test suite; CI provides Docker, so the container-backed
/// tests actually run there. <see cref="RequireAvailable"/> turns the soft skip into a hard
/// failure for a job that must not silently pass — the CI migration job uses it.
/// </para>
/// <para>
/// Consumers guard with <c>Assert.Skip(fixture.SkipReason)</c> when
/// <see cref="IsAvailable"/> is false, or call <see cref="RequireAvailable"/>.
/// </para>
/// </remarks>
public abstract class ContainerFixture : IAsyncLifetime
{
    /// <summary>
    /// Set when the image cannot be started — no daemon, no network, no disk. Null once the
    /// container is up.
    /// </summary>
    public string? SkipReason { get; private set; }

    /// <summary>True when the container is running and its endpoint can be used.</summary>
    public bool IsAvailable => SkipReason is null;

    /// <summary>Human name used in skip messages and failures.</summary>
    protected abstract string Name { get; }

    /// <summary>Starts the container. Exceptions become <see cref="SkipReason"/>.</summary>
    protected abstract Task StartAsync();

    /// <summary>Stops and removes the container.</summary>
    protected abstract Task StopAsync();

    /// <summary>
    /// Throws when the container is not available, so a test that must not be skipped fails
    /// loudly instead. <c>MAGERIDE_REQUIRE_CONTAINERS=1</c> makes every fixture behave this
    /// way — the CI backend job sets it, so a Docker outage on a runner is a red build rather
    /// than a green one with the integration tests quietly skipped.
    /// </summary>
    public void RequireAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                $"{Name} is required by this test but did not start: {SkipReason}");
        }
    }

    /// <summary>True when the environment demands containers rather than allowing a skip.</summary>
    public static bool ContainersRequired =>
        Environment.GetEnvironmentVariable("MAGERIDE_REQUIRE_CONTAINERS") is "1" or "true";

    async ValueTask IAsyncLifetime.InitializeAsync()
    {
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            SkipReason = $"{Name} container unavailable: {ex.GetType().Name}: {ex.Message}";

            if (ContainersRequired)
            {
                throw new InvalidOperationException(SkipReason, ex);
            }
        }
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        catch (Exception ex)
        {
            // A container that will not die must not turn a green suite red; Testcontainers'
            // Ryuk reaper removes it when the session ends.
            Console.Error.WriteLine($"warning: could not dispose the {Name} container: {ex.Message}");
        }

        GC.SuppressFinalize(this);
    }
}
