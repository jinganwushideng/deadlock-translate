"""Targeted offline Steam launch-options update. Keeps other VDF bytes untouched."""
import argparse, ctypes, hashlib, json, pathlib, re, subprocess, time, winreg

TOKEN = re.compile(r'//[^\r\n]*|"(?:\\.|[^"\\])*"|[{}]|[^\s{}"]+')

def decode(value):
    if not value.startswith('"'): return value
    return re.sub(r'\\([\\"])', r'\1', value[1:-1])

def locate(text):
    tokens=[m for m in TOKEN.finditer(text) if not m.group().startswith('//')]
    found=[]
    def block(index, parents):
        fields=[]
        while index<len(tokens) and tokens[index].group()!='}':
            key=tokens[index]; index+=1
            if index>=len(tokens): raise ValueError('Incomplete VDF')
            value=tokens[index]; index+=1
            name=decode(key.group()).lower()
            if value.group()=='{':
                index,children,end=block(index,parents+[name])
                if parents+[name]==['userlocalconfigstore','software','valve','steam','apps','1422450']:
                    found.append((children,end))
            elif value.group()=='}': raise ValueError('Invalid VDF value')
            else: fields.append((name,key,value))
        if parents:
            if index>=len(tokens): raise ValueError('Unclosed VDF block')
            return index+1,fields,tokens[index]
        return index,fields,None
    block(0,[])
    if len(found)!=1: raise ValueError('Cannot uniquely locate Deadlock launch settings')
    fields,end=found[0]
    values=[value for name,key,value in fields if name=='launchoptions']
    if len(values)>1: raise ValueError('Duplicate LaunchOptions')
    return (values[0] if values else None),end

def plan(text, executable):
    value,end=locate(text)
    before=decode(value.group()) if value else ''
    prefix='"'+str(executable)+'" --steam-game %command%'
    if before==prefix or before.startswith(prefix+' '): return text,before,before
    if '%command%' in before.lower(): raise ValueError('Existing wrapper launch options need manual integration')
    after=prefix+(' '+before if before else '')
    encoded='"'+after.replace('\\','\\\\').replace('"','\\"')+'"'
    if value: updated=text[:value.start()]+encoded+text[value.end():]
    else:
        newline='\r\n' if '\r\n' in text else '\n'
        line=text.rfind('\n',0,end.start())+1
        indent=text[line:end.start()]
        if indent.strip(): raise ValueError('Expected a separate closing-brace line')
        updated=text[:line]+indent+'\t"LaunchOptions"\t\t'+encoded+newline+text[line:]
    return updated,before,after

def steam_running():
    result=subprocess.run(['tasklist','/FI','IMAGENAME eq steam.exe','/FO','CSV','/NH'],capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW,check=True)
    return b'"steam.exe"' in result.stdout.lower()

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--runtime',required=True);parser.add_argument('--apply',action='store_true');parser.add_argument('--output',required=True);parser.add_argument('--user-id',type=int)
    args=parser.parse_args()
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Valve\Steam') as key: steam=pathlib.Path(winreg.QueryValueEx(key,'SteamPath')[0])
    if args.user_id is not None: user=args.user_id
    else:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Valve\Steam\ActiveProcess') as key: user=winreg.QueryValueEx(key,'ActiveUser')[0]
    if not user: raise ValueError('No active Steam user')
    file=steam/'userdata'/str(user)/'config/localconfig.vdf'
    executable=pathlib.Path(args.runtime).resolve()/'DeadlockTranslate.exe'
    if not executable.is_file(): raise ValueError('Runtime executable missing')
    original=file.read_bytes();text=original.decode('utf-8-sig')
    updated,before,after=plan(text,executable)
    result={'file':str(file),'before':before,'after':after,'applied':False}
    if args.apply and updated!=text:
        if steam_running(): raise ValueError('Exit Steam normally before applying launch settings')
        encoded=(b'\xef\xbb\xbf' if original.startswith(b'\xef\xbb\xbf') else b'')+updated.encode('utf-8')
        if file.read_bytes()!=original: raise ValueError('Steam settings changed during preparation')
        backup=file.with_name('localconfig.dlt-backup-'+str(time.time_ns())+'.vdf')
        temporary=file.with_name('localconfig.dlt-tmp-'+str(time.time_ns())+'.vdf')
        temporary.write_bytes(encoded)
        replace=ctypes.WinDLL('kernel32',use_last_error=True).ReplaceFileW
        replace.argtypes=[ctypes.c_wchar_p]*3+[ctypes.c_uint32,ctypes.c_void_p,ctypes.c_void_p]
        replace.restype=ctypes.c_int
        try:
            if steam_running() or file.read_bytes()!=original: raise ValueError('Steam settings no longer safe to update')
            if not replace(str(file),str(temporary),str(backup),0,None,None): raise ctypes.WinError(ctypes.get_last_error())
        finally:
            if temporary.exists(): temporary.unlink()
        if backup.read_bytes()!=original or file.read_bytes()!=encoded: raise ValueError('Persisted settings verification failed')
        result.update(applied=True,backup=str(backup),sha256=hashlib.sha256(encoded).hexdigest())
    elif args.apply: result['applied']=True
    pathlib.Path(args.output).write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(result,ensure_ascii=False))

if __name__=='__main__': main()
