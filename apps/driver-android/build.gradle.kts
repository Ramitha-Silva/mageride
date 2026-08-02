// MageRide — Driver Android application shell (C067 driver-android-shell).
//
// The host every screen group plugs into: theme, trilingual resources, navigation, the map, the
// Koin graph and the two native services KMP cannot own (position publishing, FCM). It owns NO
// wireframe screen — C068–C075 do. What was here before was C025's walking skeleton; that slice
// declared itself throwaway and this replaces it.
//
// Verify: ./gradlew :apps:driver-android:testDebugUnitTest :apps:driver-android:assembleDebug

// NO `org.jetbrains.kotlin.android`. AGP 9 has built-in Kotlin support and refuses the plugin
// outright ("no longer required for Kotlin support since AGP 9.0"), the same way it refuses
// `com.android.library` in a KMP project — see shared/kmp/CLAUDE.md. The catalogue still declares
// the alias for anything on an older AGP.
//
// The `google-services` plugin is deliberately absent even though firebase-messaging is a
// dependency: it hard-fails the build without a `google-services.json`, and the Firebase project
// is C124's (D7' §13 secrets). See the C067 handoff — FCM compiles and registers here; it does
// not deliver until that file lands.
plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.detekt)
    alias(libs.plugins.ktlint)
}

android {
    namespace = "lk.mageride.driver"
    compileSdk = libs.versions.androidCompileSdk.get().toInt()

    defaultConfig {
        applicationId = "lk.mageride.driver"
        // URD NFR-22 — Android 8.0. Not a preference; do not raise without a micro-change-set.
        minSdk = libs.versions.androidMinSdk.get().toInt()
        targetSdk = libs.versions.androidTargetSdk.get().toInt()
        versionCode = 1

        // Reaches the gateway as `X-App-Version`, where D-31's minimum-version gate reads it. The
        // gateway's floor is 1.0.0 (C008), so anything below this is answered 426 on every route.
        // `DriverEnvironment` reads it back out of BuildConfig — one number, not two.
        versionName = "1.0.0"
    }

    androidResources {
        // Si and Ta are not "extra" locales — CLAUDE.md makes all three mandatory, and En is the
        // fallback rather than the primary. Listing them keeps AGP's resource shrinker and the
        // Play language split from dropping the two that matter most here. (`localeFilters`, not
        // the `resourceConfigurations` this replaced: AGP 9 deprecated the older spelling.)
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
    // `DriverEnvironment` (di/DriverEnvironment.kt). No other file in this module may read
    // BuildConfig: a value that is read in two places is a value that gets overridden in one.
    // ---------------------------------------------------------------------------------
    buildTypes {
        getByName("debug") {
            isMinifyEnabled = false
            applicationIdSuffix = ".debug"

            // The dev gateway inside the emulator's loopback. Plain HTTP — see
            // `usesCleartextTraffic` in the debug manifest, which is scoped to this variant only.
            buildConfigField("String", "API_BASE_URL", "\"http://10.0.2.2:5000\"")
            buildConfigField("String", "MQTT_HOST", "\"10.0.2.2\"")
            buildConfigField("int", "MQTT_PORT", "1883")
            buildConfigField("boolean", "MQTT_TLS", "false")
            // PMTiles archive for the map style. R2 in production (D2' §0.1); the dev stack
            // serves the same archive off the compose `tiles` volume.
            buildConfigField("String", "PMTILES_URL", "\"http://10.0.2.2:8080/lk.pmtiles\"")
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
            buildConfigField("String", "MQTT_HOST", "\"mqtt.mageride.lk\"")
            buildConfigField("int", "MQTT_PORT", "8883")
            buildConfigField("boolean", "MQTT_TLS", "true")
            buildConfigField("String", "PMTILES_URL", "\"https://tiles.mageride.lk/lk.pmtiles\"")
            buildConfigField("long", "INTEGRITY_CLOUD_PROJECT", "0L")
        }
    }

    testOptions {
        unitTests {
            // `android.os.Build`, `Log` and `Uri` are stubs in a local unit test and every member
            // throws by default. `:shared` makes the same call for the same reason — see its
            // `withHostTestBuilder`. Nothing here asserts against a framework return value; the
            // flag is what keeps a stub from taking down a test of our own code.
            isReturnDefaultValues = true
        }
    }

    // HiveMQ ships a shaded client: Netty, RxJava and its own classes arrive in several jars, each
    // carrying its own JAR index and licence files, and the APK can hold only one of each path.
    // None of them is code — dropping them changes nothing at runtime.
    packaging {
        resources {
            excludes += setOf(
                "META-INF/INDEX.LIST",
                "META-INF/DEPENDENCIES",
                "META-INF/LICENSE*",
                "META-INF/NOTICE*",
                "META-INF/io.netty.versions.properties",
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

    // D6' §3: "Driver App client = HiveMQ (Android) in a native foreground service".
    implementation(libs.hivemq.mqtt)

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    // `LifecycleService` gives the foreground service a scope that dies with it, and
    // `ProcessLifecycleOwner` is how the shell tells foreground from background without an
    // Activity reference.
    implementation(libs.androidx.lifecycle.service)
    implementation(libs.androidx.lifecycle.process)

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.graphics)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons.extended)
    implementation(libs.compose.ui.tooling.preview)
    debugImplementation(libs.compose.ui.tooling)

    // D2' §0.1 — "a Compose @Composable route inside a NavHost/Scaffold".
    implementation(libs.androidx.navigation.compose)

    // Koin: `:shared` exports koin-core; these add `androidContext()` and `koinViewModel()`.
    implementation(libs.koin.android)
    implementation(libs.koin.androidx.compose)

    // D2' §0.3 — MapLibre GL Native over PMTiles. See the catalogue note on the `-opengl` flavour.
    //
    // No `android-sdk-ktx`: it depends on the DEFAULT `android-sdk` artifact, and having both on
    // the classpath fails `checkDebugDuplicateClasses` on every `org.maplibre.android` class. Its
    // whole contribution is `awaitMap()`/`awaitStyle()` over the callbacks, which `MageRideMap`
    // already wraps once for the whole app.
    implementation(libs.maplibre.android)

    // The foreground service's fix source.
    implementation(libs.play.services.location)

    // E-01's ride pushes. Inert until a `google-services.json` exists — see the plugins block.
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
// and C076 would share one rule set rather than drifting into three.
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
