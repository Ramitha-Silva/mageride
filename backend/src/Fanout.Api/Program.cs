using MageRide.Fanout;

// The pipeline itself lives in FanoutApplication so the test suite drives the same composition.
var app = FanoutApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
