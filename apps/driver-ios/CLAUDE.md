# Driver iOS Conventions
- Swift + SwiftUI; consumes the KMP shared module as an XCFramework via SPM
- Parity-fenced to apps/driver-android — any behaviour difference beyond a D2' Section C
  platform delta needs a micro-change-set
- Screens map to D2' §B + the driver_ios.html wireframe (41 SCR-DI ids)
- Stays native (ADD §18.2): CLLocationManager background location, CocoaMQTT, Keychain +
  Secure Enclave device binding, App Attest, CallKit, MapLibre GL Native iOS
- Trilingual: Localizable.strings for si / ta / en, Dynamic Type respected
- Not a Gradle project — this is an Xcode project owned by C085; it is deliberately absent
  from settings.gradle.kts
- **This Linux build host cannot compile iOS.** Generate code here; build and verify on macOS.
- Verify (macOS only): `xcodebuild -scheme DriverApp -destination 'generic/platform=iOS Simulator' build`
