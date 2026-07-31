using MageRide.Notification;

// The pipeline itself lives in NotificationApplication so the test suite drives the same composition.
var app = NotificationApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
