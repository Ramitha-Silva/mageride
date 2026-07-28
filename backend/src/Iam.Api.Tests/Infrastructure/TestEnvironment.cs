using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MageRide.Iam.Tests;

/// <summary>
/// A minimal <see cref="IHostEnvironment"/>. Several iam-svc components behave differently in
/// Development — the ephemeral signing key, the ephemeral OTP pepper, the dev SMS sender — and
/// those differences are the point of the tests that use this.
/// </summary>
internal sealed class TestEnvironment : IHostEnvironment
{
    public static readonly TestEnvironment Development = new(Environments.Development);
    public static readonly TestEnvironment Staging = new(Environments.Staging);
    public static readonly TestEnvironment Production = new(Environments.Production);

    private TestEnvironment(string environmentName) => EnvironmentName = environmentName;

    public string EnvironmentName { get; set; }

    public string ApplicationName { get; set; } = "iam-svc.tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
