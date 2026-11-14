"""Native dialog templates and context markers survive layout patching."""
import importlib.util,os,pathlib,sys,xml.etree.ElementTree as ET
root=pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0,str(root/'scripts'))
os.environ.setdefault('DEADLOCK_TRANSLATE_GAME',str(root))
spec=importlib.util.spec_from_file_location('prepare_mod',root/'scripts/prepare_mod.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
fixtures={
 'citadel_ui_build_details':('<CitadelBuildDetails><Label class="BuildName" text="#Citadel_HeroBuilds_BuildName"/><Panel id="DetailsTop"><Panel id="BuildDetailsTabs"/><Panel class="TagList"></Panel></Panel><Panel id="BuildDetails_Overview"><Label class="BuildDescription" text="#Citadel_HeroBuilds_BuildDescription"/></Panel></CitadelBuildDetails>','Details'),
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
  old_label=next(label for label in before.iter('Label') if label.attrib.get('text')==token)
  old_parent=next(parent for parent in before.iter() if old_label in list(parent))
  new_parent=next(parent for parent in after.iter() if labels[0] in list(parent))
  assert old_parent.tag==new_parent.tag and old_parent.attrib==new_parent.attrib,(name,'native parent changed')
  native_children=[child for child in new_parent if not child.attrib.get('id','').startswith('DltBuildContext')
                   and 'DltBoundTranslation' not in child.attrib.get('class','').split()
                   and 'DltBuildActions' not in child.attrib.get('class','').split() and child.tag!='HTML']
  assert list(old_parent).index(old_label)==native_children.index(labels[0]),(name,'native sibling ordering changed')
  if role=='Item':assert list(old_parent).index(old_label)==list(new_parent).index(labels[0]),'annotation must retain its exact native child index'
  assert any(label.attrib.get('id','').endswith('Translation') for label in new_parent.iter('Label'))
 assert not any('DltBuildTextSlot' in panel.attrib.get('class','') for panel in after.iter('Panel'))
 markers=[p for p in after.iter('Panel') if p.attrib.get('id')=='DltBuildContext'+role]
 assert len(markers)==1
 if role=='Details':
  button=next(p for p in after.iter() if p.attrib.get('id')=='DltBuildTranslate')
  header=next(p for p in after.iter() if p.attrib.get('id')=='DetailsTop')
  assert button in list(header.iter()) and button.tag=='CitadelButton'
  assert button.attrib.get('onmouseactivate')=='DLTBuildTextAction();'
 print('PASS native text tokens and explicit '+role+' context remain intact')
