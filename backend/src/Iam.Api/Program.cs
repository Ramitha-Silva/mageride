using MageRide.Iam;

// The pipeline itself lives in IamApplication so the test suite drives the same composition.
var app = IamApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
