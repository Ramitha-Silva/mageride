using MageRide.Fleet;

// The pipeline itself lives in FleetApplication so the test suite drives the same composition.
var app = FleetApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
