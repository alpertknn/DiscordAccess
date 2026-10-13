"""Prepare four new verified release assets and a separate local JSON receipt.

Requires exact clean Git HEAD, a fresh production DiscordAccess.exe, the pinned
engine, matching complete source/native companion and successful manual engine
proof. Inspects PE bytes/version resources only; never runs app/engine/tests,
uses GitHub credentials, publishes, tags, or replaces prepared output files.
Python 3.11+ is required. There is no historical CI prerequisite.
"""
from __future__ import annotations

import argparse
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import stat
import struct
import subprocess
import sys
import zipfile

sys.dont_write_bytecode = True
import ReleaseChecks as checks


def source_module():
    filename = checks.bounded_path(Path(__file__).with_name("Package-EngineSource.py"))
    spec = importlib.util.spec_from_file_location("release_source", filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def native_amd64(data: bytes) -> None:
    if len(data) < 256 or data[:2] != b"MZ":
        raise ValueError("Expected a Windows PE image")
    offset = struct.unpack_from("<I", data, 0x3c)[0]
    if offset > len(data) - 26 or data[offset:offset + 4] != b"PE\0\0" or struct.unpack_from("<H", data, offset + 4)[0] != 0x8664 or struct.unpack_from("<H", data, offset + 24)[0] != 0x20b:
        raise ValueError("Expected an amd64 PE32+ image")


def fresh_app(repo: Path, version: str, executable: bytes, engine_sha: str) -> dict:
    product, file_version = checks.release_version(version)
    assembly = checks.read(repo / "src/AssemblyInfo.cs", repo).decode("utf-8-sig")
    for attribute, expected in (("AssemblyVersion", file_version), ("AssemblyFileVersion", file_version), ("AssemblyInformationalVersion", product)):
        values = re.findall(r"\[assembly:\s*" + attribute + r'\("([^"\r\n]+)"\)\]', assembly)
        if values != [expected]:
            raise ValueError("Committed assembly version does not identify the requested release")
    app_manifest = checks.read(repo / "src/app.manifest", repo).decode("utf-8-sig")
    if re.findall(r'<assemblyIdentity\b[^>]*\bversion="([^"]+)"', app_manifest) != [file_version]:
        raise ValueError("Windows application manifest version differs from the release")
    # FileVersionInfo reads PE resources; it does not load/execute the image.
    command = "$v = (Get-Item -LiteralPath 'DiscordAccess.exe').VersionInfo; @{ file = $v.FileVersion; product = $v.ProductVersion } | ConvertTo-Json -Compress"
    result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command], cwd=repo,
                            capture_output=True, text=True, encoding="utf-8-sig", check=False)
    if result.returncode != 0:
        raise ValueError("Inert PE version-resource read failed")
    versions = checks.parse_json(result.stdout.encode("utf-8"))
    if versions != {"file": file_version, "product": product}:
        raise ValueError("Fresh production app does not have the requested final release version")
    inputs = ["Build.cmd", "build-production.rsp", "src/app.manifest", "bridge/attachment-download.cjs"]
    response = checks.read(repo / "build-production.rsp", repo).decode("utf-8-sig")
    for line in response.splitlines():
        if not line.strip():
            continue
        name = checks.clean_name(line.strip().replace("\\", "/"))
        if not name.startswith("src/") or not name.endswith(".cs") or name in {
            "src/RegressionChecks.cs", "src/ResidentInstanceChecks.cs", "src/StartupPathTrustChecks.cs", "src/PrivateFileTrustChecks.cs"}:
            raise ValueError("Production compiler response contains an unexpected/test input")
        inputs.append(name)
    app_time = (repo / "DiscordAccess.exe").stat().st_mtime_ns
    for name in inputs:
        path = checks.bounded_path(repo / name, repo)
        checks.read(path, repo)
        if path.stat().st_mtime_ns > app_time:
            raise ValueError("Production app predates a build input; compile Build.cmd on the final source first")
    if engine_sha.encode("utf-16le") not in executable:
        raise ValueError("Fresh production app does not contain the final engine hash constant")
    return versions


def verify_companion(repo: Path, version: str, commit: str, evidence: Path,
                     source_path: Path, manifest: dict) -> tuple[bytes, dict, dict]:
    source_bytes = checks.read(source_path)
    with zipfile.ZipFile(io.BytesIO(source_bytes)) as archive:
        entries = checks.checked_zip_entries(archive, "")
        if archive.testzip() is not None:
            raise ValueError("Source companion failed CRC validation")
        names = {item.filename for item in entries if not item.is_dir()}
        sums = checks.checksums(archive.read("SHA256SUMS.txt"))
        if names != set(sums) | {"SHA256SUMS.txt"}:
            raise ValueError("Source checksum inventory differs")
        for name, expected in sums.items():
            if checks.sha(archive.read(name)) != expected:
                raise ValueError("Source payload hash differs")
        pm = checks.parse_json(archive.read("SOURCE_PACKAGE_MANIFEST.json"))
        if pm["schema"] != 3 or pm["distribution_status"] != "release" or pm["release_version"] != version or pm["source_commit"] != commit or checks.digest(pm["engine_sha256"]) != checks.digest(manifest["engine_sha256"]) or pm["engine_bytes"] != manifest["engine_bytes"]:
            raise ValueError("Source companion release/commit/engine identity differs")
        records = pm["files"]
        if len(records) != len(names) - 2 or {item["path"] for item in records} != names - {"SOURCE_PACKAGE_MANIFEST.json", "SHA256SUMS.txt"}:
            raise ValueError("Source package file manifest inventory differs")
        for record in records:
            data = archive.read(checks.clean_name(record["path"]))
            if checks.sha(data) != checks.digest(record["sha256"]) or len(data) != checks.byte_count(record["bytes"]):
                raise ValueError("Source package file record differs")
    # Reconstruct the complete expected payload from the actual manual proof,
    # current repository and these hash/h1 checked fallback archives. This also
    # validates native original DLL/notice identities and optional historical role.
    expected, details = source_module().source_payloads(repo, version, commit, evidence,
                                                       pm["current_build_evidence"]["source_commit"], None, source_path)
    with zipfile.ZipFile(io.BytesIO(source_bytes)) as archive:
        if set(archive.namelist()) != set(expected):
            raise ValueError("Source companion contains extra/missing payloads")
        for name, data in expected.items():
            if archive.read(name) != data:
                raise ValueError("Source companion differs from exact current source/build/native bytes: " + name)
    return source_bytes, pm, details


def runtime_archive(prefix: str, contents: dict[str, bytes]) -> bytes:
    if set(contents) != {"DiscordAccess.exe", "engine/sing-box.exe", "OKU-BENI.txt", "LISANSLAR.txt", "KAYNAK.txt"}:
        raise ValueError("Runtime must contain exactly the five approved files")
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(contents.items()):
            info = zipfile.ZipInfo(prefix + "/" + checks.clean_name(name), checks.FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED; info.create_system = 3
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            archive.writestr(info, data, compresslevel=9)
    result = buffer.getvalue()
    with zipfile.ZipFile(io.BytesIO(result)) as archive:
        entries = checks.checked_zip_entries(archive, prefix + "/")
        if len(entries) != 5 or {item.filename for item in entries} != {prefix + "/" + name for name in contents} or archive.testzip() is not None:
            raise ValueError("Runtime ZIP inventory/CRC differs")
        for name, data in contents.items():
            if archive.read(prefix + "/" + name) != data:
                raise ValueError("Runtime ZIP changed payload bytes")
    return result


def existing_assets(output_dir: Path, assets: dict[str, bytes], receipt: Path, receipt_data: bytes) -> bool:
    if not output_dir.exists():
        if receipt.exists():
            raise ValueError("Existing receipt has no matching prepared output directory")
        return False
    if not output_dir.is_dir() or {path.name for path in output_dir.iterdir()} != set(assets):
        raise ValueError("Existing release output is not exactly the four prepared assets")
    for name, data in assets.items():
        if checks.read(output_dir / name, output_dir) != data:
            raise ValueError("Existing release asset differs from verified expected bytes: " + name)
    if not receipt.exists() or checks.read(receipt) != receipt_data:
        raise ValueError("Existing release receipt differs from verified expected bytes")
    return True


def write_assets(output_dir: Path, assets: dict[str, bytes], receipt: Path, receipt_data: bytes) -> None:
    if output_dir.exists() or receipt.exists():
        raise ValueError("Release output/receipt already exists; neither was replaced")
    created = []
    created_directory = False
    try:
        output_dir.parent.mkdir(parents=True, exist_ok=True)
        checks.bounded_path(output_dir).mkdir()
        created_directory = True
        for name, data in sorted(assets.items()):
            path = checks.bounded_path(output_dir / checks.clean_name(name), output_dir)
            with path.open("xb") as stream:
                created.append(path); stream.write(data); stream.flush(); os.fsync(stream.fileno())
            if checks.read(path, output_dir) != data:
                raise ValueError("Written release asset differs from verified bytes")
        if {path.name for path in output_dir.iterdir()} != set(assets):
            raise ValueError("Written release directory inventory differs")
        receipt.parent.mkdir(parents=True, exist_ok=True)
        checks.bounded_path(receipt)
        with receipt.open("xb") as stream:
            created.append(receipt); stream.write(receipt_data); stream.flush(); os.fsync(stream.fileno())
        if checks.read(receipt) != receipt_data:
            raise ValueError("Written release receipt differs")
    except Exception:
        for path in reversed(created):
            checks.bounded_path(path).unlink()
        if created_directory:
            checks.bounded_path(output_dir).rmdir()
        raise


def package(repo: Path, version: str, commit: str, engine_path: Path, source_path: Path, evidence: Path,
            output_dir: Path, receipt: Path, validate_only: bool = False) -> dict:
    repo = checks.bounded_path(repo); engine_path = checks.bounded_path(engine_path)
    source_path = checks.bounded_path(source_path); output_dir = checks.bounded_path(output_dir); receipt = checks.bounded_path(receipt)
    checks.release_version(version)
    if receipt.suffix.lower() != ".json" or receipt.is_relative_to(output_dir):
        raise ValueError("Use a separate local JSON receipt outside the four-asset release directory")
    if not validate_only and (output_dir.exists() or receipt.exists()):
        raise ValueError("Release output/receipt already exists; nothing was replaced")
    repository_url = checks.exact_head(repo, commit)
    manifest_bytes = checks.read(repo / "licenses/engine-source-manifest.json", repo)
    manifest = checks.parse_json(manifest_bytes)
    checks.manifest_identity(manifest)
    engine_sha = checks.digest(manifest["engine_sha256"])
    source = checks.read(repo / "src/DiscordAccess.cs", repo).decode("utf-8-sig")
    pins = re.findall(r'\binternal\s+const\s+string\s+EngineSha256\s*=\s*"([a-f0-9]{64})"\s*;', source)
    if pins != [engine_sha]:
        raise ValueError("Committed Package.EngineSha256 differs from final source manifest")
    engine = checks.read(engine_path, expected_sha=engine_sha, expected_bytes=manifest["engine_bytes"])
    executable = checks.read(repo / "DiscordAccess.exe", repo)
    native_amd64(engine); native_amd64(executable)
    versions = fresh_app(repo, version, executable, engine_sha)
    source_bytes, source_manifest, details = verify_companion(repo, version, commit, evidence, source_path, manifest)
    evidence_engine = checks.read(checks.bounded_path(evidence) / "sing-box.exe", expected_sha=engine_sha, expected_bytes=len(engine))
    if evidence_engine != engine:
        raise ValueError("Runtime engine differs from successful manual build proof")
    prefix = "DiscordAccess-v" + version
    source_name = prefix + "-engine-source.zip"
    source_sha = checks.sha(source_bytes)
    notices = checks.read(repo / "THIRD_PARTY_NOTICES.txt", repo)
    aggregate = checks.read(repo / "licenses/ENGINE-THIRD-PARTY-NOTICES.txt", repo)
    checks.validate_full_gpl(notices); checks.validate_aggregate(aggregate, manifest["components"])
    overlay_notices = [item for item in manifest["build_recipe"]["patch_files"] if Path(item["source_relative"]).name == "DISCORDACCESS-NOTICE"]
    if len(overlay_notices) != 1:
        raise ValueError("Expected one reviewed engine modification/copyright notice")
    record = overlay_notices[0]
    overlay_notice = checks.read(repo / record["payload_relative"], repo, record["after_sha256"], record["bytes"])
    if b"GPL-3.0-or-later" not in overlay_notice or b"Copyright" not in overlay_notice:
        raise ValueError("Reviewed engine modification notice is incomplete")
    heading = ("DiscordAccess v" + version + ".\nEngine source modified by DiscordAccess; reviewed overlay modification date: " +
               details["main_source_overlay"]["modification_date"] + ".\nNative binaries/wrappers retain their separate terms and attribution below.\n").encode("utf-8")
    licenses = b"\n\n".join((heading, checks.read(repo / "LICENSE", repo), notices, aggregate, overlay_notice))
    proof = details["current_build_evidence"]
    note = ("DiscordAccess v" + version + "\nRelease/tag: " + repository_url + "/releases/tag/v" + version + "\n"
            "Source commit: " + commit + "\nExact source: " + repository_url + "/tree/" + commit + "\n"
            "Engine: " + manifest["engine_version"] + " (Windows amd64)\nEngine SHA256: " + engine_sha + "\n"
            "Corresponding engine source companion: " + source_name + "\nCompanion SHA256: " + source_sha + "\n"
            "Companion bytes: " + str(len(source_bytes)) + "\nCurrent recipe SHA256: " + manifest["build_recipe"]["script_sha256"] + "\n"
            "Successful manual engine build source commit: " + proof["source_commit"] + "\n"
            "Source/native assets, exact locks, overlays/tests, full licenses and successful manual proof are included.\n"
            "Optional historical reports are identified separately and do not replace current proof.\n"
            "Distribute this exact source companion together with the runtime package.\n"
            "The companion is not needed to run DiscordAccess.\n").encode("utf-8")
    instructions = checks.read(repo / "docs/OKU-BENI.txt", repo)
    for text in (instructions.decode("utf-8-sig"), note.decode("utf-8")):
        if "v" + version not in text or re.search(r"candidate|\bunreleased\b|\baday(?:ı|ını|ın|lar|lardan)?\b|\bRC\d*\b|henüz yayımlanmamış", text, re.I):
            raise ValueError("Runtime instructions do not identify the final release status/version")
    contents = {"DiscordAccess.exe": executable, "engine/sing-box.exe": engine, "OKU-BENI.txt": instructions,
                "LISANSLAR.txt": licenses, "KAYNAK.txt": note}
    assets = {prefix + ".zip": runtime_archive(prefix, contents), source_name: source_bytes, "engine-source-manifest.json": manifest_bytes}
    assets["SHA256SUMS.txt"] = "".join(checks.sha(data) + "  " + name + "\n" for name, data in sorted(assets.items())).encode("ascii")
    checks.exact_head(repo, commit)
    if checks.read(repo / "DiscordAccess.exe", repo) != executable:
        raise ValueError("App changed during release preparation")
    checks.read(engine_path, expected_sha=engine_sha, expected_bytes=len(engine))
    checks.read(source_path, expected_sha=source_sha, expected_bytes=len(source_bytes))
    summary = {"source_commit": commit, "release_tag": "v" + version, "publication_status": "prepared", "published": False,
               "file_version": versions["file"], "product_version": versions["product"],
               "assets": [{"name": name, "path": str(output_dir / name), "bytes": len(data), "sha256": checks.sha(data),
                           "content_type": "application/zip" if name.endswith(".zip") else "application/json" if name.endswith(".json") else "text/plain"}
                          for name, data in sorted(assets.items())],
               "engine_sha256": engine_sha, "engine_bytes": len(engine), "executable_sha256": checks.sha(executable),
               "runtime_files": {name: {"bytes": len(data), "sha256": checks.sha(data)} for name, data in sorted(contents.items())},
               "runtime_file_count": 5, "release_asset_count": 4,
               "notice_blocks_verified": manifest["notice_count"], "native_notice_blocks_verified": manifest.get("native_notice_count", 0),
               "total_notice_blocks_verified": manifest.get("total_notice_count", manifest["notice_count"]),
               "native_component_count": len(details["native_components"]), "full_gpl_text_verified": True, "overlay_notice_included": True,
               "source_companion_input": {"name": source_path.name, "sha256": source_sha, "bytes": len(source_bytes)},
               "source_companion_reused_unchanged": True, "current_manual_engine_build": proof,
               "historical_engine_builds": details["historical_build_evidence"], "current_recipe_sha256": manifest["build_recipe"]["script_sha256"],
               "executables_run_by_packager": False, "tests_logs_configs_or_profiles_in_runtime": False, "repo_engine_changed": False}
    receipt_data = (json.dumps(summary, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    if validate_only:
        verified = existing_assets(output_dir, assets, receipt, receipt_data)
        return {**summary, "validation_only": True, "existing_output_verified": verified, "assets_written": False, "receipt_written": False}
    write_assets(output_dir, assets, receipt, receipt_data)
    return {**summary, "assets_written": True, "receipt_written": True, "receipt": str(receipt)}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--version", required=True, help="Final numeric release/product version, such as 4.21")
    parser.add_argument("--commit", required=True, help="Exact complete clean final source HEAD")
    parser.add_argument("--engine", type=Path, required=True, help="Verified pinned engine EXE; it is never executed")
    parser.add_argument("--source", type=Path, required=True, help="Matching schema3 source companion for this exact version/commit")
    parser.add_argument("--build-evidence", type=Path, required=True, help="Same successful manual engine artifact directory used for the companion")
    parser.add_argument("--output-dir", type=Path, required=True, help="New directory for exactly four release assets")
    parser.add_argument("--receipt", type=Path, required=True, help="New separate local JSON receipt; not uploaded as a release asset")
    parser.add_argument("--validate-only", action="store_true", help="Validate prospective hashes, or byte-check already prepared assets without replacing them")
    args = parser.parse_args()
    print(json.dumps(package(args.repo, args.version, args.commit, args.engine, args.source, args.build_evidence,
                             args.output_dir, args.receipt, args.validate_only), indent=2))


if __name__ == "__main__":
    main()
