"""Recovery fixtures never install into the real game, trust certificates or run game EXEs."""
import hashlib,importlib.util,json,shutil,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def module(name,file):
 spec=importlib.util.spec_from_file_location(name,ROOT/'tests'/file);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
update=module('update_fixture','update.test.py');workflow=module('workflow_fixture','installer-workflow.test.py')
class RecoveryTests(unittest.TestCase):
 def setUp(self):
  self.f=update.UpdateTests();self.f.setUp();self.old=workflow.fixture_vpk();self.new=self.old+b'new-version'
  self.f.target.write_bytes(self.old);(self.f.runtime/'dlt_translate_dir.vpk').write_bytes(self.old)
  self.f.install['vpkHash']=hashlib.sha256(self.old).hexdigest();update.write_json(self.f.state/'install-manifest.json',self.f.install)
  self.f.files['dlt_translate_dir.vpk']=self.new;(self.f.package/'dlt_translate_dir.vpk').write_bytes(self.new);self.f.metadata()
  self.other=self.f.target.parent/'pak20_dir.vpk';self.other.write_bytes(self.old.replace(b'panorama/',b'skinxxxx/'))
 def tearDown(self):self.f.tearDown()
 def run_update(self,*args):return self.f.run_update(args,explicit=True)
 def record(self):return json.loads((self.f.state/'install-manifest.json').read_text(encoding='utf-8-sig'))
 def test_missing_vpk_restored_with_explicit_repair(self):
  self.f.target.unlink();original_settings=(self.f.state/'settings.json').read_bytes();other=self.other.read_bytes()
  self.assertEqual(self.run_update('-RepairGameVpk'),0,self.f.output);self.assertEqual(self.f.target.read_bytes(),self.new);self.assertEqual(self.record()['vpkHash'],hashlib.sha256(self.new).hexdigest());self.assertEqual((self.f.state/'settings.json').read_bytes(),original_settings);self.assertEqual(self.other.read_bytes(),other)
 def test_missing_vpk_recovers_by_default(self):
  self.f.target.unlink();self.assertEqual(self.run_update(),0,self.f.output);self.assertEqual(self.f.target.read_bytes(),self.new)
 def test_renamed_unique_hash_relinks(self):
        new_target=self.f.target.with_name('pak98_dir.vpk');self.f.target.rename(new_target);self.assertEqual(self.run_update('-RepairGameVpk'),0,self.f.output);self.assertFalse(self.f.target.exists());self.assertEqual(new_target.read_bytes(),self.new);self.assertTrue(Path(self.record()['target']).samefile(new_target))
 def test_occupied_slot_preserves_other_mod_and_uses_free_slot(self):
  original=self.other.read_bytes();self.f.target.write_bytes(original);self.assertEqual(self.run_update('-RepairGameVpk'),0,self.f.output);self.assertEqual(self.f.target.read_bytes(),original);target=Path(self.record()['target']);self.assertEqual(target.name,'pak91_dir.vpk');self.assertEqual(target.read_bytes(),self.new)
 def test_duplicate_hash_is_consolidated_without_deletion(self):
  self.f.target.unlink();one=self.f.target.with_name('pak91_dir.vpk');two=self.f.target.with_name('pak92_dir.vpk');one.write_bytes(self.old);two.write_bytes(self.old);self.assertEqual(self.run_update(),0,self.f.output);self.assertEqual(one.read_bytes(),self.new);self.assertFalse(two.exists());self.assertEqual(next(self.f.state.glob('backups/update-*/duplicate-pak92_dir.vpk.bak')).read_bytes(),self.old)
 def test_duplicate_consolidates_when_recorded_slot_still_present(self):
  extra=self.f.target.with_name('pak91_dir.vpk');extra.write_bytes(self.old);self.assertEqual(self.run_update(),0,self.f.output);self.assertEqual(self.f.target.read_bytes(),self.new);self.assertFalse(extra.exists());self.assertEqual(next(self.f.state.glob('backups/update-*/duplicate-pak91_dir.vpk.bak')).read_bytes(),self.old)
 def test_merged_translation_conflict_never_silently_overwritten(self):
  self.f.target.unlink();merged=self.f.target.with_name('pak05_dir.vpk');merged.write_bytes(self.new+b'merged-other-mod');before=merged.read_bytes();self.assertNotEqual(self.run_update('-RepairGameVpk'),0);self.assertEqual(merged.read_bytes(),before);self.assertFalse(self.f.target.exists())
 def test_preview_destination_taken_before_apply_is_not_overwritten(self):
  self.f.target.unlink();selected=self.f.target.with_name('pak95_dir.vpk');selected.write_bytes(self.other.read_bytes());before=selected.read_bytes();self.assertNotEqual(self.run_update('-RepairGameVpk','-RecoveryTarget',str(selected)),0);self.assertEqual(selected.read_bytes(),before)
 def test_late_failure_removes_new_vpk_and_restores_runtime_record(self):
  self.f.target.unlink();before=update.snapshot(self.f.runtime);record=(self.f.state/'install-manifest.json').read_bytes()
  scripts=self.f.root/'fixture-scripts';scripts.mkdir()
  for name in ('Install-Safety.ps1','Game-SearchPaths.ps1','Vpk-Recovery.ps1','Backup-Maintenance.ps1'):shutil.copy2(ROOT/'scripts'/name,scripts/name)
  self.f.script=scripts/'Update-Mod.ps1';text=(ROOT/'scripts/Update-Mod.ps1').read_text(encoding='utf-8-sig');text=text.replace('        Replace-Bytes $taskManifestPath',"        Throw-DltFault 'fixture-record-failed' 'update-record' $taskManifestPath 'Fixture failure'\n        Replace-Bytes $taskManifestPath");self.f.script.write_text(text,encoding='utf-8-sig')
  self.assertNotEqual(self.run_update('-RepairGameVpk'),0);self.assertFalse(self.f.target.exists());self.assertEqual(update.snapshot(self.f.runtime),before);self.assertEqual((self.f.state/'install-manifest.json').read_bytes(),record)
 def test_duplicate_consolidation_failure_restores_all_original_copies(self):
  extra=self.f.target.with_name('pak91_dir.vpk');extra.write_bytes(self.old);before=update.snapshot(self.f.game)
  scripts=self.f.root/'fixture-scripts';scripts.mkdir()
  for name in ('Install-Safety.ps1','Game-SearchPaths.ps1','Vpk-Recovery.ps1','Backup-Maintenance.ps1'):shutil.copy2(ROOT/'scripts'/name,scripts/name)
  self.f.script=scripts/'Update-Mod.ps1';text=(ROOT/'scripts/Update-Mod.ps1').read_text(encoding='utf-8-sig');text=text.replace('        Replace-Bytes $taskManifestPath',"        Throw-DltFault 'fixture-record-failed' 'update-record' $taskManifestPath 'Fixture failure'\n        Replace-Bytes $taskManifestPath");self.f.script.write_text(text,encoding='utf-8-sig')
  self.assertNotEqual(self.run_update(),0);self.assertEqual(update.snapshot(self.f.game),before)
 def test_mixed_runtime_and_pairing_are_repaired_together(self):
  (self.f.runtime/'dlt_translate_dir.vpk').write_bytes(b'wrong package');(self.f.runtime/'pairing.key').write_text('corrupt')
  self.assertEqual(self.run_update('-RepairMissingRuntime'),0,self.f.output);self.assertEqual((self.f.runtime/'dlt_translate_dir.vpk').read_bytes(),self.new);self.assertEqual((self.f.runtime/'pairing.key').read_bytes(),self.f.files['pairing.key']);self.assertEqual(self.f.target.read_bytes(),self.new)
 def test_trace_contains_stages_hashes_and_no_pairing_value(self):
  self.f.target.unlink();trace=self.f.root/'trace.jsonl';self.assertEqual(self.run_update('-RepairGameVpk','-TraceFile',str(trace)),0,self.f.output);text=trace.read_text(encoding='utf-8-sig');events=[json.loads(l) for l in text.splitlines()];self.assertTrue(any(e['stage']=='vpk-inspection' and e['outcome']=='missing' for e in events));self.assertTrue(any(e['stage']=='game-vpk-commit' and e['outcome']=='committed' for e in events));self.assertTrue(any(e.get('actualHash')==hashlib.sha256(self.new).hexdigest() for e in events));self.assertNotIn('1'*48,text)
class WorkflowRecoveryTests(unittest.TestCase):
 def setUp(self):self.f=workflow.WorkflowTests();self.f.setUp();self.f.installed()
 def complete_runtime(self):
  self.f.simulate_certificate_gate()
  for path in self.f.payload.rglob('*'):
   if path.is_file():target=self.f.runtime/path.relative_to(self.f.payload);target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(path,target)
  record=json.loads((self.f.state/'install-manifest.json').read_text());record['version']='9.9.9';(self.f.state/'install-manifest.json').write_text(json.dumps(record))
 def test_matching_runtime_and_game_have_healthy_preflight(self):
  self.complete_runtime();r=self.f.call('plan');self.assertTrue(r['ok'],r);self.assertTrue(r['data']['runtimeIntegrity']['healthy']);self.assertFalse(r['data']['needsRepair']);self.assertEqual(r['data']['gameVpk']['state'],'ready')
 def test_backend_hash_mismatch_is_reported_with_filename(self):
  self.complete_runtime();(self.f.runtime/'DeadlockTranslate.exe').write_bytes(b'mixed-backend');r=self.f.call('plan');self.assertTrue(r['ok'],r);self.assertTrue(r['data']['needsRepair']);self.assertTrue(any(x['file']=='DeadlockTranslate.exe' and x['code']=='hash-mismatch' for x in r['data']['runtimeIntegrity']['issues']))
 def test_mixed_backend_repairs_by_default(self):
  self.complete_runtime();(self.f.runtime/'DeadlockTranslate.exe').write_bytes(b'mixed-backend');r=self.f.call('apply');self.assertTrue(r['ok'],r);self.assertEqual((self.f.runtime/'DeadlockTranslate.exe').read_bytes(),b'never executed')
 def test_pairing_format_and_manifest_mismatch_are_reported_without_credential(self):
  self.complete_runtime();pair='f'*48;(self.f.runtime/'pairing.key').write_text(pair);r=self.f.call('plan');self.assertTrue(r['ok'],r);self.assertTrue(r['data']['needsRepair']);self.assertNotIn(pair,json.dumps(r));self.assertTrue(any(x['file']=='pairing.key' for x in r['data']['runtimeIntegrity']['issues']))
 def test_new_package_pairing_mismatch_blocks_before_updates(self):
  self.complete_runtime()
  if b'/bridge/' not in self.f.vpk:self.skipTest('compiled bridge identity is not present in synthetic CI VPK')
  pair=self.f.payload/'pairing.key';pair.write_text('f'*48);meta=json.loads((self.f.payload/'package-manifest.json').read_text());next(x for x in meta['files'] if x['file']=='pairing.key')['sha256']=hashlib.sha256(pair.read_bytes()).hexdigest();(self.f.payload/'package-manifest.json').write_text(json.dumps(meta));before=(self.f.runtime/'DeadlockTranslate.exe').read_bytes();r=self.f.call('apply');self.assertFalse(r['ok']);self.assertEqual(r['failure']['code'],'package-pairing-mismatch');self.assertEqual((self.f.runtime/'DeadlockTranslate.exe').read_bytes(),before)
 def test_plan_reports_missing_vpk_and_runtime_integrity_without_writes(self):
  self.f.target.unlink();before=(self.f.state/'install-manifest.json').read_bytes();r=self.f.call('plan');self.assertTrue(r['ok'],r);self.assertEqual(r['data']['gameVpk']['state'],'missing');self.assertTrue(r['data']['needsGameRepair']);self.assertIn('runtimeIntegrity',r['data']);self.assertEqual((self.f.state/'install-manifest.json').read_bytes(),before)
 def test_full_gui_missing_vpk_repair_preserves_settings(self):
  self.f.target.unlink();self.f.simulate_certificate_gate();(self.f.state/'settings.json').write_text('PRIVATE SETTINGS');r=self.f.call('apply',repairGameVpk=True,repairMissing=True,recoveryTarget=str(self.f.target));self.assertTrue(r['ok'],r);self.assertEqual(self.f.target.read_bytes(),self.f.vpk);self.assertEqual((self.f.state/'settings.json').read_text(),'PRIVATE SETTINGS')
 def test_repair_mode_restores_whole_package_for_recorded_install(self):
  self.f.target.unlink();self.f.simulate_certificate_gate();r=self.f.call('plan',mode='repair');self.assertTrue(r['ok'],r);self.assertTrue(r['data']['fullRepair']);r=self.f.call('apply',mode='repair');self.assertTrue(r['ok'],r);self.assertEqual(self.f.target.read_bytes(),self.f.vpk);self.assertEqual((self.f.runtime/'DeadlockTranslate.exe').read_bytes(),b'never executed')
if __name__=='__main__':unittest.main(verbosity=2)
