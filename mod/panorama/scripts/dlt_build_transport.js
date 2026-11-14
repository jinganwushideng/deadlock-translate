function DLTSetButtonCaption(button, text) {
    button.text = text;
    var queue = [button];
    for (var visited = 0; queue.length && visited < 16; visited++) {
        var panel = queue.shift();
        if (panel !== button && panel.paneltype === 'Label') {
            panel.text = text;
            return;
        }
        if (panel.GetChildCount)
            for (var i = 0; i < panel.GetChildCount(); i++) queue.push(panel.GetChild(i));
    }
}
function DLTBuildTransport(bridge, changed) {
    var eventTitle = '', current = null;
    var stats = { sent: 0, received: 0, errors: 0, maximum: 0 };
    ['HTMLChangedTitle', 'HTMLTitle'].forEach(function (name) {
        try { $.RegisterEventHandler(name, bridge, function () {
            for (var i = 0; i < arguments.length; i++)
                if (typeof arguments[i] === 'string' && arguments[i].indexOf('DLTBUILD:') === 0) eventTitle = arguments[i];
        }); } catch (_) {}
    });
    function navigate(encoded) {
        eventTitle = '';
        bridge.SetURL('https://localhost:17866/build-text/__PAIRING__#' + encoded);
        stats.sent++;
        if (changed) changed(stats);
    }
    function upload() {
        navigate(encodeURIComponent(JSON.stringify({ wire: 1, id: current.id, part: current.index,
            count: current.upload.length, data: current.upload[current.index] })));
    }
    return {
        send: function (id, encoded) {
            var parts = [];
            if (encoded.length > 1800)
                for (var i = 0; i < encoded.length; i += 1200) parts.push(encoded.slice(i, i + 1200));
            if (parts.length > 1024) throw Error('Request too large');
            current = { id: id, upload: parts, index: 0, download: [], count: null, lastBad: '' };
            if (parts.length) upload(); else navigate(encoded);
        },
        read: function (id) {
            if (!current || current.id !== id) return null;
            var candidates = [eventTitle];
            try { candidates.push(String(bridge.title || '')); } catch (_) {}
            try { if (bridge.GetTitle) candidates.push(String(bridge.GetTitle() || '')); } catch (_) {}
            for (var c = 0; c < candidates.length; c++) {
                var value = candidates[c];
                if (!value || value.indexOf('DLTBUILD:') !== 0) continue;
                stats.maximum = Math.max(stats.maximum, value.length);
                var response;
                try { response = JSON.parse(value.slice(9)); }
                catch (_) {
                    if (current.lastBad !== value) { current.lastBad = value; stats.errors++; }
                    continue;
                }
                if (response.id !== id) continue;
                if (typeof response.ack === 'number') {
                    if (response.ack === current.index && current.index + 1 < current.upload.length) {
                        current.index++; stats.received++; upload();
                    }
                    return null;
                }
                if (response.wire !== 1) return response;
                if (!Number.isInteger(response.count) || response.count < 1 || response.count > 1024 ||
                    response.part !== current.download.length || typeof response.data !== 'string' || response.data.length > 1200) continue;
                if (current.count !== null && current.count !== response.count) continue;
                current.count = response.count;
                current.download.push(response.data); stats.received++;
                if (changed) changed(stats);
                if (current.download.length === current.count) {
                    var result = JSON.parse(decodeURIComponent(current.download.join('')));
                    return result.id === id ? result : null;
                }
                navigate(encodeURIComponent(JSON.stringify({ wire: 1, id: id, read: current.download.length })));
                return null;
            }
            if (changed) changed(stats);
            return null;
        },
        cancel: function () { current = null; eventTitle = ''; bridge.SetURL('about:blank'); }
    };
}
