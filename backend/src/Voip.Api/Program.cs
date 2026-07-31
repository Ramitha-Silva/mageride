using MageRide.Voip;

// The pipeline itself lives in VoipApplication so the test suite drives the same composition.
var app = VoipApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
