"""Windows-only storage policy tests. All mutations stay in disposable fixture roots."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
SCRIPT=ROOT/'scripts/Backup-Maintenance.ps1'
def sha(raw):return hashlib.sha256(raw).hexdigest()
def put(path,value):
 path.parent.mkdir(parents=True,exist_ok=True);path.write_text(json.dumps(value),encoding='utf-8')

class BackupTests(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory(prefix='dlt-backup-policy-');self.root=Path(self.temp.name);self.state=self.root/'个人设置';self.backups=self.state/'backups';self.backups.mkdir(parents=True)
  for name in ('settings.json','secrets.dpapi','localhost-certificate.dpapi','terms.json','install-manifest.json'):(self.state/name).write_bytes(b'private preserved '+name.encode())
  self.private={p.name:p.read_bytes() for p in self.state.iterdir() if p.is_file()}
 def tearDown(self):
  for name,data in self.private.items():self.assertEqual((self.state/name).read_bytes(),data)
  self.temp.cleanup()
 def backup(self,n,status='succeeded'):
  folder=self.backups/f'update-20261021-{n:09}-{n:032x}';folder.mkdir();raw=f'old exe {n}'.encode();vpk=f'old vpk {n}'.encode()
  files={'DeadlockTranslate.exe':raw,'dlt_translate_dir.vpk':vpk,'pairing.key':b'1'*48,'data/terms.json':b'[]','wwwroot/index.html':b'old page','wwwroot/app.js':b'old script','wwwroot/style.css':b'old style'}
  for name,data in files.items():
   p=folder/'runtime'/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(data)
  (folder/'translation-before.vpk').write_bytes(vpk)
  put(folder/'file-plan.json',[dict(relative=name,beforeHash=sha(data),backup='Z:/ignored-untrusted-absolute-path') for name,data in files.items()])
  put(folder/'install-manifest-before.json',dict(vpkHash=sha(vpk)))
  if status is not None:put(folder/'backup-state.json',dict(schema=1,kind='update',status=status))
  return folder
 def call(self,clean=False,legacy=False,token=None,ok=True):
  args=['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(SCRIPT),'-StateDirectory',str(self.state),'-Action','Clean' if clean else 'Inspect']
  if legacy:args+=['-IncludeLegacy']
  if token:args+=['-PlanToken',token]
  result=subprocess.run(args,capture_output=True,timeout=45);text=result.stdout.decode('utf-8-sig');data=json.loads(text)
  self.assertEqual(result.returncode==0,ok,(text,result.stderr.decode(errors='replace')));return data
 def test_keep_two_successful_and_protect_failed_incomplete_unknown_and_legacy(self):
  folders=[self.backup(i) for i in range(1,5)];failed=self.backup(5,'failed');active=self.backup(6,'in-progress');legacy=self.backup(7,None)
  other=self.backups/'steam-launch';other.mkdir();(other/'original.vdf').write_bytes(b'steam preserved')
  plan=self.call();self.assertEqual(plan['cleanupCount'],2);self.assertEqual(plan['keptCount'],2);self.assertEqual(plan['protectedCount'],4)
  result=self.call(True,token=plan['planToken']);self.assertEqual(result['removedCount'],2)
  self.assertFalse(folders[0].exists());self.assertFalse(folders[1].exists())
  for p in folders[2:]+[failed,active,legacy,other]:self.assertTrue(p.exists())
 def test_explicit_legacy_cleanup_preserves_two_complete_recovery_points(self):
  folders=[self.backup(i,None) for i in range(1,5)];self.assertEqual(self.call()['cleanupCount'],0)
  plan=self.call(legacy=True);self.assertEqual(plan['cleanupCount'],2)
  result=self.call(True,True,plan['planToken']);self.assertEqual(result['removedCount'],2);self.assertGreater(result['removedBytes'],0)
  self.assertEqual(len(list(self.backups.iterdir())),2);self.assertTrue(all(p.exists() for p in folders[-2:]))
 def test_stale_plan_cannot_delete(self):
  folders=[self.backup(i) for i in range(1,4)];plan=self.call();(folders[0]/'new-user-file').write_bytes(b'preserve')
  result=self.call(True,token=plan['planToken'],ok=False);self.assertIn('备份已变化',result['error']);self.assertTrue(all(p.exists() for p in folders))
 def test_malformed_missing_and_unknown_contents_are_protected(self):
  self.backup(1);bad=self.backup(2);(bad/'runtime/DeadlockTranslate.exe').unlink();unknown=self.backup(3);(unknown/'personal.txt').write_text('preserved')
  corrupt=self.backup(4);(corrupt/'backup-state.json').write_text('invalid json')
  plan=self.call();self.assertEqual(plan['cleanupCount'],0);self.assertEqual(plan['protectedCount'],3)
 def test_retained_hash_corruption_stops_cleanup_before_any_deletion(self):
  folders=[self.backup(i) for i in range(1,4)];(folders[-1]/'runtime/DeadlockTranslate.exe').write_bytes(b'corrupt')
  plan=self.call();self.call(True,token=plan['planToken'],ok=False);self.assertTrue(all(p.exists() for p in folders))
 def test_old_candidate_hash_corruption_is_preserved_with_warning(self):
  folders=[self.backup(i) for i in range(1,4)];(folders[0]/'runtime/DeadlockTranslate.exe').write_bytes(b'corrupt')
  plan=self.call();result=self.call(True,token=plan['planToken']);self.assertEqual(result['removedCount'],0);self.assertEqual(len(result['warnings']),1)
  self.assertTrue(all(p.exists() for p in folders));self.assertEqual(json.loads((folders[0]/'backup-state.json').read_text())['status'],'cleanup-failed')
 def test_relative_traversal_and_absolute_backup_hints_are_never_followed(self):
  bad=self.backup(1);outside=self.root/'outside.exe';outside.write_bytes(b'outside')
  put(bad/'file-plan.json',[dict(relative='../../outside.exe',beforeHash=sha(b'outside'),backup=str(outside))])
  self.backup(2);self.backup(3);plan=self.call(legacy=True);self.assertEqual(plan['cleanupCount'],0);self.assertEqual(outside.read_bytes(),b'outside')
 def test_cross_process_lock_prevents_scan_and_cleanup(self):
  self.backup(1);api=ctypes.WinDLL('kernel32',use_last_error=True);api.CreateFileW.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p];api.CreateFileW.restype=ctypes.c_void_p;api.CloseHandle.argtypes=[ctypes.c_void_p]
  handle=api.CreateFileW(str(self.state/'backup-maintenance.lock'),0xC0000000,0,None,4,0,None);self.assertNotEqual(handle,ctypes.c_void_p(-1).value)
  try:self.assertIn('正在更新或清理',self.call(ok=False)['error'])
  finally:api.CloseHandle(handle)
 def test_junction_is_preserved_and_external_files_untouched(self):
  outside=self.root/'outside';outside.mkdir();(outside/'original.txt').write_bytes(b'outside unchanged')
  folder=self.backup(1);link=folder/'linked'
  result=subprocess.run(['cmd.exe','/d','/c','mklink','/J',str(link),str(outside)],capture_output=True)
  if result.returncode:self.skipTest('Junction creation unavailable')
  try:
   self.backup(2);self.backup(3);plan=self.call();self.assertEqual(plan['cleanupCount'],0);self.assertEqual(plan['protectedCount'],1);self.assertEqual((outside/'original.txt').read_bytes(),b'outside unchanged')
  finally:os.rmdir(link)
 def test_cleanup_requires_a_preview_token(self):
  self.backup(1);self.assertIn('先检查',self.call(True,ok=False)['error'])
 def test_incomplete_and_mixed_pre_repair_runtimes_cannot_displace_valid_points(self):
  first=self.backup(1);second=self.backup(2);missing=self.backup(3);(missing/'runtime/DeadlockTranslate.exe').unlink()
  mixed=self.backup(4);record=json.loads((mixed/'install-manifest-before.json').read_text());record['vpkHash']='0'*64;put(mixed/'install-manifest-before.json',record)
  plan=self.call();self.assertEqual(plan['cleanupCount'],0);self.assertEqual(plan['keptCount'],2);self.assertEqual(plan['protectedCount'],2);self.assertTrue(first.exists() and second.exists())
 def test_powershell_51_bom_is_preserved(self):self.assertTrue(SCRIPT.read_bytes().startswith(b'\xef\xbb\xbf'))
 def test_preview_does_not_hash_large_exe_or_change_backup_contents(self):
  folder=self.backup(1);before={str(p.relative_to(folder)):p.read_bytes() for p in folder.rglob('*') if p.is_file()}
  self.call();after={str(p.relative_to(folder)):p.read_bytes() for p in folder.rglob('*') if p.is_file()};self.assertEqual(before,after)

if __name__=='__main__':unittest.main(verbosity=2)
