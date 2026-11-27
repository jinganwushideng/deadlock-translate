"""Exercise abandoned installer transactions in isolated game and Steam fixtures."""
import importlib.util
import ctypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import unittest
import uuid

path = Path(__file__).with_name('installer-workflow.test.py')
spec = importlib.util.spec_from_file_location('workflow', path)
workflow = importlib.util.module_from_spec(spec)
spec.loader.exec_module(workflow)

def dpapi(path, values=None):
    operation = 'Unprotect' if values is None else 'Protect'
    output = '[Console]::Write([Text.Encoding]::UTF8.GetString($result))' if values is None else '[IO.File]::WriteAllBytes($path,$result)'
    command = (
        "Add-Type -AssemblyName System.Security; $path=$env:DLT_TEST_SECRET_FILE; "
        + ("$bytes=[IO.File]::ReadAllBytes($path); " if values is None else "$bytes=[Text.Encoding]::UTF8.GetBytes([Console]::In.ReadToEnd()); ")
        + f"$result=[Security.Cryptography.ProtectedData]::{operation}($bytes,$null,'CurrentUser'); {output}"
    )
    result = subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-Command',command], capture_output=True,
                            input=b'' if values is None else json.dumps(values).encode(),
                            env={**os.environ,'DLT_TEST_SECRET_FILE':str(path)}, check=True, timeout=30)
    if values is None:
        return json.loads(result.stdout)


class TransactionTests(unittest.TestCase):
    def setUp(self):
        self.f = workflow.WorkflowTests()
        self.f.setUp()
        self.f.simulate_certificate_gate()
        self.transaction = uuid.uuid4().hex
        self.folder = self.f.state / 'transactions' / self.transaction

    def interrupt_after(self, marker, **options):
        path = self.f.script
        original = path.read_text(encoding='utf-8-sig')
        self.assertIn(marker, original)
        path.write_text(original.replace(marker, marker + '\n                    exit 79', 1), encoding='utf-8-sig')
        request = self.f.root / 'crash-request.json'
        result = self.f.root / 'crash-result.json'
        request.write_text(json.dumps(dict(self.f.q, action='apply', transactionId=self.transaction, **options)), encoding='utf-8-sig')
        try:
            child = subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(path),'-RequestFile',str(request),'-ResultFile',str(result)], capture_output=True, env=self.f.env, timeout=45)
            self.assertEqual(child.returncode, 79, (child.stdout,child.stderr,result.read_bytes() if result.exists() else b''))
            self.assertFalse(result.exists())
        finally:
            path.write_text(original, encoding='utf-8-sig')

    def test_transactional_fresh_install_and_cleanup(self):
        reply = self.f.call('apply', transactionId=self.transaction)
        self.assertTrue(reply['ok'], reply)
        self.assertEqual(json.loads((self.folder/'transaction.json').read_text())['phase'], 'complete')
        self.assertFalse((self.folder/'payload').exists())
        self.assertEqual(self.f.target.read_bytes(),self.f.vpk)

    def test_second_operation_cannot_write_while_install_lock_is_held(self):
        lock = self.f.state/'installation-operation.lock'
        create = ctypes.windll.kernel32.CreateFileW
        create.restype = ctypes.c_void_p
        create.argtypes = [ctypes.c_wchar_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_void_p]
        handle = create(str(lock), 0xC0000000, 0, None, 4, 0, None)
        self.assertNotEqual(handle, ctypes.c_void_p(-1).value)
        try:
            result = self.f.call('apply', transactionId=self.transaction)
            self.assertFalse(result['ok'], result)
            self.assertEqual(result['failure']['code'], 'operation-active')
            self.assertFalse(self.f.target.exists())
            self.assertFalse(self.folder.exists())
        finally:
            ctypes.windll.kernel32.CloseHandle(ctypes.c_void_p(handle))
        reply = self.f.call('apply', transactionId=self.transaction)
        self.assertTrue(reply['ok'], reply)
        self.assertEqual(self.f.target.read_bytes(), self.f.vpk)

    def test_process_exit_after_record_and_vpk_can_restore(self):
        gameinfo = self.f.game / 'game/citadel/gameinfo.gi'
        old = gameinfo.read_bytes()
        self.interrupt_after("Run 'Manage-Mod.ps1' @('-Action','Install','-PackageFolder',$runtime,'-GamePath',$game,'-StateDirectory',$state,'-AllowRelated','-NonInteractive')")
        self.assertTrue(self.f.target.exists())
        self.assertTrue((self.f.state/'install-manifest.json').exists())
        view = self.f.call('inspect')
        self.assertEqual(view['data']['pending'][0]['id'], self.transaction)
        reply = self.f.call('restore-transaction', recoveryId=self.transaction, resumeTransaction=True)
        self.assertTrue(reply['ok'], reply)
        self.assertTrue(reply['data']['restored'], reply)
        self.assertEqual(gameinfo.read_bytes(), old)
        self.assertFalse(self.f.target.exists())
        self.assertFalse((self.f.runtime/'DeadlockTranslate.exe').exists())
        self.assertFalse((self.f.state/'install-manifest.json').exists())

    def test_changed_vpk_is_preserved_while_other_files_restore(self):
        self.interrupt_after("Run 'Manage-Mod.ps1' @('-Action','Install','-PackageFolder',$runtime,'-GamePath',$game,'-StateDirectory',$state,'-AllowRelated','-NonInteractive')")
        self.f.target.write_bytes(b'changed by another mod')
        reply = self.f.call('restore-transaction', recoveryId=self.transaction)
        self.assertTrue(reply['ok'], reply)
        self.assertFalse(reply['data']['restored'])
        self.assertEqual(reply['data']['issues'][0]['reason'],'changed')
        self.assertEqual(self.f.target.read_bytes(), b'changed by another mod')
        self.assertFalse((self.f.runtime/'DeadlockTranslate.exe').exists())
        accepted = self.f.call('restore-transaction', recoveryId=self.transaction, acceptChanged=True)
        self.assertTrue(accepted['data']['preserved'], accepted)
        self.assertEqual(self.f.call('inspect')['data']['pending'], [])
        self.assertEqual(self.f.target.read_bytes(), b'changed by another mod')

    def test_abandoned_update_restores_previous_runtime_and_record(self):
        self.f.installed()
        record = self.f.state/'install-manifest.json'
        before = record.read_bytes()
        self.interrupt_after("Run 'Update-Mod.ps1' $a")
        self.assertNotEqual(record.read_bytes(),before)
        reply = self.f.call('restore-transaction', recoveryId=self.transaction)
        self.assertTrue(reply['ok'] and reply['data']['restored'], reply)
        self.assertEqual(record.read_bytes(),before)
        self.assertEqual((self.f.runtime/'DeadlockTranslate.exe').read_bytes(),b'never run')

    def prepare_lite_switch(self):
        self.f.installed()
        worker = self.f.runtime/'voice/DeadlockVoiceWorker.exe'
        worker.parent.mkdir()
        worker.write_bytes(b'owned full edition worker')
        manifest = self.f.runtime/'package-manifest.json'
        full = json.loads(manifest.read_text())
        full['files'] = [dict(file='voice/DeadlockVoiceWorker.exe',sha256=hashlib.sha256(worker.read_bytes()).hexdigest())]
        manifest.write_text(json.dumps(full))
        manifest = self.f.payload/'package-manifest.json'
        lite = json.loads(manifest.read_text())
        lite['edition'] = 'lite'
        manifest.write_text(json.dumps(lite))
        return worker

    def test_gui_lite_switch_backs_up_and_retires_voice(self):
        worker = self.prepare_lite_switch()
        reply = self.f.call('apply', transactionId=self.transaction)
        self.assertTrue(reply['ok'], reply)
        self.assertFalse(worker.exists())
        self.assertEqual(next(self.f.state.glob('backups/update-*/retired/voice/DeadlockVoiceWorker.exe')).read_bytes(),
                         b'owned full edition worker')
        self.assertEqual(json.loads((self.f.state/'install-manifest.json').read_text())['edition'], 'lite')
        self.assertEqual(json.loads((self.folder/'transaction.json').read_text())['phase'], 'complete')

    def test_interrupted_gui_lite_switch_restores_retired_voice(self):
        worker = self.prepare_lite_switch()
        source = self.f.state/'settings.json'
        source.write_text('{"outgoingHotkey":"F8","mode":"quality"}',encoding='utf8')
        profile = self.f.state/'lite'
        profile.mkdir()
        settings = profile/'settings.json'
        settings.write_text('{"outgoingHotkey":"F9","mode":"fast"}',encoding='utf8')
        before = settings.read_bytes()
        dpapi(self.f.state/'secrets.dpapi', {'llm':'fixture-full-llm','azure':'fixture-full-azure'})
        keys = profile/'secrets.dpapi'
        dpapi(keys, {'llm':'fixture-old-lite','azure':'fixture-lite-azure'})
        encrypted_before = keys.read_bytes()
        self.interrupt_after("Run 'Update-Mod.ps1' $a")
        self.assertFalse(worker.exists())
        self.assertEqual(json.loads(settings.read_text())['outgoingHotkey'],'F8')
        self.assertEqual(dpapi(keys), {'llm':'fixture-full-llm','azure':'fixture-lite-azure'})
        for folder in (self.folder, self.f.state/'backups'):
            self.assertFalse(any(b'fixture-full-llm' in p.read_bytes() for p in folder.rglob('*') if p.is_file()))
        reply = self.f.call('restore-transaction', recoveryId=self.transaction)
        self.assertTrue(reply['ok'] and reply['data']['restored'], reply)
        self.assertEqual(worker.read_bytes(), b'owned full edition worker')
        self.assertEqual(settings.read_bytes(),before)
        self.assertEqual(keys.read_bytes(), encrypted_before)
        self.assertFalse((profile/'terms.json').exists())

    def test_llm_configuration_and_key_follow_both_edition_switches(self):
        self.prepare_lite_switch()
        full_profile = self.f.state/'settings.json'
        full_keys = self.f.state/'secrets.dpapi'
        settings = dict(llmBaseUrl='https://api.deepseek.com',llmPreset='deepseek',model='deepseek-v4-pro',
                        llmStreaming=False,llmMaxTokens=2048,llmThinkingEnabled=False,llmIndependentProfiles=True,
                        incomingModel='deepseek-flash',outgoingModel='',incomingThinking=None,outgoingThinking=True)
        full_profile.write_text(json.dumps({**settings,'voiceInputEnabled':True,'llmCustomBuildTextModel':'full-only'}))
        dpapi(full_keys, {'llm':'fixture-full-llm','azure':'fixture-full-azure'})
        result = self.f.call('apply',transactionId=self.transaction)
        self.assertTrue(result['ok'],result)
        lite_profile = self.f.state/'lite/settings.json'
        lite_keys = self.f.state/'lite/secrets.dpapi'
        self.assertEqual(dpapi(lite_keys), {'llm':'fixture-full-llm'})
        lite_config = json.loads(lite_profile.read_text())
        self.assertEqual({name:lite_config[name] for name in settings}, settings)
        self.assertEqual((lite_config['mode'],lite_config['outgoingMode']), ('fast','fast'))
        self.assertNotIn('voiceInputEnabled', lite_config)

        custom = dict(llmBaseUrl='https://custom.example.test/v1',llmPreset='custom',model='custom-chat',
                      llmCustomBaseUrl='https://custom.example.test/v1',llmCustomModel='custom-chat',
                      llmIndependentProfiles=False,incomingModel='',outgoingThinking=None)
        lite_profile.write_text(json.dumps({**lite_config,**custom}))
        dpapi(lite_keys, {'llm':'fixture-new-lite','azure':'fixture-lite-azure'})
        manifest = self.f.payload/'package-manifest.json'
        package = json.loads(manifest.read_text())
        package.update(version='0.7.6.1',edition='full')
        manifest.write_text(json.dumps(package))
        result = self.f.call('apply',transactionId=uuid.uuid4().hex)
        self.assertTrue(result['ok'],result)
        self.assertEqual(dpapi(full_keys), {'llm':'fixture-new-lite','azure':'fixture-full-azure'})
        full_config = json.loads(full_profile.read_text())
        self.assertEqual({name:full_config[name] for name in custom}, custom)
        self.assertTrue(full_config['voiceInputEnabled'])
        self.assertEqual(full_config['llmCustomBuildTextModel'],'full-only')

    def test_unkeyed_source_does_not_retarget_destination_key(self):
        self.prepare_lite_switch()
        (self.f.state/'settings.json').write_text('{"llmBaseUrl":"https://other.example.test","outgoingHotkey":"F8"}')
        profile = self.f.state/'lite'
        profile.mkdir()
        settings = profile/'settings.json'
        settings.write_text('{"llmBaseUrl":"https://api.deepseek.com","model":"deepseek-v4-pro"}')
        keys = profile/'secrets.dpapi'
        dpapi(keys, {'llm':'fixture-existing-key'})
        before = keys.read_bytes()
        result = self.f.call('apply',transactionId=self.transaction)
        self.assertTrue(result['ok'],result)
        self.assertEqual(keys.read_bytes(), before)
        config = json.loads(settings.read_text())
        self.assertEqual((config['llmBaseUrl'],config['model'],config['outgoingHotkey']),
                         ('https://api.deepseek.com','deepseek-v4-pro','F8'))

    def test_legacy_lite_upgrade_imports_saved_full_llm(self):
        self.prepare_lite_switch()
        manifest = self.f.runtime/'package-manifest.json'
        installed = json.loads(manifest.read_text())
        manifest.write_text(json.dumps({**installed,'edition':'lite'}))
        (self.f.state/'settings.json').write_text(json.dumps(dict(
            llmBaseUrl='https://test.example.test/v1', llmPreset='custom', model='test-model',
            llmStreaming=False, incomingTargetLanguage='en', outgoingHotkey='F9')))
        dpapi(self.f.state/'secrets.dpapi', {'llm':'fixture-full-llm','azure':'fixture-full-azure'})
        lite = self.f.state/'lite'
        lite.mkdir()
        settings = lite/'settings.json'
        settings.write_text(json.dumps(dict(
            llmBaseUrl='https://api.deepseek.com/v1', llmPreset='deepseek', model='deepseek-flash',
            incomingTargetLanguage='ja', outgoingHotkey='F8')))
        keys = lite/'secrets.dpapi'
        dpapi(keys, {'azure':'fixture-lite-azure'})
        result = self.f.call('apply',transactionId=self.transaction)
        self.assertTrue(result['ok'],result)
        self.assertEqual(dpapi(keys), {'llm':'fixture-full-llm','azure':'fixture-lite-azure'})
        saved = json.loads(settings.read_text())
        self.assertEqual((saved['llmBaseUrl'],saved['model'],saved['llmStreaming']),
                         ('https://test.example.test/v1','test-model',False))
        self.assertEqual((saved['incomingTargetLanguage'],saved['outgoingHotkey']), ('ja','F8'))
        self.assertTrue(json.loads((self.f.state/'install-manifest.json').read_text())['llmProfileMigration'])

    def test_existing_lite_credential_wins_on_upgrade(self):
        self.prepare_lite_switch()
        manifest = self.f.runtime/'package-manifest.json'
        installed = json.loads(manifest.read_text())
        manifest.write_text(json.dumps({**installed,'edition':'lite'}))
        (self.f.state/'settings.json').write_text('{"llmBaseUrl":"https://test.example.test/v1"}')
        dpapi(self.f.state/'secrets.dpapi', {'llm':'fixture-full-llm'})
        lite = self.f.state/'lite'
        lite.mkdir()
        settings = lite/'settings.json'
        settings.write_text('{"model":"deepseek-flash"}')
        keys = lite/'secrets.dpapi'
        dpapi(keys, {'llm':'fixture-already-saved-in-lite'})
        before = (settings.read_bytes(), keys.read_bytes())
        result = self.f.call('apply',transactionId=self.transaction)
        self.assertTrue(result['ok'],result)
        self.assertEqual((settings.read_bytes(), keys.read_bytes()),before)

    def test_gui_lite_update_can_journal_personal_terms(self):
        self.prepare_lite_switch()
        manifest = self.f.runtime/'package-manifest.json'
        installed = json.loads(manifest.read_text())
        installed['edition'] = 'lite'
        manifest.write_text(json.dumps(installed))
        profile = self.f.state/'lite'
        profile.mkdir()
        lite_terms = profile/'terms.json'
        custom = dict(source='custom',target='个人词条',english='English',kind='name',ambiguous=False)
        lite_terms.write_text(json.dumps([custom]), encoding='utf-8')
        full_terms = self.f.state/'terms.json'
        full_terms.write_text('[]', encoding='utf-8')
        reply = self.f.call('apply', transactionId=self.transaction)
        self.assertTrue(reply['ok'], reply)
        self.assertEqual(json.loads(lite_terms.read_text(encoding='utf-8-sig')), [custom])
        self.assertEqual(full_terms.read_text(encoding='utf-8'), '[]')

    def test_independent_lite_version_can_replace_full_and_legacy_lite(self):
        self.prepare_lite_switch()
        payload = self.f.payload/'package-manifest.json'
        metadata = json.loads(payload.read_text())
        metadata.update(version='0.1.0',releaseTag='lite-v0.1.0')
        payload.write_text(json.dumps(metadata))
        full_plan = self.f.call('plan')
        self.assertTrue(full_plan['ok'],full_plan)
        self.assertEqual(full_plan['data']['installedEdition'],'full')
        installed = self.f.runtime/'package-manifest.json'
        old = json.loads(installed.read_text())
        old['edition'] = 'lite'
        installed.write_text(json.dumps(old))
        legacy_plan = self.f.call('plan')
        self.assertTrue(legacy_plan['ok'],legacy_plan)
        self.assertEqual(legacy_plan['data']['installedEdition'],'lite')
        result = self.f.call('apply',transactionId=self.transaction)
        self.assertTrue(result['ok'],result)
        record=json.loads((self.f.state/'install-manifest.json').read_text())
        self.assertEqual((record['version'],record['releaseTag']),('0.1.0','lite-v0.1.0'))
        metadata.update(version='0.0.9',releaseTag='lite-v0.0.9')
        payload.write_text(json.dumps(metadata))
        self.assertFalse(self.f.call('plan')['ok'])

    def test_revalidation_rejects_changed_gameinfo(self):
        preview = self.f.call('plan')
        self.assertTrue(preview['ok'], preview)
        gameinfo = self.f.game/'game/citadel/gameinfo.gi'
        gameinfo.write_bytes(gameinfo.read_bytes()+b' ')
        apply = self.f.call('apply', transactionId=self.transaction, reviewFingerprint=preview['data']['reviewFingerprint'])
        self.assertFalse(apply['ok'], apply)
        self.assertEqual(apply['failure']['code'],'file-changed')
        self.assertFalse(self.f.target.exists())

    def test_steam_only_failure_preserves_core_and_can_be_resolved(self):
        self.f.account(1, launch='foreign-wrapper %command%')
        reply = self.f.call('apply', transactionId=self.transaction, configureSteam=True)
        self.assertTrue(reply['ok'], reply)
        self.assertTrue(reply['data']['pendingSteam'])
        reply = self.f.call('restore-transaction', recoveryId=self.transaction, keepCore=True)
        self.assertTrue(reply['ok'], reply)
        self.assertTrue(reply['data']['steamOnly'])
        self.assertTrue(self.f.target.exists())
        self.assertTrue((self.f.state/'install-manifest.json').exists())

    def test_health_is_read_only(self):
        old = self.f.installed()
        self.f.target.unlink()
        record = self.f.state/'install-manifest.json'
        previous = record.read_bytes()
        reply = self.f.call('health')
        self.assertTrue(reply['ok'], reply)
        self.assertTrue(any(x['id']=='vpk' and x['action']=='repair-core' for x in reply['data']['checks']))
        self.assertEqual(record.read_bytes(),previous)
        self.assertFalse(self.f.target.exists())


if __name__ == '__main__':
    unittest.main(verbosity=2)
