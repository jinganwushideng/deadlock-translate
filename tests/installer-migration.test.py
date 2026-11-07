"""Game reinstalls, relocation, rollback and orphan cleanup on isolated fixtures."""
import importlib.util,json,hashlib,shutil,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('workflow',ROOT/'tests/installer-workflow.test.py');workflow=importlib.util.module_from_spec(spec);spec.loader.exec_module(workflow)

class MigrationTests(unittest.TestCase):
 def setUp(self):
  self.f=workflow.WorkflowTests();self.f.setUp();self.record=self.f.installed();self.old=self.f.game;self.old_vpk=self.f.target
  self.new=self.f.root/'new-game'
  for p in (self.new/'game/citadel/addons',self.new/'game/bin/win64'):p.mkdir(parents=True)
  for n,data in [('game/citadel/gameinfo.gi',b'GameInfo { FileSystem { SearchPaths { Game citadel Game core } } }'),('game/citadel/pak01_dir.vpk',b'base'),('game/bin/win64/deadlock.exe',b'fixture')]:
   (self.new/n).write_bytes(data)
  self.f.q['game']=str(self.new)
  self.record_file=self.f.state/'install-manifest.json';self.original_record=self.record_file.read_bytes();self.old_gi=(self.old/'game/citadel/gameinfo.gi').read_bytes()
 def trust_fixture(self):self.f.simulate_certificate_gate()
 def remove_old_game(self):
  for p in [self.old_vpk,self.old/'game/citadel/gameinfo.gi',self.old/'game/citadel/pak01_dir.vpk',self.old/'game/bin/win64/deadlock.exe']:
   if p.exists():p.unlink()
 def test_migration_plan_is_read_only_and_does_not_require_old_uninstall(self):
  r=self.f.call('plan');self.assertTrue(r['ok'],r);self.assertEqual(r['data']['migration']['newGame'],str(self.new));self.assertTrue(r['data']['needsGameRepair']);self.assertEqual(self.record_file.read_bytes(),self.original_record);self.assertEqual(self.old_vpk.read_bytes(),self.f.vpk);self.assertFalse((self.new/'game/citadel/addons/pak90_dir.vpk').exists())
 def test_removed_game_reinstalls_in_new_root_and_preserves_user_state(self):
  self.remove_old_game();self.trust_fixture();private={'settings.json':b'PRIVATE SETTINGS','secrets.dpapi':b'PRIVATE SECRETS','localhost-certificate.dpapi':b'PRIVATE CERT','terms.json':b'[]'}
  for n,b in private.items():(self.f.state/n).write_bytes(b)
  r=self.f.call('apply',mode='repair');self.assertTrue(r['ok'],r);self.assertTrue(r['data']['migrated']);m=json.loads(self.record_file.read_text(encoding='utf-8-sig'));self.assertEqual(Path(m['target']).parent,self.new/'game/citadel/addons');self.assertEqual(Path(m['target']).read_bytes(),self.f.vpk)
  self.assertEqual(next(self.f.state.glob('backups/gui-*/previous-game-install-manifest.json')).read_bytes(),self.original_record)
  for n,b in private.items():self.assertEqual((self.f.state/n).read_bytes(),b)
 def test_verified_old_translation_is_backed_up_and_other_mods_survive(self):
  self.trust_fixture();other=self.old_vpk.parent/'pak08_dir.vpk';other.write_bytes(b'OTHER MOD');r=self.f.call('apply');self.assertTrue(r['ok'],r);self.assertFalse(self.old_vpk.exists());self.assertEqual(next(self.old_vpk.parent.glob('pak90_dir.vpk.dlt-migrated-*.bak')).read_bytes(),self.f.vpk);self.assertEqual(other.read_bytes(),b'OTHER MOD');self.assertEqual((self.old/'game/citadel/gameinfo.gi').read_bytes(),self.old_gi)
 def test_changed_old_slot_is_preserved_instead_of_overwritten(self):
  self.trust_fixture();self.old_vpk.write_bytes(b'REPLACED BY OTHER MOD');r=self.f.call('apply');self.assertTrue(r['ok'],r);self.assertEqual(self.old_vpk.read_bytes(),b'REPLACED BY OTHER MOD');m=json.loads(self.record_file.read_text(encoding='utf-8-sig'));self.assertEqual(Path(m['target']).read_bytes(),self.f.vpk)
 def test_occupied_new_slot_uses_a_different_empty_slot(self):
  self.trust_fixture();occupied=self.new/'game/citadel/addons/pak90_dir.vpk';occupied.write_bytes(workflow.fixture_vpk().replace(b'fixture',b'OTHERM!'))
  # A readable unrelated VPK with no colliding resource paths.
  occupied.write_bytes(__import__('struct').pack('<III',0x55AA1234,1,1)+b'\0');before=occupied.read_bytes();r=self.f.call('apply');self.assertTrue(r['ok'],r);self.assertEqual(occupied.read_bytes(),before);self.assertEqual(Path(json.loads(self.record_file.read_text(encoding='utf-8-sig'))['target']).name,'pak91_dir.vpk')
 def test_migration_failure_restores_old_record_and_owned_vpk(self):
  self.trust_fixture();script=self.f.script;text=script.read_text(encoding='utf-8-sig');needle=";Run 'Update-Mod.ps1' $a";self.assertIn(needle,text);script.write_text(text.replace(needle,";throw 'fixture update failure'",1),encoding='utf-8-sig')
  r=self.f.call('apply');self.assertFalse(r['ok']);self.assertEqual(self.record_file.read_bytes(),self.original_record);self.assertEqual(self.old_vpk.read_bytes(),self.f.vpk);self.assertFalse((self.new/'game/citadel/addons/pak90_dir.vpk').exists())
 def test_gameinfo_uninstall_ownership_belongs_to_new_game(self):
  self.trust_fixture();self.record['gameinfoChanged']=True;self.record['gameinfoBefore']=__import__('base64').b64encode(b'OLD GAME CONFIG MUST NOT ENTER NEW GAME').decode();self.record_file.write_text(json.dumps(self.record));r=self.f.call('apply');self.assertTrue(r['ok'],r);m=json.loads(self.record_file.read_text(encoding='utf-8-sig'));self.assertNotIn('OLD GAME CONFIG',__import__('base64').b64decode(m.get('gameinfoBefore','')).decode(errors='replace'));before=(self.new/'game/citadel/gameinfo.gi').read_bytes();self.assertIn(b'GameInfo',before)
 def test_missing_old_game_can_be_uninstalled_without_touching_new_game(self):
  self.remove_old_game();new_before={str(p.relative_to(self.new)):p.read_bytes() for p in self.new.rglob('*') if p.is_file()};r=self.f.call('plan',mode='uninstall');self.assertTrue(r['ok'],r);self.assertEqual(r['data']['game'],str(self.old));r=self.f.call('apply',mode='uninstall');self.assertTrue(r['ok'],r);self.assertFalse(self.record_file.exists());self.assertEqual({str(p.relative_to(self.new)):p.read_bytes() for p in self.new.rglob('*') if p.is_file()},new_before);self.assertEqual(next(self.f.state.glob('backups/gui-*/uninstalled-install-manifest.json')).read_bytes(),self.original_record)
 def test_recover_button_gives_automatic_migration_route(self):
  r=self.f.call('recover');self.assertTrue(r['ok'],r);self.assertTrue(r['data']['reinstallRequired']);self.assertEqual(self.record_file.read_bytes(),self.original_record)
 def test_preview_record_change_is_rejected_before_any_write(self):
  self.trust_fixture();before=self.old_vpk.read_bytes();r=self.f.call('apply',reviewedRecordHash='0'*64);self.assertFalse(r['ok']);self.assertEqual(r['failure']['code'],'file-changed');self.assertEqual(self.old_vpk.read_bytes(),before);self.assertEqual(self.record_file.read_bytes(),self.original_record)
 def test_stale_selection_is_corrected_to_unique_detected_game(self):
  self.trust_fixture();script=self.f.script;text=script.read_text(encoding='utf-8-sig');text=text.replace('function ResolveGame {',"function Find-DeadlockQuick { param([string[]]$Libraries); return '"+str(self.new).replace("'","''")+"' }\nfunction ResolveGame {",1);script.write_text(text,encoding='utf-8-sig');self.remove_old_game();r=self.f.call('plan',game=str(self.old));self.assertTrue(r['ok'],r);self.assertEqual(r['data']['game'],str(self.new));self.assertIn('唯一',r['data']['gameCorrection']['message'])
 def test_steam_preview_failure_does_not_block_core_install(self):
  self.trust_fixture();r=self.f.call('plan',configureSteam=True);self.assertTrue(r['ok'],r);self.assertFalse(r['data']['blocked']);self.assertTrue(any(x['code']=='steam-manual-fallback' for x in r['data']['preflight']));r=self.f.call('apply',configureSteam=True);self.assertTrue(r['ok'],r);self.assertTrue(r['data']['coreDone']);self.assertFalse(r['data']['steamDone']);self.assertTrue(any('Steam' in x for x in r['data']['warnings']))
 def test_invalid_record_target_is_backed_up_without_touching_arbitrary_file(self):
  self.trust_fixture();foreign=self.f.root/'foreign.vpk';foreign.write_bytes(b'KEEP FOREIGN');self.record['target']=str(foreign);self.record_file.write_text(json.dumps(self.record));before=self.record_file.read_bytes();r=self.f.call('apply');self.assertTrue(r['ok'],r);self.assertEqual(foreign.read_bytes(),b'KEEP FOREIGN');self.assertEqual(next(self.f.state.glob('backups/gui-*/invalid-install-manifest.json')).read_bytes(),before)
 def test_uninstall_selects_old_recorded_game_and_preserves_new_game(self):
  target=self.new/'game/citadel/addons/pak90_dir.vpk';target.write_bytes(b'NEW GAME OTHER MOD');r=self.f.call('apply',mode='uninstall');self.assertTrue(r['ok'],r);self.assertFalse(self.old_vpk.exists());self.assertEqual(target.read_bytes(),b'NEW GAME OTHER MOD');self.assertFalse(self.record_file.exists())
 def test_orphan_uninstall_preserves_repurposed_old_slot(self):
  self.remove_old_game();self.old_vpk.write_bytes(b'REPLACED SLOT');r=self.f.call('apply',mode='uninstall');self.assertTrue(r['ok'],r);self.assertEqual(self.old_vpk.read_bytes(),b'REPLACED SLOT');self.assertFalse(self.record_file.exists())
 def test_child_failure_records_exit_code_without_leaking_stdout(self):
  self.trust_fixture();child=self.f.script.parent/'Configure-Steam.ps1';child.write_text("Write-Output 'PRIVATE_STEAM_MARKER';exit 17",encoding='utf-8-sig');r=self.f.call('plan',configureSteam=True);self.assertTrue(r['ok'],r);self.assertFalse(r['data']['blocked']);serialized=json.dumps(r);self.assertIn('17',serialized);self.assertNotIn('PRIVATE_STEAM_MARKER',serialized)
 def test_readonly_steam_config_does_not_block_migration_or_change_attributes(self):
  import os,stat
  self.trust_fixture();file=self.f.account(1,'-novid');before=file.read_bytes();os.chmod(file,stat.S_IREAD)
  try:
   r=self.f.call('plan',configureSteam=True);self.assertTrue(r['ok'],r);self.assertFalse(r['data']['blocked']);self.assertTrue(any(c['code']=='steam-manual-fallback' for c in r['data']['preflight']))
   r=self.f.call('apply',configureSteam=True);self.assertTrue(r['ok'],r);self.assertTrue(r['data']['coreDone']);self.assertFalse(r['data']['steamDone']);self.assertEqual(file.read_bytes(),before);self.assertFalse(file.stat().st_mode & stat.S_IWRITE)
  finally:os.chmod(file,stat.S_IREAD|stat.S_IWRITE)
 def test_late_update_failure_restores_runtime_both_games_and_record(self):
  self.trust_fixture();runtime_before={str(p.relative_to(self.f.runtime)):p.read_bytes() for p in self.f.runtime.rglob('*') if p.is_file()};new_gi=(self.new/'game/citadel/gameinfo.gi').read_bytes();child=self.f.script.parent/'Update-Mod.ps1';text=child.read_text(encoding='utf-8-sig');needle='        Replace-Bytes $taskManifestPath';self.assertIn(needle,text);child.write_text(text.replace(needle,"        Throw-DltFault 'fixture-late-failure' 'update-record' $taskManifestPath 'Fixture late failure'\n"+needle,1),encoding='utf-8-sig');r=self.f.call('apply');self.assertFalse(r['ok']);self.assertEqual(self.record_file.read_bytes(),self.original_record);self.assertEqual(self.old_vpk.read_bytes(),self.f.vpk);self.assertFalse((self.new/'game/citadel/addons/pak90_dir.vpk').exists());self.assertEqual((self.new/'game/citadel/gameinfo.gi').read_bytes(),new_gi);self.assertEqual({str(p.relative_to(self.f.runtime)):p.read_bytes() for p in self.f.runtime.rglob('*') if p.is_file()},runtime_before)

if __name__=='__main__':unittest.main(verbosity=2)
