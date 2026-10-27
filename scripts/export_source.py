"""Export a reviewable public source tree using an explicit allowlist. Never reads user state."""
import datetime
import hashlib
import json
from pathlib import Path
import re
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[1]
ROOT_FILES = ('README.md', 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md',
              'CONTRIBUTING.md', 'SECURITY.md', 'CHANGELOG.md', '.gitignore', '.gitattributes', 'version.json', 'build-info.json')
DOCS = ('README.md', 'TEST-REPORT.md', 'UPDATE-0.7.0.md', 'BACKUP-MAINTENANCE.md', 'UPDATE-0.6.25.md', 'UPDATE-0.6.24.md', 'UPDATE-0.6.23.1.md', 'UPDATE-0.6.23.md', 'NATIVE-BUBBLE-ANALYSIS-0.6.20.md', 'UPDATE-HISTORY.json', 'GUI-INSTALLER.md', 'USER-GUIDE.md', 'AI-INSTALL-GUIDE.md', 'BUILDING.md', 'PUBLISHING.md', 'SOURCE-AUDIT.md', 'VALIDATION.md', 'CHAT-PIPELINE-AUDIT-0.6.18.md', 'BRIDGE-RECOVERY-0.6.18.md', 'DISPLAY-RELIABILITY-0.6.20.md', 'DISPLAY-OPTIONS-0.6.20.md')
SCRIPTS = ('build_identity.py', 'native_bubble_layout.py', 'build.ps1', 'release_history.py', 'build_installer.py', 'Installer-Workflow.ps1', 'prepare_mod.py', 'build_terms.py', 'build_hero_labels.py', 'term_extensions.py', 'vpk.py',
           'package_docs.py', 'package_share.py', 'collect_licenses.py', 'export_source.py', 'merge_term_defaults.py',
           'steam_launch_options.py', 'Configure-Steam.ps1', 'Find-Deadlock.ps1','Game-SearchPaths.ps1','Install-Safety.ps1','Vpk-Recovery.ps1','Repair-GameConfig.cmd', 'Export-Diagnostics.cmd', 'Manage-Mod.ps1', 'Install.cmd', 'Uninstall.cmd',
           'Launch.cmd', 'Launch-Admin.cmd', 'Settings.cmd', 'Trust-Local-Https.ps1', 'Trust-Local-Https.cmd', 'Renew-Local-Https.cmd',
           'Backup-Maintenance.ps1', 'Update-Mod.ps1', 'Update-Archive.ps1', 'build_external_updater.py')
SCREENSHOTS = ('src/Bridge/wwwroot/settings-background-v3-flat.png', 'src/Bridge/wwwroot/community-qq.png', 'docs/images/qq-feedback-923443423.png', 'docs/images/chat-bilingual.png', 'docs/images/chat-bubbles.png')
GENERATED = {'mod/panorama/layout/citadel_hud_top_bar_chat.xml', 'mod/panorama/layout/chat.xml', 'src/Bridge/data/terms.json', 'src/Bridge/wwwroot/app.js'}
EXTENSIONS = {'.cs', '.csproj', '.ts', '.js', '.cjs', '.json', '.xml', '.css', '.html',
              '.manifest', '.py', '.ps1', '.tsv', '.md', '.yml', '.yaml', '.txt'}
SKIP_DIRS = {'bin', 'obj', 'node_modules', '__pycache__', '.git'}
PRIVATE_NAMES = {'settings.json', 'secrets.dpapi', 'pairing.key', 'install-manifest.json',
                 'credentials.json', 'appsettings.development.json'}

def public_files(root=ROOT):
    result = [root/p for p in ROOT_FILES]
    result += [root/p for p in SCREENSHOTS]
    result += [root/'docs'/p for p in DOCS]
    result += [root/'scripts'/p for p in SCRIPTS]
    for name in ('src', 'mod', 'tests', 'LICENSES', '.github'):
        for file in (root/name).rglob('*'):
            relative = file.relative_to(root)
            if (file.is_file() and not any(p in SKIP_DIRS for p in relative.parts)
                    and file.suffix in EXTENSIONS and file.name.lower() not in PRIVATE_NAMES
                    and relative.as_posix() not in GENERATED):
                result.append(file)
    result = sorted(set(result))
    for file in result:
        if file.is_symlink() or not file.resolve().is_relative_to(root.resolve()):
            raise ValueError('Linked source not allowed: ' + str(file.relative_to(root)))
        if not file.is_file():
            raise FileNotFoundError(file)
    return result

def findings(file):
    if file.suffix.lower() == '.png':
        if not file.read_bytes().startswith(b'\x89PNG\r\n\x1a\n'):
            return [{'kind': 'invalid screenshot format', 'line': 0}]
        return []  # Only explicitly allowlisted, visually reviewed screenshots.
    text = file.read_text(encoding='utf-8-sig')
    # Report location/type only. Never echo a matched credential into a log.
    patterns = {
        'API credential': r'\bsk-' + r'(?:tp-)?[A-Za-z0-9_+=/-]{20,}',
        'GitHub credential': r'\b(?:gh[pousr]_' + r'[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})',
        'private key': '-----BEGIN ' + r'(?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
        'personal Windows path': r'[A-Za-z]:[\\/]+Users[\\/]+(?!Public\b|Default\b|YOUR_USER\b)[\w.-]+',
    }
    return [{'kind': kind, 'line': text.count('\n', 0, match.start())+1}
            for kind, pattern in patterns.items() for match in re.finditer(pattern, text)]

def main():
    files = public_files()
    problems = [{'file': f.relative_to(ROOT).as_posix(), **item} for f in files for item in findings(f)]
    if problems:
        raise SystemExit(json.dumps({'blocked': problems}, ensure_ascii=False, indent=2))
    version = json.loads((ROOT/'version.json').read_text(encoding='utf-8-sig'))['version']
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    destination = ROOT/'dist'/f'DeadlockTranslate-source-{version}-{stamp}'
    destination.mkdir(parents=True, exist_ok=False)
    entries = []
    for file in files:
        relative = file.relative_to(ROOT)
        target = destination/relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(file, target)
        entries.append({'file': relative.as_posix(), 'sha256': hashlib.sha256(target.read_bytes()).hexdigest()})
    manifest = {'version': version, 'files': entries, 'scan': 'known credential formats and personal Windows paths; not an exhaustive secret audit'}
    (destination/'SOURCE-MANIFEST.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    archive = Path(str(destination)+'.zip')
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as output:
        for file in destination.rglob('*'):
            if file.is_file(): output.write(file, Path(destination.name)/file.relative_to(destination))
    print(json.dumps({'sourceDirectory': str(destination), 'archive': str(archive), 'files': len(entries),
                      'sha256': hashlib.sha256(archive.read_bytes()).hexdigest()}, ensure_ascii=False, indent=2))

if __name__ == '__main__':
    main()
