// MageRide — root Gradle build (C001 repo-scaffold).
//
// Three projects live here: the KMP shared module every app depends on, and the two
// Android apps. The iOS apps are Xcode projects and are deliberately NOT Gradle
// projects — C085 / C094 own them.
//
// The module build scripts themselves are owned by later components and are absent on
// purpose: shared/kmp/build.gradle.kts is C011, apps/*-android/build.gradle.kts are
// C067 / C076. Until then these are empty Gradle projects, which is enough for
// `./gradlew projects` to resolve the layout.

pluginManagement {
    repositories {
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    // Modules declare dependencies, never repositories.
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
        mavenCentral()
    }
}

rootProject.name = "mageride"

// The KMP module lives at shared/kmp but keeps the short path `:shared`, which is the
// name D7 §1/§6 uses in its build commands (`./gradlew :shared:assembleXCFramework`).
include(":shared")
project(":shared").projectDir = file("shared/kmp")

include(":apps:driver-android")
include(":apps:passenger-android")

// C025 — the walking-skeleton end-to-end run. A plain JVM program, not an app: it drives the
// SAME api-client, SignalR and MQTT contracts the two Android shells do, against the real
// Docker Compose stack, so the run proves the contracts and not a second implementation of them.
// `:shared` grew a `jvm()` target for it.
include(":e2e:walking-skeleton")
