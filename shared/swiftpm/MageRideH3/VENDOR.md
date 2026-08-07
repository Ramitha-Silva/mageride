# Vendored: H3

`Sources/CH3` is a verbatim copy of the H3 core library's C sources. Nothing in it has been edited
except the one file CMake generates (below).

| | |
|---|---|
| Upstream | <https://github.com/uber/h3> |
| Tag | `v4.4.0` |
| Commit | `f8e4cdad77d7e09f6a4c1fa244a8b826bb4eb95f` |
| Licence | Apache-2.0 — see `LICENSE`, copied unmodified |
| Copied from | `src/h3lib/lib/*.c` → `Sources/CH3/`, `src/h3lib/include/*.h` → `Sources/CH3/include/` |

**Why this version.** `gradle/libs.versions.toml` pins `com.uber:h3` at **4.4.0** for the Android
apps and the JVM harness, and `h3-java` is a JNI wrapper over these same sources. Matching the tag
is what makes "the two apps compute the same nineteen cells" a fact about one library rather than a
claim about two. (`pocketken.H3`, which `MageRide.Shared.Geo` binds server-side, is a C# port of the
same algorithms; the index encoding has been stable across all of 4.x.)

**The one generated file.** `src/h3lib/include/h3api.h` does not exist upstream — CMake produces it
from `h3api.h.in` by substituting three version macros, and that is the *only* substitution in the
template:

```
@H3_VERSION_MAJOR@ → 4
@H3_VERSION_MINOR@ → 4
@H3_VERSION_PATCH@ → 0
```

`Sources/CH3/include/h3api.h` is that file. Nothing else about it differs from the template, and the
macros are informational — no code in this package or in either app reads them.

**Upgrading.** Re-copy the two directories from the new tag, re-run the substitution above with the
new version numbers, update this file, and run `swift test` in this package: `H3ContractTests` pins
the R-06 view (res 7 + `ring(2)` = 19 cells) and the res-7 → res-5 parent relationship that
`GeoCells` depends on. A version bump that changed either would be a platform-wide incident, which
is what those assertions exist to turn into a failing build.

**What is NOT vendored.** Only `src/h3lib`. The command-line tools, the benchmarks, the fuzzers, the
test suite and the CMake build are all left upstream; this package needs the library and nothing
else.
