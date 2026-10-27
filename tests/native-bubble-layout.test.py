from pathlib import Path
import sys, xml.etree.ElementTree as ET
sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'scripts'))
from native_bubble_layout import patch_native_bubble, STYLE, SCRIPT

fixture = '''<root><styles><include src="native.vcss" /></styles><snippets>
<snippet name="ChatMessage"><Panel class="ChatMessage"><Panel id="ChatArrow"/><Panel id="MessageContents"><Panel class="ChatBubble"><Label id="MessageText" html="true"/></Panel><Image id="KeepIcon"/></Panel></Panel></snippet>
<snippet name="TypingIndicatorChatMessage"><Panel id="MessageContents"><Panel id="TypingAnim"/></Panel></snippet></snippets><CitadelHudTopBarChat><Panel id="Messages"/></CitadelHudTopBarChat></root>'''
passed=0
def check(value):
    global passed
    assert value
    passed+=1
out=patch_native_bubble(fixture)
root=ET.fromstring(out)
label=root.find(".//snippet[@name='ChatMessage']//Label[@id='DltBubbleTranslation']")
check(label is not None and label.attrib['text']=='' and label.attrib['html']=='false')
check('require-composition-layer' not in label.attrib and 'never-cache-composition-layer' not in label.attrib)
check([node.attrib.get('id') for node in root.iter() if 'require-composition-layer' in node.attrib or 'never-cache-composition-layer' in node.attrib]==[])
check(root.find(".//snippet[@name='TypingIndicatorChatMessage']//Label") is None)
check(root.find(".//Label[@id='MessageText']").attrib['html']=='true')
host=root.find(".//snippet[@name='ChatMessage']//Panel[@id='MessageContents']")
check([n.attrib.get('class') or n.attrib.get('id') for n in host]==['ChatBubble','KeepIcon','DltBubbleTranslation DltNativeBubbleTemplate'])
host.remove(label)
check(root.find('scripts/include').get('src')==SCRIPT)
root.remove(root.find('scripts'))
styles=root.find('styles');styles.remove(next(n for n in styles if n.attrib.get('src')==STYLE))
check(ET.tostring(root)==ET.tostring(ET.fromstring(fixture)))
for change in [lambda s:s.replace('name="ChatMessage"','name="Changed"'),lambda s:s.replace('id="MessageText"','id="Changed"'),lambda s:s.replace('id="MessageContents"','id="Changed"'),lambda s:s.replace('</snippets>',s[s.index('<snippet name="ChatMessage"'):s.index('<snippet name="TypingIndicator')]+'</snippets>'),lambda s:out]:
    try:patch_native_bubble(change(fixture));raise AssertionError('invalid game layout accepted')
    except ValueError:passed+=1
unicode_xml=fixture.replace('<root>','<!-- 中文 🐈 -->\r\n<root>')
patched=patch_native_bubble(unicode_xml)
check(patched.startswith('<!-- 中文 🐈 -->\r\n'))
check(patched.replace(f'<scripts><include src="{SCRIPT}" /></scripts>\n\t','').replace(f'<include src="{STYLE}" />\n\t','').replace('<Label id="DltBubbleTranslation" class="DltBubbleTranslation DltNativeBubbleTemplate" text="" html="false" hittest="false" />\n\t\t\t\t','')==unicode_xml)
style=(Path(__file__).resolve().parents[1]/'mod/panorama/styles/dlt_native_bubble.css').read_text(encoding='utf-8')
check('width: fit-children' in style and 'min-height:' not in style and 'min-width:' not in style and 'opacity: 0' in style and 'DltNativeReady' in style)
# The idle label must already be part of layout (visible, zero height) before any
# translation exists, so the late write only changes an existing panel's size.
check(style.index('visibility: visible') < style.index('DltNativeReady') and style.index('height: 0px') < style.index('DltNativeReady'))
check('animation-' not in style and '@keyframes' not in style)  # no animation may touch native rows or labels
check('MessageType_MapPing' in style and 'MessageType_PlayerTyping' in style and 'visibility: collapse' in style)
existing=ET.fromstring(patch_native_bubble(fixture.replace('<snippets>', '<scripts><include src="original.vjs"/></scripts><snippets>')))
check([n.get('src') for n in existing.find('scripts')]==['original.vjs',SCRIPT])
try:patch_native_bubble(fixture.replace('<snippets>', '<scripts/><scripts/><snippets>'));raise AssertionError('duplicate native script sections accepted')
except ValueError:passed+=1
print({'passed':passed,'realGame':False,'nativeChildrenPreserved':True})
