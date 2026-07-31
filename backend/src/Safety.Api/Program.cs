using MageRide.Safety;

// The pipeline itself lives in SafetyApplication so the test suite drives the same composition.
var app = SafetyApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
