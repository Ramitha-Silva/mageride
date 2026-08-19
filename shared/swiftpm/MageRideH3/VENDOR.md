# Vendored: H3

`Sources/CH3` is a verbatim copy of the H3 core library's C sources. Nothing in it has been edited
except the one file CMake generates (below), and nothing has been **moved** except the one header
SPM cannot leave in place (below that). No line of vendored C differs from upstream.

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

**The one moved file.** `polygonAlgos.h` sits in `Sources/CH3/` rather than in
`Sources/CH3/include/`, where the copy rule above would otherwise have put it. Its **contents are
untouched**; only its path differs.

It is not a header in the ordinary sense — it is a template body, included by `polygon.c` and
`linkedGeo.c` *after* each defines `TYPE`, `IS_EMPTY`, `INIT_ITERATION` and `ITERATE`, and it opens
with four `#error` directives that say exactly that. Upstream's CMake build never compiles it on its
own, so upstream can keep it in `include/`. SPM cannot: `publicHeadersPath` generates an umbrella
*directory* module map, and clang compiles every header underneath it standalone to build the
module. That yields 15 errors from this one file and fails the precompiled module, which fails every
app linking the package.

Moving it is the smallest possible fix — a quoted `#include "polygonAlgos.h"` searches the
includer's own directory first, so both `.c` files still resolve it with no edit. The alternative,
a hand-written `module.modulemap` listing every header explicitly, would have to be re-checked on
every upgrade.

**On upgrading, move it again.** A fresh copy of `src/h3lib/include/*.h` will put it back, and the
failure returns.

**Upgrading.** Re-copy the two directories from the new tag, re-run the substitution above with the
new version numbers, move `polygonAlgos.h` down one level as described above, update this file, and
run `swift test` in this package: `H3ContractTests` pins
the R-06 view (res 7 + `ring(2)` = 19 cells) and the res-7 → res-5 parent relationship that
`GeoCells` depends on. A version bump that changed either would be a platform-wide incident, which
is what those assertions exist to turn into a failing build.

**What is NOT vendored.** Only `src/h3lib`. The command-line tools, the benchmarks, the fuzzers, the
test suite and the CMake build are all left upstream; this package needs the library and nothing
else.
