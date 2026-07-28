using MageRide.HotPath.MqttBridge;

// The pipeline itself lives in MqttBridgeApplication so the test suite drives the same composition.
var app = MqttBridgeApplication.Build(new WebApplicationOptions { Args = args });

await app.RunAsync().ConfigureAwait(false);
