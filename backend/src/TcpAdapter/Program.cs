using MageRide.TcpAdapter;

// The pipeline itself lives in TcpAdapterApplication so the test suite drives the same composition.
var host = TcpAdapterApplication.Build(args);

await host.RunAsync().ConfigureAwait(false);
