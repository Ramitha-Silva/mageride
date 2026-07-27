using Microsoft.Extensions.Options;

namespace MageRide.ApiGateway.Tests.Infrastructure;

/// <summary>A fixed-value <see cref="IOptionsMonitor{T}"/> for unit-testing a component in isolation.</summary>
internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
