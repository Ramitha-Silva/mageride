# KMP Shared Module Conventions
- Kotlin Multiplatform, targets: Android + iOS
- Contains: DTOs, API client, domain logic, auth module, test kit
- This module is built FIRST — all 4 apps depend on it
- No platform-specific code here — use expect/actual only when unavoidable
- Build host is Linux: verify common + Android/JVM targets here (`./gradlew testDebugUnitTest`);
  iOS klib compilation is verified on a Mac — do not mark iOS targets DONE from this host
