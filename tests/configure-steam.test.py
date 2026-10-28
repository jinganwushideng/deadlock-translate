"""Offline PowerShell 5.1 VDF edits use fake Steam/runtime paths, never the real client."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'scripts/Configure-Steam.ps1'


class SteamConfigTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='dlt-steam-doc-')
        # Hosted Windows runners may expose TEMP through an 8.3 alias. Use the
        # same canonical directory spelling as Resolve-Path in PowerShell.
        self.root = Path(self.temp.name).resolve()
        self.runtime = self.root / '翻译程序 空格'
        self.runtime.mkdir()
        (self.runtime / 'DeadlockTranslate.exe').write_bytes(b'fake EXE - never executed')
        self.steam = self.root / 'Steam'
        self.file = self.steam / 'userdata/123/config/localconfig.vdf'
        self.file.parent.mkdir(parents=True)
        self.result = self.root / 'result.json'
        self.env = os.environ.copy()
        self.env['LOCALAPPDATA'] = str(self.root / 'local-data')

    def tearDown(self):
        self.temp.cleanup()

    def fixture(self, launch=None, extras='', bom=False, app='1422450', newline='\r\n'):
        field = '' if launch is None else '\t\t\t\t\t\t"LaunchOptions" "' + launch.replace('\\', '\\\\').replace('"', '\\"') + '"' + newline
        text = ('"UserLocalConfigStore"' + newline + '{' + newline + '\t"friends" { "nickname" "样例 中文" }' + newline
                + '\t"Software" { "Valve" { "Steam" { "apps" {' + newline + '\t\t\t\t\t"' + app + '"' + newline
                + '\t\t\t\t\t{' + newline + '\t\t\t\t\t\t"lastplayed" "12345"' + newline + field + extras
                + '\t\t\t\t\t}' + newline + '\t\t\t\t\t"999" { "LaunchOptions" "other game unchanged" }' + newline
                + '\t\t\t\t} } } }' + newline + '}' + newline)
        self.file.write_bytes((b'\xef\xbb\xbf' if bom else b'') + text.encode('utf-8'))
        return self.file.read_bytes()

    def run_script(self, *extra):
        r = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(SCRIPT),
                            '-RuntimeFolder', str(self.runtime), '-SteamPath', str(self.steam), '-UserId', '123',
                            '-ResultFile', str(self.result), *extra], capture_output=True, env=self.env, timeout=25)
        self.output = (r.stdout + r.stderr).decode('utf-8', errors='replace')
        return r.returncode

    def saved(self):
        return json.loads(self.result.read_text(encoding='utf-8-sig'))

    def expected(self, args=''):
        return '"' + str(self.runtime / 'DeadlockTranslate.exe') + '" --steam-game %command%' + (' ' + args if args else '')

    def test_missing_field_inserted_and_backup_exact(self):
        before = self.fixture()
        self.assertEqual(self.run_script('-Apply'), 0, self.output)
        r = self.saved()
        self.assertEqual(r['actual'], self.expected())
        self.assertEqual(Path(r['backup']).read_bytes(), before)
        self.assertIn(b'other game unchanged', self.file.read_bytes())
        self.assertIn('样例 中文', self.file.read_text(encoding='utf-8'))

    def test_empty_existing_field_replaced_once(self):
        self.fixture('')
        self.assertEqual(self.run_script('-Apply'), 0, self.output)
        self.assertEqual(self.saved()['actual'], self.expected())
        self.assertEqual(self.file.read_text(encoding='utf-8').count('"LaunchOptions"'), 2)

    def test_normal_arguments_preserved(self):
        self.fixture('-novid -dev')
        self.assertEqual(self.run_script('-Apply'), 0, self.output)
        self.assertEqual(self.saved()['actual'], self.expected('-novid -dev'))

    def test_previous_translation_wrapper_replaced_without_nesting(self):
        self.fixture('"D:\\Old Mod\\DeadlockTranslate.exe" --steam-game %command% -novid')
        self.assertEqual(self.run_script('-Apply'), 0, self.output)
        actual = self.saved()['actual']
        self.assertEqual(actual, self.expected('-novid'))
        self.assertEqual(actual.count('%command%'), 1)

    def test_admin_compatible_option_is_explicit(self):
        self.fixture('-novid')
        self.assertEqual(self.run_script('-Apply', '-AdministratorCompatibility'), 0, self.output)
        self.assertEqual(self.saved()['actual'], self.expected('-novid').replace(' --steam-game', ' --admin-compatible --steam-game'))

    def test_admin_option_preserved_when_updating_runtime(self):
        self.fixture('"D:\\Old Mod\\DeadlockTranslate.exe" --admin-compatible --steam-game %command% -novid')
        self.assertEqual(self.run_script('-Apply'), 0, self.output)
        self.assertEqual(self.saved()['actual'], self.expected('-novid').replace(' --steam-game', ' --admin-compatible --steam-game'))

    def test_admin_option_removed_only_on_explicit_request(self):
        self.fixture('"D:\\Old Mod\\DeadlockTranslate.exe" --admin-compatible --steam-game %command% -novid')
        self.assertEqual(self.run_script('-Apply', '-NormalPermissions'), 0, self.output)
        self.assertEqual(self.saved()['actual'], self.expected('-novid'))

    def test_identical_options_are_noop_without_backup_or_shutdown(self):
        before = self.fixture(self.expected())
        self.assertEqual(self.run_script('-Apply', '-StopSteam', '-RestartSteam'), 0, self.output)
        r = self.saved()
        self.assertTrue(r['alreadyConfigured'])
        self.assertFalse(r['changed'])
        self.assertFalse(r['steamStoppedNormally'])
        self.assertFalse(r['steamRestartRequested'])
        self.assertIsNone(r['backup'])
        self.assertEqual(self.file.read_bytes(), before)

    def test_remove_only_matching_wrapper_keeps_game_arguments(self):
        self.fixture(self.expected('-novid -console').replace(' --steam-game',' --admin-compatible --steam-game'))
        self.assertEqual(self.run_script('-Apply', '-RemoveTranslator'), 0, self.output)
        self.assertEqual(self.saved()['actual'], '-novid -console')

    def test_remove_does_not_touch_another_runtime_or_wrapper(self):
        for launch in ['"D:\\Other\\DeadlockTranslate.exe" --steam-game %command%', '"D:\\Other\\loader.exe" %command%']:
            before = self.fixture(launch)
            self.assertEqual(self.run_script('-Apply', '-RemoveTranslator'), 0, self.output)
            self.assertEqual(self.file.read_bytes(), before)

    def test_remove_works_even_when_runtime_executable_was_deleted(self):
        self.fixture(self.expected('-novid'))
        (self.runtime / 'DeadlockTranslate.exe').unlink()
        self.assertEqual(self.run_script('-Apply', '-RemoveTranslator'), 0, self.output)
        self.assertEqual(self.saved()['actual'], '-novid')

    def test_inspect_is_readonly(self):
        before = self.fixture('')
        self.assertEqual(self.run_script(), 0, self.output)
        self.assertFalse(self.saved()['applied'])
        self.assertEqual(self.file.read_bytes(), before)
        self.assertFalse((Path(self.env['LOCALAPPDATA']) / 'DeadlockTranslate').exists())

    def test_bom_and_lf_preserved(self):
        self.fixture('', bom=True, newline='\n')
        self.assertEqual(self.run_script('-Apply'), 0, self.output)
        raw = self.file.read_bytes()
        self.assertTrue(raw.startswith(b'\xef\xbb\xbf'))
        self.assertNotIn(b'\r\n', raw)

    def test_unrelated_wrapper_rejected(self):
        before = self.fixture('"D:\\other.exe" %command%')
        self.assertNotEqual(self.run_script('-Apply'), 0)
        self.assertEqual(self.file.read_bytes(), before)

    def test_multiple_command_placeholders_rejected(self):
        before = self.fixture('"D:\\Old\\DeadlockTranslate.exe" --steam-game %command% %command%')
        self.assertNotEqual(self.run_script('-Apply'), 0)
        self.assertEqual(self.file.read_bytes(), before)

    def test_missing_deadlock_record_not_invented(self):
        before = self.fixture('', app='888')
        self.assertNotEqual(self.run_script('-Apply'), 0)
        self.assertEqual(self.file.read_bytes(), before)

    def test_duplicate_launch_fields_rejected(self):
        before = self.fixture('', extras='\t\t\t\t\t\t"LaunchOptions" "duplicate"\r\n')
        self.assertNotEqual(self.run_script('-Apply'), 0)
        self.assertEqual(self.file.read_bytes(), before)

    def test_unbalanced_vdf_rejected(self):
        self.fixture('')
        before = self.file.read_bytes()[:-3]
        self.file.write_bytes(before)
        self.assertNotEqual(self.run_script('-Apply'), 0)
        self.assertEqual(self.file.read_bytes(), before)

    def test_minified_block_can_insert_field(self):
        self.file.write_text('"UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "1422450" { "lastplayed" "1" } } } } } }', encoding='utf-8')
        self.assertEqual(self.run_script('-Apply'), 0, self.output)
        self.assertEqual(self.saved()['actual'], self.expected())


if __name__ == '__main__':
    unittest.main(verbosity=2)
