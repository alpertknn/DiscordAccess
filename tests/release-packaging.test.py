"""Offline release-tool regressions; synthetic fixtures only, no PE execution."""
from __future__ import annotations

import base64
import hashlib
import importlib.util
import io
from pathlib import Path
import stat
import struct
import sys
import tarfile
import tempfile
import types
import unittest
from unittest.mock import patch
import zipfile

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "scripts"))
import ReleaseChecks as checks


def load(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, REPO / "scripts" / filename)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module


source = load("source_packager_test", "Package-EngineSource.py")
release = load("runtime_packager_test", "Package-Release.py")


def zip_bytes(files: dict[str, bytes]) -> bytes:
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as archive:
        for name, data in files.items():
            archive.writestr(name, data)
    return output.getvalue()


class Fixtures(unittest.TestCase):
    def setUp(self):
        work = checks.bounded_path(REPO / "work", REPO)
        work.mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="release-packaging-", dir=work)
        self.root = checks.bounded_path(Path(self.temporary.name), work)

    def tearDown(self):
        # Reconfirm our exact allocated fixture directory before recursive
        # TemporaryDirectory cleanup; no user files/reparse points are created.
        checks.bounded_path(self.root, REPO / "work")
        self.temporary.cleanup()

    def module(self):
        prefix = "example.org/module@v1.0.0/"
        files = {"LICENSE": b"Synthetic license\n", "main.go": b"package fixture\n"}
        digest_hash = hashlib.sha256()
        for name, data in sorted(files.items()):
            digest_hash.update((hashlib.sha256(data).hexdigest() + "  " + prefix + name + "\n").encode())
        h1 = "h1:" + base64.b64encode(digest_hash.digest()).decode()
        legal = files["LICENSE"]
        component = {"module": "example.org/module", "version": "v1.0.0", "expected_go_h1": h1, "verified_go_h1": h1,
                     "notices": [{"archive_path": "LICENSE", "sha256": checks.sha(legal), "bytes": len(legal),
                                  "aggregate_text_sha256": checks.sha(legal), "aggregate_text_bytes": len(legal)}]}
        return zip_bytes({prefix + name: data for name, data in files.items()}), component

    def native(self):
        module = "github.com/sagernet/sing-tun"
        copyright_line = "Copyright (C) 2017-2021 WireGuard LLC. All Rights Reserved."
        permission = b"Permission is hereby granted, free of charge.\nSynthetic MIT permission fixture.\n"
        mit = (copyright_line + "\n\n").encode() + permission
        binary_license = b"Prebuilt Binaries License\r\nSynthetic original terms.\r\n"
        code = ("/* SPDX-License-Identifier: MIT\n * " + copyright_line + "\n */\npackage wintun\n").encode()
        modfiles = {"internal/wintun/dll_windows_amd64.go": code}
        original = {"wintun/LICENSE.txt": binary_license}
        records = []
        prefix = b"MZengine"
        for architecture in ("amd64", "arm", "arm64", "x86"):
            data = ("synthetic signed native " + architecture).encode()
            archive_path = "wintun/bin/" + architecture + "/wintun.dll"
            modpath = "internal/wintun/" + architecture + "/wintun.dll"
            original[archive_path] = data; modfiles[modpath] = data
            records.append({"architecture": architecture, "archive_path": archive_path, "module": module,
                            "module_archive_path": modpath, "sha256": checks.sha(data), "bytes": len(data),
                            "runtime_embedded": architecture == "amd64", "runtime_embedded_offset": len(prefix) if architecture == "amd64" else None})
        notice_records = []
        payloads = {}
        for archive_path, path, content, marker in (("wintun/LICENSE.txt", "licenses/WINTUN.txt", binary_license, "native:binary"),
                                                   (None, "licenses/WIREGUARD-MIT.txt", mit, "native:mit")):
            p = self.root / path; p.parent.mkdir(exist_ok=True); p.write_bytes(content)
            item = {"archive_path": archive_path, "repository_path": path, "aggregate_marker": marker,
                    "sha256": checks.sha(content), "bytes": len(content), "aggregate_text_sha256": checks.sha(content),
                    "aggregate_text_bytes": len(content), "source_url": "https://native.example/wintun.zip"}
            if archive_path is None:
                item.update({"source_url": "https://native.example/LICENSE?id=" + "a" * 40, "upstream_commit": "a" * 40,
                             "source_sha256": checks.sha(permission), "source_bytes": len(permission),
                             "copyright_source_module": module, "copyright_source_version": "v1.0.0",
                             "copyright_source_archive_path": "internal/wintun/dll_windows_amd64.go"})
            notice_records.append(item)
        aggregate = ""
        for item in notice_records:
            text = (self.root / item["repository_path"]).read_bytes().decode()
            aggregate += "--- BEGIN NATIVE NOTICE " + item["aggregate_marker"] + " ---\nRepository path: " + item["repository_path"] + "\nNotice file SHA256: " + item["sha256"] + "\n\n" + text + "\n--- END NATIVE NOTICE " + item["aggregate_marker"] + " ---\n"
        payloads["licenses/ENGINE-THIRD-PARTY-NOTICES.txt"] = aggregate.encode()
        native = {"component_id": "wintun-0.14.1", "name": "Wintun fixture", "version": "0.14.1",
                  "source_url": "https://native.example/wintun.zip", "cache_filename": "wintun-0.14.1.zip",
                  "source_archive_sha256": checks.sha(zip_bytes(original)), "source_archive_bytes": len(zip_bytes(original)),
                  "module": module, "module_version": "v1.0.0", "module_component_id": "module-id", "module_archive_sha256": "b" * 64,
                  "files": records, "embedded_code_files": [{"archive_path": "internal/wintun/dll_windows_amd64.go",
                                                              "sha256": checks.sha(code), "bytes": len(code), "license": "MIT", "copyright": copyright_line}],
                  "notices": notice_records}
        manifest = {"components": [{"module": module, "version": "v1.0.0", "component_id": "module-id",
                                     "source_archive_sha256": "b" * 64, "expected_go_h1": "fixture"}],
                    "native_components": [native], "notice_count": 0, "native_notice_count": 2, "total_notice_count": 2,
                    "build_settings": {"GOOS": "windows", "GOARCH": "amd64"}}
        archives = types.SimpleNamespace(get=lambda *args: zip_bytes(original))
        engine = prefix + original["wintun/bin/amd64/wintun.dll"] + b"suffix"
        return manifest, archives, {module: modfiles}, engine, payloads

    def validate_native(self, fixture):
        return checks.validate_native(self.root, *fixture)

    def test_traversal_and_windows_extraction_names(self):
        for value in ("../secret", "a/../b", "/absolute", "a\\b", "C:/file", "a//b", "CON.txt", "a.", "a ", "a\0b"):
            with self.subTest(value=value), self.assertRaises(ValueError): checks.clean_name(value)

    def test_case_colliding_archive(self):
        with zipfile.ZipFile(io.BytesIO(zip_bytes({"source/A": b"a", "source/a": b"b"}))) as archive:
            with self.assertRaises(ValueError): checks.checked_zip_entries(archive, "source/")

    def test_zip_prefix_and_size_bound(self):
        with zipfile.ZipFile(io.BytesIO(zip_bytes({"other/file": b"hello"}))) as archive:
            with self.assertRaises(ValueError): checks.checked_zip_entries(archive, "source/")
        with patch.object(checks, "MAX_FILE", 2), zipfile.ZipFile(io.BytesIO(zip_bytes({"source/file": b"hello"}))) as archive:
            with self.assertRaises(ValueError): checks.checked_zip_entries(archive, "source/")

    def test_encrypted_entry_rejected(self):
        item = zipfile.ZipInfo("file"); item.flag_bits = 1
        with self.assertRaises(ValueError): checks.checked_zip_entries(types.SimpleNamespace(infolist=lambda: [item]), "")

    def test_json_duplicate_keys_rejected(self):
        with self.assertRaises(ValueError): checks.parse_json(b'{"a":1,"a":2}')

    def test_checksum_duplicate_case_and_nested_rejected(self):
        for data in (("a" * 64 + "  A\n" + "b" * 64 + "  a\n").encode(), ("a" * 64 + "  x/y\n").encode()):
            with self.assertRaises(ValueError): checks.checksums(data, flat=True)

    def test_local_path_boundary(self):
        with self.assertRaises(ValueError): checks.bounded_path(self.root.parent / "outside", self.root)

    def test_reparse_rejected_without_creating_link(self):
        info = types.SimpleNamespace(st_mode=stat.S_IFDIR, st_file_attributes=0x400)
        with patch.object(Path, "lstat", return_value=info), self.assertRaises(ValueError): checks.bounded_path(self.root)

    def test_private_report_paths_and_nul_rejected(self):
        for text in (b'C:\\Users\\Private\\report', b'/home/private/report', b'abc\x00def'):
            with self.assertRaises(ValueError): checks.public_report(text)

    def test_module_h1_valid(self):
        data, component = self.module(); checks.verify_module(data, component)

    def test_module_h1_changed_code_rejected(self):
        data, component = self.module()
        with zipfile.ZipFile(io.BytesIO(data)) as archive: files = {name: archive.read(name) for name in archive.namelist()}
        files["example.org/module@v1.0.0/main.go"] += b"changed\n"
        with self.assertRaises(ValueError): checks.verify_module(zip_bytes(files), component)

    def test_undeclared_legal_notice_rejected(self):
        with self.assertRaises(ValueError): checks.verify_notices({"LICENSE": b"x", "NOTICE": b"y"}, {"notices": []})

    def test_go_tar_link_rejected(self):
        data = io.BytesIO()
        with tarfile.open(fileobj=data, mode="w:gz") as archive:
            item = tarfile.TarInfo("go/link"); item.type = tarfile.SYMTYPE; item.linkname = "../../private"; archive.addfile(item)
        with self.assertRaises(ValueError): checks.verify_go(data.getvalue(), {"notices": []})

    def test_native_valid_original_and_mit_notices(self):
        result = self.validate_native(self.native()); self.assertEqual(len(result), 1)

    def test_native_inventory_cannot_be_erased(self):
        fixture = self.native(); fixture[0]["native_components"] = []; fixture[0]["native_notice_count"] = fixture[0]["total_notice_count"] = 0
        fixture[4]["licenses/ENGINE-THIRD-PARTY-NOTICES.txt"] = b""
        with self.assertRaises(ValueError): self.validate_native(fixture)

    def test_native_mit_notice_cannot_be_omitted(self):
        fixture = self.native(); fixture[0]["native_components"][0]["notices"].pop(); fixture[0]["native_notice_count"] = fixture[0]["total_notice_count"] = 1
        with self.assertRaises(ValueError): self.validate_native(fixture)

    def test_native_false_runtime_flags_rejected(self):
        fixture = self.native()
        for item in fixture[0]["native_components"][0]["files"]: item["runtime_embedded"] = False; item["runtime_embedded_offset"] = None
        with self.assertRaises(ValueError): self.validate_native(fixture)

    def test_native_architecture_mismatch_rejected(self):
        fixture = self.native(); fixture[0]["native_components"][0]["files"][0]["architecture"] = "arm64"
        with self.assertRaises(ValueError): self.validate_native(fixture)

    def test_native_module_bytes_mismatch_rejected(self):
        fixture = self.native(); fixture[2]["github.com/sagernet/sing-tun"]["internal/wintun/amd64/wintun.dll"] += b"tampered"
        with self.assertRaises(ValueError): self.validate_native(fixture)

    def test_native_runtime_offset_mismatch_rejected(self):
        fixture = self.native(); fixture[0]["native_components"][0]["files"][0]["runtime_embedded_offset"] += 1
        with self.assertRaises(ValueError): self.validate_native(fixture)

    def test_native_aggregate_notice_drift_rejected(self):
        fixture = self.native(); fixture[4]["licenses/ENGINE-THIRD-PARTY-NOTICES.txt"] = fixture[4]["licenses/ENGINE-THIRD-PARTY-NOTICES.txt"].replace(b"original terms", b"altered terms")
        with self.assertRaises(ValueError): self.validate_native(fixture)

    def test_native_mit_copyright_drift_rejected(self):
        fixture = self.native(); fixture[0]["native_components"][0]["embedded_code_files"][0]["copyright"] = "different copyright"
        with self.assertRaises(ValueError): self.validate_native(fixture)

    def test_final_version_mapping_and_bounds(self):
        self.assertEqual(checks.release_version("4.21"), ("4.21", "4.21.0.0"))
        self.assertEqual(checks.release_version("4.21.1"), ("4.21.1", "4.21.1.0"))
        for version in ("4.21-rc1", "04.21", "4.65536", "4.21.0.0"):
            with self.assertRaises(ValueError): checks.release_version(version)

    def test_pe_amd64_header_validation_is_inert(self):
        image = bytearray(512); image[:2] = b"MZ"; struct.pack_into("<I", image, 0x3c, 128); image[128:132] = b"PE\0\0"
        struct.pack_into("<H", image, 132, 0x8664); struct.pack_into("<H", image, 152, 0x20b)
        release.native_amd64(bytes(image)); struct.pack_into("<H", image, 132, 0x14c)
        with self.assertRaises(ValueError): release.native_amd64(bytes(image))

    def test_runtime_exact_five_and_reproducible(self):
        contents = {name: name.encode() for name in ("DiscordAccess.exe", "engine/sing-box.exe", "OKU-BENI.txt", "LISANSLAR.txt", "KAYNAK.txt")}
        a = release.runtime_archive("DiscordAccess-v4.21", contents)
        self.assertEqual(a, release.runtime_archive("DiscordAccess-v4.21", contents))
        with zipfile.ZipFile(io.BytesIO(a)) as archive: self.assertEqual(len(archive.namelist()), 5); self.assertIsNone(archive.testzip())
        contents["tests.exe"] = b"not allowed"
        with self.assertRaises(ValueError): release.runtime_archive("DiscordAccess-v4.21", contents)

    def test_source_archive_stable_crc_and_payloads(self):
        contents = {"README-SOURCE.md": b"fixture\n", "sources/native/original.zip": zip_bytes({"LICENSE": b"original"})}
        a = source.archive_bytes(contents); self.assertEqual(a, source.archive_bytes(contents))
        with self.assertRaises(ValueError): source.verify_zip(a, {"different": b"fixture"})

    def test_source_output_never_overwrites(self):
        output = self.root / "source.zip"; output.write_bytes(b"old")
        with self.assertRaises(ValueError): source.write_new(output, b"new")
        self.assertEqual(output.read_bytes(), b"old")

    def test_release_four_assets_and_existing_validation(self):
        out = self.root / "release"; receipt = self.root / "receipt.json"
        assets = {"DiscordAccess-v4.21.zip": b"runtime", "DiscordAccess-v4.21-engine-source.zip": b"source", "engine-source-manifest.json": b"{}", "SHA256SUMS.txt": b"sums"}
        release.write_assets(out, assets, receipt, b"{}\n")
        self.assertEqual(len(list(out.iterdir())), 4)
        self.assertTrue(release.existing_assets(out, assets, receipt, b"{}\n"))
        with self.assertRaises(ValueError): release.write_assets(out, assets, receipt, b"{}\n")
        (out / "SHA256SUMS.txt").write_bytes(b"tampered")
        with self.assertRaises(ValueError): release.existing_assets(out, assets, receipt, b"{}\n")

    def test_build_reports_cannot_skip_source_units(self):
        package = "github.com/sagernet/sing-box/internal/fixture"
        module = {"module": "example.org/module", "version": "v1.0.0", "expected_go_h1": "h1:fixture"}
        overlay = [{"source_relative": "internal/fixture/source.go"}, {"source_relative": "internal/fixture/source_test.go"}]
        manifest = {"go_version": "go1.26.9", "build_settings": {"-tags": "fixture"}}
        metadata = {"build_tags": "fixture", "build_settings": {"trimpath": True, "buildvcs": False, "GOTOOLCHAIN": "local",
                    "GOOS": "windows", "GOARCH": "amd64", "GOAMD64": "v1", "CGO_ENABLED": "0"},
                    "selected_modules": {"example.org/module": "v1.0.0"}, "source_unit_tests_ran": True, "source_unit_tests_passed": True}
        evidence = {"go-version-m.txt": b"sing-box.exe: go1.26.9\n\tpath\tgithub.com/sagernet/sing-box/cmd/sing-box\n\tdep\texample.org/module\tv1.0.0\th1:fixture\n\tbuild\t-tags=fixture\n",
                    "selected-modules.txt": b"github.com/sagernet/sing-box\nexample.org/module v1.0.0\n",
                    "compiled-packages.txt": ("github.com/sagernet/sing-box/cmd/sing-box\n" + package + "\n").encode(),
                    "source-unit-tests.txt": ("ok\t" + package + "\t0.001s\n").encode()}
        checks.verify_build_reports(evidence, metadata, manifest, [module], overlay)
        for ran, passed in ((False, None), (True, False), (1, 1)):
            metadata.update(source_unit_tests_ran=ran, source_unit_tests_passed=passed)
            with self.assertRaises(ValueError): checks.verify_build_reports(evidence, metadata, manifest, [module], overlay)
        metadata.update(source_unit_tests_ran=True, source_unit_tests_passed=True)
        for units in (b"source checks skipped\n", b"FAIL package\n", ("ok\t" + package + "\t0.001s\npanic: fixture\n").encode()):
            evidence["source-unit-tests.txt"] = units
            with self.assertRaises(ValueError): checks.verify_build_reports(evidence, metadata, manifest, [module], overlay)


if __name__ == "__main__":
    unittest.main(verbosity=2)
