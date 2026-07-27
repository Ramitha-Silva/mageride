# Passenger Android Conventions
- Kotlin, Jetpack Compose, Material 3; minSdk 26 — Android 8.0 (URD NFR-22)
- Depends on shared/kmp — import DTOs, API client and domain logic from there
- Screen groups map to D2' §B + the passenger_android.html wireframe (41 SCR-PA ids)
- MapLibre GL Native over PMTiles; live vehicles arrive over SignalR by geocell —
  H3 res-7 + ring(2) = 19 cells with 30 s hysteresis, never a per-vehicle subscription (R-06)
- Trilingual: every user-facing string comes from values/, values-si/, values-ta/ — no literals
- Gradle project path is `:apps:passenger-android`; versions come from gradle/libs.versions.toml
- Verify: `./gradlew :apps:passenger-android:assembleDebug` (needs the Android SDK on the host)
