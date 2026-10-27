"""Validate the installer's concise, append-only version history before packaging."""
import datetime
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def validate(document, current, changelog):
    if not isinstance(document, dict) or document.get('schema') != 1:
        raise ValueError('Unsupported update history schema')
    entries = document.get('entries')
    if not isinstance(entries, list) or not 1 <= len(entries) <= 128:
        raise ValueError('Update history must contain 1-128 versions')
    versions = []
    for entry in entries:
        version = entry.get('version', '')
        if not isinstance(version, str) or not re.fullmatch(r'\d+\.\d+\.\d+(?:\.\d+)?', version):
            raise ValueError('Invalid update history version')
        versions.append(version)
        if not isinstance(entry.get('title'), str) or not 1 <= len(entry['title']) <= 80:
            raise ValueError('Missing update title: ' + version)
        date = entry.get('date')
        if not isinstance(date, str):
            raise ValueError('Missing update date: ' + version)
        if date:
            datetime.datetime.strptime(date, '%Y-%m-%d')
        for key, minimum, maximum in [('changes', 1, 8), ('notes', 0, 4)]:
            values = entry.get(key)
            if not isinstance(values, list) or not minimum <= len(values) <= maximum:
                raise ValueError('Invalid ' + key + ': ' + version)
            if any(not isinstance(s, str) or not s.strip() or len(s) > 240 or '\n' in s or '\r' in s for s in values):
                raise ValueError('Invalid update text: ' + version)
    if len(set(versions)) != len(versions):
        raise ValueError('Duplicate version in update history')
    expected = sorted(versions, key=lambda s: tuple(map(int, s.split('.'))), reverse=True)
    if versions != expected or versions[0] != current:
        raise ValueError('Add current release first; keep older versions below it')
    recorded = set(re.findall(r'^## (\d+\.\d+\.\d+(?:\.\d+)?)(?=\s|$)', changelog, re.M))
    if set(versions) != recorded:
        raise ValueError('Concise history must retain every CHANGELOG version: missing=' + ','.join(sorted(recorded-set(versions))) + '; extra=' + ','.join(sorted(set(versions)-recorded)))
    return entries


def check(root=ROOT):
    version = json.loads((root/'version.json').read_text(encoding='utf-8-sig'))['version']
    doc = json.loads((root/'docs/UPDATE-HISTORY.json').read_text(encoding='utf-8-sig'))
    return validate(doc, version, (root/'CHANGELOG.md').read_text(encoding='utf-8-sig'))


if __name__ == '__main__':
    entries = check()
    print(json.dumps(dict(current=entries[0]['version'], versions=len(entries), oldest=entries[-1]['version'], historyRetained=True)))
