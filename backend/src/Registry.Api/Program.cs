using MageRide.Registry;

// The pipeline itself lives in RegistryApplication so the test suite drives the same composition.
var app = RegistryApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
