import pathlib,sys
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'scripts'))
from steam_launch_options import plan,locate,decode
base='"UserLocalConfigStore"\n{\n "Software"\n {\n "Valve"\n {\n "Steam"\n {\n "apps"\n {\n "1422450"\n {\n FIELD\n }\n "123" { "LaunchOptions" "untouched" }\n }\n }\n }\n }\n}\n'
exe=r'G:\含 空格\DeadlockTranslate.exe'
text=base.replace('FIELD','"LaunchOptions" "-novid -language schinese"')
updated,before,after=plan(text,exe)
assert before=='-novid -language schinese' and after.endswith('%command% -novid -language schinese')
old,_=locate(text);new,_=locate(updated)
assert updated[:new.start()]==text[:old.start()] and updated[new.end():]==text[old.end():]
assert plan(updated,exe)==(updated,after,after)
empty=base.replace('FIELD','// a comment with { braces }')
added,_,_=plan(empty,exe);assert '"123" { "LaunchOptions" "untouched" }' in added
assert decode(locate(added)[0].group()).startswith('"'+exe+'"')
try: plan(base.replace('FIELD','"LaunchOptions" "another.exe %command%"'),exe)
except ValueError: pass
else: raise AssertionError('existing wrapper overwritten')
print('PASS 6 Steam launch-options preservation and conflict checks')
