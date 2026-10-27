"""Build a portable runtime ZIP with matching public source and per-file hashes."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

from build_identity import stamp
from export_source import ROOT, findings, public_files
from build_external_updater import build as build_updater


def sha(file):
    return hashlib.sha256(file.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output-folder', type=Path)
    parser.add_argument('--archive-dir', type=Path)
    args = parser.parse_args()
    build_info = stamp(check=True)
    version_info = json.loads((ROOT / 'version.json').read_text(encoding='utf-8-sig'))
    version = version_info['version']
    release = ROOT / 'release' / version
    args.output_folder = args.output_folder or release
    args.archive_dir = args.archive_dir or release
    built = ROOT / 'dist' / f'DeadlockTranslate-{version}'
    built_identity = json.loads((built/'build-info.json').read_text(encoding='utf-8-sig'))
    if built_identity != build_info: raise SystemExit('Runtime build differs from source; build the complete runtime before packaging.')
    built_manifest = json.loads((built/'build-manifest.json').read_text(encoding='utf-8-sig'))
    if built_manifest.get('buildId') != build_info['buildId']: raise SystemExit('Incomplete runtime build identity.')
    for item in built_manifest['artifacts']:
        artifact = built/item['file']
        if not artifact.resolve().is_relative_to(built.resolve()) or not artifact.is_file() or sha(artifact).lower() != item['sha256'].lower():
            raise SystemExit('Built runtime file changed after verification: '+item['file'])

    package = args.output_folder / f'DeadlockTranslate-{version}'
    if package.exists():
        raise SystemExit('Output directory already exists; choose a new staging directory.')
    archive = args.archive_dir / f'DeadlockTranslate-{version}-win-x64.zip'
    if archive.exists():
        raise SystemExit('Archive already exists; choose another output directory.')
    files = public_files()
    problems = [f.relative_to(ROOT).as_posix() for f in files if findings(f)]
    if problems:
        raise SystemExit('Public source scan failed: ' + ', '.join(problems))
    package.mkdir(parents=True)
    for name in ('DeadlockTranslate.exe', 'DeadlockTranslate.staticwebassets.endpoints.json',
                 'dlt_translate_dir.vpk', 'pairing.key', 'Install.cmd', 'Uninstall.cmd',
                 'Launch.cmd', 'Launch-Admin.cmd', 'Settings.cmd', 'Manage-Mod.ps1',
                 'Trust-Local-Https.ps1', 'Trust-Local-Https.cmd',
                 'Renew-Local-Https.cmd', 'Steam-Launch-Options.txt', 'web.config',
                 'build-info.json', 'USER-GUIDE.md', '使用说明-先读我.txt', 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md'):
        shutil.copyfile(built / name, package / name)
    for name in ('data', 'wwwroot', 'LICENSES'):
        shutil.copytree(built / name, package / name)
    subprocess.run([sys.executable, str(ROOT / 'scripts/package_docs.py'), '--output', str(package)], check=True)
    for script in ('Configure-Steam.ps1','Find-Deadlock.ps1','Game-SearchPaths.ps1','Install-Safety.ps1','Vpk-Recovery.ps1','Backup-Maintenance.ps1','Repair-GameConfig.cmd','Export-Diagnostics.cmd'):
        shutil.copyfile(ROOT / 'scripts' / script, package / script)
    for original, name in ((ROOT / 'README.md', 'README.md'), (ROOT / 'CHANGELOG.md', 'CHANGELOG.md'),
                           (ROOT / ('docs/UPDATE-'+version+'.md'), 'UPDATE-'+version+'.md'), (ROOT / 'docs/VALIDATION.md', 'VALIDATION.md'), (ROOT / 'docs/TEST-REPORT.md', 'TEST-REPORT.md')):
        shutil.copyfile(original, package / name)
    source = package / 'source'
    source.mkdir()
    source_manifest = dict(version=version, buildId=build_info['buildId'], featureRevision=version_info.get('featureRevision'), files=[dict(file=f.relative_to(ROOT).as_posix(), sha256=sha(f)) for f in files])
    with zipfile.ZipFile(source / f'DeadlockTranslate-source-{version}.zip', 'w', zipfile.ZIP_DEFLATED) as z:
        for file in files:
            z.write(file, file.relative_to(ROOT))
        z.writestr('SOURCE-MANIFEST.json', json.dumps(source_manifest, ensure_ascii=False, indent=2))
    (package / 'SOURCE.txt').write_text('本包包含与运行程序版本一致的可编辑公开源码；构建说明见源码 docs/BUILDING.md。\n源码不包含个人 API Key、配置或证书，也不包含游戏原始资源和外部编译工具。\n', encoding='utf-8-sig')
    manifest = dict(version=version, buildId=build_info['buildId'], featureRevision=version_info.get('featureRevision'), files=[dict(file=f.relative_to(package).as_posix(), bytes=f.stat().st_size, sha256=sha(f))
                    for f in sorted(package.rglob('*')) if f.is_file()])
    (package / 'package-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    args.archive_dir.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
        for file in sorted(package.rglob('*')):
            if file.is_file():
                z.write(file, Path(package.name) / file.relative_to(package))
    with zipfile.ZipFile(archive) as z:
        if z.testzip() is not None:
            raise SystemExit('Archive CRC verification failed')
        for item in manifest['files']:
            if hashlib.sha256(z.read(package.name + '/' + item['file'])).hexdigest() != item['sha256']:
                raise SystemExit('Archive file hash mismatch: ' + item['file'])
    checksum = sha(archive)
    archive.with_suffix('.zip.sha256').write_text(checksum + '  ' + archive.name + '\n', encoding='ascii')
    updater = build_updater(args.archive_dir / '覆盖更新.cmd')
    updater.with_suffix('.cmd.sha256').write_text(sha(updater) + '  ' + updater.name + '\n', encoding='utf-8-sig')
    for name in ('AI安装说明书.txt', 'AI-INSTALL-GUIDE.md', 'Configure-Steam.ps1', 'Install-Safety.ps1','Vpk-Recovery.ps1'):
        shutil.copyfile(package / name, args.archive_dir / name)
    print(json.dumps(dict(package=str(package), archive=str(archive), sha256=checksum,
                          updater=str(updater), updaterSha256=sha(updater),
                          files=len(manifest['files']), sourceFiles=len(files)), ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
