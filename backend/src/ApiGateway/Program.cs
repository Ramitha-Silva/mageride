using MageRide.ApiGateway;

// The pipeline itself lives in GatewayApplication so the test suite drives the same composition.
var app = GatewayApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
