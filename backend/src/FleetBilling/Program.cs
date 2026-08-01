using MageRide.FleetBilling;

// The pipeline itself lives in FleetBillingApplication so the test suite drives the same
// composition the process runs.
var app = FleetBillingApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
