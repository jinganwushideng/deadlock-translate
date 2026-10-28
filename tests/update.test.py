"""Windows updater tests use isolated files, fake EXE/VPK bytes and no game/cloud calls."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'scripts/Update-Mod.ps1'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def term(source, target):
    return dict(source=source, target=target, english=source, kind='phrase', ambiguous=False)


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False), encoding='utf-8')


def snapshot(folder):
    return {str(p.relative_to(folder)): p.read_bytes() for p in folder.rglob('*') if p.is_file()}


class UpdateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='dlt-update-')
        # Match PowerShell's canonical path spelling on Windows 8.3 TEMP paths.
        self.root = Path(self.temp.name).resolve()
        self.runtime = self.root / '旧版程序'
        self.package = self.root / '新版安装包'
        self.state = self.root / '个人状态'
        self.game = self.root / 'Deadlock'
        self.target = self.game / 'game/citadel/addons/pak90_dir.vpk'
        self.gameinfo = self.game / 'game/citadel/gameinfo.gi'
        self.target.parent.mkdir(parents=True)
        self.target.write_bytes(b'old translation vpk')
        self.gameinfo.write_bytes(b'GameInfo { FileSystem { SearchPaths { Mod citadel Write citadel Mod core Write core Game citadel/addons Game citadel Game core } } CustomSetting "preserved" }')
        (self.target.parent / 'pak20_dir.vpk').write_bytes(b'QOL must be preserved')
        self.old_terms = [term('edited', 'old'), term('deleted', 'old'), term('unchanged', 'old'), term('retired', 'old')]
        self.new_terms = [term('edited', 'updated'), term('deleted', 'updated'), term('unchanged', 'updated'), term('added', 'new')]
        self.user_terms = [term('edited', 'my translation'), term('unchanged', 'old'), term('retired', 'old'), term('custom', 'my term')]
        self.files = {
            'DeadlockTranslate.exe': b'new fake EXE - never executed',
            'dlt_translate_dir.vpk': b'new translation vpk',
            'pairing.key': b'1' * 48,
            'wwwroot/app.js': b'new app',
            'wwwroot/index.html': b'new page',
            'wwwroot/style.css': b'new style',
            'data/terms.json': json.dumps(self.new_terms).encode(),
            'Install.cmd': b'new install',
            'Manage-Mod.ps1': b'new manage',
            'Update-Mod.ps1': SCRIPT.read_bytes(),
            'Update.cmd': b'new update',
            'README.md': b'new README',
        }
        for name, data in self.files.items():
            dest = self.package / name
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(data)
            if name != 'Update.cmd':
                dest = self.runtime / name
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes(b'old ' + name.encode())
        (self.runtime / 'dlt_translate_dir.vpk').write_bytes(self.target.read_bytes())
        (self.runtime / 'pairing.key').write_bytes(b'0' * 48)
        (self.runtime / 'personal-note.txt').write_bytes(b'not owned by update')
        write_json(self.runtime / 'data/terms.json', self.old_terms)
        self.install = dict(target=str(self.target), vpkHash=digest(self.target.read_bytes()),
                            version='0.6.1', runtimeFolder=str(self.runtime), gameinfoChanged=False,
                            gameinfoBefore='', gameinfoAfterHash=digest(self.gameinfo.read_bytes()))
        write_json(self.state / 'install-manifest.json', self.install)
        write_json(self.state / 'terms.json', self.user_terms)
        for name in ('settings.json', 'secrets.dpapi', 'localhost-certificate.dpapi', 'localhost-certificate.cer'):
            (self.state / name).write_bytes(b'PRIVATE bytes preserved: ' + name.encode())
        self.metadata()

    def tearDown(self):
        self.temp.cleanup()

    def metadata(self, **overrides):
        entries = [dict(file=name, sha256=digest((self.package / name).read_bytes())) for name in self.files]
        self.manifest = dict(version='0.6.2', files=entries)
        self.manifest.update(overrides)
        write_json(self.package / 'package-manifest.json', self.manifest)

    def run_update(self, extra=(), explicit=False):
        command = ['powershell.exe', '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass',
                   '-File', str(getattr(self, 'script', SCRIPT)), '-PackageFolder', str(self.package),
                   '-StateDirectory', str(self.state), '-NonInteractive']
        if explicit:
            command += ['-RuntimeFolder', str(self.runtime)]
        command += list(extra)
        result = subprocess.run(command, capture_output=True, timeout=45)
        raw = result.stdout + result.stderr
        try:
            self.output = raw.decode('utf-8')
        except UnicodeDecodeError:
            self.output = raw.decode('gb18030', errors='replace')
        return result.returncode

    def assert_rejected(self):
        before_runtime, before_game = snapshot(self.runtime), snapshot(self.game)
        before_state = snapshot(self.state)
        self.assertNotEqual(self.run_update(), 0, self.output)
        self.assertEqual(snapshot(self.runtime), before_runtime)
        self.assertEqual(snapshot(self.game), before_game)
        self.assertEqual(snapshot(self.state), before_state)

    def test_success_preserves_settings_keys_certificate_and_other_mods(self):
        state = snapshot(self.state)
        game = snapshot(self.game)
        self.assertEqual(self.run_update(), 0, self.output)
        for name, data in self.files.items():
            self.assertEqual((self.runtime / name).read_bytes(), data, name)
        self.assertEqual((self.runtime / 'personal-note.txt').read_bytes(), b'not owned by update')
        self.assertEqual(self.target.read_bytes(), self.files['dlt_translate_dir.vpk'])
        for name, data in state.items():
            if name not in ('terms.json', 'install-manifest.json'):
                self.assertEqual((self.state / name).read_bytes(), data)
        for name, data in game.items():
            if str(self.target.relative_to(self.game)) != name:
                self.assertEqual((self.game / name).read_bytes(), data)
        saved = json.loads((self.state / 'install-manifest.json').read_text(encoding='utf-8-sig'))
        self.assertEqual(saved['version'], '0.6.2')
        self.assertEqual(saved['runtimeFolder'], str(self.runtime))
        backups = list((self.state / 'backups').iterdir())
        self.assertEqual(len(backups), 1)
        self.assertEqual((backups[0] / 'translation-before.vpk').read_bytes(), b'old translation vpk')
        self.assertEqual((backups[0] / 'install-manifest-before.json').read_bytes(), state['install-manifest.json'])
        self.assertEqual((backups[0] / 'user-terms-before.json').read_bytes(), state['terms.json'])

    def test_four_component_revision_updates_from_three_component_version(self):
        self.metadata(version='0.6.23.1')
        self.assertEqual(self.run_update(), 0, self.output)
        saved = json.loads((self.state / 'install-manifest.json').read_text(encoding='utf-8-sig'))
        self.assertEqual(saved['version'], '0.6.23.1')

    def test_repeated_success_updates_prune_to_two_complete_backups(self):
        for _ in range(4):
            self.assertEqual(self.run_update(), 0, self.output)
        backups=list((self.state/'backups').glob('update-*'))
        self.assertEqual(len(backups),2,self.output)
        for backup in backups:
            self.assertEqual(json.loads((backup/'backup-state.json').read_text())['status'],'succeeded')
            plan=json.loads((backup/'file-plan.json').read_text(encoding='utf-8-sig'))
            for step in plan:
                if step['beforeHash']:self.assertEqual(digest((backup/'runtime'/step['relative']).read_bytes()),step['beforeHash'])
        self.assertEqual(self.target.read_bytes(), self.files['dlt_translate_dir.vpk'])

    def test_four_component_revision_cannot_downgrade(self):
        self.install['version'] = '0.6.23.2'
        write_json(self.state / 'install-manifest.json', self.install)
        self.metadata(version='0.6.23.1')
        self.assert_rejected()

    def test_three_way_glossary_merge(self):
        self.assertEqual(self.run_update(), 0, self.output)
        terms = {t['source']: t for t in json.loads((self.state / 'terms.json').read_text(encoding='utf-8-sig'))}
        self.assertEqual(terms['edited']['target'], 'my translation')
        self.assertEqual(terms['unchanged']['target'], 'updated')
        self.assertEqual(terms['added']['target'], 'new')
        self.assertEqual(terms['custom']['target'], 'my term')
        self.assertNotIn('deleted', terms)
        self.assertNotIn('retired', terms)

    def test_legacy_name_edits_and_empty_schema_fields_preserved(self):
        self.old_terms[0]['legacyTarget']='default old name'
        self.new_terms[0]['legacyTarget']='updated default old name'
        self.user_terms[0]={**self.old_terms[0],'legacyTarget':'my old name'}
        self.user_terms[1]['legacyTarget']=''
        self.new_terms[2]['legacyTarget']='new old-name hint'
        write_json(self.runtime/'data/terms.json',self.old_terms)
        write_json(self.package/'data/terms.json',self.new_terms)
        write_json(self.state/'terms.json',self.user_terms)
        self.metadata()
        self.assertEqual(self.run_update(),0,self.output)
        merged={t['source']:t for t in json.loads((self.state/'terms.json').read_text(encoding='utf-8-sig'))}
        self.assertEqual(merged['edited']['legacyTarget'],'my old name')
        self.assertEqual(merged['unchanged']['target'],'updated')
        self.assertEqual(merged['unchanged']['legacyTarget'],'new old-name hint')

    def test_invalid_legacy_name_rejected_before_update(self):
        self.user_terms[0]['legacyTarget']=None
        write_json(self.state/'terms.json',self.user_terms)
        self.assert_rejected()

    def test_missing_boot_paths_repaired_and_original_backed_up(self):
        broken = b'GameInfo { FileSystem { SearchPaths { Game citadel/addons Game citadel Game core } } CustomSetting "unchanged" }'
        self.gameinfo.write_bytes(broken)
        self.assertEqual(self.run_update(), 0, self.output)
        fixed = self.gameinfo.read_bytes()
        for entry in (b'Mod citadel', b'Write citadel', b'Mod core', b'Write core'):
            self.assertIn(entry, fixed)
        self.assertIn(b'CustomSetting "unchanged"', fixed)
        backup = next((self.state / 'backups').iterdir())
        self.assertEqual((backup / 'gameinfo-before.gi').read_bytes(), broken)
        saved = json.loads((self.state / 'install-manifest.json').read_text(encoding='utf-8-sig'))
        self.assertFalse(saved['gameinfoChanged'], 'generic repair must survive uninstall when mount belongs to another installer')
        self.assertEqual(saved['gameinfoAfterHash'], digest(fixed))

    def test_malformed_gameinfo_rejected_before_runtime_changes(self):
        self.gameinfo.write_bytes(b'GameInfo { FileSystem { SearchPaths { Game citadel')
        self.assert_rejected()

    def test_old_manifest_without_runtime_autodetects_steam_launch_path(self):
        self.install.pop('runtimeFolder')
        self.install.pop('version')
        write_json(self.state / 'install-manifest.json', self.install)
        steam = self.root / 'Steam'
        vdf = steam / 'userdata/123/config/localconfig.vdf'
        vdf.parent.mkdir(parents=True)
        exe = str(self.runtime / 'DeadlockTranslate.exe').replace('\\', '\\\\')
        vdf.write_text('"UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "1422450" { "LaunchOptions" "\\"' + exe + '\\" --steam-game %command% -novid" } } } } } }', encoding='utf-8')
        before = vdf.read_bytes()
        self.assertEqual(self.run_update(['-SteamPath', str(steam)]), 0, self.output)
        self.assertEqual(vdf.read_bytes(), before)

    def test_manual_overwrite_in_place_updates_game_without_erasing_user_terms(self):
        for name, data in self.files.items():
            dest = self.runtime / name
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(data)
        (self.runtime / 'package-manifest.json').write_bytes((self.package / 'package-manifest.json').read_bytes())
        self.package = self.runtime
        before = (self.state / 'terms.json').read_bytes()
        self.assertEqual(self.run_update(), 0, self.output)
        self.assertEqual((self.state / 'terms.json').read_bytes(), before)
        self.assertEqual(self.target.read_bytes(), self.files['dlt_translate_dir.vpk'])

    def test_corrupt_source_rejected(self):
        (self.package / 'wwwroot/app.js').write_bytes(b'corrupt')
        self.assert_rejected()

    def test_changed_game_vpk_rejected(self):
        self.target.write_bytes(b'belongs to somebody else now')
        self.assert_rejected()

    def test_missing_install_record_rejected(self):
        (self.state / 'install-manifest.json').unlink()
        self.assert_rejected()

    def test_downgrade_rejected(self):
        self.metadata(version='0.5.0')
        self.assert_rejected()

    def test_path_traversal_rejected(self):
        self.manifest['files'].append(dict(file='../escape.txt', sha256='0' * 64))
        write_json(self.package / 'package-manifest.json', self.manifest)
        self.assert_rejected()

    def test_private_state_in_package_rejected(self):
        self.manifest['files'].append(dict(file='data/settings.json', sha256='0' * 64))
        write_json(self.package / 'package-manifest.json', self.manifest)
        self.assert_rejected()

    def test_duplicate_alias_path_rejected(self):
        self.manifest['files'].append(dict(file='WWWROOT\\app.js', sha256=digest(self.files['wwwroot/app.js'])))
        write_json(self.package / 'package-manifest.json', self.manifest)
        self.assert_rejected()

    def test_invalid_user_glossary_rejected(self):
        write_json(self.state / 'terms.json', [term('duplicate', 'a'), term('DUPLICATE', 'b')])
        self.assert_rejected()

    def test_locked_file_rejected_before_any_write(self):
        if os.name != 'nt':
            self.skipTest('Windows file sharing semantics')
        before_runtime, before_game = snapshot(self.runtime), snapshot(self.game)
        before_state = snapshot(self.state)
        api = ctypes.WinDLL('kernel32', use_last_error=True)
        api.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
        api.CreateFileW.restype = ctypes.c_void_p
        api.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = api.CreateFileW(str(self.runtime / 'wwwroot/style.css'), 0x80000000, 1, None, 3, 0, None)
        self.assertNotEqual(handle, ctypes.c_void_p(-1).value)
        try:
            self.assertNotEqual(self.run_update(), 0, self.output)
        finally:
            api.CloseHandle(handle)
        self.assertEqual(snapshot(self.runtime), before_runtime)
        self.assertEqual(snapshot(self.game), before_game)
        for name, value in before_state.items():
            self.assertEqual((self.state / name).read_bytes(), value)
        self.assertFalse((self.state / 'backups').exists())

    def test_ps51_encoding_and_portable_entrypoint(self):
        self.assertTrue(SCRIPT.read_bytes().startswith(b'\xef\xbb\xbf'))
        updater = SCRIPT.read_text(encoding='utf-8-sig')
        self.assertNotIn('Stop-Process', updater)
        self.assertNotIn('python ', updater)
        self.assertTrue((ROOT / 'scripts/build_external_updater.py').is_file())
    def test_failure_after_runtime_commit_rolls_back(self):
        scripts=self.root/'fixture-scripts';scripts.mkdir()
        for name in ('Install-Safety.ps1','Game-SearchPaths.ps1','Vpk-Recovery.ps1','Backup-Maintenance.ps1'):
            (scripts/name).write_bytes((ROOT/'scripts'/name).read_bytes())
        self.script=scripts/'Update-Mod.ps1'
        text=SCRIPT.read_text(encoding='utf-8-sig')
        text=text.replace("        Write-DltAtomic $taskTarget ([IO.File]::ReadAllBytes($taskFiles['dlt_translate_dir.vpk']))", "        Throw-DltFault 'fixture-write-denied' 'update-game-vpk' $taskTarget 'Fixture failure after runtime commit'\n        Write-DltAtomic $taskTarget ([IO.File]::ReadAllBytes($taskFiles['dlt_translate_dir.vpk']))")
        self.script.write_text(text,encoding='utf-8-sig')
        before_runtime,before_game=snapshot(self.runtime),snapshot(self.game);before_state=snapshot(self.state)
        fault=self.root/'failure.json'
        self.assertNotEqual(self.run_update(('-ErrorResultFile',str(fault))),0,self.output)
        failure=json.loads(fault.read_text(encoding='utf-8-sig'))
        self.assertEqual(failure['code'],'fixture-write-denied');self.assertEqual(failure['stage'],'update-game-vpk')
        self.assertEqual(snapshot(self.runtime),before_runtime);self.assertEqual(snapshot(self.game),before_game)
        for name,raw in before_state.items():self.assertEqual((self.state/name).read_bytes(),raw)
        self.assertEqual(len(list((self.state/'backups').iterdir())),1)

    def test_explicit_missing_runtime_repair_preserves_user_terms(self):
        (self.runtime / 'DeadlockTranslate.exe').unlink()
        (self.runtime / 'dlt_translate_dir.vpk').unlink()
        (self.runtime / 'data/terms.json').unlink()
        terms = (self.state / 'terms.json').read_bytes()
        self.assertEqual(self.run_update(['-RepairMissingRuntime'], explicit=True), 0, self.output)
        self.assertEqual((self.runtime / 'DeadlockTranslate.exe').read_bytes(), self.files['DeadlockTranslate.exe'])
        self.assertEqual(self.target.read_bytes(), self.files['dlt_translate_dir.vpk'])
        self.assertEqual((self.state / 'terms.json').read_bytes(), terms)

    def test_recorded_missing_runtime_is_repaired_by_default(self):
        (self.runtime / 'DeadlockTranslate.exe').unlink()
        self.assertEqual(self.run_update(),0,self.output)
        self.assertEqual((self.runtime/'DeadlockTranslate.exe').read_bytes(),self.files['DeadlockTranslate.exe'])

    def test_late_manifest_failure_rolls_back_searchpath_repair(self):
        self.gameinfo.write_bytes(b'GameInfo { FileSystem { SearchPaths { Game citadel/addons Game citadel Game core } } }')
        before_runtime, before_game, before_state = snapshot(self.runtime), snapshot(self.game), snapshot(self.state)
        api = ctypes.WinDLL('kernel32', use_last_error=True)
        api.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
        api.CreateFileW.restype = ctypes.c_void_p
        api.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = api.CreateFileW(str(self.state / 'install-manifest.json'), 0x80000000, 1, None, 3, 0, None)
        self.assertNotEqual(handle, ctypes.c_void_p(-1).value)
        try:
            self.assertNotEqual(self.run_update(), 0, self.output)
        finally:
            api.CloseHandle(handle)
        self.assertEqual(snapshot(self.runtime), before_runtime)
        self.assertEqual(snapshot(self.game), before_game)
        for name, value in before_state.items():
            self.assertEqual((self.state / name).read_bytes(), value)


if __name__ == '__main__':
    unittest.main(verbosity=2)
