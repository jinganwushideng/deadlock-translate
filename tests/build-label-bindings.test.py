"""Native dialog templates and context markers survive layout patching."""
import importlib.util,os,pathlib,sys,xml.etree.ElementTree as ET
root=pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0,str(root/'scripts'))
os.environ.setdefault('DEADLOCK_TRANSLATE_GAME',str(root))
spec=importlib.util.spec_from_file_location('prepare_mod',root/'scripts/prepare_mod.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
fixtures={
 'citadel_ui_build_details':('<CitadelBuildDetails><Label class="BuildName" text="#Citadel_HeroBuilds_BuildName"/><Panel id="BuildDetails_Overview"><Label class="BuildDescription" text="#Citadel_HeroBuilds_BuildDescription"/></Panel></CitadelBuildDetails>','Details'),
 'citadel_shop_mods_build':('<snippets><snippet name="FavoriteBuildEntry"><Label class="FavoriteBuildEntryLabel SelectedBuildName" text="#Citadel_HeroBuilds_BuildName"/></snippet></snippets><CitadelShopModsBuild><Panel id="SelectedBuildOuter" /><Panel id="ControlButtons"></Panel></CitadelShopModsBuild>','Shop'),
 'citadel_shop_mods_build_category':('<CitadelShopModsBuildCategory><Panel><Label id="BuildCategoryName" text="#Citadel_HeroBuilds_CategoryName"/><Label class="CategoryDescription" text="#Citadel_HeroBuilds_CategoryDescription"/></Panel></CitadelShopModsBuildCategory>','Category'),
 'tooltips/citadel_tooltip_mod_details':('<CitadelTooltipModDetails><Label id="ModName"/><Label class="ModifiesLabel" text="#Citadel_Item_Build_Annotation"/></CitadelTooltipModDetails>','Item'),
}
for name,(body,role) in fixtures.items():
 before=ET.fromstring('<root><styles></styles>'+body+'</root>')
 text='<root><styles></styles>'+body+'</root>'
 after=ET.fromstring(module.patch_build_text_layout(text,name))
 tokens=[label.attrib['text'] for label in before.iter('Label') if label.attrib.get('text','').startswith('#')]
 for token in tokens:
  labels=[label for label in after.iter('Label') if label.attrib.get('text')==token]
  assert len(labels)==1,(name,token)
  slots=[p for p in after.iter('Panel') if 'DltBuildTextSlot' in p.attrib.get('class','') and labels[0] in list(p)]
  assert len(slots)==1,(name,'native field was detached from its token slot')
  assert any(label.attrib.get('id','').endswith('Translation') for label in slots[0].iter('Label'))
 markers=[p for p in after.iter('Panel') if p.attrib.get('id')=='DltBuildContext'+role]
 assert len(markers)==1
 print('PASS native text tokens and explicit '+role+' context remain intact')
