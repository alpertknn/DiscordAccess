"""Collect immutable dependency notices; never execute dependencies or source."""
from __future__ import annotations
import argparse, base64, concurrent.futures, hashlib, io, json, os, re, struct, tarfile, time, urllib.error, urllib.request, zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / 'licenses'
CACHE = REPO / 'work' / 'license-source-cache'
EXPECTED_EXE = None
OFFLINE = False
TAG = 'v1.14.3-discordaccess.2'
COMMIT = '7054cac5124465ffdb145870d7c01885566711b1'
SOURCE_SHA256 = 'cd8a3508f36d74ef7c9c515bdc8586f048fd06c22499d38ee3787faa7d220f74'
WINTUN_URL = 'https://www.wintun.net/builds/wintun-0.14.1.zip'
WINTUN_SHA256 = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'
WINTUN_BYTES = 750540
WINTUN_LICENSE_SHA256 = '183adac21e7d96c508c8fd34d394b7b6708bc81564ad1bad61ab66143a008cd2'
WIREGUARD_LICENSE_COMMIT = '0fa3db229ce2036ae779de8ba03d336b7aa826bd'
WIREGUARD_LICENSE_URL = 'https://git.zx2c4.com/wintun-go/plain/LICENSE?id=' + WIREGUARD_LICENSE_COMMIT
WIREGUARD_LICENSE_SHA256 = '91276db973f25602d1aa43491f59cbc84cb88e6f151e1d0cc82a755563ce0195'
WIREGUARD_COPYRIGHT = 'Copyright (C) 2017-2021 WireGuard LLC. All Rights Reserved.'
WINTUN_FILES = {
    'amd64': (427552, 'e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce'),
    'arm': (364552, 'daad267411ecdc70a0535e274d2c3e9da3d0084bdac7662cb8424dd4a031b4d9'),
    'arm64': (222488, 'f7ba89005544be9d85231a9e0d5f23b2d15b3311667e2dad0debd344918a3f80'),
    'x86': (550928, 'd694fa46ab4cfebcb2632d094c7aa97278eef2f8052438621766d863ae98a931'),
}
LEGAL = re.compile(r'^(?:licen[cs]e|copying|notice|copyright|authors|patents)(?:[._-].*)?$', re.I)
CODE_EXT = {'.go', '.c', '.cc', '.cpp', '.h', '.cs', '.py', '.js', '.ts', '.exe', '.dll', '.sys', '.png', '.jpg', '.gif', '.woff', '.woff2'}

def sha(data):
    return hashlib.sha256(data).hexdigest()

def checked_path(path):
    resolved = path.resolve()
    for root in (REPO, CACHE):
        if resolved.is_relative_to(root.resolve()):
            for ancestor in (path, *path.parents):
                if ancestor == root.parent:
                    break
                if ancestor.is_symlink():
                    raise ValueError('Collection output contains symbolic link')
            return path
    raise ValueError('Collection output escaped repository or explicit source cache')

def download(url, cache_name):
    dest = checked_path(CACHE / cache_name)
    if dest.exists():
        return dest.read_bytes()
    if OFFLINE:
        raise RuntimeError('Source is not cached for offline collection: ' + cache_name)
    last = None
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={'User-Agent': 'DiscordAccess-Dependency-Notice-Collector/1'})
            with urllib.request.urlopen(request, timeout=45) as response:
                data = response.read(160 * 1024 * 1024 + 1)
            if len(data) > 160 * 1024 * 1024:
                raise ValueError('Source archive exceeds bounded download limit')
            temp = checked_path(dest.with_suffix(dest.suffix + '.tmp'))
            temp.write_bytes(data)
            os.replace(temp, dest)
            return data
        except Exception as ex:
            last = ex
            if isinstance(ex, urllib.error.HTTPError) and ex.code in (404, 410):
                break
            time.sleep(attempt + 1)
    raise RuntimeError(f'download failed: {url}: {type(last).__name__}')

def escaped(value):
    return ''.join('!' + c.lower() if 'A' <= c <= 'Z' else c for c in value)

def legal_path(relative):
    path = Path(relative)
    if path.suffix.lower() in CODE_EXT:
        return False
    parts = relative.split('/')
    return bool(LEGAL.fullmatch(parts[-1]) or parts[-1].lower().endswith('.license') or
                any(part.lower() in ('license', 'licenses', 'licence', 'licences') for part in parts[:-1]))

def notice_record(component_id, relative, data):
    if len(data) > 4 * 1024 * 1024:
        raise ValueError('Notice file exceeds size limit')
    # Preserve original bytes. Text is decoded solely to compose the aggregate.
    text = data.decode('utf-8-sig')
    if '\x00' in text:
        raise ValueError('Notice candidate contains binary bytes')
    digest = sha(data)
    key = sha(relative.encode('utf-8'))[:16]
    destination = checked_path(CACHE / 'notice-texts' / component_id / f'{key}.txt')
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(data)
    return {'archive_path': relative, 'aggregate_marker': component_id + ':' + key,
            'sha256': digest, 'bytes': len(data), 'aggregate_text_sha256': sha(text.encode('utf-8')),
            'aggregate_text_bytes': len(text.encode('utf-8')), '_text': text}

def varint(data, offset):
    value, shift = 0, 0
    for _ in range(10):
        b = data[offset]; offset += 1
        value |= (b & 127) << shift
        if b < 128:
            return value, offset
        shift += 7
    raise ValueError('Bad buildinfo length')

def build_info(data):
    blocks = []
    offset = 0
    while True:
        offset = data.find(b'\xff Go buildinf:', offset)
        if offset < 0:
            break
        start = offset; offset += 1
        if start % 16 or len(data) < start + 32 or not data[start + 15] & 2:
            continue
        try:
            pos = start + 32
            length, pos = varint(data, pos); version = data[pos:pos + length].decode(); pos += length
            length, pos = varint(data, pos); module_bytes = data[pos:pos + length]
            if not re.fullmatch(r'go\d+\.\d+\.\d+', version):
                continue
            if len(module_bytes) >= 33 and module_bytes[-17] == 10:
                module_bytes = module_bytes[16:-16]
            module_text = module_bytes.decode('utf-8')
            if 'path\tgithub.com/sagernet/sing-box/cmd/sing-box\n' in module_text:
                blocks.append((version, module_text))
        except (ValueError, UnicodeError, IndexError):
            pass
    if not blocks:
        raise ValueError('No authentic inline Go buildinfo located')
    if len(set(blocks)) != 1:
        raise ValueError('Conflicting sing-box buildinfo blocks')
    version, text = blocks[0]
    mods = []
    for module, mod_version, expected in re.findall(r'^dep\t([^\t\r\n ]+)\t(v[^\t\r\n ]+)\t(h1:[A-Za-z0-9+/=]+)$', text, re.M):
        mods.append({'module': module, 'version': mod_version, 'expected_go_h1': expected})
    mods = sorted({(m['module'], m['version'], m['expected_go_h1']): m for m in mods}.values(), key=lambda m: (m['module'], m['version']))
    if not mods or not re.search(r'^mod\tgithub.com/sagernet/sing-box\t(?:\(devel\)|v1\.14\.3)(?:\t|\n)', text, re.M):
        raise ValueError('Unexpected engine module inventory')
    if re.search(r'^=>\t', text, re.M):
        raise ValueError('Replacement module requires explicit source attribution support')
    settings = dict(re.findall(r'^build\t([^=\r\n]+)=(.*)$', text, re.M))
    return version, settings, mods

def collect_module(item):
    module, version, expected = item['module'], item['version'], item['expected_go_h1']
    component_id = sha((module + '@' + version).encode())[:24]
    url = f'https://proxy.golang.org/{escaped(module)}/@v/{escaped(version)}.zip'
    result = {**item, 'component_id': component_id, 'source_url': url, 'notices': [], 'errors': []}
    try:
        raw = download(url, component_id + '.zip')
        result['source_archive_sha256'] = sha(raw)
        result['source_archive_bytes'] = len(raw)
        with zipfile.ZipFile(io.BytesIO(raw)) as archive:
            files = archive.infolist()
            prefix = module + '@' + version + '/'
            names = [entry.filename for entry in files]
            if len(names) != len(set(names)) or len(names) > 100000 or sum(e.file_size for e in files) > 512 * 1024 * 1024:
                raise ValueError('Invalid or oversized module archive inventory')
            digest = hashlib.sha256()
            selected = []
            for entry in sorted(files, key=lambda entry: entry.filename.encode('utf-8')):
                name = entry.filename
                if not name.startswith(prefix) or '\n' in name or '..' in name.split('/') or '\\' in name:
                    raise ValueError('Unexpected archive path')
                with archive.open(entry) as stream:
                    file_sha = hashlib.file_digest(stream, 'sha256').hexdigest()
                digest.update((file_sha + '  ' + name + '\n').encode())
                relative = name[len(prefix):]
                if not entry.is_dir() and legal_path(relative):
                    selected.append((relative, entry))
            actual = 'h1:' + base64.b64encode(digest.digest()).decode()
            result['verified_go_h1'] = actual
            if actual != expected:
                raise ValueError('Module content does not match executable Go h1')
            for relative, entry in selected:
                try:
                    result['notices'].append(notice_record(component_id, relative, archive.read(entry)))
                except Exception as ex:
                    result['errors'].append(f'{relative}: {type(ex).__name__}')
            if not result['notices']:
                result['errors'].append('No LICENSE/NOTICE/COPYING/COPYRIGHT/AUTHORS/PATENTS text found in verified module archive')
    except Exception as ex:
        result['errors'].append(str(ex))
    return result

def collect_main():
    component_id = 'sing-box-v1.14.3'
    url = f'https://github.com/SagerNet/sing-box/archive/{COMMIT}.zip'
    raw = download(url, component_id + '.zip')
    if sha(raw) != SOURCE_SHA256:
        raise ValueError('Main source archive differs from the pinned builder source')
    result = {'module': 'github.com/sagernet/sing-box', 'version': TAG, 'commit': COMMIT,
              'component_id': component_id, 'source_url': url, 'source_archive_sha256': sha(raw),
              'source_archive_bytes': len(raw), 'notices': [], 'errors': []}
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        for entry in archive.infolist():
            if entry.is_dir():
                continue
            relative = entry.filename.split('/', 1)[1]
            if legal_path(relative):
                result['notices'].append(notice_record(component_id, relative, archive.read(entry)))
    return result

def collect_go(version):
    filename = version + '.src.tar.gz'
    releases = json.loads(download('https://go.dev/dl/?mode=json&include=all', 'go-release-index-' + version + '.json'))
    matching = [file for release in releases if release['version'] == version for file in release['files'] if file['filename'] == filename]
    if len(matching) != 1:
        raise ValueError('Exact Go source release is not listed officially')
    expected = matching[0]['sha256']
    url = 'https://go.dev/dl/' + filename
    raw = download(url, filename)
    if sha(raw) != expected:
        raise ValueError('Go source archive differs from official SHA256')
    result = {'module': 'Go standard library/runtime', 'version': version, 'component_id': version,
              'source_url': url, 'source_archive_sha256': expected, 'source_archive_bytes': len(raw), 'notices': [], 'errors': []}
    with tarfile.open(fileobj=io.BytesIO(raw), mode='r:gz') as archive:
        for entry in archive.getmembers():
            if not entry.isfile():
                continue
            relative = entry.name.removeprefix('go/')
            if legal_path(relative):
                stream = archive.extractfile(entry)
                result['notices'].append(notice_record(version, relative, stream.read()))
    return result

def collect_native(results, engine, settings):
    """Bind the separately licensed DLL and Go loader to verified source bytes."""
    matches = [result for result in results if result['module'] == 'github.com/sagernet/sing-tun']
    if len(matches) != 1 or matches[0]['errors']:
        raise ValueError('Native attribution requires the verified sing-tun module')
    module = matches[0]
    raw_module = download(module['source_url'], module['component_id'] + '.zip')
    if sha(raw_module) != module['source_archive_sha256']:
        raise ValueError('Native attribution module archive changed after Go h1 verification')
    raw_native = download(WINTUN_URL, 'wintun-0.14.1.zip')
    if len(raw_native) != WINTUN_BYTES or sha(raw_native) != WINTUN_SHA256:
        raise ValueError('Wintun archive differs from the official pinned release')
    permission = download(WIREGUARD_LICENSE_URL, 'wireguard-wintun-go-0fa3db229ce2-LICENSE.txt')
    if len(permission) != 1023 or sha(permission) != WIREGUARD_LICENSE_SHA256:
        raise ValueError('WireGuard MIT permission text differs from the pinned official source')
    if settings.get('GOOS') != 'windows' or settings.get('GOARCH') not in ('amd64', 'arm', 'arm64', '386'):
        raise ValueError('Native attribution expects the supported Windows engine architecture')
    runtime_architecture = 'x86' if settings['GOARCH'] == '386' else settings['GOARCH']
    prefix = module['module'] + '@' + module['version'] + '/'
    native_files, code_files = [], []
    with zipfile.ZipFile(io.BytesIO(raw_native)) as native, zipfile.ZipFile(io.BytesIO(raw_module)) as source:
        if len(native.namelist()) != len(set(native.namelist())):
            raise ValueError('Duplicate paths in official native archive')
        binary_license = native.read('wintun/LICENSE.txt')
        if len(binary_license) != 5431 or sha(binary_license) != WINTUN_LICENSE_SHA256:
            raise ValueError('Wintun binary terms differ from the official archive license')
        if b'DLL version: 0.14.1' not in source.read(prefix + 'internal/wintun/README.md'):
            raise ValueError('Vendored Wintun version differs from the pinned native release')
        for architecture, (expected_bytes, expected_sha256) in WINTUN_FILES.items():
            archive_path = 'wintun/bin/' + architecture + '/wintun.dll'
            module_path = 'internal/wintun/' + architecture + '/wintun.dll'
            dll = native.read(archive_path)
            if len(dll) != expected_bytes or sha(dll) != expected_sha256 or source.read(prefix + module_path) != dll:
                raise ValueError('Vendored DLL differs from official Wintun release: ' + architecture)
            embedded = architecture == runtime_architecture
            offset = engine.find(dll) if embedded else None
            if embedded:
                embed_path = 'internal/wintun/dll_windows_' + settings['GOARCH'] + '.go'
                if ('//go:embed ' + architecture + '/wintun.dll').encode() not in source.read(prefix + embed_path):
                    raise ValueError('Wintun embedding declaration differs from the reviewed architecture')
                if offset < 0 or engine.find(dll, offset + 1) >= 0:
                    raise ValueError('Official runtime Wintun DLL was not uniquely located in the engine')
            native_files.append({'architecture': architecture, 'archive_path': archive_path,
                                 'module': module['module'], 'module_archive_path': module_path,
                                 'sha256': expected_sha256, 'bytes': expected_bytes,
                                 'runtime_embedded': embedded, 'runtime_embedded_offset': offset})
        for entry in sorted(source.infolist(), key=lambda item: item.filename):
            relative = entry.filename.removeprefix(prefix)
            if not relative.startswith('internal/wintun/') or not relative.endswith('.go'):
                continue
            code = source.read(entry)
            if b'SPDX-License-Identifier: MIT' not in code:
                continue
            if WIREGUARD_COPYRIGHT.encode() not in code:
                raise ValueError('MIT loader copyright requires review: ' + relative)
            code_files.append({'archive_path': relative, 'sha256': sha(code), 'bytes': len(code),
                               'license': 'MIT', 'copyright': WIREGUARD_COPYRIGHT})
    if len(code_files) != 17:
        raise ValueError('The pinned MIT loader/memory-module source inventory changed')
    loader_license = (WIREGUARD_COPYRIGHT + '\n\n').encode() + permission
    license_payloads = [
        ('licenses/WINTUN-PREBUILT-BINARIES-LICENSE.txt', 'wintun/LICENSE.txt', binary_license, WINTUN_URL),
        ('licenses/WIREGUARD-MIT.txt', None, loader_license, WIREGUARD_LICENSE_URL),
    ]
    notices = []
    for repository_path, archive_path, payload, source_url in license_payloads:
        checked_path(REPO / repository_path).write_bytes(payload)
        notice = notice_record('wintun-0.14.1', archive_path or repository_path, payload)
        notice.update({'repository_path': repository_path, 'archive_path': archive_path, 'source_url': source_url})
        if archive_path is None:
            notice.update({'source_sha256': WIREGUARD_LICENSE_SHA256, 'source_bytes': len(permission),
                           'upstream_commit': WIREGUARD_LICENSE_COMMIT,
                           'copyright_source_module': module['module'], 'copyright_source_version': module['version'],
                           'copyright_source_archive_path': 'internal/wintun/dll_windows_amd64.go',
                           'composition': 'Exact retained WireGuard copyright, two LF bytes, then the unchanged official MIT permission text.'})
        notices.append(notice)
    return [{'component_id': 'wintun-0.14.1', 'name': 'Wintun prebuilt DLLs and WireGuard Go loader notices',
             'version': '0.14.1', 'source_url': WINTUN_URL, 'source_archive_sha256': WINTUN_SHA256,
             'source_archive_bytes': len(raw_native), 'cache_filename': 'wintun-0.14.1.zip',
             'module': module['module'], 'module_version': module['version'],
             'module_component_id': module['component_id'], 'module_archive_sha256': sha(raw_module),
             'files': native_files, 'embedded_code_files': code_files, 'notices': notices,
             'scope': 'All four DLLs are preserved in the corresponding sing-tun source archive; only the engine architecture is embedded in this runtime. The Go loader/memory-module MIT notice is separate from the prebuilt DLL terms.'}]

def main():
    global EXPECTED_EXE, CACHE, OFFLINE
    parser = argparse.ArgumentParser()
    parser.add_argument('--engine', required=True)
    parser.add_argument('--expected-sha256', required=True)
    parser.add_argument('--cache-dir', type=Path, help='Source cache directory; defaults to work/license-source-cache in this checkout')
    parser.add_argument('--offline', action='store_true', help='Require every pinned source and license to exist in the source cache')
    args = parser.parse_args()
    if args.cache_dir is not None:
        CACHE = args.cache_dir.resolve()
    OFFLINE = args.offline
    if not re.fullmatch(r'[0-9a-fA-F]{64}', args.expected_sha256):
        raise ValueError('A reviewed engine SHA256 is required')
    EXPECTED_EXE = args.expected_sha256.lower()
    data = Path(args.engine).read_bytes()
    if sha(data) != EXPECTED_EXE:
        raise ValueError('Engine differs from the reviewed build hash')
    go_version, settings, modules = build_info(data)
    for folder in (OUT, CACHE):
        checked_path(folder).mkdir(parents=True, exist_ok=True)
    print(f'Exact verified binary inventory: {len(modules)} modules; runtime {go_version}', flush=True)
    results = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        futures = {pool.submit(collect_module, module): module for module in modules}
        for count, future in enumerate(concurrent.futures.as_completed(futures), 1):
            result = future.result(); results.append(result)
            if result['errors']:
                print('REVIEW ' + result['module'] + '@' + result['version'] + ': ' + '; '.join(result['errors']), flush=True)
            elif count % 10 == 0 or count == len(modules):
                print(f'Module notices verified {count}/{len(modules)}', flush=True)
    results.sort(key=lambda item: (item['module'], item['version']))
    for collect, argument in ((collect_main, None), (collect_go, go_version)):
        try:
            results.append(collect() if argument is None else collect(argument))
        except Exception as ex:
            results.append({'module': 'sing-box source' if argument is None else 'Go runtime', 'version': TAG if argument is None else go_version,
                            'notices': [], 'errors': [str(ex)]})
    native_components = collect_native(results, data, settings)
    patch_path = 'engine-patches/http-limits/manifest.json'
    patch_bytes = (REPO / patch_path).read_bytes()
    patch = json.loads(patch_bytes)
    if (patch.get('schema') != 1 or patch.get('engine_version') != TAG.removeprefix('v') or
            patch.get('main_source_commit') != COMMIT or patch.get('source_archive_sha256') != SOURCE_SHA256):
        raise ValueError('Reviewed main-source overlay identity does not match this engine')
    for file in patch['files']:
        payload = checked_path(REPO / file['payload_relative']).read_bytes()
        if len(payload) != file['bytes'] or sha(payload) != file['after_sha256']:
            raise ValueError('Reviewed source overlay payload changed')
    overlay_notice = (REPO / 'engine-patches/http-limits/source/protocol/http/DISCORDACCESS-NOTICE').read_text(encoding='utf-8')
    aggregate = ['sing-box 1.14.3-discordaccess.2 — dependency notices for the DiscordAccess build', '',
                 'Collected from pinned source archives matching the distributed engine build information.',
                 'Original notice contents are reproduced below; archive paths identify their scope.',
                 'Including source/test/platform notices is conservative and does not imply that every file is linked or used.',
                 'The JSON manifest records source URLs, versions, SHA256, and verified Go h1 content hashes.',
                 'Separately licensed Wintun DLLs and WireGuard Go loader notices are recorded in native_components.',
                 'Hash-pinned main-source HTTP limits, dependency locks and build instructions accompany this build.',
                 'No source, dependency, build script, driver or program was executed during notice collection.', '',
                 'Reviewed main-source modifications notice (provided by the overlay, not an upstream archive file):',
                 overlay_notice, '']
    count = 0
    for result in results:
        aggregate += ['=' * 78, result['module'] + ' ' + result['version'], 'Source: ' + result.get('source_url', 'UNRESOLVED'), '']
        if result['errors']:
            aggregate += ['UNRESOLVED: ' + '; '.join(result['errors']), '']
        for notice in result['notices']:
            count += 1
            aggregate += ['--- BEGIN NOTICE ' + notice['aggregate_marker'] + ' ---',
                          'Archive path: ' + notice['archive_path'], 'Original file SHA256: ' + notice['sha256'],
                          '', notice.pop('_text'), '--- END NOTICE ' + notice['aggregate_marker'] + ' ---', '']
    native_count = 0
    for component in native_components:
        aggregate += ['=' * 78, component['name'] + ' ' + component['version'], 'Source: ' + component['source_url'], '']
        for notice in component['notices']:
            native_count += 1
            aggregate += ['--- BEGIN NATIVE NOTICE ' + notice['aggregate_marker'] + ' ---',
                          'Repository path: ' + notice['repository_path'], 'Notice file SHA256: ' + notice['sha256'],
                          '', notice.pop('_text'), '--- END NATIVE NOTICE ' + notice['aggregate_marker'] + ' ---', '']
    (OUT / 'ENGINE-THIRD-PARTY-NOTICES.txt').write_text('\n'.join(aggregate), encoding='utf-8', newline='\n')
    manifest = {'schema': 1, 'engine_version': TAG, 'engine_sha256': EXPECTED_EXE, 'engine_bytes': len(data),
                'go_version': go_version, 'build_settings': settings, 'main_source_commit': COMMIT,
                'build_recipe': {'script': 'scripts/Build-SecurityEngine.ps1',
                                 'script_sha256': sha((REPO / 'scripts/Build-SecurityEngine.ps1').read_bytes()),
                                 'go_mod': 'engine-build/go.mod', 'go_mod_sha256': sha((REPO / 'engine-build/go.mod').read_bytes()),
                                 'go_sum': 'engine-build/go.sum', 'go_sum_sha256': sha((REPO / 'engine-build/go.sum').read_bytes()),
                                 'patch_manifest': {'path': patch_path, 'sha256': sha(patch_bytes)},
                                 'patch_files': patch['files'],
                                 'note': 'These exact main-source HTTP limits, dependency locks and build options produce the custom engine. Corresponding modified sources, tests and upstream notices must accompany binary distribution.'},
                'module_count': len(modules), 'notice_count': count,
                'native_notice_count': native_count, 'total_notice_count': count + native_count,
                'notice_format': 'Each aggregate_marker identifies its BEGIN/END block in ENGINE-THIRD-PARTY-NOTICES.txt. Notice text is UTF-8, omitting a leading UTF-8 BOM if present. Original archive-file SHA256 and aggregate-text SHA256 are both retained. No separate notice text files are required.',
                'hash_algorithm_reference': 'https://go.dev/src/golang.org/x/mod/sumdb/dirhash/hash.go',
                'scope': 'All legal notice filenames found in exact source archives, conservatively including source/tests/platform files, plus verified prebuilt Wintun and WireGuard MIT loader notices. No claim of legal opinion or complete package-level attribution.',
                'components': results, 'native_components': native_components}
    (OUT / 'engine-source-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    unresolved = [result for result in results if result['errors']]
    print(json.dumps({'components': len(results), 'modules': len(modules), 'notices': count,
                      'native_components': len(native_components), 'native_notices': native_count,
                      'unresolved': [{'module': r['module'], 'version': r['version'], 'errors': r['errors']} for r in unresolved]}, ensure_ascii=False), flush=True)
    return 1 if unresolved else 0

if __name__ == '__main__':
    raise SystemExit(main())
