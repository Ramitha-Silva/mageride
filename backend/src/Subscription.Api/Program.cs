using MageRide.Subscriptions;

// The pipeline itself lives in SubscriptionApplication so the test suite drives the same composition.
var app = SubscriptionApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
