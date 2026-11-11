#!/usr/bin/env python3
"""Build the Jellyfin catalog ZIP and a candidate manifest; never publish by default."""
import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
GUID = '9e405477-411c-4c2b-8541-c3b5925f7169'
REPO = 'sssnto/GlideToolkit'
ABI = '12.2.0.0'
NAME = 'Glide Video Toolkit'

def merge(existing, candidate):
    if len(candidate) != 1 or candidate[0]['guid'] != GUID:
        raise ValueError('Unexpected plugin identity')
    old = next((p for p in existing if p['guid'] == GUID), None)
    versions = {v['version']: v for v in (old or {}).get('versions', [])}
    for v in candidate[0]['versions']:
        if v['version'] in versions and versions[v['version']]['checksum'] != v['checksum']:
            raise ValueError('A published version is immutable; increment Version')
        versions[v['version']] = v
    entry = dict(candidate[0])
    entry['versions'] = sorted(versions.values(), key=lambda v: tuple(map(int, v['version'].split('.'))), reverse=True)
    return [p for p in existing if p['guid'] != GUID] + [entry]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--register', action='store_true', help='Merge an already-published candidate into root manifest')
    args = parser.parse_args()
    output = ROOT / 'artifacts'
    output.mkdir(exist_ok=True)
    manifest = ROOT / 'manifest.json'
    if args.register:
        existing = json.loads(manifest.read_text()) if manifest.exists() else []
        candidate = json.loads((output / 'manifest.json').read_text())
        manifest.write_text(json.dumps(merge(existing, candidate), ensure_ascii=False, indent=2) + '\n')
        return
    version = ET.parse(ROOT / 'Jellyfin.Plugin.GlideToolkit/Jellyfin.Plugin.GlideToolkit.csproj').findtext('.//Version')
    if not re.fullmatch(r'\d+\.\d+\.\d+', version or ''):
        raise ValueError('Use an X.Y.Z project Version')
    tag = os.environ.get('GITHUB_REF_NAME', '')
    if os.environ.get('GITHUB_REF_TYPE') == 'tag' and tag != 'v' + version:
        raise ValueError('Release tag must match project Version')
    dll = ROOT / 'Jellyfin.Plugin.GlideToolkit/bin/Release/net10.0/Jellyfin.Plugin.GlideToolkit.dll'
    if not dll.is_file():
        raise FileNotFoundError('Build the Release plugin with .NET 10 first')
    epoch = int(os.environ.get('SOURCE_DATE_EPOCH') or subprocess.check_output(['git', 'log', '-1', '--format=%ct'], cwd=ROOT).decode().strip())
    timestamp = dt.datetime.fromtimestamp(epoch, dt.timezone.utc)
    assembly_version = version + '.0'
    metadata = {'guid': GUID, 'name': NAME, 'version': assembly_version, 'targetAbi': ABI,
                'description': 'Server-side rotation queues, updated previews and verified backup recovery.',
                'status': 'Active', 'autoUpdate': True, 'assemblies': [dll.name]}
    contents = {dll.name: dll.read_bytes(), 'meta.json': (json.dumps(metadata, indent=2) + '\n').encode(),
                'README.md': (ROOT / 'README.md').read_bytes()}
    contents['SHA256SUMS'] = ''.join(hashlib.sha256(data).hexdigest() + '  ' + name + '\n' for name, data in sorted(contents.items()) if name != 'README.md').encode()
    archive = output / f'GlideToolkit-{version}-Jellyfin-12.2.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as package:
        for name, data in sorted(contents.items()):
            info = zipfile.ZipInfo(name, timestamp.timetuple()[:6])
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            package.writestr(info, data)
    data = archive.read_bytes()
    entry = {'guid': GUID, 'name': NAME, 'overview': '在服务器保存视频方向、批量队列和旋转预览',
             'description': 'For Jellyfin 12.2 on Linux Docker. Server-side MP4/MOV/M4V rotation, durable serial queues, updated preview frames and verified backup restore. Requires explicit user authorization and writable library paths. No video transfer through the phone.',
             'owner': 'sssnto', 'category': 'General', 'versions': [{
                 'version': assembly_version, 'changelog': 'Independent repository and Jellyfin catalog installation. Retains the 1.1.0 rotation engine, configuration, backups and task history.',
                 'targetAbi': ABI, 'sourceUrl': f'https://github.com/{REPO}/releases/download/v{version}/{archive.name}',
                 'checksum': hashlib.md5(data).hexdigest(), 'timestamp': timestamp.strftime('%Y-%m-%dT%H:%M:%SZ')}]}
    existing = json.loads(manifest.read_text()) if manifest.exists() else []
    (output / 'manifest.json').write_text(json.dumps(merge(existing, [entry]), ensure_ascii=False, indent=2) + '\n')
    (output / 'SHA256SUMS').write_text(hashlib.sha256(data).hexdigest() + '  ' + archive.name + '\n')
    print(archive)
    print('SHA256 ' + hashlib.sha256(data).hexdigest())

if __name__ == '__main__':
    main()
