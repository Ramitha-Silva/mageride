using MageRide.Support;

// The pipeline itself lives in SupportApplication so the test suite drives the same composition.
var app = SupportApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
