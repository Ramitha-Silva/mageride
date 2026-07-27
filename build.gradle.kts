// MageRide — root project (C001 repo-scaffold).
//
// No plugin is applied here on purpose. Every version lives in gradle/libs.versions.toml
// and each module applies what it needs via `alias(libs.plugins.…)`. Declaring plugins at
// the root — even with `apply false` — would pull AGP and the Kotlin compiler onto the
// buildscript classpath for every invocation, including `./gradlew projects`.

// IMPORTANT: the repo's top-level `build/` directory is the MageRide *build plan*
// (manifest.yaml, prompts/, progress.md, screen_coverage.md) — it is not Gradle output.
// Gradle's default root build directory is exactly that path, so move it aside. Subprojects
// keep their conventional <module>/build directories, which do not collide with anything.
layout.buildDirectory.set(layout.projectDirectory.dir(".gradle/root-build"))

tasks.register("clean", Delete::class) {
    group = "build"
    description = "Deletes the root project's build directory."
    delete(layout.buildDirectory)
}
