// MageRide — Passenger Android, walking-skeleton shell (C025).
//
// THROWAWAY FIDELITY. This slice owns no wireframe screen: it builds the thinnest versions of
// screens formally owned by C077–C080 (SCR-PA-*), and Wave 4a replaces them at full fidelity.
// What it is for is the wiring — that `:shared`'s api-client, `LiveHub` contract and geocell maths
// actually compose into an app — not for how any of it looks.
//
// Deliberately absent: MapLibre (C077 owns the real map; here the "live map" is a list),
// trilingual resources (C078 — the fence says so), theming, navigation, DI, and the on-device
// database. Adding any of them would make this look like a screen someone should keep.
//
// Verify: ./gradlew :apps:passenger-android:assembleDebug

// NO `org.jetbrains.kotlin.android`. AGP 9 has built-in Kotlin support and refuses the plugin
// outright ("no longer required for Kotlin support since AGP 9.0"), the same way it refuses
// `com.android.library` in a KMP project — see shared/kmp/CLAUDE.md. The catalogue still declares
// the alias for anything on an older AGP.
plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
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
        versionName = "1.0.0"
    }

    buildFeatures {
        compose = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    // The skeleton is a debug build and is never signed or shrunk; C103 owns the release setup.
    buildTypes {
        getByName("debug") {
            isMinifyEnabled = false
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

    // D6' §5: the SignalR Java client is the Android half of the realtime-out contract.
    implementation(libs.signalr)

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.graphics)
    implementation(libs.compose.material3)
}
