import argparse, hashlib, json, pathlib, re, secrets, subprocess, shutil, os
import xml.etree.ElementTree as ET
from vpk import Vpk
from native_bubble_layout import STYLE as NATIVE_BUBBLE_STYLE, patch_native_bubble
root = pathlib.Path(__file__).resolve().parents[1]
game = pathlib.Path(os.environ['DEADLOCK_TRANSLATE_GAME'])/'game/citadel'

BUILD_TEXT_LAYOUTS = {
    'citadel_ui_build_selector': ('<Label class="BuildName"',),
    'citadel_ui_build_details': (
        '<Label class="BuildName"', '<Label class="BuildDescription"'),
    'citadel_shop_mods_build': ('<Label class="FavoriteBuildEntryLabel SelectedBuildName"',),
    'citadel_shop_mods_build_category': (
        '<Label id="BuildCategoryName"', '<Label class="CategoryDescription"'),
    'tooltips/citadel_tooltip_mod_details': (
        '<Label id="ModName"', '<Label class="ModifiesLabel" text="#Citadel_Item_Build_Annotation"'),
}
BUILD_TEXT_RESOURCES = {
    'panorama/scripts/dlt_build_transport.vjs_c',
    'panorama/scripts/dlt_build_browser.vjs_c',
    'panorama/scripts/dlt_build_text.vjs_c',
    'panorama/styles/dlt_build_text.vcss_c',
    *(f'panorama/layout/{name}.vxml_c' for name in BUILD_TEXT_LAYOUTS),
}

def preserve_native_label_tree(xml):
    tree = ET.fromstring(xml)
    parents = {child: parent for parent in tree.iter() for child in parent}
    for slot in list(tree.iter('Panel')):
        if 'DltBuildTextSlot' not in slot.attrib.get('class', '').split():
            continue
        parent = parents[slot]
        index = list(parent).index(slot)
        original, translated = list(slot)
        parent.remove(slot)
        parent.insert(index, original)
        # Append our drawing-only label. Native children keep their original
        # parent and indexes, including the annotation label at NotesContainer[0].
        parent.append(translated)
    for parent in tree.iter():
        for translated in list(parent):
            if 'DltBoundTranslation' in translated.attrib.get('class', '').split():
                parent.remove(translated); parent.append(translated)
        for marker in list(parent):
            if marker.attrib.get('id', '').startswith('DltBuildContext'):
                parent.remove(marker); parent.append(marker)
    return ET.tostring(tree, encoding='unicode')


def patch_build_text_layout(xml, name):
    anchors = BUILD_TEXT_LAYOUTS[name]
    includes = ('<include src="s2r://panorama/styles/dlt_build_text.vcss_c" />'
                '</styles><scripts><include src="s2r://panorama/scripts/dlt_build_transport.vjs_c" />'
                '<include src="s2r://panorama/scripts/dlt_build_text.vjs_c" /></scripts>')
    if xml.count('</styles>') != 1 or '<scripts>' in xml:
        raise ValueError(f'Unexpected styles or scripts in {name}')
    xml = xml.replace('</styles>', includes)
    contexts = {
        'citadel_ui_build_selector': ('CitadelHeroBuildsSelector', 'Selector'),
        'citadel_ui_build_details': ('CitadelBuildDetails', 'Details'),
        'citadel_shop_mods_build': ('CitadelShopModsBuild', 'Shop'),
        'citadel_shop_mods_build_category': ('CitadelShopModsBuildCategory', 'Category'),
        'tooltips/citadel_tooltip_mod_details': ('CitadelTooltipModDetails', 'Item'),
    }
    tag, role = contexts[name]
    xml, count = re.subn(r'<' + tag + r'(?:\s[^>]*)?>', lambda match: match[0] +
                        f'<Panel id="DltBuildContext{role}" class="DltBuildContextMarker" hittest="false" />', xml, count=1)
    if count != 1:
        raise ValueError(f'Missing native context root in {name}')
    if name == 'citadel_ui_build_selector':
        xml = xml.replace('</scripts>', '<include src="s2r://panorama/scripts/dlt_build_browser.vjs_c" /></scripts>')
        anchor = '<Label class="BuildName"'
        if xml.count(anchor) != 1:
            raise ValueError('Build browser list changed')
        start = xml.index(anchor)
        end = xml.index('/>', start) + 2
        label = xml[start:end].replace('<Label ', '<Label id="DltListName" ', 1)
        xml = xml[:start] + label + xml[end:]
        row_anchor = '<Label class="SelectedBuild"'
        if xml.count(row_anchor) != 1:
            raise ValueError('Build browser row changed')
        name_row = '<Panel class="nameRow">'
        if xml.count(name_row) != 1:
            raise ValueError('Build browser name row changed')
        xml = xml.replace(name_row, '<Panel class="DltBuildListNames">' + name_row)
        xml = xml.replace(row_anchor, '<Label id="DltListNameTranslation" class="DltListTranslation" visible="false" html="false" /></Panel>' + row_anchor)
        anchor = '<Panel class="HeroSelectorHeader">'
        if xml.count(anchor) != 1:
            raise ValueError('Build browser header changed')
        controls = ('<Panel class="DltBuildListActions"><CitadelButton id="DltListTranslate" class="Medium Fill Dark" '
                    'text="翻译当前列表" onmouseactivate="DLTBuildListAction();" />'
                    '<Label id="DltListStatus" class="DltBuildProgressText" /></Panel>'
                    '<HTML id="DltListBridge" class="DltBuildBridge" hittest="false" hittestchildren="false" acceptsinput="false" acceptsfocus="false" />')
        return xml.replace(anchor, anchor + controls)
    category = name == 'citadel_shop_mods_build_category'
    shop = name == 'citadel_shop_mods_build'
    prefix = 'DltCategory' if category else 'DltShop' if shop else 'DltBuild'
    for field, anchor in zip(('Title', 'Note'), anchors):
        if xml.count(anchor) != 1:
            raise ValueError(f'Expected exactly one {field} label in {name}')
        start = xml.index(anchor)
        end = xml.index('/>', start) + 2
        original = xml[start:end]
        if 'id=' not in original:
            original = original.replace('<Label ', f'<Label id="{prefix}{field}" ', 1)
        style = ('ShopTitle' if shop else 'Category' + field if category else
                 'Details' + field if name == 'citadel_ui_build_details' else 'Item' + field)
        output = f'<Label id="{prefix}{field}Translation" class="DltBoundTranslation Dlt{style}Text" html="false" visible="false" />'
        # Keep the native text/localization token intact. Writing Label.text would
        # detach selected_hero_build_name/category_name dialog-variable updates.
        paired = original + output if style == 'ItemTitle' else (
            f'<Panel class="DltBuildTextSlot Dlt{style}Slot" hittest="false">' + original + output + '</Panel>')
        xml = xml[:start] + paired + xml[end:]
    bridge = ('<HTML id="DltBuildBridge" class="DltBuildBridge" hittest="false" '
              'hittestchildren="false" acceptsinput="false" acceptsfocus="false" />')
    def progress(prefix):
        return (f'<Panel id="{prefix}Progress" class="DltBuildProgress DltBuildProgressPopup" hittest="false" visible="false">'
                '<Label class="DltBuildProgressHeading" text="出装翻译" />'
                '<Panel class="DltBuildProgressTrack"><Panel id="DltBuildProgressFill" class="DltBuildProgressFill" /></Panel>'
                '<Label id="DltBuildProgressText" class="DltBuildProgressText" /></Panel>')
    def actions(prefix, button, classes, event):
        return ('<Panel class="DltBuildActions">'
                f'<{button} id="{prefix}Translate" class="{classes}" text="翻译当前出装" {event}="DLTBuildTextAction();" />'
                '</Panel>')
    if name == 'citadel_ui_build_details':
        anchor = '<Panel id="BuildDetails_Overview">'
        if xml.count(anchor) != 1:
            raise ValueError('Build details controls changed')
        xml = xml.replace(anchor, anchor + actions('DltBuild', 'TextButton', 'Medium Fill DltBuildTranslateButton', 'onactivate') + bridge)
        xml = xml.replace('</CitadelBuildDetails>', progress('DltBuild') + '</CitadelBuildDetails>')
    elif shop:
        anchor = '<Panel id="SelectedBuildOuter" />'
        if xml.count(anchor) != 1:
            raise ValueError('Shop build header changed')
        xml = xml.replace(anchor, anchor + bridge.replace('DltBuildBridge', 'DltShopBridge', 1))
        controls = '<Panel id="ControlButtons">'
        if xml.count(controls) != 1:
            raise ValueError('Shop build controls changed')
        xml = xml.replace(controls, controls + actions('DltShop', 'CitadelButton',
                          'BuildHeaderButton Medium Dark Fill DltBuildTranslateButton', 'onmouseactivate'))
        xml = xml.replace('</CitadelShopModsBuild>', progress('DltShop') + '</CitadelShopModsBuild>')
    return preserve_native_label_tree(xml)


def stage_build_text_layouts(viewer, stage):
    for name in BUILD_TEXT_LAYOUTS:
        source = root / f'artifacts/decompiled/panorama/layout/{name}.xml'
        source.parent.mkdir(parents=True, exist_ok=True)
        source.unlink(missing_ok=True)
        subprocess.run([str(viewer), '-i', str(game/'pak01_dir.vpk'), '-o', str(source),
                        '--vpk_filepath', f'panorama/layout/{name}.vxml_c', '-d'], check=True)
        target = stage / f'panorama/layout/{name}.xml'
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(patch_build_text_layout(source.read_text(encoding='utf-8-sig'), name), encoding='utf-8')


def prepare():
    source = root/'artifacts/decompiled/panorama/layout/chat.xml'
    source.parent.mkdir(parents=True, exist_ok=True)
    viewer = pathlib.Path(os.environ.get('DLT_RESOURCE_VIEWER', str(root/'tools/vrf/Source2Viewer-CLI.exe')))
    if not viewer.is_file():
        raise FileNotFoundError('Install Source 2 Viewer CLI or set DLT_RESOURCE_VIEWER.')
    # Refresh from this game build; never silently use stale decompiled resources.
    subprocess.run([str(viewer), '-i', str(game/'pak01_dir.vpk'), '-o', str(source),
                    '--vpk_filepath', 'panorama/layout/chat.vxml_c', '-d'], check=True)
    xml = source.read_text(encoding='utf-8-sig')
    for anchor in ('</styles>', '<snippets>', '<Panel id="ChatControls">'):
        if xml.count(anchor) != 1:
            raise ValueError('Game chat layout changed: expected exactly one ' + anchor)
    xml = xml.replace('</styles>', '\t<include src="s2r://panorama/styles/dlt_chat.vcss_c" />\n'
                      f'\t\t<include src="{NATIVE_BUBBLE_STYLE}" />\n\t</styles>')
    xml = xml.replace('<snippets>', '<scripts><include src="s2r://panorama/scripts/dlt_chat.vjs_c" /></scripts>\n\t<snippets>')
    xml = xml.replace('<Panel id="ChatControls">','<HTML id="DltBridge" class="DltBridge" hittest="false" hittestchildren="false" acceptsinput="false" acceptsfocus="false" />\n\t\t<Panel id="ChatControls">')
    def native_focus(match):
        tag=match.group(0)
        if tag.count('onblur="CitadelChatInputBlur();"') != 1:
            raise ValueError('Native ChatInput blur contract changed; native focus observation was not applied')
        focus=re.search(r'\bonfocus="([^"]*)"',tag)
        return tag.replace(focus.group(0),'onfocus="'+focus.group(1)+';DLTChatInputFocused();"') if focus else tag.replace('<TextEntry','<TextEntry onfocus="DLTChatInputFocused();"',1)
    xml,focus_count=re.subn(r'<TextEntry\b[^>]*\bid="ChatInput"[^>]*>',native_focus,xml)
    if focus_count != 1: raise ValueError('Expected exactly one native ChatInput focus target')
    out = root/'mod/panorama/layout/chat.xml'; out.parent.mkdir(parents=True,exist_ok=True); out.write_text(xml,encoding='utf-8')
    bubble_source = root/'artifacts/decompiled/panorama/layout/citadel_hud_top_bar_chat.xml'
    subprocess.run([str(viewer), '-i', str(game/'pak01_dir.vpk'), '-o', str(bubble_source),
                    '--vpk_filepath', 'panorama/layout/citadel_hud_top_bar_chat.vxml_c', '-d'], check=True)
    original_bubble = bubble_source.read_text(encoding='utf-8-sig')
    patched_bubble = patch_native_bubble(original_bubble)
    (root/'mod/panorama/layout/citadel_hud_top_bar_chat.xml').write_bytes(patched_bubble.encode('utf-8'))
    base_bubble = Vpk(game/'pak01_dir.vpk').read('panorama/layout/citadel_hud_top_bar_chat.vxml_c')
    (root/'artifacts/native-bubble-provenance.json').write_text(json.dumps(dict(
        resource='panorama/layout/citadel_hud_top_bar_chat.vxml_c',
        baseResourceSha256=hashlib.sha256(base_bubble).hexdigest(),
        decompiledSourceSha256=hashlib.sha256(original_bubble.encode()).hexdigest(),
        patchedSourceSha256=hashlib.sha256(patched_bubble.encode()).hexdigest(),
        nativeControlsReparented=False,translationLabelsAdded=1),indent=2),encoding='utf-8')
    key_path = root/'artifacts/pairing.key'
    if not key_path.exists(): key_path.write_text(secrets.token_hex(24),encoding='ascii')
    hero_labels=json.loads((root/'artifacts/hero-labels.json').read_text(encoding='utf-8'))
    stage = root/'artifacts/addon/content/citadel'
    for f in (root/'mod').rglob('*'):
        if not f.is_file(): continue
        dest = stage/f.relative_to(root/'mod'); dest.parent.mkdir(parents=True,exist_ok=True)
        value=f.read_text(encoding='utf-8').replace('__PAIRING__',key_path.read_text().strip())
        value=value.replace('var heroLabels = {}; // __DLT_HERO_LABELS__','var heroLabels = '+json.dumps(hero_labels,ensure_ascii=False,separators=(',',':'))+';')
        dest.write_text(value,encoding='utf-8')
    stage_build_text_layouts(viewer, stage)
    game_out=root/'artifacts/addon/game/citadel'; game_out.mkdir(parents=True,exist_ok=True)
    (game_out/'gameinfo.gi').write_text('"GameInfo" { "game" "citadel" FileSystem { SearchPaths { Game "citadel" Game "core" } } }',encoding='utf-8')
    return stage,game_out

def conflicts():
    targets={'panorama/scripts/dlt_native_bubble.vjs_c','panorama/layout/citadel_hud_top_bar_chat.vxml_c','panorama/styles/dlt_native_bubble.vcss_c','panorama/layout/chat.vxml_c','panorama/layout/dlt_menu.vxml_c','panorama/scripts/dlt_chat.vjs_c','panorama/styles/dlt_chat.vcss_c','panorama/scripts/dlt_menu.vjs_c','panorama/styles/dlt_menu.vcss_c','panorama/layout/dlt_settings_window.vxml_c','panorama/scripts/dlt_settings_window.vjs_c','panorama/styles/dlt_settings_window.vcss_c'}
    targets.update(BUILD_TEXT_RESOURCES)
    results=[]
    for file in (game/'addons').glob('*.vpk'):
        vpk=Vpk(file)
        exact=sorted(targets & vpk.entries.keys())
        related=[p for p in vpk.entries if ('chattranslat' in p.lower() or 'chat_translat' in p.lower() or 'top_bar_chat.vcss' in p.lower())]
        if exact or related: results.append({'file':str(file),'sha256':hashlib.sha256(file.read_bytes()).hexdigest(),'exact':exact,'related':related})
    (root/'artifacts/conflicts.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(results,ensure_ascii=False,indent=2))

if __name__=='__main__':
    prepare(); conflicts()
