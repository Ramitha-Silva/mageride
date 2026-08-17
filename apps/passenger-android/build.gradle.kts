// MageRide — Passenger Android application shell (C076 passenger-android-shell).
//
// The host every passenger screen group plugs into: theme, trilingual resources, navigation and
// its drawer, the MapLibre map, the SignalR live plane, the Koin graph and FCM. It owns NO
// wireframe screen — C077–C084 do. What was here before was C025's walking skeleton; that slice
// declared itself throwaway and this replaces it.
//
// Verify: ./gradlew :apps:passenger-android:testDebugUnitTest :apps:passenger-android:assembleDebug
//
// **This app has no MQTT client and never will.** D3' §3.3 splits the two real-time planes:
// device position INGEST is MQTT, passenger realtime-OUT is SignalR. A passenger publishes no
// position to the broker, so there is no HiveMQ dependency, no foreground service and no
// `MQTT_HOST` build field — the three things this build script would otherwise mirror from
// `apps/driver-android`.

// NO `org.jetbrains.kotlin.android`. AGP 9 has built-in Kotlin support and refuses the plugin
// outright ("no longer required for Kotlin support since AGP 9.0"), the same way it refuses
// `com.android.library` in a KMP project — see shared/kmp/CLAUDE.md.
//
// The `google-services` plugin is deliberately absent even though firebase-messaging is a
// dependency: it hard-fails the build without a `google-services.json`, and the Firebase project
// is C124's (D7' §13 secrets). FCM compiles and registers here; it does not deliver until that
// file lands. Same call C067 made.
plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.detekt)
    alias(libs.plugins.ktlint)
}

android {
    namespace = "lk.mageride.passenger"
    compileSdk = libs.versions.androidCompileSdk.get().toInt()

    defaultConfig {
        applicationId = "lk.mageride.passenger"
        // URD NFR-22 — Android 8.0. Not a preference; do not raise without a micro-change-set.
        minSdk = libs.versions.androidMinSdk.get().toInt()
        targetSdk = libs.versions.androidTargetSdk.get().toInt()
        versionCode = 1

        // Reaches the gateway as `X-App-Version`, where D-31's minimum-version gate reads it. The
        // gateway's floor is 1.0.0 (C008), so anything below this is answered 426 on every route.
        // `PassengerEnvironment` reads it back out of BuildConfig — one number, not two.
        versionName = "1.0.0"
    }

    androidResources {
        // Si and Ta are not "extra" locales — CLAUDE.md makes all three mandatory, and En is the
        // fallback rather than the primary. Listing them keeps AGP's resource shrinker and the
        // Play language split from dropping the two that matter most here.
        localeFilters += setOf("en", "si", "ta")
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    // ---------------------------------------------------------------------------------
    // Build-type configuration is the deployment surface, and it belongs in one place.
    //
    // Everything below reaches Kotlin through BuildConfig and is read exactly once, by
    // `PassengerEnvironment` (di/PassengerEnvironment.kt). No other file in this module may read
    // BuildConfig: a value that is read in two places is a value that gets overridden in one.
    // ---------------------------------------------------------------------------------
    buildTypes {
        getByName("debug") {
            isMinifyEnabled = false
            applicationIdSuffix = ".debug"

            // The replica edge, NOT the emulator loopback.
            //
            // `http://10.0.2.2:5000` is the host machine's localhost as seen from inside an
            // EMULATOR. On a physical handset it resolves to nothing, so every call times out and
            // the app shows its generic "Something went wrong" — which is what a debug APK on a
            // real phone did, with no request ever reaching the edge to explain it.
            //
            // Pointed at the replica so a debug build can be exercised on a real device. Revert
            // to `http://10.0.2.2:5000` for emulator work against the local dev stack; the
            // release variant below is unaffected either way.
            //
            // The SignalR hub is on the same origin (`/hubs/live`), so there is one value here
            // and not two. `usesCleartextTraffic` stays in the debug manifest for the PMTiles
            // value below, which is still loopback.
            buildConfigField("String", "API_BASE_URL", "\"https://api.mageride.lk\"")
            // PMTiles archive for the map style, on the tile host rather than the emulator
            // loopback — same reason as API_BASE_URL above: `10.0.2.2` is nothing on a physical
            // handset, so the basemap renders blank while the markers still draw. Revert to
            // `http://10.0.2.2:8080/lk.pmtiles` for emulator work against the local dev stack.
            buildConfigField("String", "PMTILES_URL", "\"https://tiles.mageride.lk/lk.pmtiles\"")
            // Play Console cloud project number for Play Integrity (D-30). 0 means "this build
            // cannot attest", which `PlatformAttestationProvider` turns into a null header and
            // the gateway into a 401 — the intended failure mode, never a silent bypass.
            buildConfigField("long", "INTEGRITY_CLOUD_PROJECT", "0L")
        }
        getByName("release") {
            // C103 owns signing and the R8 rules; what is fixed here is the deployment surface,
            // because a release build pointed at the dev gateway is the one mistake that cannot
            // be caught by a test.
            isMinifyEnabled = false
            buildConfigField("String", "API_BASE_URL", "\"https://api.mageride.lk\"")
            buildConfigField("String", "PMTILES_URL", "\"https://tiles.mageride.lk/lk.pmtiles\"")
            buildConfigField("long", "INTEGRITY_CLOUD_PROJECT", "0L")
        }
    }

    testOptions {
        unitTests {
            // `android.os.Build`, `Log` and `Uri` are stubs in a local unit test and every member
            // throws by default. `:shared` and the driver app make the same call for the same
            // reason. Nothing here asserts against a framework return value; the flag is what
            // keeps a stub from taking down a test of our own code.
            isReturnDefaultValues = true
        }
    }

    // The SignalR client ships OkHttp, RxJava and Gson, each carrying its own JAR index and
    // licence files, and the APK can hold only one of each path. None of them is code — dropping
    // them changes nothing at runtime.
    packaging {
        resources {
            excludes += setOf(
                "META-INF/INDEX.LIST",
                "META-INF/DEPENDENCIES",
                "META-INF/LICENSE*",
                "META-INF/NOTICE*",
            )
        }
    }
}

kotlin {
    jvmToolchain(libs.versions.jvmToolchain.get().toInt())
}

dependencies {
    implementation(project(":shared"))

    // C013 leaves the HTTP engine to the app on purpose — there is no multiplatform one.
    implementation(libs.ktor.client.okhttp)

    // D6' §5: "Client = SignalR Java client (Android)". The passenger half of the realtime-out
    // contract, and the reason this app is a live map at all.
    implementation(libs.signalr)
    // Its hub protocol's JSON tree type. `:shared`'s `MageRideJson` does the actual decoding —
    // see the catalogue note and `live/SignalRLiveHubTransport`.
    implementation(libs.gson)

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.graphics)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons.extended)
    implementation(libs.compose.ui.tooling.preview)
    debugImplementation(libs.compose.ui.tooling)

    // D2' §0.1 — "a screen = a Compose @Composable route inside a NavHost/Scaffold".
    implementation(libs.androidx.navigation.compose)

    // Koin: `:shared` exports koin-core; these add `androidContext()` and `koinViewModel()`.
    implementation(libs.koin.android)
    implementation(libs.koin.androidx.compose)

    // D2' §0.3 — MapLibre GL Native over PMTiles. See the catalogue note on the `-opengl`
    // flavour; the default `android-sdk` artifact requires Vulkan and Play would filter out the
    // Android 8.0 handsets this platform is for. No `android-sdk-ktx`: it depends on the default
    // artifact and having both on one classpath fails `checkDuplicateClasses`.
    implementation(libs.maplibre.android)

    // The handset's own fix. Not for publishing — a passenger publishes nothing — but for the
    // R-06 geocell anchor, MAP-02's accuracy circle and §0.3's blue user dot.
    implementation(libs.play.services.location)

    // SCR-PA-017's "Scan driver's QR" (AL-22). CameraX for the viewfinder, `zxing:core` for the
    // decode — the same pair the driver app uses for SCR-DA-027, and for the same reason: the
    // `-android-embedded` artifacts ship a scanning Activity and a theme this app would have to
    // fight. `zxing:core` is pure Java with no camera and no Android dependency.
    implementation(libs.androidx.camera.core)
    implementation(libs.androidx.camera.camera2)
    implementation(libs.androidx.camera.lifecycle)
    implementation(libs.androidx.camera.view)
    implementation(libs.zxing.core)

    // Ride, package and location-request pushes. Inert until a `google-services.json` exists —
    // see the plugins block.
    implementation(platform(libs.firebase.bom))
    implementation(libs.firebase.messaging)

    testImplementation(libs.kotlin.testjunit)
    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    // C019's test kit, on the consumable configuration its build script publishes. Fakes,
    // fixtures and the scenario builders every module's tests share.
    testImplementation(project(path = ":shared", configuration = "testKitElements"))
}

// ---------------------------------------------------------------------------------------
// Static analysis. Config lives at the repo root — C011 put it there precisely so this module
// and the driver app would share one rule set rather than drifting into two.
// ---------------------------------------------------------------------------------------
detekt {
    buildUponDefaultConfig = true
    config.setFrom(rootProject.file("config/detekt/detekt.yml"))
    source.setFrom(files("src"))
    parallel = true
}

ktlint {
    version.set(libs.versions.ktlint)
    // Generated sources — BuildConfig and R — are not ours to format.
    filter {
        exclude { it.file.path.contains("/build/") }
    }
}
