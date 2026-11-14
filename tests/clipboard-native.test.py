"""Run bitmap/text clipboard regression on a private Windows window station."""
import ctypes as c,json,pathlib,subprocess,uuid
from ctypes import wintypes as w
root=pathlib.Path(__file__).resolve().parents[1]
output=root/'artifacts'/('clipboard-native-'+uuid.uuid4().hex[:8]);output.mkdir(parents=True)
subprocess.run(['dotnet','build',str(root/'tests/BuildCatalogTests'),'-c','Release','--no-restore'],check=True)
user=c.WinDLL('user32',use_last_error=True);kernel=c.WinDLL('kernel32',use_last_error=True)
user.GetProcessWindowStation.restype=w.HANDLE
user.CreateWindowStationW.argtypes=[w.LPCWSTR,w.DWORD,w.DWORD,c.c_void_p];user.CreateWindowStationW.restype=w.HANDLE
user.SetProcessWindowStation.argtypes=[w.HANDLE];user.SetProcessWindowStation.restype=w.BOOL
user.CreateDesktopW.argtypes=[w.LPCWSTR,w.LPCWSTR,c.c_void_p,w.DWORD,w.DWORD,c.c_void_p];user.CreateDesktopW.restype=w.HANDLE
user.CloseDesktop.argtypes=[w.HANDLE];user.CloseWindowStation.argtypes=[w.HANDLE]
user.GetUserObjectInformationW.argtypes=[w.HANDLE,c.c_int,c.c_void_p,w.DWORD,c.POINTER(w.DWORD)]
class Startup(c.Structure):
 _fields_=[('cb',w.DWORD),('reserved',w.LPWSTR),('desktop',w.LPWSTR),('title',w.LPWSTR),('x',w.DWORD),('y',w.DWORD),('cx',w.DWORD),('cy',w.DWORD),('xchars',w.DWORD),('ychars',w.DWORD),('fill',w.DWORD),('flags',w.DWORD),('show',w.WORD),('reserved2size',w.WORD),('reserved2',c.c_void_p),('stdin',w.HANDLE),('stdout',w.HANDLE),('stderr',w.HANDLE)]
class Process(c.Structure):_fields_=[('process',w.HANDLE),('thread',w.HANDLE),('pid',w.DWORD),('tid',w.DWORD)]
kernel.CreateProcessW.argtypes=[w.LPCWSTR,w.LPWSTR,c.c_void_p,c.c_void_p,w.BOOL,w.DWORD,c.c_void_p,w.LPCWSTR,c.POINTER(Startup),c.POINTER(Process)];kernel.CreateProcessW.restype=w.BOOL
kernel.WaitForSingleObject.argtypes=[w.HANDLE,w.DWORD];kernel.GetExitCodeProcess.argtypes=[w.HANDLE,c.POINTER(w.DWORD)];kernel.CloseHandle.argtypes=[w.HANDLE]
old=user.GetProcessWindowStation();station=user.CreateWindowStationW(None,0,0x37f,None);desktop=None
if not station:raise c.WinError(c.get_last_error())
try:
 name_buffer=c.create_unicode_buffer(256);needed=w.DWORD()
 if not user.GetUserObjectInformationW(station,2,name_buffer,c.sizeof(name_buffer),c.byref(needed)):raise c.WinError(c.get_last_error())
 name=name_buffer.value
 assert name.lower()!='winsta0'
 desktop_names=[]
 callback_type=c.WINFUNCTYPE(w.BOOL,w.LPWSTR,w.LPARAM)
 callback=callback_type(lambda value,context:desktop_names.append(value) or True)
 user.EnumDesktopsW.argtypes=[w.HANDLE,callback_type,w.LPARAM]
 if not user.EnumDesktopsW(station,callback,0):raise c.WinError(c.get_last_error())
 desktop_name='DLTClipboard-'+uuid.uuid4().hex
 if not user.SetProcessWindowStation(station):raise c.WinError(c.get_last_error())
 try:
  desktop=user.CreateDesktopW(desktop_name,None,None,0,0xf01ff,None)
  if not desktop:raise c.WinError(c.get_last_error())
 finally:
  if not user.SetProcessWindowStation(old):raise c.WinError(c.get_last_error())
 executable=root/'tests/BuildCatalogTests/bin/Release/net10.0-windows/BuildCatalogTests.exe'
 startup=Startup();startup.cb=c.sizeof(startup);startup.desktop=name+'\\'+desktop_name;process=Process()
 command=c.create_unicode_buffer(subprocess.list2cmdline([str(executable),'--clipboard-child',str(output),name]))
 if not kernel.CreateProcessW(str(executable),command,None,None,False,0x08000000,None,str(root),c.byref(startup),c.byref(process)):raise c.WinError(c.get_last_error())
 try:
  if kernel.WaitForSingleObject(process.process,45000)!=0:raise TimeoutError('Private clipboard test did not finish')
  code=w.DWORD();kernel.GetExitCodeProcess(process.process,c.byref(code))
  if code.value:raise RuntimeError(f'Private clipboard child exited {code.value:#x}; evidence: {output}')
 finally:kernel.CloseHandle(process.thread);kernel.CloseHandle(process.process)
 result=json.loads((output/'result.json').read_text(encoding='utf-8'));assert result['nonInteractiveStation'] and result['rounds']==20
 print('PASS 20 noninteractive-station bitmap/PNG/text captures and restores; interactive clipboard untouched')
 print(json.dumps(result))
finally:
 if desktop:user.CloseDesktop(desktop)
 user.CloseWindowStation(station)
