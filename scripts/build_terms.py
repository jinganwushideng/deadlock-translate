"""Generate bilingual names from the user's installed Valve localization resources."""
import json, pathlib, re, hashlib, os
from term_extensions import ALIASES, AMBIGUOUS_ALIASES, PHRASES, AMBIGUOUS_PHRASES, AMBIGUOUS_HINTS
root=pathlib.Path(__file__).resolve().parents[1]
localization=pathlib.Path(os.environ['DEADLOCK_TRANSLATE_GAME'])/'game/citadel/resource/localization'
(root/'artifacts').mkdir(parents=True,exist_ok=True)
def read_tokens(file):
    raw=file.read_bytes()
    text=raw.decode('utf-16' if raw.startswith((b'\xff\xfe',b'\xfe\xff')) else 'utf-8-sig')
    return dict(re.findall(r'^\s*"([^"\n]+)"\s+"((?:\\.|[^"\\])*)"',text,re.M))

ambiguous={'seven','pocket','haze','ivy','warden','wraith','dynamo','mirage','paradox','viscous','lash','vindicta','infernus','abrams','silencer','ricochet','knockdown','curse','echo shard','vampire','sapper','glider','gunner','cowboy','phoenix','assassin','magician','doorman','mina','victor'}
ambiguous -= {'infernus','abrams','vindicta','viscous','echo shard'}
terms={}; provenance=[]; categories={}; skipped_aliases=[]
def add(source,target,english='',kind='phrase',ambiguous=False,category='curated',replace=False):
    # Valve's Russian localization embeds grammatical-gender tags that players never type.
    source=re.sub(r'#\|[^#\r\n]*\|#','',source).strip();target=re.sub(r'#\|[^#\r\n]*\|#','',target).strip()
    if not source or not target or len(source)>100 or len(target)>150 or len(english)>100 or any(c in source+target for c in '<>{}\n\r') or not re.search('[\u3400-\u9fff]',target):return
    key=source.casefold()
    if key in terms and not replace:return
    terms[key]={'source':source,'target':target,'english':english,'kind':kind,'ambiguous':ambiguous}
    categories[key]=category
for folder in ('citadel_gc_hero_names','citadel_gc_mod_names'):
    enfile=localization/folder/(folder+'_english.txt'); zhfile=localization/folder/(folder+'_schinese.txt')
    rufile=localization/folder/(folder+'_russian.txt')
    en=read_tokens(enfile); zh=read_tokens(zhfile); ru=read_tokens(rufile)
    for token,name in en.items():
        if '_search' in token or '_sort' in token or token not in zh: continue
        if folder.endswith('hero_names') and not token.startswith('hero_'): continue
        if folder.endswith('mod_names') and not token.startswith('upgrade_'): continue
        if token in ('hero_test:n','hero_targetdummy:n','hero_genericperson:n'):continue
        target=zh[token]
        if not re.search('[\u3400-\u9fff]',target) or len(name)>65 or '<' in target:continue
        add(name,target,name,'name',name.lower() in ambiguous,'official_names')
        if token in ru:add(ru[token],target,name,'name',name.lower() in ambiguous,'official_russian')
    provenance.extend({'file':str(f),'sha256':hashlib.sha256(f.read_bytes()).hexdigest()} for f in (enfile,zhfile,rufile))
aliases={'bebop':'Bebop','geist':'Lady Geist','mo krill':'Mo & Krill','m&k':'Mo & Krill','inferno':'Infernus','infern':'Infernus','vin':'Vindicta','yam':'Yamato','lash':'Lash','дедлок':'Deadlock','инфернус':'Infernus','бибоп':'Bebop','ямато':'Yamato','абрамс':'Abrams','виндикта':'Vindicta','хейз':'Haze','севен':'Seven','покет':'Pocket','дайнамо':'Dynamo','мираж':'Mirage'}
aliases.update({'севена':'Seven','бибопа':'Bebop','абрамса':'Abrams','виндикту':'Vindicta','виндикты':'Vindicta','покета':'Pocket','инфернуса':'Infernus'})
for alias,name in aliases.items():
    if alias in terms:continue
    if name.lower() in terms: terms[alias]={**terms[name.lower()],'source':alias,'ambiguous':False}
phrases={
 'ult':'大招','ulti':'大招','ultimate':'大招','cd':'冷却','cooldown':'冷却','oom':'没蓝了','mb':'我的锅','my bad':'我的锅','gg':'打得好','gg wp':'打得好，精彩对局','wp':'打得漂亮','gl hf':'祝好运，玩得开心','nt':'尽力了','gj':'干得好','ty':'谢谢','np':'没事','brb':'马上回来','afk':'暂时离开','dc':'掉线了','re':'回来了','ss':'敌人消失了','mia':'敌人不在线上','b':'撤退','back':'撤退','fall back':'往后撤','retreat':'撤退','go back':'撤回来','group':'集合','group up':'集合','push':'推进','push mid':'推中路','push left':'推左路','push right':'推右路','def':'防守','defend':'防守','def base':'守家','defend base':'守家','end':'一波结束','end now':'现在一波结束','farm':'打钱发育','farm safely':'安全发育','split push':'分推','dont fight':'别打','don\'t fight':'别打','wait':'等一下','wait for ult':'等大招','no ult':'没大招','ult ready':'大招好了','ult in 10':'大招还有10秒','ult in 20':'大招还有20秒','ult in 30':'大招还有30秒','ult in 60':'大招还有60秒','i have ult':'我有大招','need heal':'需要治疗','need help':'需要支援','help me':'帮我一下','on my way':'我在赶来','omw':'正在赶来','rotate':'转线支援','gank':'抓人','bait':'诱敌','peel':'保护队友','focus':'集火','low hp':'残血','one hp':'丝血','one shot':'一下就能打死','stun':'眩晕','silence':'沉默','parry':'招架','deny':'反补','souls':'魂','soul lead':'魂量领先','jungle':'野区','mid boss':'中立首领','midboss':'中立首领','rejuv':'复活强化','urn':'灵魂坛','run urn':'运灵魂坛','take urn':'拿灵魂坛','walker':'行者','guardians':'守卫','patron':'主宰','shrines':'神龛','flex slot':'弹性槽位','zipline':'滑索','bridge buff':'桥梁增益','heal cut':'减疗','anti heal':'减疗','antiheal':'减疗','bullet resist':'子弹抗性','spirit resist':'元灵抗性','yellow lane':'黄路','blue lane':'蓝路','purple lane':'紫路','green lane':'绿路',
 'ульт':'大招','ульта':'大招','нет ульты':'没大招','ульт готов':'大招好了','кд':'冷却','назад':'撤退','отходим':'撤退','не деритесь':'别打','не дерись':'别打','ждите':'等一下','помогите':'来帮忙','помоги':'帮我一下','нужна помощь':'需要支援','иду':'我在赶来','соберитесь':'集合','вместе':'一起行动','пуш':'推进','пушим':'推进','деф':'防守','дефаем':'防守','фарм':'打钱发育','хил':'治疗','нужен хил':'需要治疗','души':'魂','урна':'灵魂坛','несу урну':'我在运灵魂坛','босс':'首领','мидбосс':'中立首领','слева':'左边','справа':'右边','мид':'中路','враг пропал':'敌人消失了','мало хп':'残血','спасибо':'谢谢','извини':'抱歉','моя вина':'我的锅','хорош':'干得好','стой':'停一下','база':'基地','домой':'回家','не умирай':'别死','живи':'活下来','жди ульту':'等大招','добей':'补掉他','фокус':'集火'
}
phrases["don't finish boss until they die"]='等他们死了再给首领最后一击。'
for source,target in phrases.items():
    if source in terms:continue
    terms[source]={'source':source,'target':target,'english':'','kind':'phrase','ambiguous':False}
# Do not resolve standalone ambiguous conversational words with a hard-coded tactical meaning.
for source in ('b','back','end','focus','wait','group','push','farm','stun','silence','souls','walker','patron','urn','мид','база','стой','живи'):
    if source in terms:terms[source]['ambiguous']=True

# Skills are taken only from keys paired with an actual description, not tooltip labels.
folder='citadel_heroes'
files=[localization/folder/(folder+'_'+language+'.txt') for language in ('english','schinese','russian')]
en,zh,ru=[read_tokens(f) for f in files]
for token,name in en.items():
    if not token.startswith('citadel_ability_') or token+'_desc' not in en or token not in zh:continue
    is_ambiguous=len(name.split())==1 or name in {'Full Auto','Static Charge','Flight','Shoulder Charge'}
    add(name,zh[token],name,'name',is_ambiguous,'official_abilities')
    if token in ru:add(ru[token],zh[token],name,'name',is_ambiguous,'official_abilities_russian')
provenance.extend({'file':str(f),'sha256':hashlib.sha256(f.read_bytes()).hexdigest()} for f in files)

# Current game resources renamed the item, while players still use its old SSB abbreviation.
renamed_items={'Spirit Shredder Bullets':'Spirit Shredder'}
for canonical,sources in ALIASES.items():
    original=terms.get(canonical.casefold()) or terms.get(renamed_items.get(canonical,'').casefold())
    if not original:skipped_aliases.append(canonical);continue
    for source in sources:
        add(source,original['target'],original['english'],'name',source in AMBIGUOUS_ALIASES,'chat_aliases')
for source,target in PHRASES.items():add(source,target,ambiguous=source in AMBIGUOUS_PHRASES,category='chat_phrases',replace=True)
for source,target in AMBIGUOUS_HINTS.items():add(source,target,ambiguous=True,category='ambiguous_hints',replace=True)
# Bring objective vocabulary into line with this installed localization; retain ambiguity for ordinary nouns.
folder='citadel_main'
files=[localization/folder/(folder+'_'+language+'.txt') for language in ('english','schinese','russian')]
en,zh,ru=[read_tokens(f) for f in files]
for token in ('guardian_unit','walker_unit','titan_unit','Citadel_Hud_KillFeedBarracksBoss','Citadel_AttackerClass_CLASS_TROOPER','Citadel_AttackerClass_CLASS_SUPER_NEUTRAL'):
    if token in en and token in zh:
        add(en[token],zh[token],en[token],'name',len(en[token].split())==1,'official_objectives',True)
        if token in ru:add(ru[token],zh[token],en[token],'name',len(ru[token].split())==1,'official_objectives_russian')
for source,target in {'urn':'灵瓮','урна':'灵瓮','guardians':'卫士','shrines':'圣坛','rejuv':'复生石','несу урну':'我在运灵瓮'}.items():
    add(source,target,ambiguous=source in {'urn','урна'},category='objectives',replace=True)
provenance.extend({'file':str(f),'sha256':hashlib.sha256(f.read_bytes()).hexdigest()} for f in files)
provenance.append({'file':str(root/'scripts/term_extensions.py'),'sha256':hashlib.sha256((root/'scripts/term_extensions.py').read_bytes()).hexdigest()})
output=root/'src/Bridge/data/terms.json';output.parent.mkdir(parents=True,exist_ok=True)
output.write_text(json.dumps(list(terms.values()),ensure_ascii=False,indent=2),encoding='utf-8')
(root/'artifacts/term-sources.json').write_text(json.dumps(provenance,ensure_ascii=False,indent=2),encoding='utf-8')
for key in terms:categories.setdefault(key,'legacy_chat')
(root/'artifacts/term-expansion.json').write_text(json.dumps({'total':len(terms),'categories':{c:sum(v==c for v in categories.values()) for c in sorted(set(categories.values()))},'skippedAliases':skipped_aliases},ensure_ascii=False,indent=2),encoding='utf-8')
print(f'TERMS={len(terms)}')
