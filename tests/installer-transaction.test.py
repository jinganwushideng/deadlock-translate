"""Exercise abandoned installer transactions in isolated game and Steam fixtures."""
import importlib.util
import json
from pathlib import Path
import subprocess
import unittest
import uuid

path = Path(__file__).with_name('installer-workflow.test.py')
spec = importlib.util.spec_from_file_location('workflow', path)
workflow = importlib.util.module_from_spec(spec)
spec.loader.exec_module(workflow)


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
