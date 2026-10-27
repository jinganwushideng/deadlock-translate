"""QOL policy fixtures: temporary game trees; no real game, certificate or Steam changes."""
import importlib.util,struct,subprocess,unittest,zlib
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('qol_workflow',ROOT/'tests/installer-workflow.test.py');fixture=importlib.util.module_from_spec(spec);spec.loader.exec_module(fixture)

def vpk(resources):
 groups={};data=b'fixture';tree=bytearray()
 for item in resources:
  directory,name=item.rsplit('/',1);stem,ext=name.rsplit('.',1);groups.setdefault(ext,{}).setdefault(directory,[]).append(stem)
 for ext,directories in groups.items():
  tree.extend(ext.encode()+b'\0')
  for directory,names in directories.items():
   tree.extend(directory.encode()+b'\0')
   for name in names:tree.extend(name.encode()+b'\0'+struct.pack('<IHHIIH',zlib.crc32(data),len(data),0x7fff,0,0,0xffff)+data)
   tree.extend(b'\0')
  tree.extend(b'\0')
 tree.extend(b'\0');return struct.pack('<III',0x55aa1234,1,len(tree))+tree

QOL='panorama/styles/qollock_global.vcss_c';CHAT='panorama/layout/chat.vxml_c';RELATED='panorama/styles/citadel_hud_top_bar_chat.vcss_c'
class QolTests(unittest.TestCase):
 def setUp(self):
  self.f=fixture.WorkflowTests();self.f.setUp();self.other=self.f.target.with_name('pak01_dir.vpk')
 def put(self,resources,name=None):
  p=self.other if name is None else self.other.with_name(name);p.write_bytes(vpk(resources));return p
 def manager(self,action):
  return subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(ROOT/'scripts/Manage-Mod.ps1'),'-Action',action,'-PackageFolder',str(self.f.payload),'-GamePath',str(self.f.game),'-StateDirectory',str(self.f.state),'-NonInteractive'],capture_output=True,timeout=30)
 def recovery_check(self):
  script=self.f.root/'recovery-check.ps1';script.write_text("param($Root,$Addons,$Vpk)\n$ErrorActionPreference='Stop'\n. (Join-Path $Root 'scripts/Install-Safety.ps1')\n. (Join-Path $Root 'scripts/Vpk-Recovery.ps1')\nAssert-DltRecoveryConflicts $Addons $Vpk ''\n",encoding='utf-8-sig')
  return subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(script),'-Root',str(ROOT),'-Addons',str(self.other.parent),'-Vpk',str(self.f.payload/'dlt_translate_dir.vpk')],capture_output=True,timeout=30)
 def test_numbered_qol_related_resources_do_not_need_coexistence_consent(self):
  self.put([QOL,RELATED,'panorama/scripts/manifests/ql_chat_translate/manifest.vjs_c']);before=self.other.read_bytes();p=self.f.call('plan');self.assertTrue(p['ok'],p);self.assertEqual(p['data']['conflicts'],[]);self.assertEqual(self.other.read_bytes(),before)
 def test_known_qol_is_ignored_even_with_overlapping_chat_resource(self):
  self.put([QOL,CHAT]);p=self.f.call('plan');self.assertTrue(p['ok'],p);self.assertEqual(p['data']['conflicts'],[])
 def test_all_three_observed_qol_core_markers_are_recognized(self):
  for marker in (QOL,'panorama/styles/qollock_defines.vcss_c','scripts/qollock_translations.vjs_c'):
   with self.subTest(marker=marker):
    self.put([marker,RELATED]);p=self.f.call('plan');self.assertTrue(p['ok'],p);self.assertEqual(p['data']['conflicts'],[])
 def test_qol_name_without_resource_identity_does_not_exempt_other_mods(self):
  p=self.put([CHAT],'qol_dir.vpk');r=self.f.call('plan');self.assertTrue(r['ok'],r);self.assertEqual(r['data']['conflicts'][0]['file'],p.name);self.assertEqual(r['data']['conflicts'][0]['kind'],'exact')
 def test_non_qol_related_mod_still_needs_consent(self):
  self.put([RELATED]);r=self.f.call('plan');self.assertTrue(r['ok'],r);self.assertEqual(r['data']['conflicts'][0]['kind'],'related')
 def test_qol_does_not_hide_a_different_conflicting_package(self):
  self.put([QOL,RELATED]);other=self.put([CHAT],'pak02_dir.vpk');r=self.f.call('plan');self.assertTrue(r['ok'],r);self.assertEqual([c['file'] for c in r['data']['conflicts']],[other.name])
 def test_script_install_skips_qol_and_preserves_exact_bytes(self):
  self.put([QOL,CHAT,RELATED]);before=self.other.read_bytes();r=self.manager('Install');self.assertEqual(r.returncode,0,(r.stdout+r.stderr).decode(errors='replace'));self.assertTrue(self.f.target.exists());self.assertEqual(self.other.read_bytes(),before)
 def test_recovery_skips_qol_overlaps(self):
  self.put([QOL,CHAT]);before=self.other.read_bytes();r=self.recovery_check();self.assertEqual(r.returncode,0,(r.stdout+r.stderr).decode(errors='replace'));self.assertEqual(self.other.read_bytes(),before)
 def test_recovery_still_rejects_non_qol_overlap(self):
  self.put([CHAT]);r=self.recovery_check();self.assertNotEqual(r.returncode,0)
 def test_full_update_restores_missing_vpk_beside_qol(self):
  spec=importlib.util.spec_from_file_location('qol_recovery',ROOT/'tests/vpk-recovery.test.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);f=module.RecoveryTests();f.setUp()
  try:
   f.f.target.unlink();qol=f.f.target.with_name('pak01_dir.vpk');before=vpk([QOL,CHAT,RELATED]);qol.write_bytes(before)
   self.assertEqual(f.run_update(),0,f.f.output);self.assertEqual(qol.read_bytes(),before);self.assertEqual(f.f.target.read_bytes(),f.new)
  finally:f.tearDown()
 def test_full_gui_workflow_never_disables_qol(self):
  self.f.simulate_certificate_gate();self.put([QOL,CHAT,RELATED]);before=self.other.read_bytes();r=self.f.call('apply');self.assertTrue(r['ok'],r);self.assertEqual(self.other.read_bytes(),before);self.assertEqual(list(self.other.parent.glob('*.dlt-disabled-*.bak')),[]);self.assertTrue(self.f.target.exists())
 def test_ignored_qol_appears_only_in_operation_diagnostics(self):
  self.put([QOL,CHAT]);r=self.f.call('plan');self.assertTrue(r['ok'],r);trace=(self.f.root/'operations.jsonl').read_text(encoding='utf-8-sig');self.assertIn('qol-ignored',trace);self.assertNotIn('pairing',trace.lower())

if __name__=='__main__':unittest.main(verbosity=2)
