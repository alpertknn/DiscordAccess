"""Bounded, offline release/source validation shared by the packaging commands.

Only supplied files and read-only local Git/PE metadata are inspected. This
module never downloads data, reads credential stores or executes application,
engine or source-unit binaries. Python 3.11 or newer is required.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import struct
import subprocess
import tarfile
from urllib.parse import urlsplit
import uuid
import zipfile


MAX_FILE = 128 * 1024 * 1024
MAX_ARCHIVE_FILES = 100000
MAX_EXPANDED = 512 * 1024 * 1024
FIXED_TIME = (1980, 1, 1, 0, 0, 0)
# Complete normalized GPL v3 block, byte-equal to the previously verified
# v4.19 source companion. This is a license-text identity, not an engine pin.
GPL_TEXT_SHA256 = "605e9047a563c5c8396ffb18232aa4304ec56586aee537c45064c6fb425e44ad"
LEGAL = re.compile(r"^(?:licen[cs]e|copying|notice|copyright|authors|patents)(?:[._-].*)?$", re.I)
CODE_EXT = {".go", ".c", ".cc", ".cpp", ".h", ".cs", ".py", ".js", ".ts", ".exe", ".dll", ".sys", ".png", ".jpg", ".gif", ".woff", ".woff2"}
UNIT_REPORT = "source-unit-tests.txt"
EVIDENCE_NAMES = ("build-metadata.json", "Build-SecurityEngine.ps1", "compiled-packages.txt",
                  "go-version-m.txt", "selected-modules.txt", UNIT_REPORT,
                  "reviewed-main-source-patch.json", "patched-source-files.txt")


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def digest(value: str) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[A-Fa-f0-9]{64}", value):
        raise ValueError("Expected a SHA-256 digest")
    return value.lower()


def byte_count(value: int) -> int:
    if type(value) is not int or not 0 <= value <= MAX_FILE:
        raise ValueError("Invalid bounded source byte count")
    return value


def bounded_path(path: Path, root: Path | None = None) -> Path:
    path = Path(os.path.abspath(path))
    root = Path(os.path.abspath(root)) if root is not None else None
    if root is not None and not path.is_relative_to(root):
        raise ValueError("Path escaped its intended source/output root")
    if str(path).startswith("\\\\") or (os.name == "nt" and ":" in str(path)[2:]):
        raise ValueError("Use local paths without alternate streams")
    for current in [path, *path.parents]:
        try:
            info = current.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Source/output path contains a reparse point")
    return path


def read(path: Path, root: Path | None = None, expected_sha: str | None = None, expected_bytes: int | None = None) -> bytes:
    path = bounded_path(path, root)
    if not path.is_file() or path.stat().st_size > MAX_FILE:
        raise ValueError("Source input is missing or oversized: " + path.name)
    data = path.read_bytes()
    if len(data) > MAX_FILE:
        raise ValueError("Source input grew beyond its read bound")
    if expected_sha is not None and sha(data) != digest(expected_sha):
        raise ValueError("Source input SHA-256 mismatch: " + path.name)
    if expected_bytes is not None and len(data) != byte_count(expected_bytes):
        raise ValueError("Source input byte-count mismatch: " + path.name)
    return data


def clean_name(name: str) -> str:
    if not isinstance(name, str) or not name or len(name) > 240 or "\\" in name or ":" in name or any(ord(c) < 32 for c in name):
        raise ValueError("Unsafe package path")
    path = PurePosixPath(name)
    if path.is_absolute() or any(part in ("", ".", "..") for part in name.split("/")):
        raise ValueError("Unsafe package path")
    if any(part.endswith((".", " ")) or re.fullmatch(r"(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", part, re.I) for part in path.parts):
        raise ValueError("Unsafe Windows extraction path")
    return name


def no_duplicates(pairs: list[tuple[str, object]]) -> dict:
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("JSON contains duplicate keys")
        value[key] = item
    return value


def parse_json(data: bytes) -> dict:
    value = json.loads(data.decode("utf-8-sig"), object_pairs_hook=no_duplicates)
    if not isinstance(value, dict):
        raise ValueError("Expected a JSON object")
    return value


def source_url(value: str) -> None:
    parsed = urlsplit(value)
    if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError("Source URL contains credentials or unsupported URL fields")


def public_report(data: bytes) -> str:
    text = data.decode("utf-8-sig")
    if re.search(r"(?<![A-Za-z0-9])[A-Za-z]:[\\/]|[\\/]Users[\\/]|/home/|/root/", text, re.I) or "\x00" in text:
        raise ValueError("Build evidence contains a machine-local path or binary content")
    return text


def legal_path(relative: str) -> bool:
    path = PurePosixPath(relative)
    return path.suffix.lower() not in CODE_EXT and bool(
        LEGAL.fullmatch(path.name) or path.name.lower().endswith(".license") or
        any(part.lower() in ("license", "licenses", "licence", "licences") for part in path.parts[:-1]))


def checked_zip_entries(archive: zipfile.ZipFile, prefix: str) -> list[zipfile.ZipInfo]:
    entries = archive.infolist()
    if len(entries) > MAX_ARCHIVE_FILES or sum(entry.file_size for entry in entries) > MAX_EXPANDED:
        raise ValueError("Source archive exceeds its inventory bounds")
    names = set()
    for entry in entries:
        if entry.file_size > MAX_FILE or entry.flag_bits & 1 or not entry.filename.startswith(prefix):
            raise ValueError("Unexpected source archive prefix/size/encryption")
        name = entry.filename.rstrip("/")
        if name:
            clean_name(name)
        if name.casefold() in names:
            raise ValueError("Source archive has duplicate or case-colliding paths")
        names.add(name.casefold())
        mode = entry.external_attr >> 16
        if stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFDIR):
            raise ValueError("Source ZIP contains a non-regular filesystem entry")
    return entries


def zip_inventory(data: bytes, prefix: str) -> dict[str, bytes]:
    result = {}
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        for entry in checked_zip_entries(archive, prefix):
            relative = entry.filename[len(prefix):].rstrip("/")
            if relative and not entry.is_dir():
                result[clean_name(relative)] = archive.read(entry)
    return result


def verify_notices(files: dict[str, bytes], component: dict) -> None:
    expected = {clean_name(item["archive_path"]) for item in component["notices"]}
    if len(expected) != len(component["notices"]):
        raise ValueError("Component has duplicate notice paths")
    actual = {name for name in files if legal_path(name)}
    if actual != expected:
        raise ValueError("Manifest notice inventory does not cover the complete source archive")
    for notice in component["notices"]:
        payload = files[notice["archive_path"]]
        if sha(payload) != digest(notice["sha256"]) or len(payload) != byte_count(notice["bytes"]):
            raise ValueError("Notice does not match its exact source archive")
        text = payload.decode("utf-8-sig").encode("utf-8")
        if b"\x00" in text or sha(text) != digest(notice["aggregate_text_sha256"]) or len(text) != byte_count(notice["aggregate_text_bytes"]):
            raise ValueError("Decoded source notice identity differs from the manifest")


def verify_module(data: bytes, component: dict) -> None:
    prefix = component["module"] + "@" + component["version"] + "/"
    digest_hash = hashlib.sha256()
    notices = {}
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = checked_zip_entries(archive, prefix)
        for entry in sorted(entries, key=lambda item: item.filename.encode("utf-8")):
            with archive.open(entry) as stream:
                digest_hash.update((hashlib.file_digest(stream, "sha256").hexdigest() + "  " + entry.filename + "\n").encode())
            relative = entry.filename[len(prefix):]
            if not entry.is_dir() and legal_path(relative):
                notices[relative] = archive.read(entry)
    actual = "h1:" + base64.b64encode(digest_hash.digest()).decode()
    if actual != component["expected_go_h1"] or actual != component["verified_go_h1"]:
        raise ValueError("Module archive did not match its engine Go h1")
    verify_notices(notices, component)


def verify_go(data: bytes, component: dict) -> None:
    notices = {}
    seen = set()
    total = 0
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
        for entry in archive:
            total += entry.size
            name = entry.name.rstrip("/")
            if not name.startswith("go") or (name != "go" and not name.startswith("go/")):
                raise ValueError("Go archive has an unexpected source prefix")
            clean_name(name)
            if name.casefold() in seen or len(seen) >= MAX_ARCHIVE_FILES or total > MAX_EXPANDED or entry.size > MAX_FILE:
                raise ValueError("Go source archive inventory is not bounded/unique")
            seen.add(name.casefold())
            if not (entry.isfile() or entry.isdir()):
                raise ValueError("Go source archive contains a non-regular filesystem entry")
            relative = name.removeprefix("go/")
            if entry.isfile() and legal_path(relative):
                stream = archive.extractfile(entry)
                if stream is None:
                    raise ValueError("Go notice is not a regular source file")
                notices[relative] = stream.read()
    verify_notices(notices, component)


def checksums(data: bytes, flat: bool = False) -> dict[str, str]:
    values = {}
    folded = set()
    for line in data.decode("ascii").splitlines():
        match = re.fullmatch(r"([a-f0-9]{64})  (.+)", line)
        if not match:
            raise ValueError("Checksum evidence is malformed")
        name = clean_name(match[2])
        if name.casefold() in folded or (flat and "/" in name):
            raise ValueError("Checksum evidence has duplicate/unsafe paths")
        folded.add(name.casefold())
        values[name] = match[1]
    if not values:
        raise ValueError("Checksum evidence is empty")
    return values


def json_bytes(value: object) -> bytes:
    return (json.dumps(value, indent=2, ensure_ascii=False, sort_keys=True) + "\n").encode("utf-8")


def recipe_constants(data: bytes) -> dict[str, str]:
    """Read reviewed literal pins; never evaluate or execute PowerShell."""
    text = data.decode("utf-8-sig")
    values = {}
    for key in ("sourceCommit", "sourceUrl", "sourceSha", "goVersion", "goUrl", "goSha",
                "engineVersion", "engineSha256", "patchManifestSha", "lockModSha", "lockSumSha", "buildTags"):
        matches = re.findall(r"^\$" + re.escape(key) + r"\s*=\s*'([^'\r\n]+)'\s*$", text, re.M)
        if len(matches) != 1:
            raise ValueError("Build recipe has missing/ambiguous literal pin: " + key)
        values[key] = matches[0]
    sizes = re.findall(r"^\$engineBytes\s*=\s*([0-9]+)\s*$", text, re.M)
    if len(sizes) != 1 or not 0 < byte_count(int(sizes[0])):
        raise ValueError("Build recipe has missing/invalid engine byte pin")
    values["engineBytes"] = sizes[0]
    return values


class SourceArchives:
    """Read exact archives from cache, or a hash/CRC-checked prior companion."""

    def __init__(self, cache: Path | None, companion: Path | None):
        self.cache = cache
        self.companion = companion
        self.archive = None
        self.prior_hashes = {}

    def fallback(self) -> zipfile.ZipFile:
        if self.archive is not None:
            return self.archive
        if self.companion is None:
            raise ValueError("Source cache is incomplete and no previous companion was supplied")
        path = bounded_path(self.companion)
        self.archive = zipfile.ZipFile(path)
        entries = checked_zip_entries(self.archive, "")
        if self.archive.testzip() is not None:
            raise ValueError("Previous source companion failed CRC validation")
        self.prior_hashes = checksums(self.archive.read("SHA256SUMS.txt"))
        names = {entry.filename for entry in entries if not entry.is_dir()}
        if names != set(self.prior_hashes) | {"SHA256SUMS.txt"}:
            raise ValueError("Previous companion checksum inventory differs")
        for name, expected in self.prior_hashes.items():
            if sha(self.archive.read(name)) != expected:
                raise ValueError("Previous source companion failed payload hash validation")
        package_manifest = parse_json(self.archive.read("SOURCE_PACKAGE_MANIFEST.json"))
        records = package_manifest["files"]
        if {item["path"] for item in records} != names - {"SHA256SUMS.txt", "SOURCE_PACKAGE_MANIFEST.json"} or len(records) != len(names) - 2:
            raise ValueError("Previous companion file manifest inventory differs")
        for item in records:
            data = self.archive.read(clean_name(item["path"]))
            if sha(data) != digest(item["sha256"]) or len(data) != byte_count(item["bytes"]):
                raise ValueError("Previous companion file manifest identity differs")
        return self.archive

    def get(self, filename: str, package_path: str, component: dict) -> bytes:
        source = self.cache / clean_name(filename) if self.cache is not None else None
        if source is not None and source.exists():
            return read(source, self.cache, component["source_archive_sha256"], component["source_archive_bytes"])
        data = self.fallback().read(clean_name(package_path))
        if sha(data) != digest(component["source_archive_sha256"]) or len(data) != byte_count(component["source_archive_bytes"]):
            raise ValueError("Reused source archive differs from the FINAL engine manifest")
        return data

    def close(self) -> None:
        if self.archive is not None:
            self.archive.close()


def validate_aggregate(data: bytes, components: list[dict]) -> None:
    text = data.decode("utf-8-sig")
    expected_markers = {notice["aggregate_marker"] for component in components for notice in component["notices"]}
    begin = re.findall(r"^--- BEGIN NOTICE (.+) ---$", text, re.M)
    end = re.findall(r"^--- END NOTICE (.+) ---$", text, re.M)
    expected_count = sum(len(component["notices"]) for component in components)
    if len(expected_markers) != expected_count or set(begin) != expected_markers or set(end) != expected_markers or len(begin) != expected_count or len(end) != expected_count:
        raise ValueError("Aggregate notice marker inventory differs from the manifest")
    for component in components:
        for notice in component["notices"]:
            marker = re.escape(notice["aggregate_marker"])
            expression = (r"^--- BEGIN NOTICE " + marker + r" ---\nArchive path: " + re.escape(notice["archive_path"]) +
                          r"\nOriginal file SHA256: " + re.escape(notice["sha256"]) + r"\n\n(.*?)\n--- END NOTICE " + marker + r" ---$")
            matches = re.findall(expression, text, re.M | re.S)
            if len(matches) != 1:
                raise ValueError("Aggregate notice header/block differs from its manifest")
            payload = matches[0].encode("utf-8")
            if sha(payload) != digest(notice["aggregate_text_sha256"]) or len(payload) != byte_count(notice["aggregate_text_bytes"]):
                raise ValueError("Aggregate notice text did not match its exact source notice")


def validate_full_gpl(data: bytes) -> None:
    text = data.decode("utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")
    headings = list(re.finditer(r"^[ \t]*GNU GENERAL PUBLIC LICENSE\n[ \t]*Version 3, 29 June 2007\n", text, re.M))
    if len(headings) != 1:
        raise ValueError("Complete GPL v3 text is missing or ambiguous")
    payload = (text[headings[0].start():].strip() + "\n").encode("utf-8")
    if sha(payload) != GPL_TEXT_SHA256 or len(payload) != 35129:
        raise ValueError("Complete GPL v3 text differs from the verified license text")


def validate_overlay(repo: Path, recipe: dict, engine_manifest: dict, upstream: dict[str, bytes], payloads: dict[str, bytes]) -> tuple[dict, bytes, list[dict]]:
    pin = recipe["patch_manifest"]
    name = clean_name(pin["path"])
    data = read(repo / name, repo, pin["sha256"])
    patch = parse_json(data)
    if patch["schema"] != 1 or patch["engine_version"].removeprefix("v") != engine_manifest["engine_version"].removeprefix("v") or patch["main_source_commit"] != engine_manifest["main_source_commit"]:
        raise ValueError("Overlay manifest main-source/version identity differs")
    main = next(item for item in engine_manifest["components"] if item["module"] == "github.com/sagernet/sing-box" and "expected_go_h1" not in item)
    if digest(patch["source_archive_sha256"]) != digest(main["source_archive_sha256"]):
        raise ValueError("Overlay manifest upstream archive identity differs")
    fields = {"source_relative", "payload_relative", "before_sha256", "after_sha256", "bytes"}
    records = patch["files"]
    if not isinstance(records, list) or not records or records != recipe["patch_files"]:
        raise ValueError("Current recipe overlay inventory differs from its pinned manifest")
    sources = set()
    payload_names = set()
    enriched = []
    payloads[name] = data
    for item in records:
        if set(item) != fields:
            raise ValueError("Overlay file record has unexpected/missing fields")
        source_name = clean_name(item["source_relative"])
        payload_name = clean_name(item["payload_relative"])
        if source_name in ("go.mod", "go.sum") or source_name.casefold() in sources or payload_name.casefold() in payload_names:
            raise ValueError("Overlay duplicates another file or a dependency lock")
        if not payload_name.startswith(str(PurePosixPath(name).parent) + "/source/") or not payload_name.endswith("/" + source_name):
            raise ValueError("Overlay payload does not preserve its mirrored main-source path")
        sources.add(source_name.casefold())
        payload_names.add(payload_name.casefold())
        previous = upstream.get(source_name)
        if item["before_sha256"] is None:
            if previous is not None:
                raise ValueError("Declared added source already exists upstream")
        elif previous is None or sha(previous) != digest(item["before_sha256"]):
            raise ValueError("Overlay before identity differs from upstream source")
        modified = read(repo / payload_name, repo, item["after_sha256"], item["bytes"])
        if previous == modified:
            raise ValueError("Overlay file declares an unchanged source")
        payloads[payload_name] = modified
        enriched.append({**item, "before_bytes": len(previous) if previous is not None else None,
                         "change": "modified" if previous is not None else "added"})
    source_root = bounded_path(repo / PurePosixPath(name).parent / "source", repo)
    actual_names = set()
    for path in source_root.rglob("*"):
        bounded_path(path, repo)
        if path.is_file():
            actual_names.add(path.relative_to(repo).as_posix().casefold())
    if actual_names != payload_names:
        raise ValueError("Overlay source directory contains undeclared or missing payload files")
    if not any(item["source_relative"].endswith("_test.go") for item in records) or not any(PurePosixPath(item["source_relative"]).name == "DISCORDACCESS-NOTICE" for item in records):
        raise ValueError("Source overlay omits corresponding tests or the modification notice")
    return patch, data, enriched


def verify_build_reports(evidence: dict[str, bytes], metadata: dict, manifest: dict, modules: list[dict], overlay: list[dict]) -> None:
    report = public_report(evidence["go-version-m.txt"])
    if report.splitlines()[0] != "sing-box.exe: " + manifest["go_version"] or "\tpath\tgithub.com/sagernet/sing-box/cmd/sing-box" not in report:
        raise ValueError("Build build-info target/toolchain differs")
    dep_lines = [line.split("\t") for line in report.splitlines() if line.startswith("\tdep\t")]
    if any(len(parts) != 5 for parts in dep_lines):
        raise ValueError("Build build-info dependency record is malformed")
    actual = {(parts[2], parts[3], parts[4]) for parts in dep_lines}
    expected = {(item["module"], item["version"], item["expected_go_h1"]) for item in modules}
    if actual != expected or len(dep_lines) != len(expected) or "\t=>\t" in report:
        raise ValueError("Build build-info module set differs from exact source manifest")
    settings = dict(re.findall(r"^\tbuild\t([^=\r\n]+)=([^\r\n]*)\r?$", report, re.M))
    for key, expected_value in manifest["build_settings"].items():
        if settings.get(key) != expected_value:
            raise ValueError("Build build-info setting differs from final source manifest: " + key)
    if metadata["build_tags"] != settings.get("-tags"):
        raise ValueError("Build metadata build tags differ from build-info")
    for key, value in {"trimpath": True, "buildvcs": False, "GOTOOLCHAIN": "local",
                       "GOOS": "windows", "GOARCH": "amd64", "GOAMD64": "v1", "CGO_ENABLED": "0"}.items():
        if metadata["build_settings"].get(key) != value:
            raise ValueError("Build metadata build setting differs: " + key)
    selected = {}
    for line in public_report(evidence["selected-modules.txt"]).splitlines():
        parts = line.split()
        if len(parts) == 1 and parts[0] == "github.com/sagernet/sing-box":
            continue
        if len(parts) != 2 or parts[0] in selected:
            raise ValueError("Build selected-module evidence is malformed or replaced")
        selected[parts[0]] = parts[1]
    if selected != metadata["selected_modules"] or any(selected.get(item["module"]) != item["version"] for item in modules):
        raise ValueError("Build selected module graph differs from metadata/linked sources")
    packages = public_report(evidence["compiled-packages.txt"]).splitlines()
    if len(packages) != len(set(packages)) or any(not re.fullmatch(r"[A-Za-z0-9_./!+\-]+", item) for item in packages):
        raise ValueError("Build compiled-package evidence is malformed")
    required = {"github.com/sagernet/sing-box/cmd/sing-box"}
    for item in overlay:
        name = item["source_relative"]
        if name.endswith(".go") and not name.endswith("_test.go"):
            required.add("github.com/sagernet/sing-box/" + str(PurePosixPath(name).parent))
    if not required.issubset(packages) or any(item == "golang.org/x/crypto/openpgp" or item.startswith("golang.org/x/crypto/openpgp/") for item in packages):
        raise ValueError("Build compiled package set omits modified code or includes disallowed OpenPGP")
    units = public_report(evidence[UNIT_REPORT])
    if metadata.get("source_unit_tests_ran") is not True or metadata.get("source_unit_tests_passed") is not True or not units.strip():
        raise ValueError("NEW build must contain successful source-unit test evidence")
    if re.search(r"(^|\n)(?:FAIL(?:\s|$)|--- FAIL:|panic:)", units):
        raise ValueError("Build source-unit report contains a failure")
    tested = {"github.com/sagernet/sing-box/" + str(PurePosixPath(item["source_relative"]).parent)
              for item in overlay if item["source_relative"].endswith("_test.go")}
    for package_name in tested:
        if not re.search(r"^ok\s+" + re.escape(package_name) + r"\s", units, re.M):
            raise ValueError("Build source-unit report omits an overlay test package")


def git_bytes(repo: Path, *arguments: str) -> bytes:
    result = subprocess.run(["git", *arguments], cwd=repo, capture_output=True, check=False)
    if result.returncode != 0:
        raise ValueError("Read-only Git identity check failed")
    return result.stdout


def git_text(repo: Path, *arguments: str) -> str:
    return git_bytes(repo, *arguments).decode("utf-8").strip()


def commit_sha(value: str) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[a-f0-9]{40}", value):
        raise ValueError("Supply a complete lowercase 40-hex source commit")
    return value


def release_version(value: str) -> tuple[str, str]:
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?", value):
        raise ValueError("Use a final numeric release version, such as 4.21 or 4.21.1")
    parts = [int(part) for part in value.split(".")]
    if any(part > 65535 for part in parts):
        raise ValueError("Release version exceeds Windows version-resource bounds")
    return value, ".".join(str(part) for part in parts + [0] * (4 - len(parts)))


def exact_head(repo: Path, commit: str) -> str:
    repo = bounded_path(repo)
    commit_sha(commit)
    if bounded_path(Path(git_text(repo, "rev-parse", "--show-toplevel"))) != repo or git_text(repo, "rev-parse", "HEAD") != commit:
        raise ValueError("Repository is not at the requested exact source HEAD")
    if git_text(repo, "status", "--porcelain=v1", "--untracked-files=all"):
        raise ValueError("Repository must be clean, including non-ignored untracked files")
    # Only inspect the local origin string; never query GitHub/credential stores.
    remote = git_text(repo, "remote", "get-url", "origin")
    match = re.fullmatch(r"(?:https://github\.com/|git@github\.com:)([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\.git)?", remote)
    if not match or any(part in (".", "..") for part in match.groups()):
        raise ValueError("Use a credential-free GitHub origin for release/source URLs")
    return "https://github.com/" + match[1] + "/" + match[2]


def evidence_ancestor(repo: Path, evidence_commit: str, release_commit: str) -> None:
    commit_sha(evidence_commit); commit_sha(release_commit)
    result = subprocess.run(["git", "merge-base", "--is-ancestor", evidence_commit, release_commit],
                            cwd=repo, capture_output=True, check=False)
    if result.returncode != 0:
        raise ValueError("Build evidence commit is not an ancestor of the final source commit")


def manifest_identity(manifest: dict) -> tuple[list[dict], dict]:
    components = manifest["components"]
    modules = [item for item in components if "expected_go_h1" in item]
    if manifest["schema"] != 1 or not modules or len(modules) > 4096 or manifest["module_count"] != len(modules) or len(components) != len(modules) + 2:
        raise ValueError("Source manifest module/main/Go component inventory differs")
    if len({(item["module"], item["version"]) for item in components}) != len(components) or len({item["component_id"] for item in components}) != len(components):
        raise ValueError("Source manifest has duplicate component identities")
    if any(item["errors"] or not item["notices"] for item in components):
        raise ValueError("Source manifest has unresolved/empty notices")
    if type(manifest["notice_count"]) is not int or manifest["notice_count"] != sum(len(item["notices"]) for item in components):
        raise ValueError("Source notice count differs from component records")
    if not re.fullmatch(r"go[0-9]+\.[0-9]+(?:\.[0-9]+)?", manifest["go_version"]):
        raise ValueError("Source manifest Go version is malformed")
    digest(manifest["engine_sha256"]); byte_count(manifest["engine_bytes"])
    mains = [item for item in components if item["module"] == "github.com/sagernet/sing-box" and "expected_go_h1" not in item]
    gos = [item for item in components if item["module"] == "Go standard library/runtime"]
    if len(mains) != 1 or len(gos) != 1 or mains[0]["commit"] != manifest["main_source_commit"] or mains[0]["version"].removeprefix("v") != manifest["engine_version"].removeprefix("v") or gos[0]["version"] != manifest["go_version"]:
        raise ValueError("Main/Go source identity differs from the engine manifest")
    return modules, mains[0]


def current_recipe(repo: Path, manifest: dict, payloads: dict[str, bytes]) -> dict[str, str]:
    recipe = manifest["build_recipe"]
    for path_key, hash_key in (("script", "script_sha256"), ("go_mod", "go_mod_sha256"), ("go_sum", "go_sum_sha256")):
        name = clean_name(recipe[path_key])
        payloads[name] = read(repo / name, repo, recipe[hash_key])
    pins = recipe_constants(payloads[recipe["script"]])
    _, main = manifest_identity(manifest)
    expected = {"sourceCommit": manifest["main_source_commit"], "sourceUrl": main["source_url"],
                "sourceSha": main["source_archive_sha256"], "goVersion": manifest["go_version"],
                "goUrl": "https://go.dev/dl/" + manifest["go_version"] + ".windows-amd64.zip",
                "engineVersion": manifest["engine_version"].removeprefix("v"), "engineSha256": manifest["engine_sha256"],
                "engineBytes": str(manifest["engine_bytes"]), "patchManifestSha": recipe["patch_manifest"]["sha256"],
                "lockModSha": recipe["go_mod_sha256"], "lockSumSha": recipe["go_sum_sha256"], "buildTags": manifest["build_settings"]["-tags"]}
    for key, value in expected.items():
        if key.endswith("Sha") or key.endswith("Sha256"):
            value = digest(value)
            actual = digest(pins[key])
        else:
            actual = pins[key]
        if actual != value:
            raise ValueError("Current recipe literal pin differs: " + key)
    digest(pins["goSha"])
    return pins


def source_payloads(repo: Path, manifest: dict, archives: SourceArchives,
                    payloads: dict[str, bytes]) -> tuple[dict[str, bytes], list[dict], dict[str, dict[str, bytes]]]:
    inventory, modules, upstream = [], {}, None
    for component in manifest["components"]:
        source_url(component["source_url"])
        identifier = component["component_id"]
        if not re.fullmatch(r"[A-Za-z0-9_.-]+", identifier):
            raise ValueError("Unsafe source component ID")
        if "expected_go_h1" in component:
            filename = identifier + ".zip"
            name = "sources/modules/" + filename
        elif component["module"] == "github.com/sagernet/sing-box":
            filename = identifier + ".zip"
            name = "sources/main/sing-box-upstream-" + component["commit"] + ".zip"
        elif component["module"] == "Go standard library/runtime":
            filename = identifier + ".src.tar.gz"
            name = "sources/go/" + filename
        else:
            raise ValueError("Unexpected source component")
        data = archives.get(filename, name, component)
        if "expected_go_h1" in component:
            verify_module(data, component)
            modules[component["module"]] = zip_inventory(data, component["module"] + "@" + component["version"] + "/")
        elif component["module"] == "github.com/sagernet/sing-box":
            upstream = zip_inventory(data, "sing-box-" + component["commit"] + "/")
            verify_notices(upstream, component)
        else:
            verify_go(data, component)
        payloads[clean_name(name)] = data
        inventory.append({"module": component["module"], "version": component["version"], "package_path": name,
                          "source_url": component["source_url"], "sha256": sha(data), "bytes": len(data),
                          **({"verified_go_h1": component["verified_go_h1"]} if "expected_go_h1" in component else {})})
    if upstream is None:
        raise ValueError("Pinned main source archive is missing")
    return upstream, inventory, modules


def validate_build(repo: Path, evidence_dir: Path, manifest: dict, payloads: dict[str, bytes],
                   upstream: dict[str, bytes], overlay: list[dict], current_pins: dict[str, str],
                   evidence_commit: str, release_commit: str, require_current: bool = True) -> tuple[dict[str, bytes], bytes, dict]:
    evidence_dir = bounded_path(evidence_dir)
    evidence_ancestor(repo, evidence_commit, release_commit)
    checksum_bytes = read(evidence_dir / "SHA256SUMS.txt", evidence_dir)
    sums = checksums(checksum_bytes, flat=True)
    required = set(EVIDENCE_NAMES) | {"sing-box.exe", "modified-source.zip", "go.mod", "go.sum", "upstream-source.zip", "upstream-go.mod", "upstream-go.sum"}
    if set(sums) != required or sums["sing-box.exe"] != digest(manifest["engine_sha256"]):
        raise ValueError("Build checksum inventory/engine identity differs")
    # Validate every checksum-bound artifact, even evidence not redistributed.
    for name, expected in sums.items():
        read(evidence_dir / name, evidence_dir, expected)
    evidence = {name: read(evidence_dir / name, evidence_dir, sums[name]) for name in EVIDENCE_NAMES}
    for name, data in evidence.items():
        if name not in ("Build-SecurityEngine.ps1", "reviewed-main-source-patch.json"):
            public_report(data)
    metadata = parse_json(evidence["build-metadata.json"])
    if metadata["engine_version"].removeprefix("v") != manifest["engine_version"].removeprefix("v") or digest(metadata["engine_sha256"]) != digest(manifest["engine_sha256"]) or metadata["engine_bytes"] != manifest["engine_bytes"]:
        raise ValueError("Build engine identity differs from the source manifest")
    engine = read(evidence_dir / "sing-box.exe", evidence_dir, manifest["engine_sha256"], manifest["engine_bytes"])
    for key, value in {"engine_executed": False, "windows_settings_changed": False,
                       "source_unit_tests_ran": True, "source_unit_tests_passed": True}.items():
        if metadata.get(key) is not value:
            raise ValueError("Build must contain successful source tests without engine execution/settings changes: " + key)
    if require_current and (metadata.get("engine_pin_verified") is not True or metadata.get("review_candidate") is not False):
        raise ValueError("Primary build must verify the final engine pin, not be a review candidate")
    recipe = manifest["build_recipe"]
    script = evidence["Build-SecurityEngine.ps1"]
    if digest(metadata["build_script_sha256"]) != sha(script) or git_bytes(repo, "show", evidence_commit + ":" + clean_name(recipe["script"])) != script:
        raise ValueError("Build recipe differs from checksum-bound metadata or its claimed Git commit")
    if require_current and script != payloads[recipe["script"]]:
        raise ValueError("Primary build did not use the exact current recipe bytes")
    build_pins = recipe_constants(script)
    for key, expected in current_pins.items():
        if not require_current and key in ("engineSha256", "engineBytes"):
            continue
        actual = build_pins[key]
        if key.endswith("Sha") or key.endswith("Sha256"):
            actual, expected = digest(actual), digest(expected)
        if actual != expected:
            raise ValueError("Build source/toolchain/overlay pin differs: " + key)
    _, main = manifest_identity(manifest)
    for key, value in {"go_version": manifest["go_version"], "main_source_commit": manifest["main_source_commit"],
                       "source_archive_url": main["source_url"], "source_archive_sha256": main["source_archive_sha256"],
                       "go_archive_url": current_pins["goUrl"], "go_archive_sha256": current_pins["goSha"],
                       "main_source_patch_mode": "hash-pinned-exact-file-overlay", "main_source_patch_sha256": recipe["patch_manifest"]["sha256"]}.items():
        actual = metadata[key]
        if key.endswith("sha256"):
            actual, value = digest(actual), digest(value)
        if actual != value:
            raise ValueError("Build provenance differs: " + key)
    if evidence["reviewed-main-source-patch.json"] != payloads[recipe["patch_manifest"]["path"]]:
        raise ValueError("Build reviewed overlay manifest differs")
    report = [item["source_relative"] + " | before=" + (item["before_sha256"] or "<added>") + " | after=" + item["after_sha256"] + " | bytes=" + str(item["bytes"]) for item in overlay]
    if public_report(evidence["patched-source-files.txt"]).splitlines() != report:
        raise ValueError("Build overlay report differs")
    if "github.com/sagernet/sing-box/constant.Version=" + manifest["engine_version"].removeprefix("v") not in metadata["linker_flags"]:
        raise ValueError("Build linker engine version differs")
    expected_files = dict(upstream)
    for name, key in (("go.mod", "go_mod"), ("go.sum", "go_sum")):
        data = payloads[recipe[key]]
        if read(evidence_dir / name, evidence_dir) != data or digest(metadata["modified_" + name.replace(".", "_") + "_sha256"]) != sha(data):
            raise ValueError("Build modified dependency lock differs")
        if read(evidence_dir / ("upstream-" + name), evidence_dir) != upstream[name] or digest(metadata["source_" + name.replace(".", "_") + "_sha256"]) != sha(upstream[name]):
            raise ValueError("Build upstream dependency lock differs")
        expected_files[name] = data
    read(evidence_dir / "upstream-source.zip", evidence_dir, main["source_archive_sha256"], main["source_archive_bytes"])
    for item in overlay:
        expected_files[item["source_relative"]] = payloads[item["payload_relative"]]
    modified = read(evidence_dir / "modified-source.zip", evidence_dir, metadata["modified_source_sha256"])
    actual_files = zip_inventory(modified, "source/")
    if actual_files != expected_files:
        raise ValueError("Build modified source differs from complete upstream + exact locks/overlay")
    changes = sorted(name for name in expected_files if name not in upstream or expected_files[name] != upstream[name])
    declared = sorted(name for name in ["go.mod", "go.sum"] + [item["source_relative"] for item in overlay]
                      if name not in upstream or expected_files[name] != upstream[name])
    if changes != declared:
        raise ValueError("Modified source changed files are not exactly the changed locks and declared overlay")
    modules, _ = manifest_identity(manifest)
    verify_build_reports(evidence, metadata, manifest, modules, overlay)
    evidence["SHA256SUMS.txt"] = checksum_bytes
    proof = {"source_commit": evidence_commit, "engine_sha256": sha(engine), "engine_bytes": len(engine),
             "recipe_sha256": sha(script), "metadata_sha256": sha(evidence["build-metadata.json"]),
             "checksum_evidence_sha256": sha(checksum_bytes), "source_unit_tests_ran": True,
             "source_unit_tests_passed": True, "engine_pin_verified": require_current,
             "verification_role": "Build execution and source-commit association supplied by the caller; files are checksum/source/Git-identity checked",
             "modified_source_sha256": sha(modified), "modified_source_bytes": len(modified), "changed_files": changes}
    return evidence, modified, proof


def native_source_url(value: str) -> None:
    parsed = urlsplit(value)
    if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password or parsed.fragment:
        raise ValueError("Native provenance URL contains credentials/unsupported fields")
    if parsed.query and not re.fullmatch(r"id=[a-f0-9]{40}", parsed.query):
        raise ValueError("Native provenance query must identify an exact public source commit")


def validate_native(repo: Path, manifest: dict, archives: SourceArchives, modules: dict[str, dict[str, bytes]],
                    engine: bytes, payloads: dict[str, bytes]) -> list[dict]:
    components = manifest.get("native_components", [])
    if not isinstance(components, list) or len(components) > 256:
        raise ValueError("Native inventory is not a bounded component list")
    expected_count = sum(len(item["notices"]) for item in components)
    if manifest.get("native_notice_count", 0) != expected_count or manifest.get("total_notice_count", manifest["notice_count"]) != manifest["notice_count"] + expected_count:
        raise ValueError("Native/total notice counts differ")
    if len({item["component_id"] for item in components}) != len(components):
        raise ValueError("Native component IDs are duplicated")
    # Native metadata cannot erase assets already present in verified sources.
    wintun_module = modules.get("github.com/sagernet/sing-tun", {})
    has_wintun = any(path.startswith("internal/wintun/") and path.lower().endswith("/wintun.dll") for path in wintun_module)
    wintun_records = [item for item in components if item["component_id"].startswith("wintun-") and item.get("module") == "github.com/sagernet/sing-tun"]
    if has_wintun and len(wintun_records) != 1:
        raise ValueError("Verified vendored Wintun assets require exactly one native/license inventory record")
    inventory = []
    aggregate = payloads["licenses/ENGINE-THIRD-PARTY-NOTICES.txt"].decode("utf-8-sig")
    markers = set()
    for component in components:
        identifier = component["component_id"]
        if not re.fullmatch(r"[A-Za-z0-9_.-]+", identifier):
            raise ValueError("Unsafe native component ID")
        source_url(component["source_url"])
        filename = clean_name(component["cache_filename"])
        if "/" in filename or not filename.endswith(".zip"):
            raise ValueError("Native cache filename must be a flat original ZIP name")
        name = "sources/native/" + filename
        data = archives.get(filename, name, component)
        original = zip_inventory(data, "")
        payloads[name] = data
        mod = component["module"]
        module_record = next((item for item in manifest["components"] if item["module"] == mod and "expected_go_h1" in item), None)
        if module_record is None or component["module_version"] != module_record["version"] or component["module_component_id"] != module_record["component_id"] or digest(component["module_archive_sha256"]) != digest(module_record["source_archive_sha256"]):
            raise ValueError("Native inventory does not identify its exact source module")
        module_files = modules[mod]
        files = component["files"]
        if not files or len(files) > 256 or len({item["archive_path"] for item in files}) != len(files):
            raise ValueError("Native file inventory is empty/duplicated/oversized")
        declared_module_paths = set()
        declared_architectures = set()
        for item in files:
            archive_path = clean_name(item["archive_path"])
            module_path = clean_name(item["module_archive_path"])
            if item["module"] != mod or archive_path not in original or module_path not in module_files:
                raise ValueError("Native original/module file is missing")
            content = original[archive_path]
            if sha(content) != digest(item["sha256"]) or len(content) != byte_count(item["bytes"]) or content != module_files[module_path]:
                raise ValueError("Native DLL does not match original archive/module bytes")
            if type(item["runtime_embedded"]) is not bool:
                raise ValueError("Native runtime embedding flag is malformed")
            architecture = item["architecture"]
            if architecture in declared_architectures:
                raise ValueError("Native architecture inventory is duplicated")
            declared_architectures.add(architecture)
            if identifier.startswith("wintun-"):
                if architecture not in {"amd64", "arm", "arm64", "x86"} or archive_path != "wintun/bin/" + architecture + "/wintun.dll" or module_path != "internal/wintun/" + architecture + "/wintun.dll":
                    raise ValueError("Wintun native architecture/path identity differs")
                target = {"386": "x86"}.get(manifest["build_settings"]["GOARCH"], manifest["build_settings"]["GOARCH"])
                expected_embedded = manifest["build_settings"]["GOOS"] == "windows" and architecture == target
                if item["runtime_embedded"] != expected_embedded:
                    raise ValueError("Wintun runtime embedding flag differs from the verified binary target")
            offset = item.get("runtime_embedded_offset")
            if item["runtime_embedded"]:
                if type(offset) is not int or offset < 0 or offset + len(content) > len(engine) or engine[offset:offset + len(content)] != content:
                    raise ValueError("Native runtime embedding offset/bytes differ")
            elif offset is not None:
                raise ValueError("Non-embedded native file has a runtime offset")
            declared_module_paths.add(module_path)
        actual_module_dlls = {path for path in module_files if path.startswith("internal/wintun/") and path.lower().endswith("/wintun.dll")}
        if component["component_id"].startswith("wintun-") and actual_module_dlls != declared_module_paths:
            raise ValueError("Native Wintun inventory omits an original vendored DLL")
        if identifier.startswith("wintun-") and declared_architectures != {"amd64", "arm", "arm64", "x86"}:
            raise ValueError("Original Wintun archive architecture inventory is incomplete")
        code_records = component.get("embedded_code_files", [])
        if len({item["archive_path"] for item in code_records}) != len(code_records):
            raise ValueError("Native wrapper copyright file inventory duplicates a path")
        for record in code_records:
            path = clean_name(record["archive_path"])
            content = module_files[path]
            if sha(content) != digest(record["sha256"]) or len(content) != byte_count(record["bytes"]) or record["license"] != "MIT" or b"SPDX-License-Identifier: MIT" not in content or record["copyright"].encode("utf-8") not in content:
                raise ValueError("Native MIT wrapper copyright/source identity differs")
        if identifier.startswith("wintun-"):
            all_headers = {path for path, content in module_files.items() if path.startswith("internal/wintun/") and path.endswith(".go") and b"SPDX-License-Identifier: MIT" in content}
            if all_headers != {item["archive_path"] for item in code_records}:
                raise ValueError("Native MIT notice inventory omits a retained wrapper/memmod header")
            notices = component["notices"]
            if len(notices) != 2 or sum(item["archive_path"] == "wintun/LICENSE.txt" for item in notices) != 1 or sum(item["archive_path"] is None for item in notices) != 1:
                raise ValueError("Wintun requires original prebuilt terms and a separate retained wrapper MIT notice")
        for notice in component["notices"]:
            path = clean_name(notice["repository_path"])
            if not path.startswith("licenses/") or not path.endswith(".txt"):
                raise ValueError("Native legal payload must be a text file under licenses/")
            content = read(repo / path, repo, notice["sha256"], notice["bytes"])
            native_source_url(notice["source_url"])
            archive_path = notice["archive_path"]
            if archive_path is not None:
                if original.get(clean_name(archive_path)) != content:
                    raise ValueError("Native notice does not equal its original binary archive notice")
            else:
                source_mod = notice["copyright_source_module"]
                source_record = next(item for item in manifest["components"] if item["module"] == source_mod and "expected_go_h1" in item)
                source_path = clean_name(notice["copyright_source_archive_path"])
                if notice["copyright_source_version"] != source_record["version"] or source_mod != mod:
                    raise ValueError("MIT copyright provenance source version differs")
                source_code = modules[source_mod][source_path]
                copyrights = {item["copyright"] for item in code_records}
                if len(copyrights) != 1:
                    raise ValueError("MIT copyright notice requires exact unambiguous source attribution")
                copyright_line = next(iter(copyrights))
                prefix = (copyright_line + "\n\n").encode("utf-8")
                if copyright_line.encode() not in source_code or not content.startswith(prefix):
                    raise ValueError("MIT notice does not retain its exact source copyright")
                body = content[len(prefix):]
                if sha(body) != digest(notice["source_sha256"]) or len(body) != byte_count(notice["source_bytes"]) or "id=" + commit_sha(notice["upstream_commit"]) != urlsplit(notice["source_url"]).query:
                    raise ValueError("MIT permission body differs from pinned authoritative source")
            decoded = content.decode("utf-8-sig").encode("utf-8")
            if sha(decoded) != digest(notice["aggregate_text_sha256"]) or len(decoded) != byte_count(notice["aggregate_text_bytes"]):
                raise ValueError("Native aggregate notice identity differs from source text")
            marker = notice["aggregate_marker"]
            if marker in markers:
                raise ValueError("Native notice marker is duplicated")
            markers.add(marker)
            expression = (r"^--- BEGIN NATIVE NOTICE " + re.escape(marker) + r" ---\nRepository path: " + re.escape(path) +
                          r"\nNotice file SHA256: " + re.escape(notice["sha256"]) + r"\n\n(.*?)\n--- END NATIVE NOTICE " + re.escape(marker) + r" ---$")
            blocks = re.findall(expression, aggregate, re.M | re.S)
            if len(blocks) != 1 or blocks[0].encode("utf-8") != decoded:
                raise ValueError("Native aggregate notice header/content differs")
            payloads[path] = content
        inventory.append({"component_id": identifier, "name": component["name"], "version": component["version"],
                          "package_path": name, "source_url": component["source_url"], "sha256": sha(data), "bytes": len(data),
                          "files": files, "embedded_code_files": code_records, "notices": component["notices"]})
    begins = re.findall(r"^--- BEGIN NATIVE NOTICE (.+) ---$", aggregate, re.M)
    ends = re.findall(r"^--- END NATIVE NOTICE (.+) ---$", aggregate, re.M)
    if len(begins) != expected_count or len(ends) != expected_count or set(begins) != markers or set(ends) != markers:
        raise ValueError("Native aggregate marker inventory differs")
    return inventory


def preserve_history(repo: Path, archives: SourceArchives, manifest: dict, payloads: dict[str, bytes],
                     upstream: dict[str, bytes], overlay: list[dict], pins: dict[str, str], release_commit: str) -> list[dict]:
    if archives.companion is None:
        return []
    prior = archives.fallback()
    pm = parse_json(prior.read("SOURCE_PACKAGE_MANIFEST.json"))
    if digest(pm["engine_sha256"]) != digest(manifest["engine_sha256"]):
        return []  # Archive reuse can be useful across engines without reusing their proof.
    # Existing source companions have already checked public, allowlisted
    # historical reports. Preserve the original bytes and declared role, never
    # relabel an old CI build as the successful current manual build.
    roots = []
    if pm.get("schema") == 2 and "candidate_build_evidence" in pm:
        proof = pm["candidate_build_evidence"]
        roots.append((proof["path"], proof, proof["candidate_source_commit"]))
    elif pm.get("schema") == 3:
        for proof in pm.get("historical_build_evidence", []):
            roots.append((proof["path"], proof, proof["source_commit"]))
    histories = []
    allowed = set(EVIDENCE_NAMES) | {"SHA256SUMS.txt", "CI-PROVENANCE.json", "BUILD-PROVENANCE.json"}
    for index, (prefix, proof, source_commit) in enumerate(roots):
        clean_name(prefix.rstrip("/")); evidence_ancestor(repo, source_commit, release_commit)
        reports = {}
        for name in prior.namelist():
            if not name.startswith(prefix) or name.endswith("/"):
                continue
            flat = name[len(prefix):]
            if flat not in allowed:
                raise ValueError("Prior historical evidence contains an unexpected report")
            reports[flat] = prior.read(name)
        if not set(EVIDENCE_NAMES).issubset(reports) or "SHA256SUMS.txt" not in reports:
            raise ValueError("Prior historical build evidence is incomplete")
        sums = checksums(reports["SHA256SUMS.txt"], flat=True)
        for name in EVIDENCE_NAMES:
            if sha(reports[name]) != sums[name]:
                raise ValueError("Prior historical report differs from original artifact checksums")
            if name not in ("Build-SecurityEngine.ps1", "reviewed-main-source-patch.json"):
                public_report(reports[name])
        metadata = parse_json(reports["build-metadata.json"])
        if digest(metadata["engine_sha256"]) != digest(manifest["engine_sha256"]) or digest(metadata["build_script_sha256"]) != sha(reports["Build-SecurityEngine.ps1"]) or git_bytes(repo, "show", source_commit + ":" + clean_name(manifest["build_recipe"]["script"])) != reports["Build-SecurityEngine.ps1"]:
            raise ValueError("Prior historical recipe/engine/Git identity differs")
        if reports["reviewed-main-source-patch.json"] != payloads[manifest["build_recipe"]["patch_manifest"]["path"]]:
            raise ValueError("Prior historical overlay differs from this engine source")
        old_pins = recipe_constants(reports["Build-SecurityEngine.ps1"])
        for key, value in pins.items():
            if key not in ("engineSha256", "engineBytes") and old_pins[key] != value:
                raise ValueError("Prior historical source/toolchain/overlay literal pins differ")
        verify_build_reports(reports, metadata, manifest, manifest_identity(manifest)[0], overlay)
        target = "evidence/historical-build-" + str(index + 1) + "/"
        for name, content in reports.items():
            payloads[target + name] = content
        histories.append({"path": target, "source_commit": source_commit,
                          "ci_run_url": proof.get("ci_run_url"), "kind": "historical",
                          "recipe_sha256": sha(reports["Build-SecurityEngine.ps1"]), "metadata_sha256": sha(reports["build-metadata.json"]),
                          "role": "Preserved original historical evidence; distinct from the primary current manual build"})
    return histories


def collect_payloads(repo: Path, build_evidence: Path, manifest_path: Path, version: str,
                     evidence_commit: str, release_commit: str, cache_dir: Path | None = None,
                     companion: Path | None = None) -> tuple[dict[str, bytes], dict]:
    repo = bounded_path(repo)
    release_version(version); commit_sha(release_commit)
    data = read(manifest_path, repo)
    manifest = parse_json(data)
    modules, main = manifest_identity(manifest)
    payloads = {"licenses/engine-source-manifest.json": data}
    pins = current_recipe(repo, manifest, payloads)
    for name in ("LICENSE", "THIRD_PARTY_NOTICES.txt", "licenses/ENGINE-THIRD-PARTY-NOTICES.txt"):
        payloads[name] = read(repo / name, repo)
    validate_full_gpl(payloads["THIRD_PARTY_NOTICES.txt"])
    validate_aggregate(payloads["licenses/ENGINE-THIRD-PARTY-NOTICES.txt"], manifest["components"])
    archives = SourceArchives(bounded_path(cache_dir) if cache_dir is not None else None,
                              bounded_path(companion) if companion is not None else None)
    try:
        upstream, inventory, module_files = source_payloads(repo, manifest, archives, payloads)
        _, patch_bytes, overlay = validate_overlay(repo, manifest["build_recipe"], manifest, upstream, payloads)
        evidence, modified, proof = validate_build(repo, build_evidence, manifest, payloads, upstream, overlay, pins,
                                                   evidence_commit, release_commit)
        engine = read(bounded_path(build_evidence) / "sing-box.exe", bounded_path(build_evidence), manifest["engine_sha256"], manifest["engine_bytes"])
        natives = validate_native(repo, manifest, archives, module_files, engine, payloads)
        historical = preserve_history(repo, archives, manifest, payloads, upstream, overlay, pins, release_commit)
    finally:
        archives.close()
    payloads["sources/main/sing-box-modified-source.zip"] = modified
    prefix = "evidence/current-build/"
    primary = {"path": prefix, "kind": "manual", **proof}
    for name, content in evidence.items():
        payloads[prefix + name] = content
    payloads[prefix + "BUILD-PROVENANCE.json"] = json_bytes(primary)
    # Preserve modification dates from source history rather than using the
    # date that license or packaging metadata was later corrected.
    modification_date = git_text(repo, "log", "-1", "--format=%cs", "--", manifest["build_recipe"]["patch_manifest"]["path"])
    if not re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}", modification_date):
        raise ValueError("Reviewed overlay modification date is unavailable from Git history")
    details = {"release_version": version, "source_commit": release_commit, "engine_version": manifest["engine_version"],
               "engine_sha256": digest(manifest["engine_sha256"]), "engine_bytes": manifest["engine_bytes"], "go_version": manifest["go_version"],
               "source_component_count": len(manifest["components"]), "module_count": len(modules),
               "verified_notice_count": manifest["notice_count"], "native_notice_count": manifest.get("native_notice_count", 0),
               "total_notice_count": manifest.get("total_notice_count", manifest["notice_count"]), "source_archives": inventory,
               "native_components": natives, "build_recipe": manifest["build_recipe"],
               "main_source_overlay": {"manifest_path": manifest["build_recipe"]["patch_manifest"]["path"],
                                       "manifest_sha256": sha(patch_bytes), "modification_date": modification_date, "files": overlay},
               "modified_source": {"path": "sources/main/sing-box-modified-source.zip", "sha256": sha(modified), "bytes": len(modified),
                                   "changed_files": proof["changed_files"], "role": "Complete successfully built main source, exact locks, overlay, tests and notice"},
               "current_build_evidence": primary, "historical_build_evidence": historical}
    return payloads, details
