#!/usr/bin/env python3
"""Verify the catalog and the actual install archive without using a private server."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import urllib.request
import uuid
import zipfile
from package import ABI, GUID, ROOT

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--local', action='store_true')
parser.add_argument('--report', type=Path)
args = parser.parse_args()
if args.local:
    manifest = json.loads((ROOT / 'artifacts/manifest.json').read_text())
else:
    with urllib.request.urlopen('https://raw.githubusercontent.com/sssnto/GlideToolkit/main/manifest.json', timeout=30) as r:
        manifest = json.load(r)
assert len(manifest) == 1 and str(uuid.UUID(manifest[0]['guid'])) == GUID
assert manifest[0]['name'] == 'Glide Video Toolkit'
version = manifest[0]['versions'][0]
assert version['targetAbi'] == ABI
assert version['sourceUrl'].startswith('https://github.com/sssnto/GlideToolkit/releases/download/v')
if args.local:
    data = (ROOT / 'artifacts' / version['sourceUrl'].rsplit('/', 1)[1]).read_bytes()
else:
    with urllib.request.urlopen(version['sourceUrl'], timeout=90) as r:
        data = r.read()
assert hashlib.md5(data).hexdigest().lower() == version['checksum'].lower()
with zipfile.ZipFile(io.BytesIO(data)) as z:
    assert set(z.namelist()) == {'Jellyfin.Plugin.GlideToolkit.dll', 'meta.json', 'README.md', 'SHA256SUMS'}
    assert all('/' not in n and '\\' not in n for n in z.namelist())
    meta = json.loads(z.read('meta.json'))
    assert meta['guid'] == GUID and meta['version'] == version['version'] and meta['targetAbi'] == ABI
    assert meta['autoUpdate'] is True
    assert z.read('Jellyfin.Plugin.GlideToolkit.dll')[:2] == b'MZ'
    for line in z.read('SHA256SUMS').decode().splitlines():
        checksum, name = line.split('  ', 1)
        assert hashlib.sha256(z.read(name)).hexdigest() == checksum
report = {'passed': True, 'mode': 'local' if args.local else 'public', 'version': version['version'],
          'targetAbi': ABI, 'zipBytes': len(data), 'zipSHA256': hashlib.sha256(data).hexdigest(),
          'checks': ['catalog_identity', 'abi', 'download_url', 'jellyfin_md5', 'flat_managed_only_archive', 'metadata_version', 'catalog_updates_enabled', 'pe_binary', 'internal_sha256']}
if args.report:
    args.report.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2))
