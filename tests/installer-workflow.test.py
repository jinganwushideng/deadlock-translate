"""GUI workflow fixtures never launch the game or change certificate trust."""
import ctypes,hashlib,json,os,shutil,subprocess,uuid,unittest,struct,zlib
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def fixture_vpk():
 paths=('layout/chat.vxml_c','layout/dlt_menu.vxml_c','layout/dlt_settings_window.vxml_c','scripts/dlt_chat.vjs_c','scripts/dlt_menu.vjs_c','scripts/dlt_settings_window.vjs_c','styles/dlt_chat.vcss_c','styles/dlt_menu.vcss_c','styles/dlt_settings_window.vcss_c')
 groups={}
 for p in paths:
  directory,filename=('panorama/'+p).rsplit('/',1);name,extension=filename.rsplit('.',1);groups.setdefault(extension,{}).setdefault(directory,[]).append(name)
 tree=bytearray();data=b'fixture'
 for extension,dirs in groups.items():
  tree.extend(extension.encode()+b'\0')
  for directory,names in dirs.items():
   tree.extend(directory.encode()+b'\0')
   for name in names:tree.extend(name.encode()+b'\0'+struct.pack('<IHHIIH',zlib.crc32(data),len(data),0x7fff,0,0,0xffff)+data)
   tree.extend(b'\0')
  tree.extend(b'\0')
 tree.extend(b'\0');return struct.pack('<III',0x55AA1234,1,len(tree))+tree
class WorkflowTests(unittest.TestCase):
 def setUp(self):
  self.root=ROOT/'artifacts'/('gui-workflow-'+uuid.uuid4().hex);self.root.mkdir()
  self.state=self.root/'state';self.payload=self.root/'payload';self.runtime=self.root/'runtime';self.game=self.root/'game-root';self.steam=self.root/'Steam'
  for p in (self.state,self.payload,self.runtime,self.game/'game/citadel/addons',self.game/'game/bin/win64',self.steam):p.mkdir(parents=True,exist_ok=True)
  version=json.loads((ROOT/'version.json').read_text(encoding='utf-8-sig'))['version'];built=ROOT/'dist'/('DeadlockTranslate-'+version)
  self.vpk=(built/'dlt_translate_dir.vpk').read_bytes() if (built/'dlt_translate_dir.vpk').is_file() else fixture_vpk();self.target=self.game/'game/citadel/addons/pak90_dir.vpk'
  (self.payload/'dlt_translate_dir.vpk').write_bytes(self.vpk);(self.payload/'package-manifest.json').write_text(json.dumps({'version':'9.9.9','files':[]}),encoding='utf-8')
  (self.game/'game/citadel/gameinfo.gi').write_text('GameInfo { FileSystem { SearchPaths { Game citadel Game core } } }',encoding='utf-8')
  (self.game/'game/citadel/pak01_dir.vpk').write_bytes(b'fake base');(self.game/'game/bin/win64/deadlock.exe').write_bytes(b'not executable');(self.steam/'Steam.exe').write_bytes(b'not executable')
  self.q=dict(state=str(self.state),payload=str(self.payload),runtime=str(self.runtime),game=str(self.game),steam=str(self.steam),userId='1',mode='install',configureSteam=False,stopSteam=False,admin=False,allowRelated=False,restoreConflicts=False,removeCertificate=False)
  self.env=os.environ.copy();self.env['LOCALAPPDATA']=str(self.root/'local-data')
  self.script=ROOT/'scripts/Installer-Workflow.ps1'
 def call(self,action,**values):
  request=self.root/('request-'+uuid.uuid4().hex+'.json');result=request.with_suffix('.result.json');request.write_text(json.dumps(dict(self.q,action=action,**values)),encoding='utf-8-sig')
  p=subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(self.script),'-RequestFile',str(request),'-ResultFile',str(result)],capture_output=True,env=self.env,timeout=45)
  self.assertTrue(result.exists(),(p.stdout+p.stderr).decode(errors='replace'));return json.loads(result.read_text(encoding='utf-8-sig'))
 def installed(self):
  self.target.write_bytes(self.vpk);(self.runtime/'dlt_translate_dir.vpk').write_bytes(self.vpk);(self.runtime/'DeadlockTranslate.exe').write_bytes(b'never run');pairing=__import__('re').search(rb'/bridge/([0-9a-fA-F]{48})',self.vpk);(self.runtime/'pairing.key').write_bytes(pairing.group(1) if pairing else b'0'*48);(self.runtime/'data').mkdir();(self.runtime/'data/terms.json').write_text('[]');(self.runtime/'package-manifest.json').write_text(json.dumps(dict(version='0.6.7')))
  record=dict(target=str(self.target),vpkHash=hashlib.sha256(self.vpk).hexdigest(),runtimeFolder=str(self.runtime),version='0.6.7',gameinfoChanged=False,gameinfoBefore='',gameinfoAfterHash='unused')
  (self.state/'install-manifest.json').write_text(json.dumps(record),encoding='utf-8');return record
 def account(self,user,launch=None,appid='1422450'):
  file=self.steam/'userdata'/str(user)/'config/localconfig.vdf';file.parent.mkdir(parents=True)
  v='' if launch is None else '"LaunchOptions" "'+launch.replace('\\','\\\\').replace('"','\\"')+'"'
  file.write_text('"UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "'+appid+'" { '+v+' } } } } } }',encoding='utf-8');return file
 def simulate_certificate_gate(self):
  scripts=self.root/'trusted-test-scripts';scripts.mkdir()
  for name in ('Installer-Workflow.ps1','Find-Deadlock.ps1','Game-SearchPaths.ps1','Install-Safety.ps1','Vpk-Recovery.ps1','Manage-Mod.ps1','Update-Mod.ps1','Backup-Maintenance.ps1','Configure-Steam.ps1','Trust-Local-Https.ps1'):shutil.copy2(ROOT/'scripts'/name,scripts/name)
  self.script=scripts/'Installer-Workflow.ps1';text=self.script.read_text(encoding='utf-8-sig').replace('function CertStatus {','function UnusedRealCertStatus {',1)
  text=text.replace('function Inspect {',"function CertStatus { return @{ready=$true;state='fixture'} }\nfunction Start-Process { param($FilePath,$ArgumentList,$WindowStyle,[switch]$PassThru,[switch]$Wait); return [pscustomobject]@{ExitCode=0} }\nfunction Inspect {",1)
  self.script.write_text(text,encoding='utf-8-sig')
  pairing=__import__('re').search(rb'/bridge/([0-9a-fA-F]{48})',self.vpk);test_pair=pairing.group(1) if pairing else b'1'*48
  for name,data in {'DeadlockTranslate.exe':b'never executed','pairing.key':test_pair,'data/terms.json':b'[]','wwwroot/app.js':b'test','wwwroot/index.html':b'test','wwwroot/style.css':b'test','Install.cmd':b'test','Manage-Mod.ps1':b'test'}.items():
   p=self.payload/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(data)
  entries=[dict(file=p.relative_to(self.payload).as_posix(),bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in self.payload.rglob('*') if p.is_file() and p.name!='package-manifest.json']
  (self.payload/'package-manifest.json').write_text(json.dumps(dict(version='9.9.9',files=entries)))
 def test_full_fresh_install_with_simulated_certificate_gate(self):
  self.simulate_certificate_gate();r=self.call('apply');self.assertTrue(r['ok'],r);self.assertTrue(r['data']['coreDone']);self.assertEqual(self.target.read_bytes(),self.vpk);self.assertEqual((self.runtime/'DeadlockTranslate.exe').read_bytes(),b'never executed');self.assertTrue((self.state/'install-manifest.json').exists())
 def test_full_update_with_simulated_certificate_gate(self):
  self.installed();self.simulate_certificate_gate();(self.state/'settings.json').write_text('KEEP');r=self.call('apply');self.assertTrue(r['ok'],r);self.assertEqual((self.runtime/'DeadlockTranslate.exe').read_bytes(),b'never executed');self.assertEqual((self.state/'settings.json').read_text(),'KEEP')
 def test_failed_install_restores_selected_conflict(self):
  self.simulate_certificate_gate();conflict=self.target.parent/'pak21_dir.vpk';conflict.write_bytes(self.vpk);(self.game/'game/citadel/gameinfo.gi').write_text('broken gameinfo')
  r=self.call('apply',disable=[dict(file=conflict.name,hash=hashlib.sha256(self.vpk).hexdigest())]);self.assertFalse(r['ok']);self.assertEqual(conflict.read_bytes(),self.vpk);self.assertFalse(self.target.exists());self.assertFalse((self.state/'install-manifest.json').exists());self.assertEqual(list(self.runtime.iterdir()),[])
 def test_plan_does_not_create_certificate_or_modify_game(self):
  before=(self.game/'game/citadel/gameinfo.gi').read_bytes();r=self.call('plan');self.assertTrue(r['ok'],r);self.assertFalse(r['data']['certificate']['ready']);self.assertEqual((self.game/'game/citadel/gameinfo.gi').read_bytes(),before);self.assertFalse((self.state/'localhost-certificate.cer').exists())
 def test_hidden_parent_directory_does_not_break_inspection_or_install(self):
  self.simulate_certificate_gate();attrs=ctypes.windll.kernel32.GetFileAttributesW(str(self.root));self.assertTrue(ctypes.windll.kernel32.SetFileAttributesW(str(self.root),attrs|2));r=self.call('inspect');self.assertTrue(r['ok'],r);r=self.call('apply');self.assertTrue(r['ok'],r);self.assertEqual(self.target.read_bytes(),self.vpk)
 def test_inspect_uses_selected_steam_library(self):
  (self.steam/'steamapps').mkdir();(self.steam/'steamapps/libraryfolders.vdf').write_text('"libraryfolders" { "0" { "path" "'+str(self.game).replace('\\','\\\\')+'" } }');r=self.call('inspect');self.assertTrue(r['ok'],r);self.assertIn(str(self.game),r['data']['games'])
 def test_missing_certificate_blocks_install_before_copy(self):
  r=self.call('apply');self.assertFalse(r['ok']);self.assertEqual(list(self.runtime.iterdir()),[]);self.assertFalse(self.target.exists())
 def test_overlap_target_refused(self):
  r=self.call('plan',runtime=str(self.game/'translator'));self.assertFalse(r['ok'])
 def test_unrecognized_nonempty_target_redirected_without_modification(self):
  (self.runtime/'notes.txt').write_text('keep');r=self.call('plan');self.assertTrue(r['ok'],r);self.assertEqual(r['data']['reinstall']['kind'],'fresh-directory');self.assertNotEqual(r['data']['runtime'],str(self.runtime));self.assertFalse(Path(r['data']['runtime']).exists());self.assertEqual((self.runtime/'notes.txt').read_text(),'keep')
 def test_exact_conflict_is_reported_without_disabling(self):
  self.target.write_bytes(self.vpk);r=self.call('plan');self.assertTrue(r['ok'],r);self.assertEqual(r['data']['conflicts'][0]['kind'],'exact');self.assertTrue(self.target.exists())
 def test_unique_recovery_creates_conservative_record(self):
  self.installed();(self.state/'install-manifest.json').unlink();r=self.call('recover');self.assertTrue(r['ok'],r);m=json.loads((self.state/'install-manifest.json').read_text(encoding='utf-8-sig'));self.assertFalse(m['gameinfoChanged']);self.assertEqual(m['target'],str(self.target))
 def test_duplicate_vpk_recovery_refused(self):
  self.installed();(self.state/'install-manifest.json').unlink();(self.target.parent/'pak91_dir.vpk').write_bytes(self.vpk);self.assertFalse(self.call('recover')['ok']);self.assertFalse((self.state/'install-manifest.json').exists())
 def test_broken_record_requires_confirmation_then_backs_up(self):
  self.installed();record=self.state/'install-manifest.json';record.write_text('broken');self.assertFalse(self.call('recover')['ok']);self.assertEqual(record.read_text(),'broken');r=self.call('recover',replaceBrokenRecord=True);self.assertTrue(r['ok'],r);self.assertEqual(next(self.state.glob('install-manifest.json.invalid-*.bak')).read_text(),'broken')
 def test_runtime_removal_preserves_modified_unlisted_and_private_files(self):
  self.installed();(self.runtime/'settings.json').write_text('PRIVATE');(self.runtime/'notes.txt').write_text('KEEP');entries=[dict(file=n,sha256=hashlib.sha256((self.runtime/n).read_bytes()).hexdigest()) for n in ('DeadlockTranslate.exe','dlt_translate_dir.vpk','data/terms.json','settings.json')];(self.runtime/'package-manifest.json').write_text(json.dumps(dict(version='0.6.7',files=entries)));(self.runtime/'data/terms.json').write_text('CUSTOM');r=self.call('apply',mode='uninstall',removeRuntime=True);self.assertTrue(r['ok'],r);self.assertFalse((self.runtime/'DeadlockTranslate.exe').exists());self.assertEqual((self.runtime/'data/terms.json').read_text(),'CUSTOM');self.assertEqual((self.runtime/'settings.json').read_text(),'PRIVATE');self.assertEqual((self.runtime/'notes.txt').read_text(),'KEEP');self.assertEqual(next(self.state.glob('backups/gui-*/removed-runtime/DeadlockTranslate.exe')).read_bytes(),b'never run')
 def test_missing_runtime_detected_for_repair_update(self):
  self.installed();(self.runtime/'DeadlockTranslate.exe').unlink();r=self.call('plan');self.assertTrue(r['ok'],r);self.assertTrue(r['data']['needsRepair'])
 def test_repair_preserves_personal_files(self):
  self.simulate_certificate_gate();(self.state/'settings.json').write_text('PRIVATE marker');r=self.call('apply',mode='repair');self.assertTrue(r['ok'],r);self.assertIn('Mod citadel',(self.game/'game/citadel/gameinfo.gi').read_text());self.assertEqual((self.state/'settings.json').read_text(),'PRIVATE marker');self.assertTrue(self.target.exists())
 def test_uninstall_cleans_matching_accounts_only(self):
  self.installed();prefix='"'+str(self.runtime/'DeadlockTranslate.exe')+'" --steam-game %command%'
  one=self.account(1,prefix+' -novid');two=self.account(2,prefix+' -console');other=self.account(3,'"D:\\Other\\other.exe" %command%');other_before=other.read_bytes();self.account(4,'-novid',appid='777')
  r=self.call('apply',mode='uninstall',configureSteam=True);self.assertTrue(r['ok'],r);self.assertFalse(self.target.exists());self.assertNotIn('--steam-game',one.read_text());self.assertNotIn('--steam-game',two.read_text());self.assertIn('-novid',one.read_text());self.assertEqual(other.read_bytes(),other_before);self.assertTrue((self.runtime/'DeadlockTranslate.exe').exists())
 def test_uninstall_failure_restores_removed_steam_prefix(self):
  self.installed();self.target.write_bytes(self.vpk+b'changed');file=self.account(1,'"'+str(self.runtime/'DeadlockTranslate.exe')+'" --steam-game %command% -novid');before=file.read_bytes()
  r=self.call('apply',mode='uninstall',configureSteam=True);self.assertFalse(r['ok']);self.assertEqual(file.read_bytes(),before);self.assertTrue(self.target.exists());self.assertTrue((self.state/'install-manifest.json').exists())
 def test_readonly_gameinfo_requires_consent_before_copy(self):
  self.simulate_certificate_gate();gi=self.game/'game/citadel/gameinfo.gi';before=gi.read_bytes();attrs=ctypes.windll.kernel32.GetFileAttributesW(str(gi));ctypes.windll.kernel32.SetFileAttributesW(str(gi),attrs|1)
  try:
   p=self.call('plan');self.assertTrue(p['ok'],p);self.assertTrue(p['data']['needsReadOnlyConsent']);self.assertFalse(p['data']['blocked'])
   r=self.call('apply');self.assertFalse(r['ok']);self.assertEqual(r['failure']['code'],'read-only');self.assertEqual(r['failure']['path'],str(gi));self.assertEqual(list(self.runtime.iterdir()),[]);self.assertFalse(self.target.exists());self.assertEqual(gi.read_bytes(),before)
  finally:ctypes.windll.kernel32.SetFileAttributesW(str(gi),attrs)
 def test_readonly_consent_restores_attribute_and_uninstall_original(self):
  self.simulate_certificate_gate();gi=self.game/'game/citadel/gameinfo.gi';before=gi.read_bytes();attrs=ctypes.windll.kernel32.GetFileAttributesW(str(gi));ctypes.windll.kernel32.SetFileAttributesW(str(gi),attrs|1)
  try:
   r=self.call('apply',allowReadOnlyGameinfo=True);self.assertTrue(r['ok'],r);self.assertTrue(ctypes.windll.kernel32.GetFileAttributesW(str(gi))&1)
   r=self.call('apply',mode='uninstall',allowReadOnlyGameinfo=True);self.assertTrue(r['ok'],r);self.assertEqual(gi.read_bytes(),before);self.assertTrue(ctypes.windll.kernel32.GetFileAttributesW(str(gi))&1)
  finally:ctypes.windll.kernel32.SetFileAttributesW(str(gi),attrs)
 def test_locked_gameinfo_reports_full_path_and_native_code(self):
  self.simulate_certificate_gate();gi=self.game/'game/citadel/gameinfo.gi'
  create=ctypes.windll.kernel32.CreateFileW;create.restype=ctypes.c_void_p;create.argtypes=[ctypes.c_wchar_p,ctypes.c_ulong,ctypes.c_ulong,ctypes.c_void_p,ctypes.c_ulong,ctypes.c_ulong,ctypes.c_void_p]
  handle=create(str(gi),0x80000000,0,None,3,0,None);self.assertNotEqual(handle,ctypes.c_void_p(-1).value)
  try:
   r=self.call('apply');self.assertFalse(r['ok']);self.assertEqual(r['failure']['code'],'file-locked');self.assertEqual(r['failure']['nativeError'],32);self.assertEqual(r['failure']['path'],str(gi));self.assertFalse(self.target.exists());self.assertEqual(list(self.runtime.iterdir()),[])
  finally:ctypes.windll.kernel32.CloseHandle(ctypes.c_void_p(handle))
 def inject_manifest_failure(self,external_change=False):
  self.simulate_certificate_gate();manager=self.script.parent/'Manage-Mod.ps1';text=manager.read_text(encoding='utf-8-sig')
  extra="[IO.File]::WriteAllText($script:taskGameinfo,'EXTERNAL CHANGE');" if external_change else ''
  hook="""\n$script:fixtureAtomic=${function:Write-DltAtomic}
function Write-DltAtomic {param([string]$Path,[byte[]]$Bytes,[string]$ExpectedHash='',[string]$Backup='',[switch]$AllowReadOnly,[string]$Stage='commit')
 if($Stage -eq 'install-record'){EXTRA Throw-DltFault 'fixture-denied' $Stage $Path 'Fixture record failure'}
 & $script:fixtureAtomic @PSBoundParameters
}
""".replace('EXTRA',extra)
  text=text.replace(". (Join-Path $PSScriptRoot 'Install-Safety.ps1')",". (Join-Path $PSScriptRoot 'Install-Safety.ps1')"+hook);manager.write_text(text,encoding='utf-8-sig')
 def test_manifest_failure_rolls_back_gameinfo_vpk_and_keeps_root_error(self):
  self.inject_manifest_failure();gi=self.game/'game/citadel/gameinfo.gi';before=gi.read_bytes();r=self.call('apply')
  self.assertFalse(r['ok']);self.assertEqual(r['failure']['code'],'fixture-denied');self.assertEqual(r['failure']['stage'],'install-record');self.assertEqual(gi.read_bytes(),before);self.assertFalse(self.target.exists());self.assertFalse((self.state/'install-manifest.json').exists());self.assertTrue(list(self.game.glob('game/citadel/gameinfo.gi.dlt-before-install-*.bak')))
 def test_rollback_never_overwrites_external_gameinfo_change(self):
  self.inject_manifest_failure(external_change=True);gi=self.game/'game/citadel/gameinfo.gi';r=self.call('apply');self.assertFalse(r['ok']);self.assertEqual(r['failure']['code'],'fixture-denied');self.assertEqual(gi.read_text(),'EXTERNAL CHANGE');self.assertTrue(r['failure']['rollback']);self.assertFalse(self.target.exists())
 def test_steam_account_preflight_blocks_before_copy(self):
  self.simulate_certificate_gate();file=self.account(1,'-novid',appid='777');before=file.read_bytes();r=self.call('plan',configureSteam=True)
  self.assertTrue(r['ok'],r);self.assertTrue(r['data']['blocked']);self.assertTrue(any(c['code']=='steam-no-game-record' for c in r['data']['preflight']))
  r=self.call('apply',configureSteam=True);self.assertFalse(r['ok']);self.assertEqual(r['failure']['code'],'steam-no-game-record');self.assertEqual(file.read_bytes(),before);self.assertEqual(list(self.runtime.iterdir()),[]);self.assertFalse(self.target.exists())
 def test_valid_steam_preflight_is_readonly_with_missing_runtime(self):
  file=self.account(1,'-novid');before=file.read_bytes();r=self.call('plan',configureSteam=True);self.assertTrue(r['ok'],r);self.assertFalse(r['data']['blocked']);self.assertEqual(file.read_bytes(),before);self.assertEqual(list(self.runtime.iterdir()),[])
 def test_directory_access_denial_keeps_full_fault_and_no_copy(self):
  self.simulate_certificate_gate();safety=self.script.parent/'Install-Safety.ps1';text=safety.read_text(encoding='utf-8-sig')
  text += "\nfunction Assert-DltWritableDirectory {param([string]$Path,[string]$Stage);$ex=[IO.IOException]::new('Fixture permission denial',-2147024891);$er=[Management.Automation.ErrorRecord]::new($ex,'fixture',[Management.Automation.ErrorCategory]::PermissionDenied,$Path);$ex.Data['dltFault']=Get-DltFault $er $Stage $Path;throw $ex}\n";safety.write_text(text,encoding='utf-8-sig')
  r=self.call('apply');self.assertFalse(r['ok']);self.assertEqual(r['failure']['code'],'access-denied');self.assertEqual(r['failure']['nativeError'],5);self.assertEqual(r['failure']['hresult'],'0x80070005');self.assertTrue(r['failure']['path']);self.assertEqual(list(self.runtime.iterdir()),[]);self.assertFalse(self.target.exists())
if __name__=='__main__':unittest.main(verbosity=2)
