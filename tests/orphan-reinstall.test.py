"""Reproduce deleted game VPK + lost install record + surviving runtime, in isolated folders."""
import hashlib,importlib.util,json,shutil,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('fixture_workflow',ROOT/'tests/installer-workflow.test.py');fixture=importlib.util.module_from_spec(spec);spec.loader.exec_module(fixture)
class OrphanReinstallTests(unittest.TestCase):
 def setUp(self):
  self.f=fixture.WorkflowTests();self.f.setUp();self.f.simulate_certificate_gate();self.record=self.f.state/'install-manifest.json'
 def install_runtime(self):
  for p in self.f.payload.rglob('*'):
   if p.is_file():target=self.f.runtime/p.relative_to(self.f.payload);target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,target)
 def test_supplied_log_scenario_reinstalls_without_record_or_game_vpk(self):
  self.install_runtime();(self.f.state/'settings.json').write_text('PRIVATE SETTINGS');(self.f.state/'secrets.dpapi').write_bytes(b'PRIVATE SECRET');(self.f.runtime/'notes.txt').write_text('KEEP')
  plan=self.f.call('plan');self.assertTrue(plan['ok'],plan);self.assertEqual(plan['data']['reinstall']['kind'],'reinstall-in-place');self.assertEqual(plan['data']['gameVpk']['state'],'missing');self.assertFalse(self.record.exists())
  r=self.f.call('apply',repairMissing=True,repairGameVpk=True,recoveryTarget=plan['data']['gameVpk']['target']);self.assertTrue(r['ok'],r);self.assertEqual(self.f.target.read_bytes(),self.f.vpk);self.assertEqual((self.f.state/'settings.json').read_text(),'PRIVATE SETTINGS');self.assertEqual((self.f.state/'secrets.dpapi').read_bytes(),b'PRIVATE SECRET');self.assertEqual((self.f.runtime/'notes.txt').read_text(),'KEEP');self.assertEqual(json.loads(self.record.read_text(encoding='utf-8-sig'))['target'],str(self.f.target))
 def test_known_orphan_renamed_game_vpk_is_reassociated_without_duplicate(self):
  self.install_runtime();renamed=self.f.target.with_name('pak95_dir.vpk');renamed.write_bytes(self.f.vpk);p=self.f.call('plan');self.assertTrue(p['ok'],p);self.assertEqual(p['data']['conflicts'],[]);self.assertEqual(p['data']['gameVpk']['state'],'renamed');r=self.f.call('apply');self.assertTrue(r['ok'],r);self.assertFalse(self.f.target.exists());self.assertEqual(json.loads(self.record.read_text(encoding='utf-8-sig'))['target'],str(renamed))
 def test_unknown_residue_is_left_untouched_and_installed_into_new_empty_folder(self):
  (self.f.runtime/'personal.txt').write_text('KEEP');p=self.f.call('plan');self.assertTrue(p['ok'],p);new=Path(p['data']['runtime']);self.assertFalse(new.exists());self.assertFalse(self.record.exists());r=self.f.call('apply',runtime=str(new));self.assertTrue(r['ok'],r);self.assertTrue((new/'DeadlockTranslate.exe').exists());self.assertEqual((self.f.runtime/'personal.txt').read_text(),'KEEP');self.assertEqual(json.loads(self.record.read_text(encoding='utf-8-sig'))['runtimeFolder'],str(new))
 def test_repair_mode_also_reinstalls_orphaned_runtime(self):
  self.install_runtime();p=self.f.call('plan',mode='repair');self.assertTrue(p['ok'],p);self.assertTrue(p['data']['fullRepair']);r=self.f.call('apply',mode='repair');self.assertTrue(r['ok'],r);self.assertTrue(self.f.target.exists())
 def test_corrupt_record_backed_up_and_rebuilt(self):
  self.install_runtime();self.record.write_text('broken record');p=self.f.call('plan');self.assertTrue(p['ok'],p);self.assertEqual(p['data']['reinstall']['recordState'],'invalid');self.assertEqual(self.record.read_text(),'broken record');r=self.f.call('apply');self.assertTrue(r['ok'],r);self.assertEqual(next(self.f.state.glob('backups/gui-*/invalid-install-manifest.json')).read_text(),'broken record');self.assertEqual(json.loads(self.record.read_text(encoding='utf-8-sig'))['vpkHash'],hashlib.sha256(self.f.vpk).hexdigest())
 def test_unknown_residue_with_corrupt_record_uses_fresh_install(self):
  (self.f.runtime/'keep.txt').write_text('KEEP');self.record.write_text('broken record');p=self.f.call('plan');self.assertTrue(p['ok'],p);r=self.f.call('apply',runtime=p['data']['runtime']);self.assertTrue(r['ok'],r);self.assertEqual((self.f.runtime/'keep.txt').read_text(),'KEEP');self.assertEqual(next(self.f.state.glob('backups/gui-*/invalid-install-manifest.json')).read_text(),'broken record')
 def test_partial_runtime_without_vpk_uses_fresh_empty_folder(self):
  self.install_runtime();(self.f.runtime/'dlt_translate_dir.vpk').unlink();p=self.f.call('plan');self.assertTrue(p['ok'],p);self.assertEqual(p['data']['reinstall']['kind'],'fresh-directory');r=self.f.call('apply',runtime=p['data']['runtime']);self.assertTrue(r['ok'],r);self.assertTrue(self.f.target.exists())
 def test_uninstall_without_record_is_redirected_to_reinstallation(self):
  self.install_runtime();p=self.f.call('plan',mode='uninstall');self.assertTrue(p['ok'],p);self.assertTrue(p['data']['reinstallSuggested']);self.assertFalse(self.record.exists());self.assertTrue((self.f.runtime/'DeadlockTranslate.exe').exists())
 def test_record_recovery_without_game_vpk_provides_reinstall_instead_of_error(self):
  self.install_runtime();r=self.f.call('recover');self.assertTrue(r['ok'],r);self.assertTrue(r['data']['reinstallRequired']);self.assertFalse(self.record.exists())
 def test_recover_when_old_runtime_vpk_deleted_still_provides_reinstall(self):
  r=self.f.call('recover');self.assertTrue(r['ok'],r);self.assertTrue(r['data']['reinstallRequired']);self.assertFalse(self.record.exists())
 def test_conflicting_other_mod_prevents_writes_until_reviewed(self):
  self.install_runtime();other=self.f.target.with_name('pak03_dir.vpk');other.write_bytes(self.f.vpk+b'other-combined');before=other.read_bytes();p=self.f.call('plan');self.assertTrue(p['ok'],p);self.assertTrue(p['data']['conflicts']);r=self.f.call('apply');self.assertFalse(r['ok']);self.assertEqual(other.read_bytes(),before);self.assertFalse(self.record.exists());self.assertFalse(self.f.target.exists())
 def test_provisional_record_removed_after_failure_without_touching_runtime(self):
  self.install_runtime();before=(self.f.runtime/'DeadlockTranslate.exe').read_bytes();script=self.f.script.parent/'Update-Mod.ps1';script.write_text("param($PackageFolder,$RuntimeFolder,$GamePath,$StateDirectory,[switch]$NonInteractive,[switch]$RepairMissingRuntime,[switch]$RepairGameVpk,$RecoveryTarget,$ErrorResultFile,$TraceFile)\nexit 9\n",encoding='utf-8-sig');r=self.f.call('apply');self.assertFalse(r['ok']);self.assertFalse(self.record.exists());self.assertEqual((self.f.runtime/'DeadlockTranslate.exe').read_bytes(),before);self.assertFalse(self.f.target.exists())
 def test_original_broken_record_restored_when_reinstall_fails(self):
  self.install_runtime();self.record.write_text('broken old record');script=self.f.script.parent/'Update-Mod.ps1';script.write_text("param($PackageFolder,$RuntimeFolder,$GamePath,$StateDirectory,[switch]$NonInteractive,[switch]$RepairMissingRuntime,[switch]$RepairGameVpk,$RecoveryTarget,$ErrorResultFile,$TraceFile)\nexit 9\n",encoding='utf-8-sig');r=self.f.call('apply');self.assertFalse(r['ok']);self.assertEqual(self.record.read_text(),'broken old record')
 def test_unknown_directory_redirect_keeps_steam_prefix_consistent(self):
  (self.f.runtime/'keep.txt').write_text('KEEP');file=self.f.account(1,'"'+str(self.f.runtime/'DeadlockTranslate.exe')+'" --steam-game %command% -novid');p=self.f.call('plan',configureSteam=True);self.assertTrue(p['ok'],p);target=Path(p['data']['runtime']);r=self.f.call('apply',runtime=str(target),configureSteam=True);self.assertTrue(r['ok'],r);self.assertIn(str(target/'DeadlockTranslate.exe').replace('\\','\\\\'),file.read_text());self.assertIn('-novid',file.read_text());self.assertEqual((self.f.runtime/'keep.txt').read_text(),'KEEP')
 def test_foreign_vpk_identity_cannot_authorize_overwriting_existing_directory(self):
  self.install_runtime();(self.f.runtime/'dlt_translate_dir.vpk').write_bytes(b'not a translator VPK');meta=json.loads((self.f.runtime/'package-manifest.json').read_text());next(e for e in meta['files'] if e['file']=='dlt_translate_dir.vpk')['sha256']=hashlib.sha256(b'not a translator VPK').hexdigest();(self.f.runtime/'package-manifest.json').write_text(json.dumps(meta));p=self.f.call('plan');self.assertTrue(p['ok'],p);self.assertEqual(p['data']['reinstall']['kind'],'fresh-directory');self.assertEqual((self.f.runtime/'dlt_translate_dir.vpk').read_bytes(),b'not a translator VPK')
 def test_deleted_addons_directory_can_be_recreated(self):
  self.install_runtime();self.f.target.parent.rmdir();p=self.f.call('recover');self.assertTrue(p['ok'],p);self.assertTrue(p['data']['reinstallRequired']);r=self.f.call('apply');self.assertTrue(r['ok'],r);self.assertEqual(self.f.target.read_bytes(),self.f.vpk)
if __name__=='__main__':unittest.main(verbosity=2)
