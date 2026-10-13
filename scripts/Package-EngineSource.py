"""Create a new verified corresponding-engine-source ZIP, without executing PEs.

Inputs are explicit local files. A successful checksum-bound manual engine
build is required; historical CI evidence is optional and remains historical.
Python 3.11+ is required. No network or credential-store access is performed.
"""
from __future__ import annotations

import argparse
import io
import json
import os
from pathlib import Path
import stat
import sys
import zipfile

sys.dont_write_bytecode = True
import ReleaseChecks as checks


def source_payloads(repo: Path, version: str, commit: str, evidence: Path, evidence_commit: str,
                    cache_dir: Path | None, companion: Path | None) -> tuple[dict[str, bytes], dict]:
    payloads, details = checks.collect_payloads(repo, evidence, repo / "licenses/engine-source-manifest.json",
                                               version, evidence_commit, commit, cache_dir, companion)
    changes = "\n".join("- `" + path + "`" for path in details["modified_source"]["changed_files"])
    native_names = ", ".join(item["name"] + " " + item["version"] for item in details["native_components"]) or "none"
    readme = f"""# DiscordAccess v{version} corresponding engine source companion

Source commit: {commit}.
Engine: {details['engine_version']} for Windows amd64.
Engine SHA-256: {details['engine_sha256']}.

This package preserves complete source archives for all {details['module_count']}
linked Go modules, {details['go_version']}, upstream main source, and the exact
successfully built modified main source. Module archives match the binary's
recorded Go h1 values. Original source/test fixtures and native assets inside
their archives are preserved byte for byte without execution.

The main-source overlay was modified for DiscordAccess.
Reviewed overlay modification date: {details['main_source_overlay']['modification_date']}.
Its declared changed locks and replacement/added files are:

{changes}

The overlay manifest records original/modified hashes; all mirrored payloads,
tests and DISCORDACCESS-NOTICE are included. Every other upstream main-source
file is unchanged. Source notices remain under their original licenses.

Native component inventory: {native_names}. Original native ZIPs are under
sources/native/. Their DLL identities are checked against the vendored module
assets and the distributed engine. Native binary terms and retained wrapper
MIT copyright/permission notices are separate from the Go module licenses;
the complete verified texts are included under licenses/ and in the aggregate.
The helper's MIT license does not replace sing-box's GPL or native binary terms.

Primary evidence/current-build/ describes a successful manual engine rebuild
and source-unit tests using the exact current recipe. It records the actual
build-source commit separately from this release-source commit. Any optional
evidence/historical-build-*/ reports retain their original historical role;
they are not used in place of successful current recipe/source-unit evidence.
Original build checksum lists may name compiled artifacts that are deliberately
absent from this source package. No loose app, engine, toolchain executable,
compiled test program, machine-private log or profile is included.

To rebuild, extract this entire companion into a writable local directory and
run the included byte-pinned recipe with 64-bit PowerShell on Windows x64:

```powershell
.\\scripts\\Build-SecurityEngine.ps1
```

The recipe downloads hash-pinned upstream/Go archives and pinned public modules.
This companion is not an offline Go module cache. The toolchain/evidence remain
under the recipe's local work directory; the resulting engine is hash/size
verified and is not executed. No system toolchain or Windows setting is changed.

For all source regression groups, explicitly add -RunSourceTests in an ordinary
Windows environment allowed to execute their synthetic/loopback test fixtures.
That option executes generated test programs, never the resulting engine.

Full GPL text, {details['verified_notice_count']} source notices,
{details['native_notice_count']} native notices, and the modification notice are
preserved. Distribute this exact companion with the engine binary it identifies.

SOURCE_PACKAGE_MANIFEST.json lists every payload's SHA-256 and byte count,
excluding itself and SHA256SUMS.txt. SHA256SUMS.txt additionally hashes that
manifest and excludes only itself. The complete ZIP is CRC/inventory/byte checked;
outer ordering, timestamps and permissions are deterministic. Original nested
archives preserve their precise bytes and provenance.
"""
    payloads["README-SOURCE.md"] = readme.encode("utf-8")
    forbidden = {".exe", ".dll", ".sys", ".pdb", ".log", ".etl", ".dmp", ".prof", ".pprof", ".test"}
    if any(Path(name).suffix.lower() in forbidden for name in payloads):
        raise ValueError("Source payload contains a loose program/log/profile")
    if len({name.casefold() for name in payloads}) != len(payloads):
        raise ValueError("Source payload has case-colliding paths")
    package_manifest = {"schema": 3, "distribution_status": "release",
                        "purpose": "Complete engine build sources/native assets, notices, exact overlay and current manual build proof",
                        **details, "file_hash_scope": "All payloads except this manifest/SHA256SUMS; SHA256SUMS also hashes the manifest",
                        "files": [{"path": name, "bytes": len(data), "sha256": checks.sha(data)} for name, data in sorted(payloads.items())]}
    payloads["SOURCE_PACKAGE_MANIFEST.json"] = checks.json_bytes(package_manifest)
    payloads["SHA256SUMS.txt"] = "".join(checks.sha(data) + "  " + name + "\n" for name, data in sorted(payloads.items())).encode("ascii")
    return payloads, details


def verify_zip(data: bytes, payloads: dict[str, bytes]) -> None:
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = checks.checked_zip_entries(archive, "")
        if len(entries) != len(payloads) or {entry.filename for entry in entries} != set(payloads) or archive.testzip() is not None:
            raise ValueError("Corresponding-source ZIP inventory/CRC differs")
        for name, content in payloads.items():
            if archive.read(name) != content:
                raise ValueError("Corresponding-source ZIP changed payload bytes")


def archive_bytes(payloads: dict[str, bytes]) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_STORED) as archive:
        for name, data in sorted(payloads.items()):
            info = zipfile.ZipInfo(checks.clean_name(name), checks.FIXED_TIME)
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            info.compress_type = zipfile.ZIP_STORED if name.endswith((".zip", ".tar.gz")) else zipfile.ZIP_DEFLATED
            archive.writestr(info, data, compresslevel=9)
    result = buffer.getvalue()
    verify_zip(result, payloads)
    return result


def write_new(path: Path, data: bytes) -> None:
    path = checks.bounded_path(path)
    if path.exists():
        raise ValueError("Source output already exists; it was not replaced")
    path.parent.mkdir(parents=True, exist_ok=True)
    checks.bounded_path(path)
    created = False
    try:
        with path.open("xb") as stream:
            created = True
            stream.write(data); stream.flush(); os.fsync(stream.fileno())
        if checks.read(path) != data:
            raise ValueError("Written source ZIP differs from verified bytes")
    except Exception:
        if created:
            checks.bounded_path(path).unlink()
        raise


def package(repo: Path, version: str, commit: str, evidence: Path, evidence_commit: str,
            output: Path, cache_dir: Path | None = None, companion: Path | None = None,
            validate_only: bool = False) -> dict:
    repo = checks.bounded_path(repo); output = checks.bounded_path(output)
    if (output.exists() and not validate_only) or output.suffix.lower() != ".zip":
        raise ValueError("Supply a new corresponding-source ZIP output path")
    checks.exact_head(repo, commit)
    payloads, details = source_payloads(repo, version, commit, evidence, evidence_commit, cache_dir, companion)
    data = archive_bytes(payloads)
    checks.exact_head(repo, commit)
    summary = {"output": str(output), "source_commit": commit, "release_version": version,
               "bytes": len(data), "sha256": checks.sha(data), "entries": len(payloads),
               "engine_sha256": details["engine_sha256"], "engine_bytes": details["engine_bytes"],
               "modules": details["module_count"], "source_components": details["source_component_count"],
               "verified_notices": details["verified_notice_count"], "native_notices": details["native_notice_count"],
               "total_notices": details["total_notice_count"], "native_components": len(details["native_components"]),
               "current_build_evidence": details["current_build_evidence"], "historical_build_count": len(details["historical_build_evidence"]),
               "applications_executed": False, "loose_executables_logs_profiles": 0}
    if validate_only:
        if output.exists() and checks.read(output) != data:
            raise ValueError("Existing source output differs from the verified deterministic archive")
        return {**summary, "validation_only": True, "existing_output_verified": output.exists(), "archive_written": False}
    write_new(output, data)
    return {**summary, "archive_written": True}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--version", required=True, help="Final release/product version, such as 4.21")
    parser.add_argument("--commit", required=True, help="Exact complete clean final source HEAD")
    parser.add_argument("--build-evidence", type=Path, required=True, help="Successful current manual engine artifact directory")
    parser.add_argument("--evidence-commit", required=True, help="Actual engine build commit; may be an earlier ancestor with identical recipe bytes")
    parser.add_argument("--cache-dir", type=Path, help="Collector source/native archive cache, using exact component filenames")
    parser.add_argument("--source-companion", type=Path, help="Prior verified companion for archive fallback and optional original historical evidence")
    parser.add_argument("--output", type=Path, required=True, help="New source ZIP; existing files are never replaced")
    parser.add_argument("--validate-only", action="store_true", help="Validate and compute the prospective source hash without writing")
    args = parser.parse_args()
    print(json.dumps(package(args.repo, args.version, args.commit, args.build_evidence, args.evidence_commit,
                             args.output, args.cache_dir, args.source_companion, args.validate_only), indent=2))


if __name__ == "__main__":
    main()
