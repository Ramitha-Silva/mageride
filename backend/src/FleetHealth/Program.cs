using MageRide.FleetHealth;

// The pipeline itself lives in FleetHealthApplication so the test suite drives the same
// composition the process runs.
var app = FleetHealthApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
