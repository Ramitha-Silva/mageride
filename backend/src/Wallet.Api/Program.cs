using MageRide.Wallet;

// The pipeline itself lives in WalletApplication so the test suite drives the same composition.
var app = WalletApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
