"""Collect notices from the exact restored runtime package versions for a self-contained release."""
import argparse
import json
from pathlib import Path
import shutil

def collect(output):
    root = Path(__file__).resolve().parents[1]
    assets = json.loads((root/'src/Bridge/obj/project.assets.json').read_text(encoding='utf-8-sig'))
    folders = [Path(p) for p in assets['packageFolders']]
    required = {'system.security.cryptography.protecteddata': '10.0.0'}
    for framework in assets['project']['frameworks'].values():
        for dependency in framework.get('downloadDependencies', []):
            name = dependency['name'].lower()
            if name in ('microsoft.netcore.app.runtime.win-x64', 'microsoft.aspnetcore.app.runtime.win-x64'):
                required[name] = dependency['version'].strip('[]').split(',')[0].strip()
    if len(required) != 3:
        raise ValueError('Publish for win-x64 before collecting runtime notices.')
    for name, version in required.items():
        package = next((p/name/version for p in folders if (p/name/version).is_dir()), None)
        if package is None:
            raise FileNotFoundError(name + '/' + version)
        destination = output/'LICENSES'/(name+'-'+version)
        destination.mkdir(parents=True, exist_ok=True)
        notices = [p for p in package.rglob('*') if p.is_file()
                   and ('license' in p.name.lower() or 'third-party-notices' in p.name.lower()
                        or 'thirdpartynotices' in p.name.lower()) and p.suffix.lower() in ('.txt', '.md', '')]
        if not any('notice' in p.name.lower() for p in notices):
            raise ValueError('Missing original third-party notices: ' + name)
        for file in notices:
            target = destination/file.relative_to(package)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(file, target)
        shutil.copy2(root/'LICENSES/dotnet-MIT.txt', destination/'dotnet-MIT.txt')
    parsers = {'valveresourceformat': ('20.1.7467', 'cache-valveresourceformat-MIT.txt'),
               'valvekeyvalue': ('0.70.0.499', 'cache-valvekeyvalue-MIT.txt'),
               'k4os.compression.lz4': ('1.3.8', 'cache-lz4-MIT.txt'),
               'zstdsharp.port': ('0.8.8', 'cache-zstdsharp-MIT.txt'),
               'system.io.hashing': ('10.0.10', 'dotnet-MIT.txt')}
    for name, (version, notice) in parsers.items():
        package = next((p/name/version for p in folders if (p/name/version).is_dir()), None)
        if package is None:
            raise FileNotFoundError(name + '/' + version)
        original = root/'LICENSES'/notice
        if original.stat().st_size < 500:
            raise ValueError('Incomplete parser license: ' + name)
        destination = output/'LICENSES'/(name+'-'+version)
        destination.mkdir(parents=True, exist_ok=True)
        shutil.copy2(original, destination/original.name)
        for file in package.rglob('THIRD-PARTY-NOTICES.TXT'):
            shutil.copy2(file, destination/file.name)
    print('Collected exact-version runtime, ProtectedData and build-cache parser license notices.')

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', required=True, type=Path)
    collect(parser.parse_args().output)
