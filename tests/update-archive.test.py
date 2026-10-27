"""Exercise the outside-ZIP updater, including the generated double-click CMD."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts'))
from build_external_updater import build, bundle

spec = importlib.util.spec_from_file_location('local_update_tests', ROOT / 'tests/update.test.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.fixture = module.UpdateTests()
        self.fixture.setUp()
        self.root = self.fixture.root
        self.download = self.root / '下载 更新包'
        self.download.mkdir()
        self.temp = self.root / '临时解压'
        self.temp.mkdir()
        self.launcher = build(self.download / '覆盖更新.cmd')
        self.script = self.root / 'outside-updater.ps1'
        self.script.write_text(bundle(), encoding='utf-8-sig')
        for name in ('Update-Mod.ps1', 'Update.cmd'):
            self.fixture.files.pop(name)
        self.fixture.metadata()
        self.zip_path = self.download / 'DeadlockTranslate-0.6.2-win-x64.zip'
        self.write_zip()

    def tearDown(self):
        self.fixture.tearDown()

    def write_zip(self, extras=(), version='0.6.2', name=None):
        target = self.download / (name or f'DeadlockTranslate-{version}-win-x64.zip')
        with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as z:
            for file in self.fixture.manifest['files']:
                z.write(self.fixture.package / file['file'], f'DeadlockTranslate-{version}/' + file['file'])
            z.write(self.fixture.package / 'package-manifest.json', f'DeadlockTranslate-{version}/package-manifest.json')
            for name, value in extras:
                z.writestr(name, value)
        return target

    def run_update(self):
        r = subprocess.run(['powershell.exe', '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass',
                            '-File', str(self.script), '-DownloadFolder', str(self.download),
                            '-StateDirectory', str(self.fixture.state), '-TempParent', str(self.temp),
                            '-NonInteractive'], capture_output=True, timeout=50)
        self.output = (r.stdout + r.stderr).decode('utf-8', errors='replace')
        return r.returncode

    def unchanged(self):
        before = [module.snapshot(p) for p in (self.fixture.runtime, self.fixture.state, self.fixture.game)]
        self.assertNotEqual(self.run_update(), 0, self.output)
        after = [module.snapshot(p) for p in (self.fixture.runtime, self.fixture.state, self.fixture.game)]
        self.assertEqual(after, before)
        self.assertEqual(list(self.temp.iterdir()), [])

    def assert_updated(self, version='0.6.2'):
        self.assertEqual(self.fixture.target.read_bytes(), self.fixture.files['dlt_translate_dir.vpk'])
        self.assertEqual((self.fixture.runtime / 'DeadlockTranslate.exe').read_bytes(), self.fixture.files['DeadlockTranslate.exe'])
        installed = json.loads((self.fixture.state / 'install-manifest.json').read_text(encoding='utf-8-sig'))
        self.assertEqual(installed['version'], version)
        self.assertEqual(installed['runtimeFolder'], str(self.fixture.runtime))
        self.assertEqual(list(self.temp.iterdir()), [])

    def test_auto_extract_and_paired_update_without_updater_inside_zip(self):
        with zipfile.ZipFile(self.zip_path) as z:
            self.assertNotIn('DeadlockTranslate-0.6.2/Update-Mod.ps1', z.namelist())
            self.assertNotIn('DeadlockTranslate-0.6.2/Update.cmd', z.namelist())
        self.assertEqual(self.run_update(), 0, self.output)
        self.assert_updated()
        self.assertTrue(self.zip_path.is_file())
        self.assertTrue((self.fixture.state / 'backups').is_dir())

    def test_highest_semantic_version_selected(self):
        self.fixture.metadata(version='0.6.10')
        self.write_zip(version='0.6.10')
        self.fixture.metadata(version='0.6.3')
        self.write_zip(version='0.6.3')
        self.assertEqual(self.run_update(), 0, self.output)
        self.assert_updated('0.6.10')

    def test_four_component_revision_selected_and_extracted(self):
        self.fixture.metadata(version='0.6.23.1')
        self.write_zip(version='0.6.23.1')
        self.assertEqual(self.run_update(), 0, self.output)
        self.assert_updated(version='0.6.23.1')

    def test_browser_duplicate_download_name(self):
        self.zip_path.rename(self.download / 'DeadlockTranslate-0.6.2-win-x64 (1).zip')
        self.assertEqual(self.run_update(), 0, self.output)
        self.assert_updated()

    def test_missing_zip_preserves_install(self):
        self.zip_path.unlink()
        self.unchanged()

    def test_invalid_zip_preserves_install(self):
        self.zip_path.write_bytes(b'truncated invalid ZIP')
        self.unchanged()

    def test_wrong_optional_checksum_preserves_install(self):
        self.zip_path.with_suffix('.zip.sha256').write_text('0' * 64 + '  ' + self.zip_path.name)
        self.unchanged()

    def test_matching_optional_checksum_updates(self):
        self.zip_path.with_suffix('.zip.sha256').write_text(module.digest(self.zip_path.read_bytes()) + '  ' + self.zip_path.name)
        self.assertEqual(self.run_update(), 0, self.output)
        self.assert_updated()

    def test_zip_traversal_rejected_before_extraction(self):
        self.write_zip(extras=[('DeadlockTranslate-0.6.2/../../escape.txt', b'escape')])
        self.unchanged()
        self.assertFalse((self.root / 'escape.txt').exists())

    def test_zip_duplicate_case_alias_rejected(self):
        self.write_zip(extras=[('DeadlockTranslate-0.6.2/WWWROOT/app.js', b'alias')])
        self.unchanged()

    def test_zip_windows_trailing_dot_alias_rejected(self):
        self.write_zip(extras=[('DeadlockTranslate-0.6.2/wwwroot/app.js.', b'alias')])
        self.unchanged()

    def test_zip_symlink_rejected(self):
        link = zipfile.ZipInfo('DeadlockTranslate-0.6.2/data/link')
        link.create_system = 3
        link.external_attr = 0o120777 << 16
        with zipfile.ZipFile(self.zip_path, 'a') as z:
            z.writestr(link, '../../outside')
        self.unchanged()

    def test_filename_and_package_version_mismatch_rejected(self):
        self.fixture.metadata(version='0.6.3')
        self.write_zip()
        self.unchanged()

    def test_bad_internal_hash_cleans_temporary_files(self):
        (self.fixture.package / 'wwwroot/app.js').write_bytes(b'corrupt')
        self.write_zip()
        self.unchanged()

    def test_actual_external_cmd_auto_finds_zip_and_install(self):
        # This invokes the same CMD users double-click. Both installation and ZIP
        # detection use defaults; it needs no adjacent PS1, Python or .NET SDK.
        local = self.root / 'local-data'
        local.mkdir()
        state = local / 'DeadlockTranslate'
        self.fixture.state.rename(state)
        self.fixture.state = state
        environment = os.environ.copy()
        environment.update(LOCALAPPDATA=str(local), TEMP=str(self.temp), TMP=str(self.temp))
        r = subprocess.run(['cmd.exe', '/d', '/c', str(self.launcher)], input=b'\r\n',
                           env=environment, capture_output=True, timeout=50)
        self.assertEqual(r.returncode, 0, (r.stdout + r.stderr).decode('utf-8', errors='replace'))
        self.assert_updated()
        self.assertEqual((state / 'secrets.dpapi').read_bytes(), b'PRIVATE bytes preserved: secrets.dpapi')
        self.assertEqual((self.fixture.target.parent / 'pak20_dir.vpk').read_bytes(), b'QOL must be preserved')


if __name__ == '__main__':
    unittest.main(verbosity=2)
