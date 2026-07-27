# Passenger iOS Conventions
- Swift + SwiftUI; consumes the KMP shared module as an XCFramework via SPM
- Parity-fenced to apps/passenger-android — any behaviour difference beyond a D2' Section C
  platform delta needs a micro-change-set
- Screens map to D2' §B + the passenger_ios.html wireframe (41 SCR-PI ids)
- Stays native (ADD §18.2): SignalR Swift client, MapLibre GL Native iOS, Keychain + Secure
  Enclave, App Attest, APNs via FCM
- Map subscribes by geocell — H3 res-7 + ring(2) = 19 cells with hysteresis, never per-vehicle (R-06)
- Trilingual: Localizable.strings for si / ta / en, Dynamic Type respected
- Not a Gradle project — this is an Xcode project owned by C094; it is deliberately absent
  from settings.gradle.kts
- **This Linux build host cannot compile iOS.** Generate code here; build and verify on macOS.
- Verify (macOS only): `xcodebuild -scheme PassengerApp -destination 'generic/platform=iOS Simulator' build`
