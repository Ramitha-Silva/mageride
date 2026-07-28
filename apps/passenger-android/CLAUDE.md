# Passenger Android Conventions
- Kotlin, Jetpack Compose, Material 3; minSdk 26 — Android 8.0 (URD NFR-22)
- Depends on shared/kmp — import DTOs, API client and domain logic from there
- Screen groups map to D2' §B + the passenger_android.html wireframe (41 SCR-PA ids)
- MapLibre GL Native over PMTiles; live vehicles arrive over SignalR by geocell —
  H3 res-7 + ring(2) = 19 cells with 30 s hysteresis, never a per-vehicle subscription (R-06)
- Trilingual: every user-facing string comes from values/, values-si/, values-ta/ — no literals
- Gradle project path is `:apps:passenger-android`; versions come from gradle/libs.versions.toml
- Verify: `./gradlew :apps:passenger-android:assembleDebug` (needs the Android SDK on the host)

## Walking-skeleton shell (C025) — throwaway, and it claims no screen id

What is here today is the thinnest passenger book flow that proves `:shared` composes into an app:
phone OTP sign-in, a live map that is a **list**, one booking, and a ride state read back over REST.
It owns **no SCR-PA id** — C077–C080 own the real screens and Wave 4a replaces every composable.

Deliberately absent, so nothing here reads as finished:
- **MapLibre / PMTiles.** C077's. The 19 res-7 cells are joined for real and the `VehiclePositions`
  frames arrive for real; they are rendered as rows.
- **Trilingual resources.** The platform rule is Si/Ta/En from `values*/` with no literals; C078
  owns the catalogue, and three half-written translations of doomed screens would be worse than one.
- **Koin, navigation, theming and the on-device database.** `SkeletonClient` binds the two things
  C013 leaves to an app (an `HttpClientEngine` and an `ApiConfig`) by hand and holds its token in
  memory rather than in `PlatformSecureStore`.

Load-bearing even so:
- the cells come from `:shared`'s `GeoCells` over the platform H3 grid, so they are the ids
  position-processor-svc writes its streams under — computing them any other way joins groups
  nothing publishes to, and the symptom is an empty map with no error anywhere;
- `LiveHub`'s method and event names are taken from `:shared`, never typed as literals: SignalR
  resolves both by string;
- the hub credential is the 30-minute API access token in `access_token` (D-29), **never** the MQTT
  session JWT (E-02);
- `versionName` must match `ApiConfig.appVersion` — the gateway reads it as `X-App-Version` and
  answers `426` below D-31's floor on every route.

**No `org.jetbrains.kotlin.android` plugin.** AGP 9 has built-in Kotlin support and refuses it, the
same way it refuses `com.android.library` in a KMP project.
