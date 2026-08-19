#!/usr/bin/env python3
"""Writes an iOS app's `*.xcodeproj/project.pbxproj` from its source tree.

WHY THIS EXISTS
---------------
`.pbxproj` is the committed artefact — `.github/workflows/ci.yml` probes for it and runs
`xcodebuild` against it, and Xcode opens it directly. It is also a file in which every source file
needs two 24-hex object ids, a group membership and a build-phase entry, which is exactly the kind
of hand-editing that goes wrong quietly: a file added to the group and forgotten in `Sources`
compiles for the person who added it (their Xcode fixed it locally) and not in CI.

So the tree is the source of truth and this script derives the project from it. Ids are the first
24 hex characters of `sha1(kind + ':' + path)`, so the output is byte-identical for the same tree on
any machine and a diff shows only what actually changed.

WHY IT IS HERE RATHER THAN IN EACH APP (Δ C094)
-----------------------------------------------
There are two iOS apps and the generator is the same program for both — the C085 handoff asked the
second one not to make a third copy. What differs between them is data, not logic: the target names,
the bundle id and the Swift-package list. Those are an [AppProject]; everything below it is shared.

`apps/*-ios/Tools/generate_xcodeproj.py` are two-line shims that describe their app and call
[generate]. Run either from anywhere; paths resolve relative to the app directory.

**This is NOT one of `build/`'s generated files** and CLAUDE.md's "never hand-edit a generated file"
rule is not what governs it — that rule is about `build/prompts`, `build/progress.md` and
`build/screen_coverage.md`, which are generated from the manifest. Editing a `.pbxproj` by hand is
allowed; re-running this is just easier and safer. What you must not do is change one and not the
other.

WHAT IT DOES NOT DO
-------------------
It does not resolve Swift packages, sign anything, or know whether the code compiles. `xcodebuild`
does all three, on macOS.

ADDING A FILE
-------------
Put the `.swift` in `<AppTarget>/<Group>/`, or the test in `<AppTarget>Tests/`, and re-run the app's
shim. A new directory becomes a new group automatically. A new *resource* (an asset catalogue, a
`.strings`, a `.json`) is picked up too — see RESOURCE_SUFFIXES.
"""

from __future__ import annotations

import hashlib
import pathlib
import sys

# Anything compiled.
SOURCE_SUFFIXES = {".swift"}

# Anything copied into the bundle. `.lproj` directories are handled as variant groups below;
# `Info.plist` and `.entitlements` are build settings rather than resources and are excluded.
RESOURCE_SUFFIXES = {".xcassets", ".json"}


class AppProject:
    """Everything that differs between the two iOS apps.

    Deliberately small. If something has to be added here to make one app build, check first that it
    is genuinely per-app and not a shared decision that has drifted — the deployment target, the
    object version and the three `.lproj` are shared on purpose.
    """

    def __init__(
        self,
        root: pathlib.Path,
        app_target: str,
        bundle_id: str,
        packages: list[dict],
        app_products: list[str],
        deployment_target: str = "16.0",
    ) -> None:
        #: The app directory — `apps/driver-ios` or `apps/passenger-ios`.
        self.root = root.resolve()
        #: The app target, which is also the source directory and the `.xcodeproj` stem.
        self.app_target = app_target
        #: The test target. Always `<app>Tests`, which is also its directory.
        self.test_target = f"{app_target}Tests"
        self.bundle_id = bundle_id
        #: Swift packages, in the order the project lists them.
        self.packages = packages
        #: Which products the app target links.
        self.app_products = app_products
        self.deployment_target = deployment_target

    @property
    def project_dir(self) -> pathlib.Path:
        return self.root / f"{self.app_target}.xcodeproj"

    @property
    def entitlements_name(self) -> str:
        return f"{self.app_target}.entitlements"


def oid(kind: str, key: str) -> str:
    """A stable 24-hex object id. Deterministic, so the file diffs cleanly."""
    return hashlib.sha1(f"{kind}:{key}".encode()).hexdigest()[:24].upper()


class Tree:
    """The source tree, as the groups and files the project needs."""

    def __init__(self, project: AppProject) -> None:
        self.project = project
        self.sources: dict[str, list[pathlib.Path]] = {project.app_target: [], project.test_target: []}
        self.resources: list[pathlib.Path] = []
        self.variant_groups: dict[str, list[pathlib.Path]] = {}
        self.files: list[pathlib.Path] = []

    def scan(self) -> None:
        project, root = self.project, self.project.root
        for target, directory in (
            (project.app_target, root / project.app_target),
            (project.test_target, root / project.test_target),
        ):
            # Sorted on the POSIX string, not on the `Path`. `PurePath` comparison uses the HOST's
            # rules — `PureWindowsPath` case-folds and `PurePosixPath` does not — so the same tree
            # scanned on Windows emitted the same ids in a different ORDER, and a one-file change
            # arrived as a diff touching every group. See `rel`, which the ids themselves needed.
            for path in sorted(directory.rglob("*"), key=lambda entry: entry.as_posix()):
                if any(part.endswith(".lproj") for part in path.parts):
                    continue
                if any(part.endswith(".xcassets") for part in path.parts[:-1]):
                    continue
                if path.suffix in SOURCE_SUFFIXES and path.is_file():
                    self.sources[target].append(path)
                    self.files.append(path)
                elif path.suffix in RESOURCE_SUFFIXES and target == project.app_target:
                    self.resources.append(path)
                    self.files.append(path)

        # `.lproj` siblings become one variant group per table, which is how Xcode models a
        # localised resource: one entry in `Resources`, one child per language.
        for lproj in sorted((root / project.app_target / "Resources").glob("*.lproj"), key=lambda p: p.as_posix()):
            for table in sorted(lproj.glob("*.strings"), key=lambda p: p.as_posix()):
                # NOT added to `self.files`: a localised table's reference is emitted by the
                # variant-group section with a language-scoped path, and a second reference with
                # the same id would be a duplicate object.
                self.variant_groups.setdefault(table.name, []).append(table)


def rel(root: pathlib.Path, path: pathlib.Path) -> str:
    """The path as the project file spells it — always POSIX, on every host.

    `str()` on a `PurePath` uses the HOST's separator, and this string is both what Xcode reads and
    what every object id is a hash of. Generated on Windows it produced `Booking\MapPickModel.swift`
    — a path Xcode cannot resolve, and a different sha1 for every file in the project, so a one-file
    change rewrote all 211 ids. `as_posix()` is what `str()` already returns on Linux and macOS, so
    the output there is unchanged; this only stops a Windows session rewriting the whole file.
    """
    return path.relative_to(root).as_posix()


def file_type(path: pathlib.Path) -> str:
    return {
        ".swift": "sourcecode.swift",
        ".json": "text.json",
        ".xcassets": "folder.assetcatalog",
        ".strings": "text.plist.strings",
        ".plist": "text.plist.xml",
        ".entitlements": "text.plist.entitlements",
        ".xcconfig": "text.xcconfig",
    }.get(path.suffix, "text")


def build_pbxproj(tree: Tree) -> str:
    project = tree.project
    root = project.root
    app_target, test_target = project.app_target, project.test_target
    out: list[str] = []
    w = out.append

    def path_of(path: pathlib.Path) -> str:
        return rel(root, path)

    def file_ref(path: pathlib.Path) -> str:
        return oid("fileRef", path_of(path))

    def build_file(path: pathlib.Path, phase: str) -> str:
        return oid(f"buildFile:{phase}", path_of(path))

    # ---- header -------------------------------------------------------------------------
    w("// !$*UTF8*$!")
    w("{")
    w("\tarchiveVersion = 1;")
    w("\tclasses = {")
    w("\t};")
    # 56 = Xcode 14. Deliberately not 77 (Xcode 16's file-system-synchronised groups): this project
    # is authored on a Linux host that cannot open it, so the format has to be the one every Xcode
    # from 14 to the current release reads. The generator above is what buys back the convenience
    # objectVersion 77 would have given.
    w("\tobjectVersion = 56;")
    w("\tobjects = {")

    # ---- PBXBuildFile -------------------------------------------------------------------
    w("\n/* Begin PBXBuildFile section */")
    for target, paths in tree.sources.items():
        for path in paths:
            w(
                f"\t\t{build_file(path, target)} /* {path.name} in Sources */ = "
                f"{{isa = PBXBuildFile; fileRef = {file_ref(path)} /* {path.name} */; }};"
            )
    for path in tree.resources:
        w(
            f"\t\t{build_file(path, 'Resources')} /* {path.name} in Resources */ = "
            f"{{isa = PBXBuildFile; fileRef = {file_ref(path)} /* {path.name} */; }};"
        )
    for table in sorted(tree.variant_groups):
        group = oid("variantGroup", table)
        w(
            f"\t\t{oid('buildFile:Resources', table)} /* {table} in Resources */ = "
            f"{{isa = PBXBuildFile; fileRef = {group} /* {table} */; }};"
        )
    for product in project.app_products:
        w(
            f"\t\t{oid('buildFile:Frameworks', product)} /* {product} in Frameworks */ = "
            f"{{isa = PBXBuildFile; productRef = {oid('productDep', product)} /* {product} */; }};"
        )
    w("/* End PBXBuildFile section */")

    # ---- PBXFileReference ---------------------------------------------------------------
    w("\n/* Begin PBXFileReference section */")
    for path in sorted(set(tree.files), key=path_of):
        w(
            f"\t\t{file_ref(path)} /* {path.name} */ = {{isa = PBXFileReference; "
            f'lastKnownFileType = {file_type(path)}; name = "{path.name}"; '
            f'path = "{path.name}"; sourceTree = "<group>"; }};'
        )
    for extra in (f"{app_target}/Info.plist", f"{app_target}/{project.entitlements_name}",
                  "Config/Shared.xcconfig", "Config/Debug.xcconfig", "Config/Release.xcconfig"):
        path = root / extra
        w(
            f"\t\t{oid('fileRef', extra)} /* {path.name} */ = {{isa = PBXFileReference; "
            f'lastKnownFileType = {file_type(path)}; name = "{path.name}"; '
            f'path = "{path.name}"; sourceTree = "<group>"; }};'
        )
    w(
        f"\t\t{oid('product', app_target)} /* {app_target}.app */ = {{isa = PBXFileReference; "
        f"explicitFileType = wrapper.application; includeInIndex = 0; "
        f'path = "{app_target}.app"; sourceTree = BUILT_PRODUCTS_DIR; }};'
    )
    w(
        f"\t\t{oid('product', test_target)} /* {test_target}.xctest */ = {{isa = PBXFileReference; "
        f"explicitFileType = wrapper.cfbundle; includeInIndex = 0; "
        f'path = "{test_target}.xctest"; sourceTree = BUILT_PRODUCTS_DIR; }};'
    )
    w("/* End PBXFileReference section */")

    # ---- PBXFrameworksBuildPhase --------------------------------------------------------
    w("\n/* Begin PBXFrameworksBuildPhase section */")
    w(f"\t\t{oid('phase', 'frameworks:app')} = {{")
    w("\t\t\tisa = PBXFrameworksBuildPhase;")
    w("\t\t\tbuildActionMask = 2147483647;")
    w("\t\t\tfiles = (")
    for product in project.app_products:
        w(f"\t\t\t\t{oid('buildFile:Frameworks', product)} /* {product} in Frameworks */,")
    w("\t\t\t);")
    w("\t\t\trunOnlyForDeploymentPostprocessing = 0;")
    w("\t\t};")
    w(f"\t\t{oid('phase', 'frameworks:tests')} = {{")
    w("\t\t\tisa = PBXFrameworksBuildPhase;")
    w("\t\t\tbuildActionMask = 2147483647;")
    w("\t\t\tfiles = (")
    w("\t\t\t);")
    w("\t\t\trunOnlyForDeploymentPostprocessing = 0;")
    w("\t\t};")
    w("/* End PBXFrameworksBuildPhase section */")

    # ---- PBXGroup -----------------------------------------------------------------------
    # The group tree mirrors the directory tree, so "where is this file" has one answer.
    groups: dict[str, list[str]] = {}

    def group_children(directory: pathlib.Path) -> list[str]:
        children: list[str] = []
        for entry in sorted(directory.iterdir(), key=lambda p: (p.is_file(), p.name)):
            if entry.name.endswith(".lproj"):
                continue
            if entry.is_dir() and not entry.name.endswith(".xcassets"):
                if not any(
                    p.suffix in SOURCE_SUFFIXES | RESOURCE_SUFFIXES or p.name.endswith(".lproj")
                    for p in entry.rglob("*")
                ):
                    continue
                children.append(f"{oid('group', path_of(entry))} /* {entry.name} */,")
                register_group(entry)
            elif entry.name in ("Info.plist", project.entitlements_name):
                children.append(f"{oid('fileRef', path_of(entry))} /* {entry.name} */,")
            elif entry.suffix in SOURCE_SUFFIXES or entry.suffix in RESOURCE_SUFFIXES:
                children.append(f"{oid('fileRef', path_of(entry))} /* {entry.name} */,")
        if directory.name == "Resources":
            for table in sorted(tree.variant_groups):
                children.append(f"{oid('variantGroup', table)} /* {table} */,")
        return children

    def register_group(directory: pathlib.Path) -> None:
        groups[path_of(directory)] = group_children(directory)

    register_group(root / app_target)
    register_group(root / test_target)

    w("\n/* Begin PBXGroup section */")
    w(f"\t\t{oid('group', 'root')} = {{")
    w("\t\t\tisa = PBXGroup;")
    w("\t\t\tchildren = (")
    w(f"\t\t\t\t{oid('group', 'Config')} /* Config */,")
    w(f"\t\t\t\t{oid('group', app_target)} /* {app_target} */,")
    w(f"\t\t\t\t{oid('group', test_target)} /* {test_target} */,")
    w(f"\t\t\t\t{oid('group', 'Products')} /* Products */,")
    w("\t\t\t);")
    w("\t\t\tsourceTree = \"<group>\";")
    w("\t\t};")
    w(f"\t\t{oid('group', 'Config')} /* Config */ = {{")
    w("\t\t\tisa = PBXGroup;")
    w("\t\t\tchildren = (")
    for name in ("Shared.xcconfig", "Debug.xcconfig", "Release.xcconfig"):
        w(f"\t\t\t\t{oid('fileRef', 'Config/' + name)} /* {name} */,")
    w("\t\t\t);")
    w("\t\t\tpath = Config;")
    w("\t\t\tsourceTree = \"<group>\";")
    w("\t\t};")
    w(f"\t\t{oid('group', 'Products')} /* Products */ = {{")
    w("\t\t\tisa = PBXGroup;")
    w("\t\t\tchildren = (")
    w(f"\t\t\t\t{oid('product', app_target)} /* {app_target}.app */,")
    w(f"\t\t\t\t{oid('product', test_target)} /* {test_target}.xctest */,")
    w("\t\t\t);")
    w("\t\t\tname = Products;")
    w("\t\t\tsourceTree = \"<group>\";")
    w("\t\t};")
    for path, children in sorted(groups.items()):
        name = pathlib.Path(path).name
        w(f"\t\t{oid('group', path)} /* {name} */ = {{")
        w("\t\t\tisa = PBXGroup;")
        w("\t\t\tchildren = (")
        for child in children:
            w(f"\t\t\t\t{child}")
        w("\t\t\t);")
        w(f'\t\t\tpath = "{name}";')
        w("\t\t\tsourceTree = \"<group>\";")
        w("\t\t};")
    w("/* End PBXGroup section */")

    # ---- PBXVariantGroup ----------------------------------------------------------------
    w("\n/* Begin PBXVariantGroup section */")
    for table, members in sorted(tree.variant_groups.items()):
        w(f"\t\t{oid('variantGroup', table)} /* {table} */ = {{")
        w("\t\t\tisa = PBXVariantGroup;")
        w("\t\t\tchildren = (")
        for member in sorted(members, key=path_of):
            language = member.parent.name.removesuffix(".lproj")
            w(f"\t\t\t\t{oid('fileRef', path_of(member))} /* {language} */,")
        w("\t\t\t);")
        w(f'\t\t\tname = "{table}";')
        w("\t\t\tsourceTree = \"<group>\";")
        w("\t\t};")
    w("/* End PBXVariantGroup section */")

    # A localised file's reference is named for its LANGUAGE, not for the table — that is how Xcode
    # tells `en.lproj/Localizable.strings` from `si.lproj/Localizable.strings`.
    localised_refs: list[str] = []
    for table, members in sorted(tree.variant_groups.items()):
        for member in sorted(members, key=path_of):
            language = member.parent.name.removesuffix(".lproj")
            localised_refs.append(
                f"\t\t{oid('fileRef', path_of(member))} /* {language} */ = {{isa = PBXFileReference; "
                f"lastKnownFileType = text.plist.strings; name = {language}; "
                f'path = "{language}.lproj/{table}"; sourceTree = "<group>"; }};'
            )

    # ---- PBXNativeTarget ----------------------------------------------------------------
    w("\n/* Begin PBXNativeTarget section */")
    w(f"\t\t{oid('target', app_target)} /* {app_target} */ = {{")
    w("\t\t\tisa = PBXNativeTarget;")
    w(f"\t\t\tbuildConfigurationList = {oid('configList', 'target:' + app_target)};")
    w("\t\t\tbuildPhases = (")
    w(f"\t\t\t\t{oid('phase', 'sources:' + app_target)} /* Sources */,")
    w(f"\t\t\t\t{oid('phase', 'frameworks:app')} /* Frameworks */,")
    w(f"\t\t\t\t{oid('phase', 'resources:' + app_target)} /* Resources */,")
    w("\t\t\t);")
    w("\t\t\tbuildRules = (\n\t\t\t);")
    w("\t\t\tdependencies = (\n\t\t\t);")
    w(f"\t\t\tname = {app_target};")
    w("\t\t\tpackageProductDependencies = (")
    for product in project.app_products:
        w(f"\t\t\t\t{oid('productDep', product)} /* {product} */,")
    w("\t\t\t);")
    w(f"\t\t\tproductName = {app_target};")
    w(f"\t\t\tproductReference = {oid('product', app_target)} /* {app_target}.app */;")
    w('\t\t\tproductType = "com.apple.product-type.application";')
    w("\t\t};")
    w(f"\t\t{oid('target', test_target)} /* {test_target} */ = {{")
    w("\t\t\tisa = PBXNativeTarget;")
    w(f"\t\t\tbuildConfigurationList = {oid('configList', 'target:' + test_target)};")
    w("\t\t\tbuildPhases = (")
    w(f"\t\t\t\t{oid('phase', 'sources:' + test_target)} /* Sources */,")
    w(f"\t\t\t\t{oid('phase', 'frameworks:tests')} /* Frameworks */,")
    w("\t\t\t);")
    w("\t\t\tbuildRules = (\n\t\t\t);")
    w("\t\t\tdependencies = (")
    w(f"\t\t\t\t{oid('dependency', test_target)} /* PBXTargetDependency */,")
    w("\t\t\t);")
    w(f"\t\t\tname = {test_target};")
    w("\t\t\tpackageProductDependencies = (\n\t\t\t);")
    w(f"\t\t\tproductName = {test_target};")
    w(f"\t\t\tproductReference = {oid('product', test_target)} /* {test_target}.xctest */;")
    w('\t\t\tproductType = "com.apple.product-type.bundle.unit-test";')
    w("\t\t};")
    w("/* End PBXNativeTarget section */")

    # ---- PBXProject ---------------------------------------------------------------------
    w("\n/* Begin PBXProject section */")
    w(f"\t\t{oid('project', app_target)} /* Project object */ = {{")
    w("\t\t\tisa = PBXProject;")
    w("\t\t\tattributes = {")
    w("\t\t\t\tBuildIndependentTargetsInParallel = 1;")
    w("\t\t\t\tLastSwiftUpdateCheck = 1500;")
    w("\t\t\t\tLastUpgradeCheck = 1500;")
    w("\t\t\t\tTargetAttributes = {")
    w(f"\t\t\t\t\t{oid('target', test_target)} = {{")
    w(f"\t\t\t\t\t\tTestTargetID = {oid('target', app_target)};")
    w("\t\t\t\t\t};")
    w("\t\t\t\t};")
    w("\t\t\t};")
    w(f"\t\t\tbuildConfigurationList = {oid('configList', 'project')};")
    w('\t\t\tcompatibilityVersion = "Xcode 14.0";')
    w("\t\t\tdevelopmentRegion = en;")
    w("\t\t\thasScannedForEncodings = 0;")
    w("\t\t\tknownRegions = (")
    for region in ("en", "si", "ta", "Base"):
        w(f"\t\t\t\t{region},")
    w("\t\t\t);")
    w(f"\t\t\tmainGroup = {oid('group', 'root')};")
    w("\t\t\tpackageReferences = (")
    for package in project.packages:
        key = package["path"] if package["kind"] == "local" else package["url"]
        marker = "XCLocalSwiftPackageReference" if package["kind"] == "local" else "XCRemoteSwiftPackageReference"
        w(f"\t\t\t\t{oid('package', key)} /* {marker} */,")
    w("\t\t\t);")
    w(f"\t\t\tproductRefGroup = {oid('group', 'Products')} /* Products */;")
    w('\t\t\tprojectDirPath = "";')
    w('\t\t\tprojectRoot = "";')
    w("\t\t\ttargets = (")
    w(f"\t\t\t\t{oid('target', app_target)} /* {app_target} */,")
    w(f"\t\t\t\t{oid('target', test_target)} /* {test_target} */,")
    w("\t\t\t);")
    w("\t\t};")
    w("/* End PBXProject section */")

    # ---- PBXResourcesBuildPhase ---------------------------------------------------------
    w("\n/* Begin PBXResourcesBuildPhase section */")
    w(f"\t\t{oid('phase', 'resources:' + app_target)} /* Resources */ = {{")
    w("\t\t\tisa = PBXResourcesBuildPhase;")
    w("\t\t\tbuildActionMask = 2147483647;")
    w("\t\t\tfiles = (")
    for path in tree.resources:
        w(f"\t\t\t\t{oid('buildFile:Resources', path_of(path))} /* {path.name} in Resources */,")
    for table in sorted(tree.variant_groups):
        w(f"\t\t\t\t{oid('buildFile:Resources', table)} /* {table} in Resources */,")
    w("\t\t\t);")
    w("\t\t\trunOnlyForDeploymentPostprocessing = 0;")
    w("\t\t};")
    w("/* End PBXResourcesBuildPhase section */")

    # ---- PBXSourcesBuildPhase -----------------------------------------------------------
    w("\n/* Begin PBXSourcesBuildPhase section */")
    for target, paths in tree.sources.items():
        w(f"\t\t{oid('phase', 'sources:' + target)} /* Sources */ = {{")
        w("\t\t\tisa = PBXSourcesBuildPhase;")
        w("\t\t\tbuildActionMask = 2147483647;")
        w("\t\t\tfiles = (")
        for path in paths:
            w(f"\t\t\t\t{oid('buildFile:' + target, path_of(path))} /* {path.name} in Sources */,")
        w("\t\t\t);")
        w("\t\t\trunOnlyForDeploymentPostprocessing = 0;")
        w("\t\t};")
    w("/* End PBXSourcesBuildPhase section */")

    # ---- PBXTargetDependency ------------------------------------------------------------
    w("\n/* Begin PBXTargetDependency section */")
    w(f"\t\t{oid('dependency', test_target)} /* PBXTargetDependency */ = {{")
    w("\t\t\tisa = PBXTargetDependency;")
    w(f"\t\t\ttarget = {oid('target', app_target)} /* {app_target} */;")
    w(f"\t\t\ttargetProxy = {oid('proxy', test_target)} /* PBXContainerItemProxy */;")
    w("\t\t};")
    w("/* End PBXTargetDependency section */")

    w("\n/* Begin PBXContainerItemProxy section */")
    w(f"\t\t{oid('proxy', test_target)} /* PBXContainerItemProxy */ = {{")
    w("\t\t\tisa = PBXContainerItemProxy;")
    w(f"\t\t\tcontainerPortal = {oid('project', app_target)} /* Project object */;")
    w("\t\t\tproxyType = 1;")
    w(f"\t\t\tremoteGlobalIDString = {oid('target', app_target)};")
    w(f"\t\t\tremoteInfo = {app_target};")
    w("\t\t};")
    w("/* End PBXContainerItemProxy section */")

    # ---- XCBuildConfiguration -----------------------------------------------------------
    # Almost everything is in the xcconfigs. What stays here is what an xcconfig cannot express
    # (the base configuration reference itself) and what differs per target.
    w("\n/* Begin XCBuildConfiguration section */")

    def configuration(scope: str, name: str, settings: dict[str, str], base: str | None) -> None:
        w(f"\t\t{oid('config', scope + ':' + name)} /* {name} */ = {{")
        w("\t\t\tisa = XCBuildConfiguration;")
        if base:
            w(f"\t\t\tbaseConfigurationReference = {oid('fileRef', base)} /* {pathlib.Path(base).name} */;")
        w("\t\t\tbuildSettings = {")
        for key, value in sorted(settings.items()):
            w(f'\t\t\t\t{key} = "{value}";')
        w("\t\t\t};")
        w(f"\t\t\tname = {name};")
        w("\t\t};")

    project_common = {
        "ALWAYS_SEARCH_USER_PATHS": "NO",
        "CLANG_ENABLE_MODULES": "YES",
        "CLANG_ENABLE_OBJC_ARC": "YES",
        "ENABLE_STRICT_OBJC_MSGSEND": "YES",
        "GCC_NO_COMMON_BLOCKS": "YES",
        "SDKROOT": "iphoneos",
        "SWIFT_EMIT_LOC_STRINGS": "YES",
    }
    configuration("project", "Debug", project_common | {"ENABLE_TESTABILITY": "YES"}, "Config/Debug.xcconfig")
    configuration("project", "Release", project_common, "Config/Release.xcconfig")

    app_common = {
        "GENERATE_INFOPLIST_FILE": "NO",
        "LD_RUNPATH_SEARCH_PATHS": "$(inherited) @executable_path/Frameworks",
        "PRODUCT_BUNDLE_IDENTIFIER": project.bundle_id,
        "SWIFT_EMIT_LOC_STRINGS": "YES",
    }
    configuration("target:" + app_target, "Debug", app_common, None)
    configuration("target:" + app_target, "Release", app_common, None)

    test_common = {
        "BUNDLE_LOADER": "$(TEST_HOST)",
        "CODE_SIGN_ENTITLEMENTS": "",
        "GENERATE_INFOPLIST_FILE": "YES",
        "IPHONEOS_DEPLOYMENT_TARGET": project.deployment_target,
        "PRODUCT_BUNDLE_IDENTIFIER": project.bundle_id + ".tests",
        "PRODUCT_NAME": "$(TARGET_NAME)",
        # The tests read the app's own bundle — the asset catalogue, the three `.lproj` — so they
        # run **hosted**. A standalone test bundle would find neither.
        "TEST_HOST": f"$(BUILT_PRODUCTS_DIR)/{app_target}.app/{app_target}",
        "INFOPLIST_FILE": "",
    }
    configuration("target:" + test_target, "Debug", test_common, None)
    configuration("target:" + test_target, "Release", test_common, None)
    w("/* End XCBuildConfiguration section */")

    # ---- XCConfigurationList ------------------------------------------------------------
    w("\n/* Begin XCConfigurationList section */")
    for scope, label in (
        ("project", "PBXProject"),
        ("target:" + app_target, f"PBXNativeTarget {app_target}"),
        ("target:" + test_target, f"PBXNativeTarget {test_target}"),
    ):
        w(f"\t\t{oid('configList', scope)} /* Build configuration list for {label} */ = {{")
        w("\t\t\tisa = XCConfigurationList;")
        w("\t\t\tbuildConfigurations = (")
        w(f"\t\t\t\t{oid('config', scope + ':Debug')} /* Debug */,")
        w(f"\t\t\t\t{oid('config', scope + ':Release')} /* Release */,")
        w("\t\t\t);")
        w("\t\t\tdefaultConfigurationIsVisible = 0;")
        w("\t\t\tdefaultConfigurationName = Release;")
        w("\t\t};")
    w("/* End XCConfigurationList section */")

    # ---- Swift packages -----------------------------------------------------------------
    locals_ = [p for p in project.packages if p["kind"] == "local"]
    remotes = [p for p in project.packages if p["kind"] == "remote"]

    if locals_:
        w("\n/* Begin XCLocalSwiftPackageReference section */")
        for package in locals_:
            w(f"\t\t{oid('package', package['path'])} /* XCLocalSwiftPackageReference */ = {{")
            w("\t\t\tisa = XCLocalSwiftPackageReference;")
            w(f'\t\t\trelativePath = "{package["path"]}";')
            w("\t\t};")
        w("/* End XCLocalSwiftPackageReference section */")

    if remotes:
        w("\n/* Begin XCRemoteSwiftPackageReference section */")
        for package in remotes:
            kind, version = package["requirement"]
            w(f"\t\t{oid('package', package['url'])} /* XCRemoteSwiftPackageReference */ = {{")
            w("\t\t\tisa = XCRemoteSwiftPackageReference;")
            w(f'\t\t\trepositoryURL = "{package["url"]}";')
            w("\t\t\trequirement = {")
            w(f"\t\t\t\tkind = {kind};")
            w(f'\t\t\t\tminimumVersion = "{version}";')
            w("\t\t\t};")
            w("\t\t};")
        w("/* End XCRemoteSwiftPackageReference section */")

    w("\n/* Begin XCSwiftPackageProductDependency section */")
    for package in project.packages:
        key = package["path"] if package["kind"] == "local" else package["url"]
        for product in package["products"]:
            w(f"\t\t{oid('productDep', product)} /* {product} */ = {{")
            w("\t\t\tisa = XCSwiftPackageProductDependency;")
            if package["kind"] == "remote":
                w(f"\t\t\tpackage = {oid('package', key)} /* XCRemoteSwiftPackageReference */;")
            w(f"\t\t\tproductName = {product};")
            w("\t\t};")
    w("/* End XCSwiftPackageProductDependency section */")

    # The localised file references, appended to the PBXFileReference section Xcode reads as one
    # dictionary — order inside `objects` is cosmetic.
    w("\n/* Begin PBXFileReference section (localised) */")
    for line in localised_refs:
        w(line)
    w("/* End PBXFileReference section (localised) */")

    w("\t};")
    w(f"\trootObject = {oid('project', app_target)} /* Project object */;")
    w("}")
    return "\n".join(out) + "\n"


WORKSPACE = """<?xml version="1.0" encoding="UTF-8"?>
<Workspace version = "1.0">
   <FileRef location = "self:">
   </FileRef>
</Workspace>
"""


def scheme(project: AppProject, app_target_id: str, test_target_id: str) -> str:
    APP_TARGET, TEST_TARGET = project.app_target, project.test_target
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<Scheme LastUpgradeVersion = "1500" version = "1.7">
   <BuildAction parallelizeBuildables = "YES" buildImplicitDependencies = "YES">
      <BuildActionEntries>
         <BuildActionEntry buildForTesting = "YES" buildForRunning = "YES" buildForProfiling = "YES" buildForArchiving = "YES" buildForAnalyzing = "YES">
            <BuildableReference
               BuildableIdentifier = "primary"
               BlueprintIdentifier = "{app_target_id}"
               BuildableName = "{APP_TARGET}.app"
               BlueprintName = "{APP_TARGET}"
               ReferencedContainer = "container:{APP_TARGET}.xcodeproj">
            </BuildableReference>
         </BuildActionEntry>
      </BuildActionEntries>
   </BuildAction>
   <TestAction buildConfiguration = "Debug" selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB" shouldUseLaunchSchemeArgsEnv = "YES">
      <Testables>
         <TestableReference skipped = "NO">
            <BuildableReference
               BuildableIdentifier = "primary"
               BlueprintIdentifier = "{test_target_id}"
               BuildableName = "{TEST_TARGET}.xctest"
               BlueprintName = "{TEST_TARGET}"
               ReferencedContainer = "container:{APP_TARGET}.xcodeproj">
            </BuildableReference>
         </TestableReference>
      </Testables>
   </TestAction>
   <LaunchAction buildConfiguration = "Debug" selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB" launchStyle = "0" useCustomWorkingDirectory = "NO" ignoresPersistentStateOnLaunch = "NO" debugDocumentVersioning = "YES" debugServiceExtension = "internal" allowLocationSimulation = "YES">
      <BuildableProductRunnable runnableDebuggingMode = "0">
         <BuildableReference
            BuildableIdentifier = "primary"
            BlueprintIdentifier = "{app_target_id}"
            BuildableName = "{APP_TARGET}.app"
            BlueprintName = "{APP_TARGET}"
            ReferencedContainer = "container:{APP_TARGET}.xcodeproj">
         </BuildableReference>
      </BuildableProductRunnable>
   </LaunchAction>
   <ProfileAction buildConfiguration = "Release" shouldUseLaunchSchemeArgsEnv = "YES" savedToolIdentifier = "" useCustomWorkingDirectory = "NO" debugDocumentVersioning = "YES">
      <BuildableProductRunnable runnableDebuggingMode = "0">
         <BuildableReference
            BuildableIdentifier = "primary"
            BlueprintIdentifier = "{app_target_id}"
            BuildableName = "{APP_TARGET}.app"
            BlueprintName = "{APP_TARGET}"
            ReferencedContainer = "container:{APP_TARGET}.xcodeproj">
         </BuildableReference>
      </BuildableProductRunnable>
   </ProfileAction>
   <AnalyzeAction buildConfiguration = "Debug">
   </AnalyzeAction>
   <ArchiveAction buildConfiguration = "Release" revealArchiveInOrganizer = "YES">
   </ArchiveAction>
</Scheme>
"""


def generate(project: AppProject) -> int:
    """Writes `project`'s `.pbxproj`, workspace stub and shared scheme. Returns a process exit code."""
    tree = Tree(project)
    tree.scan()
    if not tree.sources[project.app_target]:
        print(f"no Swift sources found under {project.app_target}/ — wrong directory?", file=sys.stderr)
        return 1

    destination = project.project_dir
    destination.mkdir(parents=True, exist_ok=True)
    (destination / "project.pbxproj").write_text(build_pbxproj(tree))

    workspace = destination / "project.xcworkspace"
    workspace.mkdir(parents=True, exist_ok=True)
    (workspace / "contents.xcworkspacedata").write_text(WORKSPACE)

    schemes = destination / "xcshareddata" / "xcschemes"
    schemes.mkdir(parents=True, exist_ok=True)
    (schemes / f"{project.app_target}.xcscheme").write_text(
        scheme(project, oid("target", project.app_target), oid("target", project.test_target))
    )

    print(
        f"wrote {destination.name}: "
        f"{len(tree.sources[project.app_target])} app sources, "
        f"{len(tree.sources[project.test_target])} test sources, "
        f"{len(tree.resources)} resources, "
        f"{len(tree.variant_groups)} localised tables"
    )
    return 0
