using MageRide.Dispatch;

// The pipeline itself lives in DispatchApplication so the test suite drives the same composition.
var app = DispatchApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
