"""Runtime selection, preview binding and migration with isolated game files."""
import importlib.util
import json
from pathlib import Path
import unittest
import uuid


spec=importlib.util.spec_from_file_location('workflow',Path(__file__).with_name('installer-workflow.test.py'))
workflow=importlib.util.module_from_spec(spec)
spec.loader.exec_module(workflow)


class RuntimeChoiceTests(unittest.TestCase):
    def setUp(self):
        self.f=workflow.WorkflowTests()
        self.f.setUp()
        self.f.simulate_certificate_gate()
        self.target=self.f.root/'new runtime'
        self.record=self.f.state/'install-manifest.json'

    def approve_current(self):
        proposal=self.f.call('plan',runtime=str(self.target))
        self.assertTrue(proposal['ok'],proposal)
        choice=proposal['data']['runtimeChoice']
        self.assertTrue(choice['canUseCurrent'],choice)
        approval=dict(runtime=str(self.target),runtimeChoice='current',runtimeChoicePath=choice['current'],runtimeChoiceRecordHash=choice['recordHash'])
        plan=self.f.call('plan',**approval)
        self.assertTrue(plan['ok'],plan)
        return approval,plan['data']

    def apply(self,plan,**fields):
        return self.f.call('apply',transactionId=uuid.uuid4().hex,reviewFingerprint=plan['reviewFingerprint'],
                           reviewedRecordHash=plan['recordHash'],**fields)

    def test_blank_fresh_install_uses_default_and_writes_to_that_location(self):
        expected=Path(self.f.env['LOCALAPPDATA'])/'Programs/DeadlockTranslate'
        preview=self.f.call('plan',runtime='   ')
        self.assertTrue(preview['ok'],preview)
        self.assertTrue(preview['data']['runtimeDefaulted'])
        self.assertEqual(Path(preview['data']['runtime']),expected)
        self.assertFalse(expected.exists())
        reply=self.apply(preview['data'],runtime=str(expected))
        self.assertTrue(reply['ok'],reply)
        self.assertTrue((expected/'DeadlockTranslate.exe').is_file())

    def test_blank_runtime_uses_valid_previous_path_for_plan_and_record_recovery(self):
        self.f.installed();before=self.record.read_bytes()
        for action in ('plan','recover'):
            result=self.f.call(action,runtime='')
            self.assertTrue(result['ok'],result)
            self.assertEqual(Path(result['data']['runtime']),self.f.runtime)
        self.assertEqual(self.record.read_bytes(),before)

    def test_old_record_without_valid_runtime_uses_empty_path_and_preserves_vpk_ownership(self):
        old=self.f.installed();old['runtimeFolder']='invalid:relative';self.record.write_text(json.dumps(old),encoding='utf-8')
        self.assertTrue(self.f.call('inspect')['data']['runtime'])
        preview=self.f.call('plan',runtime='')
        self.assertTrue(preview['ok'],preview)
        plan=preview['data'];self.assertEqual(plan['runtimeMove']['kind'],'recover-missing-path')
        reply=self.apply(plan,runtime=plan['runtime'])
        self.assertTrue(reply['ok'],reply)
        self.assertEqual(json.loads(self.record.read_text(encoding='utf-8-sig'))['target'],str(self.f.target))
        self.assertTrue((Path(plan['runtime'])/'DeadlockTranslate.exe').exists())

    def test_choose_previous_reuses_old_directory(self):
        self.f.installed();before=self.record.read_bytes()
        choice=self.f.call('plan',runtime=str(self.target))['data']['runtimeChoice']
        self.assertEqual(Path(choice['previous']),self.f.runtime)
        self.assertFalse(self.target.exists());self.assertEqual(before,self.record.read_bytes())
        plan=self.f.call('plan',runtime=choice['previous'])
        self.assertTrue(plan['ok'],plan)
        self.assertIsNone(plan['data']['runtimeMove'])
        reply=self.apply(plan['data'],runtime=choice['previous'])
        self.assertTrue(reply['ok'],reply)
        self.assertFalse(self.target.exists())

    def test_choose_current_copies_program_and_retargets_steam_without_touching_old_directory(self):
        self.f.installed()
        before={p.relative_to(self.f.runtime):p.read_bytes() for p in self.f.runtime.rglob('*') if p.is_file()}
        old_wrapper=f'"{self.f.runtime / "DeadlockTranslate.exe"}" --steam-game %command% -novid'
        vdf=self.f.account(1,old_wrapper)
        self.f.q['configureSteam']=True
        approval,plan=self.approve_current()
        self.assertTrue(plan['launch'].endswith(' -novid'),plan['launch'])
        self.assertFalse(self.target.exists())
        result=self.apply(plan,**approval)
        self.assertTrue(result['ok'] and result['data']['steamDone'],result)
        self.assertEqual(json.loads(self.record.read_text(encoding='utf-8-sig'))['runtimeFolder'],str(self.target))
        self.assertEqual((self.target/'DeadlockTranslate.exe').read_bytes(),b'never executed')
        self.assertEqual({p.relative_to(self.f.runtime):p.read_bytes() for p in self.f.runtime.rglob('*') if p.is_file()},before)
        vdf_text=vdf.read_text(encoding='utf-8')
        self.assertIn(str(self.target).replace('\\','\\\\'),vdf_text)
        self.assertIn('-novid',vdf_text)

    def test_foreign_file_appearing_after_preview_stops_before_update(self):
        self.f.installed();before=self.record.read_bytes()
        approval,plan=self.approve_current()
        self.target.mkdir();foreign=self.target/'DeadlockTranslate.exe';foreign.write_bytes(b'foreign')
        result=self.apply(plan,**approval)
        self.assertFalse(result['ok'],result)
        self.assertEqual(result['failure']['code'],'runtime-choice-required')
        self.assertEqual(foreign.read_bytes(),b'foreign')
        self.assertEqual(before,self.record.read_bytes())

    def test_nonempty_choice_still_offers_previous(self):
        self.f.installed();self.target.mkdir();(self.target/'other.txt').write_text('other')
        proposal=self.f.call('plan',runtime=str(self.target))
        self.assertTrue(proposal['ok'],proposal)
        choice=proposal['data']['runtimeChoice']
        self.assertFalse(choice['canUseCurrent'])
        self.assertEqual(Path(choice['previous']),self.f.runtime)


if __name__=='__main__':unittest.main(verbosity=2)
