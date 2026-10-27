"""Add one native-created translation label without rewriting Valve's other bytes."""
from xml.parsers import expat

STYLE = 's2r://panorama/styles/dlt_native_bubble.vcss_c'
SCRIPT = 's2r://panorama/scripts/dlt_native_bubble.vjs_c'
LABEL = 'DltBubbleTranslation'


def patch_native_bubble(xml: str) -> str:
    if LABEL in xml or STYLE in xml or SCRIPT in xml:
        raise ValueError('Native bubble layout already contains a translation template')
    raw = xml.encode('utf-8')
    parser = expat.ParserCreate()
    stack = []
    styles = []
    hosts = []
    scripts = []
    snippets = []
    messages = 0
    sources = 0

    def start(name, attrs):
        nonlocal messages, sources
        if name == 'snippets' and len(stack) == 1:
            snippets.append(parser.CurrentByteIndex)
        snippet = attrs.get('name') if name == 'snippet' else None
        if snippet == 'ChatMessage':
            messages += 1
        in_message = any(item[1] == 'ChatMessage' for item in stack)
        if in_message and any(item[2] for item in stack) and attrs.get('id') == 'MessageText':
            sources += 1
        is_host = in_message and name == 'Panel' and attrs.get('id') == 'MessageContents'
        stack.append((name, snippet, is_host))

    def end(name):
        node = stack.pop()
        if node[2]:
            hosts.append(parser.CurrentByteIndex)
        if name == 'styles' and len(stack) == 1:
            styles.append(parser.CurrentByteIndex)
        if name == 'scripts' and len(stack) == 1:
            scripts.append(parser.CurrentByteIndex)

    parser.StartElementHandler = start
    parser.EndElementHandler = end
    parser.Parse(raw, True)
    if messages != 1 or sources != 1 or len(hosts) != 1 or len(styles) != 1 or len(snippets) != 1 or len(scripts) > 1:
        raise ValueError('Native bubble layout changed: expected one ChatMessage, MessageText, MessageContents and root styles')
    # The native layout loader owns this controller; no dynamic context-execution
    # API is required. The existing chat renderer remains a functional fallback.
    additions = [
        (styles[0], f'<include src="{STYLE}" />\n\t'.encode()),
        (hosts[0], (f'<Label id="{LABEL}" class="DltBubbleTranslation DltNativeBubbleTemplate" '
                    'text="" html="false" hittest="false" />\n\t\t\t\t').encode()),
    ]
    if scripts:
        additions.append((scripts[0], f'<include src="{SCRIPT}" />\n\t'.encode()))
    else:
        additions.append((snippets[0], f'<scripts><include src="{SCRIPT}" /></scripts>\n\t'.encode()))
    for offset, addition in sorted(additions, reverse=True):
        raw = raw[:offset] + addition + raw[offset:]
    return raw.decode('utf-8')
