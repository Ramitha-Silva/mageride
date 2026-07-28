using MageRide.Provisioning;

// The pipeline itself lives in ProvisioningApplication so the test suite drives the same composition.
var app = ProvisioningApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
