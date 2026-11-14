"""Batch launch options tests use fixture VDFs and never run the real Steam client."""
import importlib.util
from pathlib import Path
import unittest
import uuid

spec=importlib.util.spec_from_file_location('workflow',Path(__file__).with_name('installer-workflow.test.py'))
workflow=importlib.util.module_from_spec(spec);spec.loader.exec_module(workflow)


class SteamBatchTests(unittest.TestCase):
    def setUp(self):
        self.f=workflow.WorkflowTests();self.f.setUp();self.f.installed();self.f.simulate_certificate_gate()
        # Hide the real game's process only from these isolated PowerShell fixtures.
        mock=r"""
function Get-Process {
    [CmdletBinding()] param([string[]]$Name,[int]$Id)
    if($Name -and -not @($Name|Where-Object {$_ -notin @('deadlock','citadel')}).Count){return}
    Microsoft.PowerShell.Management\Get-Process @PSBoundParameters
}
"""
        for name in ('Installer-Workflow.ps1','Configure-Steam.ps1','Update-Mod.ps1'):
            script=self.f.script.parent/name
            source=script.read_text(encoding='utf-8-sig')
            anchor="$ErrorActionPreference='Stop'"
            assert anchor in source,name
            script.write_text(source.replace(anchor,anchor+'\n'+mock,1),encoding='utf-8-sig')
        self.one=self.f.account(1,'-novid')
        self.two=self.f.account(2,'+custom setting')
        self.other=self.f.account(3,'-untouched')

    def preview(self,ids):
        result=self.f.call('steam-review',userIds=ids)
        self.assertTrue(result['ok'],result)
        return result['data']

    def apply(self,ids,review):
        return self.f.call('steam-retry',userIds=ids,reviewedSteamToken=review['reviewToken'],transactionId=uuid.uuid4().hex)

    def test_preview_two_accounts_and_apply_only_selected(self):
        originals=[p.read_bytes() for p in (self.one,self.two,self.other)]
        listing=self.f.call('steam-accounts')
        self.assertTrue(listing['ok'],listing)
        self.assertEqual({a['id'] for a in listing['data']['users']},{'1','2','3'})
        preview=self.preview(['1','2']);self.assertFalse(preview['blocked'],preview)
        self.assertEqual([a['id'] for a in preview['accounts']],['1','2'])
        self.assertEqual(originals,[p.read_bytes() for p in (self.one,self.two,self.other)])
        result=self.apply(['1','2'],preview);self.assertTrue(result['ok'],result)
        for path,parameter in ((self.one,'-novid'),(self.two,'+custom setting')):
            text=path.read_text(encoding='utf-8-sig')
            self.assertIn('DeadlockTranslate.exe',text);self.assertIn(parameter,text)
        self.assertEqual(self.other.read_bytes(),originals[-1])

    def test_changed_selection_or_config_rejects_before_writing(self):
        preview=self.preview(['1','2']);before=self.one.read_bytes()
        changed=self.apply(['1','3'],preview);self.assertFalse(changed['ok'],changed)
        self.two.write_bytes(self.two.read_bytes()+b' ')
        changed=self.apply(['1','2'],preview);self.assertFalse(changed['ok'],changed)
        self.assertEqual(self.one.read_bytes(),before)

    def test_legacy_elevation_flag_removed_but_game_arguments_preserved(self):
        self.one.write_text(self.one.read_text(encoding='utf-8').replace('-novid',r'\"D:\\Old Mod\\DeadlockTranslate.exe\" --admin-compatible --steam-game %command% -novid -condebug'),encoding='utf-8')
        plan=self.preview(['1'])
        self.assertFalse(plan['blocked'],plan)
        result=self.apply(['1'],plan)
        self.assertTrue(result['ok'],result)
        text=self.one.read_text(encoding='utf-8-sig')
        self.assertNotIn('--admin-compatible',text)
        self.assertIn('--steam-game %command% -novid -condebug',text)
        self.assertEqual(text.count('%command%'),1)

    def test_unsupported_account_in_batch_is_shown_without_modifying_any(self):
        before=self.one.read_bytes()
        self.two.write_text(self.two.read_text(encoding='utf-8').replace('+custom setting','other-wrapper %command%'),encoding='utf-8')
        preview=self.preview(['1','2']);self.assertTrue(preview['blocked'],preview)
        self.assertTrue(preview['accounts'][1]['error'])
        self.assertFalse(self.apply(['1','2'],preview)['ok'])
        self.assertEqual(self.one.read_bytes(),before)

    def test_install_updates_checked_accounts_only(self):
        untouched=self.other.read_bytes()
        proposal=self.f.call('plan',configureSteam=True,userIds=['1','2'])
        self.assertTrue(proposal['ok'],proposal)
        self.assertEqual([a['id'] for a in proposal['data']['steamAccounts']],['1','2'])
        result=self.f.call('apply',configureSteam=True,userIds=['1','2'],transactionId=uuid.uuid4().hex,
                           reviewFingerprint=proposal['data']['reviewFingerprint'])
        self.assertTrue(result['ok'] and result['data']['steamDone'],result)
        self.assertIn('DeadlockTranslate.exe',self.one.read_text(encoding='utf-8-sig'))
        self.assertIn('DeadlockTranslate.exe',self.two.read_text(encoding='utf-8-sig'))
        self.assertEqual(self.other.read_bytes(),untouched)

    def test_second_account_write_failure_rolls_back_first(self):
        script=self.f.script.parent/'Configure-Steam.ps1'
        content=script.read_text(encoding='utf-8-sig')
        anchor='try {\n\nfunction Launch-Hash'
        self.assertIn(anchor,content)
        script.write_text(content.replace(anchor,"try {\nif($Apply -and $UserId -eq 2){throw 'fixture rejects second account write'}\n\nfunction Launch-Hash",1),encoding='utf-8-sig')
        originals=[path.read_bytes() for path in (self.one,self.two,self.other)]
        result=self.apply(['1','2'],self.preview(['1','2']))
        self.assertFalse(result['ok'],result)
        self.assertEqual(originals,[path.read_bytes() for path in (self.one,self.two,self.other)])

    def test_mixed_selection_skips_missing_record_and_updates_available_accounts(self):
        self.two.write_text(self.two.read_text(encoding='utf-8').replace('1422450','111'),encoding='utf-8')
        missing=self.two.read_bytes();untouched=self.other.read_bytes()
        preview=self.preview(['2','1'])
        self.assertFalse(preview['blocked'],preview)
        self.assertTrue(preview['accounts'][0]['skipped'])
        self.assertEqual(preview['accounts'][0]['code'],'steam-no-game-record')
        result=self.f.call('apply',configureSteam=True,userIds=['2','1'],transactionId=uuid.uuid4().hex)
        self.assertTrue(result['ok'] and result['data']['steamDone'],result)
        self.assertFalse(result['data']['pendingSteam'])
        self.assertIn('DeadlockTranslate.exe',self.one.read_text(encoding='utf-8-sig'))
        self.assertEqual(self.two.read_bytes(),missing)
        self.assertEqual(self.other.read_bytes(),untouched)

    def test_all_missing_accounts_complete_core_without_pending_or_writes(self):
        for path in (self.one,self.two):path.write_text(path.read_text(encoding='utf-8').replace('1422450','111'),encoding='utf-8')
        originals=[p.read_bytes() for p in (self.one,self.two,self.other)]
        result=self.f.call('apply',configureSteam=True,userIds=['1','2'],transactionId=uuid.uuid4().hex)
        self.assertTrue(result['ok'] and result['data']['coreDone'] and result['data']['complete'],result)
        self.assertFalse(result['data']['steamDone']);self.assertFalse(result['data']['pendingSteam'])
        self.assertTrue(all(a['skipped'] for a in result['data']['steamAccounts']))
        self.assertEqual(originals,[p.read_bytes() for p in (self.one,self.two,self.other)])
        self.assertEqual(self.f.call('inspect')['data']['pending'],[])

    def test_duplicate_record_is_blocked_not_classified_as_missing(self):
        self.two.write_text(self.two.read_text(encoding='utf-8').replace('"apps" {','"apps" { "1422450" { } ',1),encoding='utf-8')
        before=self.one.read_bytes()
        preview=self.preview(['1','2'])
        self.assertTrue(preview['blocked'],preview)
        self.assertFalse(preview['accounts'][1]['skipped'])
        self.assertEqual(preview['accounts'][1]['code'],'steam-duplicate-game-record')
        self.assertEqual(self.one.read_bytes(),before)

    def test_old_steam_only_pending_can_resume_after_package_changes(self):
        import json
        self.two.write_text(self.two.read_text(encoding='utf-8').replace('+custom setting','other-wrapper %command%'),encoding='utf-8')
        txn=uuid.uuid4().hex
        installed=self.f.call('apply',configureSteam=True,userIds=['1','2'],transactionId=txn)
        self.assertTrue(installed['ok'] and installed['data']['pendingSteam'],installed)
        manifest=self.f.payload/'package-manifest.json'
        value=json.loads(manifest.read_text(encoding='utf-8-sig'));value['buildId']='new-fixture-build';manifest.write_text(json.dumps(value),encoding='utf-8')
        self.two.write_text(self.two.read_text(encoding='utf-8').replace('1422450','111'),encoding='utf-8')
        recovered=self.f.call('restore-transaction',recoveryId=txn,resumeTransaction=True)
        self.assertTrue(recovered['ok'] and recovered['data']['steamOnly'] and recovered['data']['restored'],recovered)
        preview=self.preview(['1','2']);self.assertFalse(preview['blocked'])
        result=self.apply(['1','2'],preview)
        self.assertTrue(result['ok'] and result['data']['steamDone'],result)
        self.assertTrue(result['data']['accounts'][1]['skipped'])
        self.assertEqual(self.f.call('inspect')['data']['pending'],[])


if __name__=='__main__':unittest.main(verbosity=2)
