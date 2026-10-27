"""Three-way glossary upgrade: preserve edits and intentional deletions."""
import argparse,json,pathlib,os

def merge(old,new,user):
    def index(items):
        result={}
        for item in items:
            key=item['source'].casefold()
            if key in result:raise ValueError('Duplicate glossary source')
            legacy=item.get('legacyTarget','')
            if not isinstance(legacy,str) or len(legacy)>150:raise ValueError('Invalid legacy display name')
            result[key]=item
        return result
    previous,updated,saved=index(old),index(new),index(user)
    def same(left,right):
        return {**left,'legacyTarget':left.get('legacyTarget','')}=={**right,'legacyTarget':right.get('legacyTarget','')}
    output=[];seen=set()
    for key,item in saved.items():
        if key in previous and same(item,previous[key]):
            if key not in updated:continue
            item=updated[key]
        output.append(item);seen.add(key)
    for key,item in updated.items():
        if key not in previous and key not in seen:output.append(item)
    if len(output)>5000:raise ValueError('Merged glossary exceeds limit')
    return output

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--old',required=True);p.add_argument('--new',required=True);p.add_argument('--user',required=True);a=p.parse_args()
    paths=[pathlib.Path(s) for s in (a.old,a.new,a.user)]
    raw=paths[2].read_bytes();data=[json.loads(s.read_text(encoding='utf-8-sig')) for s in paths]
    result=merge(*data)
    if result!=data[2]:
        if paths[2].read_bytes()!=raw:raise ValueError('User glossary changed during merge')
        temporary=paths[2].with_suffix('.upgrade.tmp')
        temporary.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
        os.replace(temporary,paths[2])
    print('Glossary updated; user customizations and deletions preserved; entries='+str(len(result)))
