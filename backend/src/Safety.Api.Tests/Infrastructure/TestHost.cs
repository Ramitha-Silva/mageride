using System.Runtime.CompilerServices;

namespace MageRide.Safety.Tests.Infrastructure;

/// <summary>Process-wide settings this suite needs before the first host is built.</summary>
internal static class TestHost
{
    /// <summary>
    /// Turns off the configuration file watchers <c>WebApplication.CreateBuilder</c> installs.
    /// </summary>
    /// <remarks>
    /// <b>This suite builds five hosts per test</b> — safety-svc, a real notification-svc, a
    /// content-svc stub and two SMS gateways — and each one watches <c>appsettings*.json</c> for
    /// changes. On Linux every watcher is an inotify instance, and the default per-user limit is
    /// 128: a few tests in, host construction starts failing with "the configured user limit on the
    /// number of inotify instances has been reached", which looks like a product fault and is not
    /// one.
    /// <para>
    /// Nothing edits a settings file mid-run, so reload-on-change buys this suite nothing. The
    /// switch is the documented host one (<c>DOTNET_hostBuilder:reloadConfigOnChange</c>) and is set
    /// in a module initializer so it is in place before any fixture runs.
    /// </para>
    /// </remarks>
    [ModuleInitializer]
    internal static void DisableConfigurationFileWatchers() =>
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder:reloadConfigOnChange", "false");
}
