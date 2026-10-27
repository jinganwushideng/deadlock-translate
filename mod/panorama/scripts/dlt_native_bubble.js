// SPDX-License-Identifier: GPL-3.0-only
// Loaded by the native top-bar chat layout, not evaluated from a remote message.
//
// Layout protocol (why this file looks the way it does):
// - The translation label is created by the native snippet, so it is part of the
//   first layout of a chat row. dlt_native_bubble.css keeps it visible and
//   zero-sized until a translation arrives; the game's own row arrangement then
//   only has to accept a size change of an already laid out panel.
// - The game does not always relayout a bubble after its content changed. If the
//   label still reads 0x0 after STALL_MS, the chat root is resized by one pixel
//   for about three frames (no visibility change, nothing moves) and restored.
//   The whole root is collapsed only if that nudge did not help, at most once per
//   message, so a visible flash is a last resort instead of the normal path.
// - No animation is attached to native rows: adding/removing one changes the
//   row's animation-name and makes the game's chat_appear pop-in replay.
(function () {
    'use strict';
    var root = $.GetContextPanel(), cursor = 0, stopped = false, scheduled = null, heartbeatAt = 0;
    var generation = String(Date.now()) + ':' + String(Math.random());
    var gateClass = 'DltNativeRelayoutGate';
    var gate = null, nudge = null, nextGateAt = 0, nextNudgeAt = 0;
    var STALL_MS = 250;
    function valid(panel) { try { return !!panel && panel.IsValid(); } catch (_) { return false; } }
    function get(panel, key) { try { return panel.GetAttributeString(key, ''); } catch (_) { return ''; } }
    function set(panel, key, value) { panel.SetAttributeString(key, value); }
    function clear(panel, key) { try { set(panel, key, ''); } catch (_) {} }
    function has(panel, name) { try { return valid(panel) && panel.BHasClass(name); } catch (_) { return false; } }
    function norm(value) { return String(value || '').replace(/\s+/g, ' ').trim(); }
    function box(panel) { try { return { width: Number(panel.actuallayoutwidth), height: Number(panel.actuallayoutheight) }; } catch (_) { return { width: 0, height: 0 }; } }
    function hidden(panel) {
        for (var i = 0; i < 32 && valid(panel); i++) {
            var style = panel.style || {}, raw = style.opacity;
            if (panel.visible === false || style.visibility === 'collapse' || style.visibility === 'hidden' ||
                (raw !== undefined && raw !== null && raw !== '' && isFinite(Number(raw)) && Number(raw) <= 0.001)) return true;
            panel = panel.GetParent();
        }
        return false;
    }
    // Do not turn an unexpected loader context into a game-root traversal.
    // These are the two native IDs used by the current top-bar layout.
    if (!valid(root) || (root.id !== 'Team1Chat' && root.id !== 'Team2Chat') ||
        typeof root.SetAttributeString !== 'function' || typeof root.GetAttributeString !== 'function') return;
    try {
        set(root, 'dlt_local_owner', generation);
        // Retire a stale gate left by an earlier loader before taking ownership.
        root.RemoveClass(gateClass); set(root, 'dlt_local_gate_owner', '');
    } catch (_) { return; }
    function reflowFlags(label, bits) {
        try { set(label, 'dlt_native_reflow_flags', String((Number(get(label, 'dlt_native_reflow_flags')) || 0) | bits)); } catch (_) {}
    }
    function applyRootStyles(labels) {
        try {
            if (typeof root.ApplyStyles === 'function') { root.ApplyStyles(true); for (var i = 0; i < labels.length; i++) reflowFlags(labels[i], 8); }
        } catch (_) { for (var j = 0; j < labels.length; j++) reflowFlags(labels[j], 16); }
    }
    function restoreNudge() {
        var current = nudge; nudge = null;
        if (!current || !valid(root) || get(root, 'dlt_local_owner') !== generation) return;
        try {
            if (typeof root.ClearPropertyFromCode === 'function') {
                if (current.width) root.style.width = current.width; else root.ClearPropertyFromCode('width');
                if (current.height) root.style.height = current.height; else root.ClearPropertyFromCode('height');
            }
            else { root.style.width = current.width; root.style.height = current.height; }
            for (var i = 0; i < current.labels.length; i++) if (valid(current.labels[i])) set(current.labels[i], 'dlt_native_nudge_state', '2');
        } catch (_) {}
    }
    // Resize the chat root by one pixel for about three frames. The bubbles are
    // anchored to the root's top-left corner, so nothing moves on screen; the
    // engine only has to run a layout pass over the chat again. No visibility or
    // opacity is touched, so no pop-in animation restarts.
    function beginNudge(labels) {
        if (!labels.length || nudge || Date.now() < nextNudgeAt || !valid(root) || hidden(root) ||
            has(root, 'gScoreboardOpen') || has(root, 'gDetailView') || typeof root.ClearPropertyFromCode !== 'function') return false;
        var width = Number(root.actuallayoutwidth), height = Number(root.actuallayoutheight);
        if (!isFinite(width) || !isFinite(height) || width < 8 || height < 8) return false;
        nudge = { at: Date.now(), labels: labels, width: String(root.style.width || ''), height: String(root.style.height || ''),
            targetWidth: Math.max(1, Math.round(width) - 1), targetHeight: Math.max(1, Math.round(height) - 1) };
        nextNudgeAt = Date.now() + 800;
        try {
            for (var i = 0; i < labels.length; i++) {
                set(labels[i], 'dlt_native_nudge_done', '1'); set(labels[i], 'dlt_native_nudge_state', '1');
                set(labels[i], 'dlt_native_nudge_at', String(Date.now()));
                var before = box(labels[i]); set(labels[i], 'dlt_native_nudge_w', String(before.width)); set(labels[i], 'dlt_native_nudge_h', String(before.height));
            }
            root.style.width = nudge.targetWidth + 'px';
            root.style.height = nudge.targetHeight + 'px';
            var current = nudge;
            $.Schedule(0.05, function () { if (nudge === current) restoreNudge(); });
            return true;
        } catch (_) {
            restoreNudge();
            return false;
        }
    }
    function restoreGate() {
        var current = gate; gate = null;
        if (!current || !valid(root) || get(root, 'dlt_local_gate_owner') !== generation) return;
        try {
            root.RemoveClass(gateClass);
            set(root, 'dlt_local_gate_owner', '');
            applyRootStyles(current.labels);
            for (var i = 0; i < current.labels.length; i++) reflowFlags(current.labels[i], has(root, gateClass) ? 16 : 4);
        } catch (_) {
            // This class is ours alone; never restore native visibility properties
            // or remove the user's scoreboard/detail classes.
            try { root.RemoveClass(gateClass); } catch (_) {}
            for (var j = 0; j < current.labels.length; j++) reflowFlags(current.labels[j], 16);
        }
    }
    function beginGate(labels) {
        if (!labels.length || gate || Date.now() < nextGateAt || !valid(root) ||
            get(root, 'dlt_local_owner') !== generation || hidden(root) || has(root, 'gScoreboardOpen') || has(root, 'gDetailView')) return;
        // One batched root cycle at most per 800ms, only after the local row
        // cycle already failed. The normal path never collapses anything.
        gate = { at: Date.now(), labels: labels }; nextGateAt = Date.now() + 800;
        try {
            set(root, 'dlt_local_gate_owner', generation);
            root.AddClass(gateClass);
            for (var i = 0; i < labels.length; i++) {
                set(labels[i], 'dlt_native_root_gate', '1');
                set(labels[i], 'dlt_native_reflow_done', get(labels[i], 'dlt_native_value') || '1');
                reflowFlags(labels[i], has(root, gateClass) ? 3 : 17);
            }
            applyRootStyles(labels);
            // Restore in a different rendered frame. An existing tick provides
            // an additional cleanup path if this short callback is lost.
            var current = gate;
            $.Schedule(0.03, function () { if (gate === current) restoreGate(); });
        } catch (_) {
            for (var j = 0; j < labels.length; j++) reflowFlags(labels[j], 16);
            restoreGate();
        }
    }
    function owned(host) {
        for (var i = 0, n = Math.min(host.GetChildCount(), 16); i < n; i++) {
            var item = host.GetChild(i);
            if (valid(item) && item.id === 'DltNativeText' && has(item, 'DltNativeBubbleTemplate') && get(item, 'dlt_native_owned') === '1') return item;
        }
        return null;
    }
    function remember(panel, packet) {
        try {
            set(panel, 'dlt_native_value', packet.value);
            set(panel, 'dlt_native_font', String(packet.fontSize));
            set(panel, 'dlt_native_alpha', String(packet.opacity / 100));
            set(panel, 'dlt_native_original', packet.original);
            set(panel, 'dlt_native_message', packet.message);
            if (!get(panel, 'dlt_native_layout_at')) set(panel, 'dlt_native_layout_at', String(Date.now()));
        } catch (_) {}
    }
    function apply(row, label, command) {
        var code = 6, packet = null, started = Date.now();
        try {
            packet = JSON.parse(command);
            if (!packet || packet.schema !== 1 || typeof packet.id !== 'string' || !packet.id || packet.id.length > 120) return;
            if (typeof packet.at !== 'number' || !isFinite(packet.at) || Date.now() - packet.at > 3000 || packet.at - Date.now() > 1000) { code = 7; return; }
            if (has(row, 'Expired') || hidden(row)) { code = 3; return; }
            var host = label.GetParent(), source = host.FindChildTraverse('MessageText');
            if (!valid(host) || host.id !== 'MessageContents' || host.GetParent() !== row || !valid(source) || hidden(source) || norm(source.text) !== packet.original) { code = 4; return; }
            if (packet.nativeId) {
                var id = typeof row.GetAttributeUInt32 === 'function' ? row.GetAttributeUInt32('chat_message_id', 4294967295) : -1;
                if ((id === -1 || id === 4294967295) && typeof row.GetAttributeInt === 'function') id = row.GetAttributeInt('chat_message_id', -1);
                if (String(id) !== packet.nativeId) { code = 4; return; }
            }
            // The producer's clear path always hides the label again. A delayed
            // command must never undo disable/mode/session cleanup.
            if (label.visible === false || label.style.visibility === 'collapse' || !(Number(label.style.opacity) > 0)) { code = 2; return; }
            if (typeof packet.value !== 'string' || !packet.value || packet.value.length > 2500 ||
                typeof packet.fontSize !== 'number' || packet.fontSize < 12 || packet.fontSize > 24 ||
                typeof packet.opacity !== 'number' || packet.opacity < 20 || packet.opacity > 100 ||
                typeof packet.message !== 'string' || !packet.message || packet.message.length > 120) { code = 5; return; }
            // Only a mod-created label is ever retired. The native source, host and
            // prebuilt label stay in place.
            if (label.id === 'DltNativeText' && get(label, 'dlt_native_message') && get(label, 'dlt_native_message') !== packet.message) { code = 2; return; }
            label.html = false; label.hittest = false;
            if (String(label.text || '') !== packet.value) label.text = packet.value;
            if (String(label.text || '') !== packet.value) { code = 5; return; }
            label.style.fontSize = packet.fontSize + 'px';
            label.AddClass('DltNativeReady'); label.style.visibility = 'visible'; label.style.opacity = String(packet.opacity / 100);
            if (!get(label, 'dlt_native_layout_at')) set(label, 'dlt_native_layout_at', String(Date.now()));
            remember(label, packet);
            code = 1;
        } catch (_) { code = 6; }
        finally {
            try {
                if (valid(label)) {
                    set(label, 'dlt_local_command', '');
                    if (packet && typeof packet.id === 'string') set(label, 'dlt_local_receipt', JSON.stringify({ id: packet.id, code: code, ms: Math.max(0, Date.now() - started) }));
                }
            } catch (_) {}
        }
    }
    function tick() {
        if (stopped || !valid(root) || get(root, 'dlt_local_owner') !== generation) return;
        if (nudge && Date.now() - nudge.at >= 200) restoreNudge();
        if (gate && Date.now() - gate.at >= 150) restoreGate();
        try {
            if (Date.now() - heartbeatAt >= 1000) { set(root, 'dlt_local_tick', String(Date.now())); heartbeatAt = Date.now(); }
            var messages = root.FindChildTraverse('Messages'), n = valid(messages) ? messages.GetChildCount() : 0;
            var start = Math.max(0, n - 8), older = start, indices = [], applied = 0, stalled = [], toNudge = [];
            for (var i = start; i < n; i++) indices.push(i);
            if (older > 0) {
                if (cursor >= older) cursor = 0;
                for (var j = cursor, end = Math.min(older, cursor + 8); j < end; j++) indices.push(j);
                cursor = end >= older ? 0 : end;
            }
            // At most sixteen native message rows inspected and two commands
            // applied per tick; no game-root search or retained message wrappers.
            for (var k = 0; k < indices.length; k++) {
                var row = messages.GetChild(indices[k]); if (!valid(row) || !has(row, 'ChatMessage')) continue;
                var host = row.FindChildTraverse('MessageContents'); if (!valid(host)) continue;
                var carrier = host.FindChildTraverse('DltBubbleTranslation'), local = owned(host);
                var active = valid(local) ? local : carrier;
                if (valid(active) && !has(row, 'Expired') && !hidden(row) && !hidden(active)) {
                    // Give the engine STALL_MS of ordinary layout first; only then
                    // escalate: a one-pixel root resize, then (only if that failed)
                    // the whole-root visibility cycle.
                    var layoutAt = Number(get(active, 'dlt_native_layout_at'));
                    var rect = box(active), age = layoutAt > 0 ? Date.now() - layoutAt : -1;
                    if (get(active, 'dlt_native_nudge_state') === '2') {
                        set(active, 'dlt_native_nudge_state', rect.width > 0 && rect.height > 0 ? '3' : '4');
                        set(active, 'dlt_native_nudge_after_w', String(rect.width)); set(active, 'dlt_native_nudge_after_h', String(rect.height));
                        set(active, 'dlt_native_nudge_elapsed', String(Math.max(0, Date.now() - Number(get(active, 'dlt_native_nudge_at')))));
                    }
                    if (age >= STALL_MS && (rect.width === 0 || rect.height === 0)) {
                        if (get(active, 'dlt_native_nudge_done') !== '1') {
                            // Busy/cooldown is deferred, never an excuse to collapse the root
                            // before this message has had its non-visibility attempt.
                            if (typeof root.ClearPropertyFromCode !== 'function' || Number(root.actuallayoutwidth) < 8 || Number(root.actuallayoutheight) < 8) {
                                set(active, 'dlt_native_nudge_done', '1'); set(active, 'dlt_native_nudge_state', '5');
                                set(active, 'dlt_native_nudge_after_w', String(rect.width)); set(active, 'dlt_native_nudge_after_h', String(rect.height));
                                stalled.push(active);
                            } else toNudge.push(active);
                        } else if (!nudge && get(active, 'dlt_native_root_gate') !== '1') stalled.push(active);
                    }
                }
                if (applied >= 2 || gate) continue;
                var label = valid(local) ? local : carrier;
                if (!valid(label) || label.GetParent() !== host || !has(label, 'DltNativeBubbleTemplate')) continue;
                var command = get(label, 'dlt_local_command'); if (!command) continue;
                applied++; apply(row, label, command);
            }
            // One size change covers all eligible rows in this bounded batch.
            if (!gate) beginNudge(toNudge);
            if (!nudge) beginGate(stalled);
        } catch (_) {}
        if (!stopped && valid(root) && get(root, 'dlt_local_owner') === generation) scheduled = $.Schedule(0.2, tick);
    }
    function dispose() { stopped = true; restoreNudge(); restoreGate(); if (scheduled !== null && $.CancelScheduled) { try { $.CancelScheduled(scheduled); } catch (_) {} } }
    try { $.RegisterEventHandler('PanelDeleted', root, function (panel) { if (panel === root) dispose(); }); } catch (_) {}
    tick();
})();
