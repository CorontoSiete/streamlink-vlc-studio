#!/usr/bin/env python3
"""Build and verify the narrowly patched VLC 3.0.23 Windows core DLL."""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tarfile
import tempfile
import sys


UPSTREAM_SHA256 = "847f1c4d7b848b3f6a3c04b7d0c05ab9fbc5f174c9f8adcd6203057e8908285e"
REFERENCE_LIBVLC_SHA256 = "8ae9f16a72441f43fb4ae8f72c843736726e067ea4a8def2646748631cc4e872"
REFERENCE_CORE_SHA256 = "d3475b834dd3eb77910f37f71b0341d358bcbdda5b9f04cc4a3a8e2be1bc8e35"
SOURCE_ROOT_NAME = "vlc-3.0.23"
PATCH_OLD = """            if (!LOOKUP(WaitOnAddress)
             || !LOOKUP(WakeByAddressAll) || !LOOKUP(WakeByAddressSingle))
            {"""
PATCH_NEW = """            BOOL haveNativeAddressWaits =
                LOOKUP(WaitOnAddress)
             && LOOKUP(WakeByAddressAll) && LOOKUP(WakeByAddressSingle);
            if (!haveNativeAddressWaits)
            {
                /* Windows forwards these APIs through KernelBase on supported builds. */
                h = GetModuleHandle(TEXT("kernelbase.dll"));
                if (h != NULL)
                    haveNativeAddressWaits = LOOKUP(WaitOnAddress)
                        && LOOKUP(WakeByAddressAll) && LOOKUP(WakeByAddressSingle);
            }

            if (!haveNativeAddressWaits)
            {"""
SYSTEM_DLLS = {
    "advapi32.dll",
    "bcrypt.dll",
    "combase.dll",
    "gdi32.dll",
    "iphlpapi.dll",
    "kernel32.dll",
    "kernelbase.dll",
    "msvcrt.dll",
    "ntdll.dll",
    "ole32.dll",
    "oleaut32.dll",
    "rpcrt4.dll",
    "secur32.dll",
    "shell32.dll",
    "user32.dll",
    "version.dll",
    "winmm.dll",
    "ws2_32.dll",
}


def resolve(root: Path, value: str) -> Path:
    path = Path(value)
    return path.resolve() if path.is_absolute() else (root / path).resolve()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def run(command: list[str], *, cwd: Path, environment: dict[str, str], capture: bool = True) -> str:
    result = subprocess.run(
        command,
        cwd=cwd,
        env=environment,
        text=True,
        stdout=subprocess.PIPE if capture else None,
        stderr=subprocess.STDOUT if capture else None,
        check=False,
    )
    output = result.stdout or ""
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}): {command[0]}\n{output[-12000:]}")
    return output


def extract_verified_source(archive_path: Path, destination: Path) -> Path:
    actual_hash = sha256(archive_path)
    if actual_hash != UPSTREAM_SHA256:
        raise ValueError(
            f"VLC source archive SHA-256 mismatch: expected {UPSTREAM_SHA256}, got {actual_hash}"
        )

    destination.mkdir(parents=True)
    selected_roots = {"src", "include", "compat", "lib"}
    selected_files = {"AUTHORS", "COPYING", "COPYING.LIB", "THANKS"}
    with tarfile.open(archive_path, "r:gz") as archive:
        for member in archive.getmembers():
            parts = PurePosixPath(member.name).parts
            if not parts or parts[0] != SOURCE_ROOT_NAME or len(parts) < 2:
                continue
            relative = PurePosixPath(*parts[1:])
            if relative.is_absolute() or ".." in relative.parts:
                raise ValueError(f"Unsafe source archive path: {member.name}")
            if relative.parts[0] not in selected_roots and str(relative) not in selected_files:
                continue
            if not member.isfile():
                if member.isdir():
                    continue
                raise ValueError(f"Unsupported file type in VLC source tree: {member.name}")
            output = destination.joinpath(*relative.parts)
            output.parent.mkdir(parents=True, exist_ok=True)
            source_file = archive.extractfile(member)
            if source_file is None:
                raise ValueError(f"Cannot read source archive entry: {member.name}")
            with source_file, output.open("wb") as target:
                shutil.copyfileobj(source_file, target)

    required = [
        destination / "src" / "Makefile.am",
        destination / "src" / "libvlccore.sym",
        destination / "src" / "misc" / "fourcc_gen.c",
        destination / "src" / "win32" / "thread.c",
        destination / "include" / "vlc_common.h",
        destination / "COPYING.LIB",
    ]
    missing = [str(path.relative_to(destination)) for path in required if not path.is_file()]
    if missing:
        raise ValueError(f"Pinned VLC source archive is missing required files: {missing}")
    return destination


def apply_wait_patch(thread_path: Path, output_path: Path) -> None:
    source = thread_path.read_text(encoding="utf-8")
    occurrences = source.count(PATCH_OLD)
    if occurrences != 1:
        raise ValueError(f"Expected exactly one VLC 3.0.23 wait lookup to patch, found {occurrences}")
    output_path.write_text(source.replace(PATCH_OLD, PATCH_NEW), encoding="utf-8", newline="\n")


def generated_about_header(source: Path, output: Path) -> None:
    chunks = []
    for name, filename in [("license", "COPYING"), ("thanks", "THANKS"), ("authors", "AUTHORS")]:
        lines = (source / filename).read_text(encoding="utf-8").splitlines()
        chunks.append(
            "static const char psz_" + name + "[] =\n"
            + "\n".join(json.dumps(line + "\n", ensure_ascii=True) for line in lines)
            + ";\n"
        )
    output.write_text("".join(chunks), encoding="ascii", newline="\n")


def source_files(source: Path) -> list[str]:
    makefile = (source / "src" / "Makefile.am").read_text(encoding="utf-8")
    try:
        core_list = makefile.split("libvlccore_la_SOURCES = \\\n", 1)[1].split("libvlccore_la_LIBADD", 1)[0]
    except IndexError as exc:
        raise ValueError("VLC core source list was not found in src/Makefile.am") from exc
    files = set(re.findall(r"\b[\w/.-]+\.c\b", core_list))
    files.update("win32/" + name + ".c" for name in ["dirs", "error", "filesystem", "netconf", "plugin", "rand", "specific", "thread", "winsock", "timer"])
    files.update("stream_output/" + name + ".c" for name in ["sap", "sdp", "stream_output"])
    files.update("input/" + name + ".c" for name in ["vlm", "vlm_event", "vlmshell"])
    files.update(["misc/update.c", "misc/update_crypto.c"])
    compat = [
        "asprintf", "strndup", "strnstr", "strcasestr", "strverscmp", "memrchr", "strlcpy", "getdelim",
        "gmtime_r", "localtime_r", "timegm", "poll", "timespec_get", "aligned_alloc", "tdestroy",
        "posix_memalign", "flockfile", "qsort_r", "lfind", "fsync", "ffsll", "setenv", "strsep", "nrand48",
        "recvmsg", "sendmsg",
    ]
    files.update("../compat/" + name + ".c" for name in compat if (source / "compat" / (name + ".c")).is_file())
    files.discard("revision.c")
    missing = [name for name in sorted(files) if not (source / "src" / name).is_file()]
    if missing:
        raise ValueError(f"Pinned VLC source archive is missing core translation units: {missing[:12]}")
    return sorted(files)


def export_names(objdump: Path, binary: Path, environment: dict[str, str]) -> set[str]:
    output = run([str(objdump), "-p", str(binary)], cwd=binary.parent, environment=environment)
    marker = "[Ordinal/Name Pointer] Table -- Ordinal Base"
    if marker not in output:
        raise ValueError(f"No PE export-name table found in {binary}")
    section = output.split(marker, 1)[1]
    names = set(re.findall(r"^\s*\[\s*\d+\]\s+\+base\[\s*\d+\]\s+[0-9a-fA-F]+\s+(\S+)\s*$", section, re.MULTILINE))
    return names


def pe_imports(objdump: Path, binary: Path, environment: dict[str, str]) -> tuple[set[str], str]:
    output = run([str(objdump), "-p", str(binary)], cwd=binary.parent, environment=environment)
    imports = {name.casefold() for name in re.findall(r"DLL Name:\s*([^\s]+)", output, re.IGNORECASE)}
    return imports, output


def verify_build_inputs(
    gcc: Path,
    toolchain_archive: Path,
    contrib_archive_dir: Path,
    contrib_prefix: Path,
    provenance: dict,
    workspace: Path,
) -> None:
    inputs = provenance["buildInputs"]
    toolchain = inputs["toolchain"]
    for path, expected, description in [
        (toolchain_archive, toolchain["archiveSha256"], "WinLibs toolchain archive"),
        (gcc, toolchain["compilerBinarySha256"], "GCC executable"),
    ]:
        actual = sha256(path)
        if actual.casefold() != expected.casefold():
            raise ValueError(f"{description} SHA-256 mismatch: expected {expected}, got {actual}")

    for package in inputs["contribPackages"]:
        package_archive = contrib_archive_dir / package["archive"]
        if not package_archive.is_file():
            raise FileNotFoundError(f"Required VLC contrib package is missing: {package_archive}")
        actual_archive_hash = sha256(package_archive)
        if actual_archive_hash.casefold() != package["archiveSha256"].casefold():
            raise ValueError(
                f"VLC contrib package SHA-256 mismatch for {package['archive']}: "
                f"expected {package['archiveSha256']}, got {actual_archive_hash}"
            )

        library = contrib_prefix.joinpath(*PurePosixPath(package["library"]).parts)
        if not library.is_file():
            raise FileNotFoundError(f"Required VLC contrib library is missing: {library}")
        actual_library_hash = sha256(library)
        if actual_library_hash.casefold() != package["librarySha256"].casefold():
            raise ValueError(
                f"VLC contrib library SHA-256 mismatch for {library.name}: "
                f"expected {package['librarySha256']}, got {actual_library_hash}"
            )

    environment = dict(os.environ)
    environment["PATH"] = str(gcc.parent) + os.pathsep + environment.get("PATH", "")
    for library in toolchain["linkedRuntimeLibraries"]:
        reported = run(
            [str(gcc), f"-print-file-name={library['name']}"],
            cwd=workspace,
            environment=environment,
        ).strip()
        path = Path(reported).resolve()
        if not path.is_file():
            raise FileNotFoundError(f"GCC could not resolve its pinned runtime library: {library['name']}")
        actual = sha256(path)
        if actual.casefold() != library["sha256"].casefold():
            raise ValueError(
                f"GCC runtime library SHA-256 mismatch for {library['name']}: "
                f"expected {library['sha256']}, got {actual}"
            )


def build_once(
    source: Path,
    gcc: Path,
    objdump: Path,
    contrib: Path,
    reference_core: Path,
    workspace: Path,
) -> Path:
    build = workspace / "build"
    build.mkdir(parents=True)
    # Use one stable relative source name for both clean builds. GCC embeds
    # __FILE__ in this object, so unique temporary-directory names would make
    # otherwise identical DLLs differ.
    patched_source = source / ".studio_build_thread.c"
    apply_wait_patch(source / "src" / "win32" / "thread.c", patched_source)
    generated_about_header(source, build / "vlc_about.h")
    (build / "revision.c").write_text(
        'const char psz_vlc_changeset[] = "3.0.23-windows-address-waits";\n', encoding="ascii"
    )
    (build / "config.h").write_text(
        '#include "' + (Path(__file__).resolve().parent.parent / "native" / "vlc-core" / "core-config.h").as_posix() + '"\n',
        encoding="ascii",
    )

    compiler_root = gcc.parent
    environment = dict(os.environ)
    environment["PATH"] = str(compiler_root) + os.pathsep + environment.get("PATH", "")
    environment["SOURCE_DATE_EPOCH"] = "0"
    compiler_version = run([str(gcc), "--version"], cwd=workspace, environment=environment)
    compiler_target = run([str(gcc), "-dumpmachine"], cwd=workspace, environment=environment).strip()
    if "16.1.0" not in compiler_version or "msvcrt" not in compiler_version.casefold() or compiler_target != "x86_64-w64-mingw32":
        raise ValueError("The VLC core build requires WinLibs GCC 16.1.0 x86_64 MSVCRT.")

    required_libraries = ["libintl.a", "libiconv.a", "libidn.a", "libgcrypt.a", "libgpg-error.a"]
    for library in required_libraries:
        if not (contrib / "lib" / library).is_file():
            raise ValueError(f"Required VLC contrib library is missing: {contrib / 'lib' / library}")
    include_paths = [build, source / "include", source / "src", source / "compat" / "stdckdint", contrib / "include"]
    flags = [
        "-O2", "-DNDEBUG", "-DHAVE_CONFIG_H", "-fno-strict-aliasing", "-std=gnu11",
        "-fno-ident", "-fvisibility=hidden",
        *("-I" + path.as_posix() for path in include_paths),
        "-include", (Path(__file__).resolve().parent.parent / "native" / "vlc-core" / "core-config.h").as_posix(),
    ]

    fourcc_generator = build / "fourcc_gen.exe"
    run(
        [str(gcc), "-I" + (source / "src").as_posix(), (source / "src" / "misc" / "fourcc_gen.c").as_posix(), "-o", str(fourcc_generator)],
        cwd=workspace,
        environment=environment,
    )
    fourcc_tables = subprocess.run(
        [str(fourcc_generator)], cwd=workspace, env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False
    )
    if fourcc_tables.returncode:
        raise RuntimeError(f"VLC fourcc table generation failed: {fourcc_tables.stderr.decode(errors='replace')}")
    (build / "fourcc_tables.h").write_bytes(fourcc_tables.stdout)

    objects = build / "objects"
    objects.mkdir()
    files = source_files(source)
    jobs = [(name, patched_source if name == "win32/thread.c" else source / "src" / name) for name in files]

    def compile_one(item: tuple[str, Path]) -> tuple[Path | None, str]:
        name, source_file = item
        output = objects / (name.replace("/", "_").replace(".", "_") + ".o")
        if name == "win32/thread.c":
            argument = ".studio_build_thread.c"
        else:
            argument = "src/" + name
        result = subprocess.run(
            [str(gcc), *flags, "-c", argument, "-o", os.path.relpath(output, source).replace("\\", "/")],
            cwd=source,
            env=environment,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            check=False,
        )
        return (output if result.returncode == 0 else None, f"{name}\n{result.stdout}" if result.returncode else "")

    with ThreadPoolExecutor(max_workers=6) as pool:
        compiled = list(pool.map(compile_one, jobs))
    failures = [log for output, log in compiled if output is None]
    if failures:
        raise RuntimeError("VLC core compilation failed:\n" + "\n".join(failures)[:18000])
    revision_object = objects / "generated_revision.o"
    run(
        [str(gcc), *flags, "-c", (build / "revision.c").as_posix(), "-o", str(revision_object)],
        cwd=workspace,
        environment=environment,
    )
    export_definition = build / "exports.def"
    export_definition.write_text(
        "LIBRARY libvlccore.dll\nEXPORTS\n" + (source / "src" / "libvlccore.sym").read_text(encoding="utf-8"),
        encoding="utf-8",
        newline="\n",
    )
    response = build / "objects.rsp"
    object_paths = [output for output, _ in compiled if output is not None] + [revision_object]
    response.write_text("\n".join('"' + os.path.relpath(path, build).replace("\\", "/") + '"' for path in object_paths), encoding="utf-8", newline="\n")
    result_dll = build / "libvlccore.dll"
    libraries = contrib / "lib"
    command = [
        str(gcc), "-shared", "-static", "-static-libgcc", "-s", "@objects.rsp", str(export_definition),
        "-L" + libraries.as_posix(), "-lintl", "-liconv", "-lidn", "-lgcrypt", "-lgpg-error", "-lintl",
        "-liconv", "-lws2_32", "-lshell32", "-ladvapi32", "-luser32", "-lole32", "-luuid", "-liphlpapi",
        "-lsynchronization", "-lbcrypt", "-lwinpthread", "-Wl,--no-insert-timestamp,--image-base,0x180000000",
        "-o", str(result_dll),
    ]
    run(command, cwd=build, environment=environment)

    expected_exports = {
        symbol.strip()
        for symbol in (source / "src" / "libvlccore.sym").read_text(encoding="utf-8").splitlines()
        if symbol.strip() and not symbol.lstrip().startswith("#")
    }
    actual_exports = export_names(objdump, result_dll, environment)
    reference_exports = export_names(objdump, reference_core, environment)
    if actual_exports != expected_exports or actual_exports != reference_exports:
        missing = sorted(reference_exports - actual_exports)
        extra = sorted(actual_exports - reference_exports)
        raise ValueError(f"VLC core export mismatch: missing={missing[:12]} extra={extra[:12]}")

    imports, import_text = pe_imports(objdump, result_dll, environment)
    unapproved = sorted(name for name in imports if name not in SYSTEM_DLLS and not name.startswith("api-ms-win-"))
    if unapproved:
        raise ValueError(f"VLC core has unexpected non-system imports: {unapproved}")
    if "waitonaddress" in import_text.casefold() or "wakebyaddressall" in import_text.casefold() or "wakebyaddresssingle" in import_text.casefold():
        raise ValueError("VLC address-wait functions must be resolved at runtime, not imported directly.")
    return result_dll


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-archive", required=True, help="SHA-256-pinned upstream VLC 3.0.23 tar.gz")
    parser.add_argument("--gcc", required=True, help="WinLibs GCC 16.1.0 x64 MSVCRT compiler")
    parser.add_argument("--toolchain-archive", required=True, help="SHA-256-pinned WinLibs compiler archive")
    parser.add_argument("--contrib-prefix", required=True, help="VLC 3.0.23 x64 MinGW contrib prefix")
    parser.add_argument("--contrib-archive-dir", required=True, help="Directory containing the pinned MSYS2 package archives")
    parser.add_argument("--reference-libvlc", required=True, help="Official VLC 3.0.23 libvlc.dll")
    parser.add_argument("--reference-core", required=True, help="Official VLC 3.0.23 libvlccore.dll")
    parser.add_argument("--output", required=True, help="Destination for the rebuilt libvlccore.dll")
    args = parser.parse_args()

    root = Path(__file__).resolve().parent.parent
    archive = resolve(root, args.source_archive)
    gcc = resolve(root, args.gcc)
    toolchain_archive = resolve(root, args.toolchain_archive)
    contrib = resolve(root, args.contrib_prefix)
    contrib_archive_dir = resolve(root, args.contrib_archive_dir)
    reference_libvlc = resolve(root, args.reference_libvlc)
    reference_core = resolve(root, args.reference_core)
    output = resolve(root, args.output)
    for path in [archive, gcc, toolchain_archive, contrib, contrib_archive_dir, reference_libvlc, reference_core]:
        if not path.exists():
            raise FileNotFoundError(path)
    if sha256(reference_libvlc) != REFERENCE_LIBVLC_SHA256:
        raise ValueError("The VLC libvlc.dll does not match the pinned official 3.0.23 build.")
    if sha256(reference_core) != REFERENCE_CORE_SHA256:
        raise ValueError("The VLC libvlccore.dll does not match the pinned official 3.0.23 build.")

    manifest_path = root / "native" / "vlc-core" / "provenance.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    with tempfile.TemporaryDirectory(prefix="vlc-core-input-check-") as temporary:
        verify_build_inputs(
            gcc,
            toolchain_archive,
            contrib_archive_dir,
            contrib,
            manifest,
            Path(temporary),
        )

    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="vlc-core-3.0.23-") as temporary:
        workspace = Path(temporary)
        source = extract_verified_source(archive, workspace / SOURCE_ROOT_NAME)
        first = build_once(source, gcc, gcc.parent / "objdump.exe", contrib, reference_core, workspace / "first")
        first_hash = sha256(first)
        second = build_once(source, gcc, gcc.parent / "objdump.exe", contrib, reference_core, workspace / "second")
        second_hash = sha256(second)
        if first_hash != second_hash:
            diagnostics = root / ".tmp" / "gpu-resource-2026-09-27" / "core-build-repro"
            diagnostics.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(first, diagnostics / "first.dll")
            shutil.copyfile(second, diagnostics / "second.dll")
            raise ValueError(
                f"Independent clean builds differ: {first_hash} != {second_hash}; "
                f"preserved both DLLs in {diagnostics}"
            )
        temporary_output = output.with_name(output.name + ".tmp")
        shutil.copyfile(second, temporary_output)
        os.replace(temporary_output, output)

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["referenceVlcSha256"] = REFERENCE_LIBVLC_SHA256
    manifest["referenceVlcCoreSha256"] = REFERENCE_CORE_SHA256
    manifest["binarySha256"] = sha256(output)
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"Verified two independent VLC core builds: {first_hash}")
    print(f"Wrote {output} ({output.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"build-vlc-core.py: {error}", file=sys.stderr)
        raise SystemExit(1)
