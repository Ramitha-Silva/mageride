# Driver Android Conventions

- Kotlin, Jetpack Compose, Material 3
- Depends on shared/kmp — import DTOs, the API client, the domain state machines and the MQTT
  contracts from there. Nothing that exists in `:shared` may be reimplemented here.
- Screen groups map to D2' §B + the `specs/wireframes/driver_android.html` wireframe
- minSdk 26 — Android 8.0 (URD NFR-22); Gradle project path is `:apps:driver-android`
- Verify: `./gradlew :apps:driver-android:testDebugUnitTest :apps:driver-android:assembleDebug`
  (the Android SDK is installed on this build host at `/opt/android-sdk`; `local.properties` points
  AGP at it)

## The shell (C067) — what exists, and where a screen plugs in

C025's walking skeleton is **gone**. Its four files (`MainActivity`, `MainViewModel`,
`DriverMqtt`, `SkeletonClient`) were declared throwaway by their own component and were deleted
here; nothing in this module is throwaway any more.

```
lk.mageride.driver
├── DriverApplication.kt      Koin start, notification channels, Play Integrity warm-up
├── MainActivity.kt           the ONE Activity. Do not add a second.
├── di/                       DriverEnvironment (the only BuildConfig reader) + the app module
├── nav/                      DriverRoute (every destination), DriverTab, DriverNavHost, bottom bar
├── shell/                    DriverShell (Scaffold host), offline banner, update gate, connectivity
├── ui/theme/                 D2' §0.2 tokens — colour, type, spacing/radius/elevation/controls
├── ui/component/             MageRideCta + the cluster-1 controls (C068)
├── map/                      MageRideMap (MapLibre host), MapStyles, VehicleLayers (MAP-*)
├── location/                 PositionForegroundService, PositionPipeline, MQTT transport, journal
├── push/                     FCM service, PushRouter (deep links), channels, PushTokenProvider
├── capture/                  CapturedImage + DocumentCaptureCoordinator — the seam to SCR-DA-005
└── onboarding/               C068 · SCR-DA-001, 002, 003, 003a, 007 + their data layer
```

**To add a screen (C069–C075):**

1. Its route is already in `nav/DriverRoute.kt`. Use it; do not invent a path.
2. Replace its `placeholder(...)` line in `nav/DriverNavHost.kt` with the real composable. That file
   is the only `NavHost` in the app.
3. Put every user-facing string in **all three** `res/values*/strings.xml` files at once.
   `StringResourceTest` fails the build on a key that exists in one and not the others, on a
   translation left equal to its English, and on a format placeholder dropped in translation.
4. Reach for `MageRideTheme.spacing` / `.radius` / `.elevation` / `.status` / `.vehicle` / `.mode`
   and `MaterialTheme.colorScheme` — never a raw `dp` or hex. `ThemeTokensTest` holds §0.2.
5. The full-width orange bar in the wireframes is `MageRideCta`, not a `Button`.

## Cluster 1 (C068) — what the next screen group can reuse

- **`OnboardingRouter.next(...)` is the only place that decides where a driver belongs.** Splash,
  the login screen after a verify and Profile Setup after a save all call it. If a new gate is ever
  added before Home it goes in that function, not in a screen.
- **`DocumentCaptureCoordinator` is the seam to SCR-DA-005** (C069's scanner). A screen calls
  `open(target)` and navigates to `DriverRoute.DocumentCapture`; the scanner reads `pending`, shows
  the right title and calls `deliver(image)`. The route carries no arguments, so this is the only
  way to say what a capture is for.
- **`DriverDocumentUploader` has no route behind it.** `PUT /v1/drivers/profile` takes
  `docs.uploads` ids and nothing in `backend/contracts` creates one for a driver photo or licence.
  The binding is `UnavailableDriverDocumentUploader`, which fails loudly; swap it when the route
  lands. C069's four vehicle documents do **not** need it — they go up inside
  `PUT /v1/vehicles/{id}/onboarding/{step}`.
- **`OnboardingPreferences`** holds the three answers given before there is a session (language,
  operating city, permissions-seen) and `OnboardingRepository.syncPreferences()` pushes the first
  two to `iam.users` on the first authenticated call.
- **Language is applied by `MainActivity.attachBaseContext`** through `DriverLocale.wrap`, and a
  change calls `recreate()`. Per-app locale (`LocaleManager`) is API 33+; the floor here is 26.
- Reusable UI: `SelectionBox`, `SectionLabel`, `PagerDots`, `IllustrationPanel`,
  `LabelledTextField`, `PhoneNumberField`, `OtpEntry`/`OtpProgress`, `CaptureTile`,
  `AdminVerifyChip`, `ExtractedFieldRow`, `NoticeCard`, and `ui/VehicleTypeLabels.kt`.
- **A proper noun is data, not copy.** The language endonyms (`සිංහල`), the `+94` prefix and the
  `7X XXX XXXX` mask are Kotlin constants, because three identical values in the three
  `strings.xml` files is exactly what `StringResourceTest` (correctly) fails on. City names come
  from `config.operating_cities` for the same reason.

## Rules this module is built on

- **AL-31: no hamburger.** Navigation is the bottom-nav **Menu** tab, and `nav/DriverTab.kt` is the
  only place a top-level destination is declared. `NavigationShellTest` asserts there are exactly
  four tabs and that Menu is one of them.
- **No dynamic colour.** D2' §0.2 is the single source of truth shared with Figma, SwiftUI and the
  Tailwind preset; `dynamicLightColorScheme` would hand the app the wallpaper's palette.
- **`DriverEnvironment` is the only file that reads `BuildConfig`.** The gateway origin and the MQTT
  host are the two values a release build cannot afford to have wrong in two places.
- **Background GPS and MQTT stay native.** `PositionForegroundService` owns the socket, the fixes,
  the wake lock and the notification; `:shared` supplies the cadence (`AdaptiveRateEngine`), the
  topics (`MqttTopics`), the payload (`PositionCodec`) and the replay pacing
  (`PositionReplayQueue`). `PositionPipeline` is the seam between them and is where the R-17
  behaviour is tested, on the JVM, with no radio.
- **`seq` must never rewind.** It comes from C018's `PersistentPositionSequencer` through
  `GpsBufferJournal`; a counter that restarted at zero makes `position-processor-svc` discard
  everything the app publishes while the app believes it is publishing (R-17/T-05).
- **The MQTT username is the vehicle id**, and the credential is the MQTT **session** JWT — never
  the API access token. EMQX validates it at CONNECT only, so a rotation means a reconnect.
- **A deep link is resolved, not trusted.** `PushRouter` maps a `mageride://…` URI onto a known
  `DriverRoute`; an unrecognised one opens nothing.

## Things that will bite

- **`org.jetbrains.kotlin.android` is not applied.** AGP 9 has built-in Kotlin support and refuses
  the plugin outright.
- **The `google-services` plugin is not applied either**, and there is no `google-services.json`.
  firebase-messaging compiles and `DriverMessagingService` is registered, but **FCM does not deliver
  until C124 lands the Firebase project**. Nothing else about push is blocked by it.
- **MapLibre is the `-opengl` flavour** (`org.maplibre.gl:android-sdk-opengl`). The default
  `android-sdk` artifact requires Vulkan 1.0 in its manifest, which Play uses to filter devices —
  on the Android 8.0 floor that cuts off exactly the budget handsets this platform is for. Do not
  add `android-sdk-ktx`: it depends on the default artifact and fails `checkDuplicateClasses`.
- **Kotlin block comments nest.** A KDoc containing `values*/strings.xml` closes itself on the `*/`
  and the file stops parsing several declarations later. Same trap C014 hit.
- **detekt's `LongMethod` and `LongParameterList` carry `ignoreAnnotated: ['Composable']`**
  (C068, in `config/detekt/detekt.yml`). A screen function is the wireframe's layout tree and a
  component's parameter list is an M3 slot API; nothing non-composable is exempt.
- **The C019 test kit's top-level functions need the jar's `.kotlin_module`.** Without it
  `FakeApiBackend` imports and `backend.mageRideApi()` does not — Kotlin finds a package's
  file-facade classes through that file alone. Fixed in `shared/kmp/build.gradle.kts` by C068.
- **ktlint's `function-naming` needs the `@Composable` exemption**, which is set once in the repo
  root `.editorconfig`. detekt's config is `config/detekt/detekt.yml`, also shared.
- **`kotlin-test` resolves to no variant under AGP's built-in Kotlin.** Use `libs.kotlin.testjunit`
  (the `kotlin-test-junit` artifact) — the alias is deliberately not named `kotlin-test-junit`,
  because that would turn `kotlin-test` into a catalogue *group* and break `libs.kotlin.test.get()`
  in `shared/kmp/build.gradle.kts`.
- **Unit tests run with `isReturnDefaultValues = true`** and the working directory is the module
  directory, which is what lets `ManifestTest` and `StringResourceTest` read the real files.
- **iOS does not compile on this host** (root CLAUDE.md). Nothing here is iOS, but C085's SwiftUI
  shell mirrors these tokens and route names — keep them in step.
