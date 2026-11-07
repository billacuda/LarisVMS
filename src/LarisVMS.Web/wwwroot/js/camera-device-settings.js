// Camera device settings — the camera's own stream encoder, image, and audio configuration, read and
// written live over ONVIF through /api/cameras/{id}/device-settings. Framework-free, same convention
// as zones-editor.js/schedule-editor.js.
//
// Everything is driven by what the camera reports: a setting with no options (null range, empty or
// single-entry list) is simply not rendered, a group with no settings left is dropped, and a section
// with no groups is dropped. Each section (one stream, image, one audio encoder) keeps its own working
// copy of the values and its own Apply button; only fields that differ from what the camera reported
// are sent.
//
// Bulk: when other cameras of the same make and model are ticked in "Apply to", each Apply goes to
// /api/device-settings/bulk with just the changed fields, and a per-camera result list appears under
// that section.
window.larisvmsDeviceSettings = (function () {
    'use strict';

    var CODEC_NAMES = { H264: 'H.264', H265: 'H.265', MPEG4: 'MPEG-4', JPEG: 'MJPEG', G711: 'G.711', G726: 'G.726', AAC: 'AAC' };
    var MODE_NAMES = { ON: larisvmsI18n.t('js.cameraDeviceSettings.modeOn'), OFF: larisvmsI18n.t('js.cameraDeviceSettings.modeOff'), AUTO: larisvmsI18n.t('js.cameraDeviceSettings.irAuto'), MANUAL: larisvmsI18n.t('js.cameraDeviceSettings.modeManual') };
    var IR_CUT_NAMES = { AUTO: larisvmsI18n.t('js.cameraDeviceSettings.irAuto'), ON: larisvmsI18n.t('js.cameraDeviceSettings.irDay'), OFF: larisvmsI18n.t('js.cameraDeviceSettings.irNight') };

    function codecName(c) { return CODEC_NAMES[(c || '').toUpperCase()] || c || '—'; }
    function modeName(m) { return MODE_NAMES[(m || '').toUpperCase()] || m; }
    function num(v) { return v === null || v === undefined ? '' : String(Math.round(v * 100) / 100); }
    function same(a, b) { return a === b || (a !== null && b !== null && a !== undefined && b !== undefined && String(a).toUpperCase() === String(b).toUpperCase()); }
    function ranged(r) { return !!r && r.max > r.min; }

    // Small DOM builder: el('div', { class: 'x', onclick: fn }, [children or text]).
    function el(tag, attrs, children) {
        var node = document.createElement(tag);
        Object.keys(attrs || {}).forEach(function (k) {
            var v = attrs[k];
            if (v === null || v === undefined || v === false) return;
            if (k.indexOf('on') === 0 && typeof v === 'function') node.addEventListener(k.slice(2), v);
            else if (k === 'class') node.className = v;
            else if (k === 'text') node.textContent = v;
            else node.setAttribute(k, v === true ? '' : v);
        });
        (children || []).forEach(function (c) {
            if (c === null || c === undefined || c === false) return;
            node.appendChild(typeof c === 'string' ? document.createTextNode(c) : c);
        });
        return node;
    }

    function problemText(response, fallback) {
        return response.json().then(function (p) { return (p && (p.detail || p.title)) || fallback; }, function () { return fallback; });
    }

    function init(opts) {
        var root = document.getElementById(opts.rootId);
        var data = null;
        var peers = [];
        var selectedPeers = {};
        var activeRole = null;
        var work = { video: {}, image: null, audio: {} };
        var lastResults = {}; // section key -> { payload, results }

        (opts.withIds || []).forEach(function (id) { selectedPeers[id] = true; });

        // ── Loading ────────────────────────────────────────────────────────────────────────────

        function load(keepWork) {
            root.innerHTML = '';
            root.appendChild(el('p', { class: 'text-muted small' }, [
                el('span', { class: 'spinner-border spinner-border-sm me-1', 'aria-hidden': 'true' }), larisvmsI18n.t('js.cameraDeviceSettings.readingSettingsFromTheCamera')]));
            return fetch('/api/cameras/' + opts.cameraId + '/device-settings', { credentials: 'same-origin' })
                .then(function (r) {
                    if (!r.ok) return problemText(r, larisvmsI18n.t('js.cameraDeviceSettings.couldntReadTheCameraSettings')).then(function (t) { throw new Error(t); });
                    return r.json();
                })
                .then(function (dto) {
                    data = dto;
                    if (!keepWork) work = { video: {}, image: null, audio: {} };
                    seedWork();
                    render();
                })
                .catch(function (err) {
                    root.innerHTML = '';
                    root.appendChild(el('div', { class: 'alert alert-warning d-flex flex-wrap align-items-center gap-2' }, [
                        el('span', { class: 'flex-grow-1', text: err.message }),
                        el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', onclick: function () { load(false); } }, [larisvmsI18n.t('js.cameraDeviceSettings.retry')])
                    ]));
                });
        }

        function seedWork() {
            data.streams.forEach(function (s) {
                if (!work.video[s.role]) work.video[s.role] = Object.assign({}, s.current);
            });
            if (data.image && !work.image) work.image = Object.assign({}, data.image.current);
            data.audio.forEach(function (a) {
                if (!work.audio[a.configToken]) work.audio[a.configToken] = Object.assign({}, a.current);
            });
            if (!activeRole || !data.streams.some(function (s) { return s.role === activeRole; }))
                activeRole = data.streams.length ? data.streams[0].role : null;
        }

        function loadPeers() {
            fetch('/api/cameras/' + opts.cameraId + '/device-settings/peers', { credentials: 'same-origin' })
                .then(function (r) { return r.ok ? r.json() : []; })
                .then(function (list) {
                    peers = list || [];
                    var known = {};
                    peers.forEach(function (p) { known[p.cameraId] = true; });
                    Object.keys(selectedPeers).forEach(function (id) { if (!known[id]) delete selectedPeers[id]; });
                    renderApplyTo();
                }, function () { /* no bulk targets — the control just stays hidden */ });
        }

        // ── Apply to (bulk targets) ────────────────────────────────────────────────────────────

        function selectedPeerIds() { return Object.keys(selectedPeers); }

        function renderApplyTo() {
            var details = document.getElementById(opts.applyToId);
            var label = document.getElementById(opts.applyToLabelId);
            var menu = document.getElementById(opts.applyToMenuId);
            var note = document.getElementById(opts.bulkNoteId);
            if (!details) return;
            details.classList.toggle('d-none', peers.length === 0);
            if (peers.length === 0) { note.classList.add('d-none'); return; }

            var count = selectedPeerIds().length;
            label.textContent = count === 0 ? larisvmsI18n.t('js.cameraDeviceSettings.thisCameraOnly') : larisvmsI18n.plural('js.cameraDeviceSettings.thisCameraPlus', count);
            note.classList.toggle('d-none', count === 0);
            note.textContent = count === 0 ? '' :
                larisvmsI18n.plural('js.cameraDeviceSettings.changesApplyTo', count);

            menu.innerHTML = '';
            menu.appendChild(el('div', { class: 'small text-muted mb-1', text: larisvmsI18n.t('js.cameraDeviceSettings.alsoApplyChangesTo') }));
            var all = el('input', { type: 'checkbox', class: 'form-check-input', id: 'dsPeerAll' });
            all.checked = count === peers.length;
            all.indeterminate = count > 0 && count < peers.length;
            all.addEventListener('change', function () {
                selectedPeers = {};
                if (all.checked) peers.forEach(function (p) { selectedPeers[p.cameraId] = true; });
                renderApplyTo();
            });
            menu.appendChild(el('div', { class: 'form-check border-bottom pb-1 mb-1' }, [
                all, el('label', { class: 'form-check-label small fw-semibold', for: 'dsPeerAll', text: larisvmsI18n.plural('js.cameraDeviceSettings.allOfModel', peers.length) })]));
            peers.forEach(function (p) {
                var id = 'dsPeer-' + p.cameraId;
                var cb = el('input', { type: 'checkbox', class: 'form-check-input', id: id });
                cb.checked = !!selectedPeers[p.cameraId];
                cb.addEventListener('change', function () {
                    if (cb.checked) selectedPeers[p.cameraId] = true; else delete selectedPeers[p.cameraId];
                    renderApplyTo();
                });
                menu.appendChild(el('div', { class: 'form-check' }, [cb,
                    el('label', { class: 'form-check-label small', for: id }, [
                        p.name,
                        p.firmwareDiffers ? el('span', { class: 'badge bg-body-secondary text-body border ms-1', title: larisvmsI18n.t('js.cameraDeviceSettings.firmwareTitle', { version: p.firmwareVersion || larisvmsI18n.t('js.cameraDeviceSettings.unknown') }), text: larisvmsI18n.t('js.cameraDeviceSettings.firmwareDiffers') }) : null,
                        p.isEnabled ? null : el('span', { class: 'text-muted ms-1', text: '(disabled)' })
                    ])]));
            });
        }

        // ── Field builders ─────────────────────────────────────────────────────────────────────

        // Each builder returns an .lv-field, or null when there's nothing to choose.
        function selectField(label, values, current, display, onchange, hint) {
            if (!values || values.length < 2) return null;
            var sel = el('select', { class: 'form-select form-select-sm' });
            var matched = false;
            values.forEach(function (v) {
                var o = el('option', { value: String(v), text: display ? display(v) : String(v) });
                if (same(v, current)) { o.selected = true; matched = true; }
                sel.appendChild(o);
            });
            if (!matched && current !== null && current !== undefined) {
                var extra = el('option', { value: String(current), text: (display ? display(current) : String(current)) + ' (current)' });
                extra.selected = true;
                sel.insertBefore(extra, sel.firstChild);
            }
            sel.addEventListener('change', function () { onchange(sel.value); });
            return el('div', { class: 'lv-field' }, [el('label', { class: 'form-label', text: label }), sel, hint ? el('div', { class: 'lv-hint', text: hint }) : null]);
        }

        function numberField(label, range, current, step, unit, onchange, hint) {
            var input = el('input', {
                type: 'number', class: 'form-control form-control-sm', step: String(step || 1),
                min: range ? String(range.min) : null, max: range ? String(range.max) : null,
                value: current === null || current === undefined ? '' : num(current)
            });
            input.addEventListener('input', function () {
                var v = input.value === '' ? null : Number(input.value);
                onchange(v === null || isNaN(v) ? null : v);
            });
            var rangeHint = range ? num(range.min) + '–' + num(range.max) + (unit ? ' ' + unit : '') : null;
            var group = unit
                ? el('div', { class: 'input-group input-group-sm' }, [input, el('span', { class: 'input-group-text', text: unit })])
                : input;
            return el('div', { class: 'lv-field' }, [el('label', { class: 'form-label', text: label }), group,
                (hint || rangeHint) ? el('div', { class: 'lv-hint', text: [rangeHint, hint].filter(Boolean).join('. ') }) : null]);
        }

        function sliderField(label, range, current, onchange) {
            if (!ranged(range)) return null;
            var step = range.max - range.min <= 2 ? 0.01 : 1;
            var value = current === null || current === undefined ? (range.min + range.max) / 2 : current;
            var input = el('input', { type: 'range', class: 'form-range', min: String(range.min), max: String(range.max), step: String(step), value: String(value) });
            var out = el('output', { class: 'text-muted small ms-2', text: num(value) });
            input.addEventListener('input', function () { out.textContent = num(Number(input.value)); onchange(Number(input.value)); });
            return el('div', { class: 'lv-field' }, [
                el('label', { class: 'form-label d-flex justify-content-between' }, [label, out]), input]);
        }

        // A titled group: a full-width heading row followed by its fields. Null when no fields survive.
        function group(title, fields) {
            var kept = fields.filter(Boolean);
            if (!kept.length) return null;
            return [el('div', { class: 'lv-wide ds-group-title', text: title })].concat(kept);
        }

        function section(title, sub, bodyParts, key) {
            var parts = [].concat.apply([], bodyParts.filter(Boolean));
            if (!parts.length) return null;
            var body = el('div', { class: 'lv-section-body' }, parts);
            return el('section', { class: 'lv-section', 'data-ds-section': key }, [
                el('div', { class: 'lv-section-head' }, [el('h2', { class: 'lv-section-title', text: title }), sub ? el('div', { class: 'lv-section-sub', text: sub }) : null]),
                body]);
        }

        function actions(key, isDirty, apply) {
            var btn = el('button', { type: 'button', class: 'btn btn-primary btn-sm', disabled: !isDirty() });
            btn.textContent = larisvmsI18n.t('js.cameraDeviceSettings.apply');
            var reset = el('button', { type: 'button', class: 'btn btn-link btn-sm', disabled: !isDirty(), text: larisvmsI18n.t('js.cameraDeviceSettings.reset') });
            var status = el('span', { class: 'small text-muted', 'data-ds-status': key });
            btn.addEventListener('click', function () { apply(btn, status); });
            reset.addEventListener('click', function () { resetSection(key); });
            var wrap = el('div', { class: 'lv-wide d-flex flex-wrap align-items-center gap-2 ds-actions' }, [btn, reset, status]);
            wrap._refresh = function () { btn.disabled = !isDirty(); reset.disabled = !isDirty(); };
            var results = lastResults[key] ? resultsList(key) : null;
            return results ? [wrap, results] : [wrap];
        }

        // ── Video ──────────────────────────────────────────────────────────────────────────────

        function codecOptions(stream, codec) {
            return stream.codecs.filter(function (c) { return same(c.codec, codec); })[0] || null;
        }

        function videoChange(stream) {
            var w = work.video[stream.role], c = stream.current, change = {}, any = false;
            ['codec', 'profile', 'frameRate', 'bitrateKbps', 'rateControl', 'govLength', 'quality'].forEach(function (k) {
                if (w[k] !== null && w[k] !== undefined && !same(w[k], c[k])) { change[k] = w[k]; any = true; }
            });
            if (w.width && w.height && (w.width !== c.width || w.height !== c.height)) { change.width = w.width; change.height = w.height; any = true; }
            return any ? change : null;
        }

        // Keeps the working values valid for a newly chosen codec: a resolution, profile, or frame
        // rate that codec doesn't offer moves to the closest one it does.
        function conformToCodec(stream) {
            var w = work.video[stream.role];
            var o = codecOptions(stream, w.codec);
            if (!o) return;
            if (o.resolutions.length && !o.resolutions.some(function (r) { return r.width === w.width && r.height === w.height; })) {
                var target = (w.width || 0) * (w.height || 0);
                var best = o.resolutions.slice().sort(function (a, b) { return Math.abs(a.width * a.height - target) - Math.abs(b.width * b.height - target); })[0];
                w.width = best.width; w.height = best.height;
            }
            if (o.profiles.length && !o.profiles.some(function (p) { return same(p, w.profile); })) w.profile = o.profiles[0];
            if (o.frameRates.length && !o.frameRates.some(function (f) { return Math.abs(f - w.frameRate) < 0.01; })) {
                w.frameRate = o.frameRates.slice().sort(function (a, b) { return Math.abs(a - w.frameRate) - Math.abs(b - w.frameRate); })[0];
            } else if (o.frameRateRange && w.frameRate !== null) {
                w.frameRate = Math.min(Math.max(w.frameRate, o.frameRateRange.min), o.frameRateRange.max);
            }
            if (o.bitrateKbps && w.bitrateKbps !== null) w.bitrateKbps = Math.min(Math.max(w.bitrateKbps, o.bitrateKbps.min), o.bitrateKbps.max);
            if (!o.rateControlSupported) w.rateControl = stream.current.rateControl;
        }

        function summary(v) {
            return [
                v.width && v.height ? v.width + '×' + v.height : null,
                v.codec ? codecName(v.codec) + (v.profile ? ' ' + v.profile : '') : null,
                v.frameRate ? num(v.frameRate) + ' fps' : null,
                v.bitrateKbps ? v.bitrateKbps + ' kbps' + (v.rateControl ? ' ' + v.rateControl : '') : null
            ].filter(Boolean).join(' · ');
        }

        function renderVideo() {
            if (!data.streams.length) return null;
            var stream = data.streams.filter(function (s) { return s.role === activeRole; })[0] || data.streams[0];
            var w = work.video[stream.role];
            var o = codecOptions(stream, w.codec) || { resolutions: [], frameRates: [], profiles: [] };
            var rerender = function () { render(); };
            var update = function (k, v) { w[k] = v; refreshDirty(); };

            var pills = data.streams.length > 1 ? el('div', { class: 'lv-wide' }, [el('ul', { class: 'nav nav-pills ds-pills', role: 'tablist' },
                data.streams.map(function (s) {
                    var dirty = !!videoChange(s);
                    return el('li', { class: 'nav-item' }, [el('button', {
                        type: 'button', role: 'tab', class: 'nav-link py-1 px-3' + (s.role === stream.role ? ' active' : ''),
                        'aria-selected': s.role === stream.role ? 'true' : 'false',
                        onclick: function () { activeRole = s.role; render(); }
                    }, [s.label, dirty ? el('span', { class: 'ds-dirty-dot ms-1', title: larisvmsI18n.t('js.cameraDeviceSettings.unsavedChanges'), 'aria-label': larisvmsI18n.t('js.cameraDeviceSettings.unsavedChanges2') }) : null])]);
                }))]) : null;

            var info = el('div', { class: 'lv-wide small text-muted' }, [
                larisvmsI18n.t('js.cameraDeviceSettings.currently', { summary: summary(stream.current) }),
                stream.sharedWith.length ? el('div', { class: 'text-warning-emphasis mt-1', text: larisvmsI18n.plural('js.cameraDeviceSettings.sharedEncoder', stream.sharedWith.length, { streams: joinList(stream.sharedWith) }) }) : null
            ]);

            var frameRateField = o.frameRates.length > 1
                ? selectField(larisvmsI18n.t('js.cameraDeviceSettings.frameRate'), o.frameRates, w.frameRate, function (f) { return num(f) + ' fps'; }, function (v) { update('frameRate', Number(v)); })
                : (ranged(o.frameRateRange) ? numberField(larisvmsI18n.t('js.cameraDeviceSettings.frameRate'), o.frameRateRange, w.frameRate, 1, 'fps', function (v) { update('frameRate', v); }) : null);

            var bitrateField = (o.bitrateKbps || w.bitrateKbps !== null)
                ? numberField(larisvmsI18n.t('js.cameraDeviceSettings.bitrate'), o.bitrateKbps, w.bitrateKbps, 1, 'kbps', function (v) { update('bitrateKbps', v === null ? null : Math.round(v)); },
                    w.rateControl === 'CBR' ? larisvmsI18n.t('js.cameraDeviceSettings.theStreamStaysAtThis') : larisvmsI18n.t('js.cameraDeviceSettings.theMostTheStreamWill'))
                : null;

            var showGov = !same(w.codec, 'JPEG') && (ranged(o.govLength) || stream.current.govLength !== null);
            var govField = showGov ? numberField(larisvmsI18n.t('js.cameraDeviceSettings.keyframeInterval'), o.govLength, w.govLength, 1, 'frames', function (v) { update('govLength', v === null ? null : Math.round(v)); },
                larisvmsI18n.t('js.cameraDeviceSettings.twiceTheFrameRateOne')) : null;

            var groups = [
                group(larisvmsI18n.t('js.cameraDeviceSettings.encoding'), [
                    selectField(larisvmsI18n.t('js.cameraDeviceSettings.codec'), stream.codecs.map(function (c) { return c.codec; }), w.codec, codecName, function (v) { w.codec = v; conformToCodec(stream); rerender(); },
                        larisvmsI18n.t('js.cameraDeviceSettings.h265Hint')),
                    selectField(larisvmsI18n.t('js.cameraDeviceSettings.encodingProfile'), o.profiles, w.profile, null, function (v) { update('profile', v); })
                ]),
                group(larisvmsI18n.t('js.cameraDeviceSettings.picture'), [
                    selectField(larisvmsI18n.t('js.cameraDeviceSettings.resolution'), o.resolutions.map(function (r) { return r.width + 'x' + r.height; }),
                        w.width && w.height ? w.width + 'x' + w.height : null,
                        function (v) { return v.replace('x', '×'); },
                        function (v) { var p = v.split('x'); w.width = Number(p[0]); w.height = Number(p[1]); refreshDirty(); }),
                    frameRateField
                ]),
                group(larisvmsI18n.t('js.cameraDeviceSettings.bitrate'), [
                    o.rateControlSupported ? selectField(larisvmsI18n.t('js.cameraDeviceSettings.bitrateMode'), ['VBR', 'CBR'], w.rateControl, function (m) { return m === 'CBR' ? larisvmsI18n.t('js.cameraDeviceSettings.constantCbr') : larisvmsI18n.t('js.cameraDeviceSettings.variableVbr'); },
                        function (v) { w.rateControl = v; rerender(); }, larisvmsI18n.t('js.cameraDeviceSettings.variableSavesStorageOnQuiet')) : null,
                    bitrateField,
                    sliderField(larisvmsI18n.t('js.cameraDeviceSettings.quality'), ranged(o.quality) ? o.quality : null, w.quality, function (v) { update('quality', v); }),
                    govField
                ])
            ];
            var parts = [pills ? [pills] : null, [info]].concat(groups);
            if (!groups.some(Boolean)) {
                // Nothing adjustable on this stream beyond the read-only summary.
                parts.push([el('div', { class: 'lv-wide small text-muted', text: larisvmsI18n.t('js.cameraDeviceSettings.theCameraDoesntOfferAny') })]);
            } else {
                parts.push(actions('video:' + stream.role, function () { return !!videoChange(stream); }, function (btn, status) {
                    var change = videoChange(stream);
                    if (!change) return;
                    var payload = { video: {} };
                    payload.video[stream.role] = change;
                    apply('video:' + stream.role, btn, status,
                        larisvmsI18n.t('js.cameraDeviceSettings.applyStreamConfirm', { stream: stream.label }),
                        'PUT', '/api/cameras/' + opts.cameraId + '/device-settings/video/' + encodeURIComponent(stream.role), change, payload,
                        function () { delete work.video[stream.role]; });
                }));
            }
            return section(larisvmsI18n.t('js.cameraDeviceSettings.videoStreams'), larisvmsI18n.t('js.cameraDeviceSettings.eachStreamReconnectsBrieflyWhen'), parts, 'video');
        }

        // ── Image ──────────────────────────────────────────────────────────────────────────────

        var IMAGE_KEYS = ['brightness', 'saturation', 'contrast', 'sharpness', 'irCutFilter', 'wdrMode', 'wdrLevel',
            'backlightMode', 'backlightLevel', 'exposureMode', 'whiteBalanceMode', 'whiteBalanceCrGain', 'whiteBalanceCbGain'];

        function imageChange() {
            if (!data.image || !work.image) return null;
            var change = {}, any = false;
            IMAGE_KEYS.forEach(function (k) {
                var v = work.image[k];
                if (v !== null && v !== undefined && !same(v, data.image.current[k])) { change[k] = v; any = true; }
            });
            return any ? change : null;
        }

        function renderImage() {
            if (!data.image) return null;
            var o = data.image.options, w = work.image;
            var update = function (k, v) { w[k] = v; refreshDirty(); };
            var rerender = function (k, v) { w[k] = v; render(); };
            var levelShown = function (mode) { return same(mode, 'ON'); };

            var parts = [
                group(larisvmsI18n.t('js.cameraDeviceSettings.adjustments'), [
                    sliderField(larisvmsI18n.t('js.cameraDeviceSettings.brightness'), o.brightness, w.brightness, function (v) { update('brightness', v); }),
                    sliderField(larisvmsI18n.t('js.cameraDeviceSettings.contrast'), o.contrast, w.contrast, function (v) { update('contrast', v); }),
                    sliderField(larisvmsI18n.t('js.cameraDeviceSettings.saturation'), o.saturation, w.saturation, function (v) { update('saturation', v); }),
                    sliderField(larisvmsI18n.t('js.cameraDeviceSettings.sharpness'), o.sharpness, w.sharpness, function (v) { update('sharpness', v); })
                ]),
                group(larisvmsI18n.t('js.cameraDeviceSettings.dayAndNight'), [
                    selectField(larisvmsI18n.t('js.cameraDeviceSettings.dayNightMode'), o.irCutFilterModes, w.irCutFilter, function (m) { return IR_CUT_NAMES[(m || '').toUpperCase()] || m; },
                        function (v) { update('irCutFilter', v); }, larisvmsI18n.t('js.cameraDeviceSettings.autoSwitchesToBlackAnd'))
                ]),
                group(larisvmsI18n.t('js.cameraDeviceSettings.lightAndExposure'), [
                    selectField(larisvmsI18n.t('js.cameraDeviceSettings.wideDynamicRangeWdr'), o.wdrModes, w.wdrMode, modeName, function (v) { rerender('wdrMode', v); }, larisvmsI18n.t('js.cameraDeviceSettings.evensOutBrightWindowsAnd')),
                    levelShown(w.wdrMode) ? sliderField(larisvmsI18n.t('js.cameraDeviceSettings.wdrLevel'), o.wdrLevel, w.wdrLevel, function (v) { update('wdrLevel', v); }) : null,
                    selectField(larisvmsI18n.t('js.cameraDeviceSettings.backlightCompensation'), o.backlightModes, w.backlightMode, modeName, function (v) { rerender('backlightMode', v); }, larisvmsI18n.t('js.cameraDeviceSettings.brightensSubjectsInFrontOf')),
                    levelShown(w.backlightMode) ? sliderField(larisvmsI18n.t('js.cameraDeviceSettings.backlightLevel'), o.backlightLevel, w.backlightLevel, function (v) { update('backlightLevel', v); }) : null,
                    selectField(larisvmsI18n.t('js.cameraDeviceSettings.exposure'), o.exposureModes, w.exposureMode, modeName, function (v) { update('exposureMode', v); })
                ]),
                group(larisvmsI18n.t('js.cameraDeviceSettings.whiteBalance'), [
                    selectField(larisvmsI18n.t('js.cameraDeviceSettings.whiteBalance'), o.whiteBalanceModes, w.whiteBalanceMode, modeName, function (v) { rerender('whiteBalanceMode', v); }),
                    same(w.whiteBalanceMode, 'MANUAL') ? sliderField(larisvmsI18n.t('js.cameraDeviceSettings.redGain'), o.whiteBalanceCrGain, w.whiteBalanceCrGain, function (v) { update('whiteBalanceCrGain', v); }) : null,
                    same(w.whiteBalanceMode, 'MANUAL') ? sliderField(larisvmsI18n.t('js.cameraDeviceSettings.blueGain'), o.whiteBalanceCbGain, w.whiteBalanceCbGain, function (v) { update('whiteBalanceCbGain', v); }) : null
                ])
            ];
            if (!parts.filter(Boolean).length) return null;
            parts.push(actions('image', function () { return !!imageChange(); }, function (btn, status) {
                var change = imageChange();
                if (!change) return;
                apply('image', btn, status, null, 'PUT', '/api/cameras/' + opts.cameraId + '/device-settings/image', change, { image: change },
                    function () { work.image = null; });
            }));
            return section(larisvmsI18n.t('js.cameraDeviceSettings.image'), larisvmsI18n.t('js.cameraDeviceSettings.takesEffectRightAwayWithout'), parts, 'image');
        }

        // ── Audio ──────────────────────────────────────────────────────────────────────────────

        function audioChange(a) {
            var w = work.audio[a.configToken], change = {}, any = false;
            ['codec', 'bitrateKbps', 'sampleRateKhz'].forEach(function (k) {
                if (w[k] !== null && w[k] !== undefined && !same(w[k], a.current[k])) { change[k] = w[k]; any = true; }
            });
            return any ? change : null;
        }

        function renderAudio() {
            var parts = [];
            data.audio.forEach(function (a) {
                var w = work.audio[a.configToken];
                var o = a.codecs.filter(function (c) { return same(c.codec, w.codec); })[0] || { bitratesKbps: [], sampleRatesKhz: [] };
                var fields = [
                    selectField(larisvmsI18n.t('js.cameraDeviceSettings.codec'), a.codecs.map(function (c) { return c.codec; }), w.codec, codecName, function (v) {
                        w.codec = v;
                        var no = a.codecs.filter(function (c) { return same(c.codec, v); })[0];
                        if (no && no.bitratesKbps.length && no.bitratesKbps.indexOf(w.bitrateKbps) < 0) w.bitrateKbps = no.bitratesKbps[0];
                        if (no && no.sampleRatesKhz.length && no.sampleRatesKhz.indexOf(w.sampleRateKhz) < 0) w.sampleRateKhz = no.sampleRatesKhz[0];
                        render();
                    }),
                    selectField(larisvmsI18n.t('js.cameraDeviceSettings.bitrate'), o.bitratesKbps, w.bitrateKbps, function (b) { return b + ' kbps'; }, function (v) { w.bitrateKbps = Number(v); refreshDirty(); }),
                    selectField(larisvmsI18n.t('js.cameraDeviceSettings.sampleRate'), o.sampleRatesKhz, w.sampleRateKhz, function (s) { return s + ' kHz'; }, function (v) { w.sampleRateKhz = Number(v); refreshDirty(); })
                ];
                if (!fields.filter(Boolean).length) return;
                var title = data.audio.length > 1 ? larisvmsI18n.t('js.cameraDeviceSettings.usedBy', { streams: joinList(a.roles) }) : null;
                var g = title ? group(title, fields) : fields.filter(Boolean);
                parts.push(g);
                parts.push(actions('audio:' + a.configToken, function () { return !!audioChange(a); }, function (btn, status) {
                    var change = audioChange(a);
                    if (!change) return;
                    apply('audio:' + a.configToken, btn, status, larisvmsI18n.t('js.cameraDeviceSettings.applyAudioSettingsStreamsThat'),
                        'PUT', '/api/cameras/' + opts.cameraId + '/device-settings/audio/' + encodeURIComponent(a.configToken), change, { audio: change },
                        function () { delete work.audio[a.configToken]; });
                }));
            });
            if (!parts.length) return null;
            return section(larisvmsI18n.t('js.cameraDeviceSettings.audio'), larisvmsI18n.t('js.cameraDeviceSettings.theMicrophoneAudioCarriedWith'), parts, 'audio');
        }

        // ── Apply ──────────────────────────────────────────────────────────────────────────────

        function apply(key, btn, status, confirmText, method, url, change, bulkPayload, onApplied) {
            var others = selectedPeerIds();
            var bulk = others.length > 0;
            var total = others.length + 1;
            var question = bulk
                ? larisvmsI18n.t('js.cameraDeviceSettings.applyToCameras', { count: total }) + (confirmText ? ' ' + confirmText.replace(/^[^?]*\?\s*/, '') : '')
                : confirmText;
            if (question && !window.confirm(question)) return;

            btn.disabled = true;
            status.className = 'small text-muted';
            status.textContent = bulk ? larisvmsI18n.t('js.cameraDeviceSettings.applyingTo', { count: total }) : larisvmsI18n.t('js.cameraDeviceSettings.saving');

            var request = bulk
                ? fetch('/api/device-settings/bulk', {
                    method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(Object.assign({ cameraIds: [opts.cameraId].concat(others) }, bulkPayload))
                })
                : fetch(url, { method: method, credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(change) });

            request.then(function (r) {
                if (!r.ok) return problemText(r, larisvmsI18n.t('js.cameraDeviceSettings.couldntApplyTheChange')).then(function (t) { throw new Error(t); });
                return bulk ? r.json() : null;
            }).then(function (results) {
                if (results) lastResults[key] = { payload: bulkPayload, results: results };
                else delete lastResults[key];
                onApplied();
                return load(true).then(function () {
                    var s = root.querySelector('[data-ds-status="' + key + '"]');
                    if (s && !results) { s.className = 'small text-success'; s.textContent = larisvmsI18n.t('js.cameraDeviceSettings.saved'); }
                });
            }).catch(function (err) {
                status.className = 'small text-danger';
                status.textContent = err.message;
                btn.disabled = false;
            });
        }

        var STATUS_ICON = { Applied: '✓', Partial: '◐', Skipped: '–', Failed: '✗' };
        var STATUS_CLASS = { Applied: 'text-success', Partial: 'text-warning-emphasis', Skipped: 'text-muted', Failed: 'text-danger' };

        function resultsList(key) {
            var entry = lastResults[key];
            var failed = entry.results.filter(function (r) { return r.status === 'Failed'; });
            var applied = entry.results.filter(function (r) { return r.status === 'Applied'; }).length;
            var list = el('ul', { class: 'list-unstyled small mb-0 ds-results' }, entry.results.map(function (r) {
                return el('li', {}, [
                    el('span', { class: STATUS_CLASS[r.status] + ' me-1', 'aria-hidden': 'true', text: STATUS_ICON[r.status] }),
                    el('span', { class: 'fw-semibold', text: r.cameraName }),
                    el('span', { class: 'text-muted', text: ' · ' + r.status.toLowerCase() + (r.messages.length ? ': ' + r.messages.join(' ') : '') })
                ]);
            }));
            var retry = failed.length ? el('button', {
                type: 'button', class: 'btn btn-sm btn-outline-secondary mt-1',
                onclick: function (e) { retryFailed(key, failed, e.target); }
            }, [larisvmsI18n.t('js.cameraDeviceSettings.retryFailed', { count: failed.length })]) : null;
            return el('div', { class: 'lv-wide border-top pt-2' }, [
                el('div', { class: 'small fw-semibold mb-1', text: larisvmsI18n.plural('js.cameraDeviceSettings.appliedTo', entry.results.length, { applied: applied }) }),
                list, retry,
                el('button', { type: 'button', class: 'btn btn-sm btn-link mt-1', text: larisvmsI18n.t('js.cameraDeviceSettings.dismiss'), onclick: function () { delete lastResults[key]; render(); } })
            ]);
        }

        function retryFailed(key, failed, btn) {
            var entry = lastResults[key];
            btn.disabled = true;
            btn.textContent = larisvmsI18n.t('js.cameraDeviceSettings.retrying');
            fetch('/api/device-settings/bulk', {
                method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(Object.assign({ cameraIds: failed.map(function (r) { return r.cameraId; }) }, entry.payload))
            }).then(function (r) {
                if (!r.ok) return problemText(r, larisvmsI18n.t('js.cameraDeviceSettings.couldntApplyTheChange')).then(function (t) { throw new Error(t); });
                return r.json();
            }).then(function (retried) {
                var byId = {};
                retried.forEach(function (r) { byId[r.cameraId] = r; });
                entry.results = entry.results.map(function (r) { return byId[r.cameraId] || r; });
                render();
            }).catch(function (err) {
                btn.disabled = false;
                btn.textContent = err.message + ' ' + larisvmsI18n.t('js.cameraDeviceSettings.tryAgain');
            });
        }

        function resetSection(key) {
            if (key.indexOf('video:') === 0) delete work.video[key.slice(6)];
            else if (key === 'image') work.image = null;
            else if (key.indexOf('audio:') === 0) delete work.audio[key.slice(6)];
            seedWork();
            render();
        }

        // ── Render ─────────────────────────────────────────────────────────────────────────────

        function anyDirty() {
            if (!data) return false;
            return data.streams.some(function (s) { return !!videoChange(s); }) || !!imageChange()
                || data.audio.some(function (a) { return !!audioChange(a); });
        }

        // Enables/disables Apply buttons without rebuilding the form (which would steal focus mid-typing).
        function refreshDirty() {
            Array.prototype.forEach.call(root.querySelectorAll('.ds-actions'), function (a) { if (a._refresh) a._refresh(); });
        }

        function render() {
            root.innerHTML = '';
            if (data.warnings.length) {
                root.appendChild(el('div', { class: 'alert alert-warning small py-2' }, data.warnings.map(function (w) { return el('div', { text: w }); })));
            }
            var sections = [renderVideo(), renderImage(), renderAudio()].filter(Boolean);
            if (!sections.length) {
                root.appendChild(el('p', { class: 'text-muted', text: larisvmsI18n.t('js.cameraDeviceSettings.thisCameraDoesntReportAny') }));
                return;
            }
            sections.forEach(function (s) { root.appendChild(s); });
        }

        // "A, B, and C" in the page's language.
        function joinList(items) {
            try { return new Intl.ListFormat(larisvmsI18n.locale, { type: 'conjunction' }).format(items); }
            catch (e) { return items.join(', '); }
        }

        window.addEventListener('beforeunload', function (e) {
            if (anyDirty()) { e.preventDefault(); e.returnValue = ''; }
        });
        document.addEventListener('click', function (e) {
            var details = document.getElementById(opts.applyToId);
            if (details && details.open && !details.contains(e.target)) details.open = false;
        });
        var refreshBtn = document.getElementById(opts.refreshBtnId);
        if (refreshBtn) refreshBtn.addEventListener('click', function () {
            if (anyDirty() && !window.confirm(larisvmsI18n.t('js.cameraDeviceSettings.discardYourUnsavedChangesAnd'))) return;
            load(false);
        });

        load(false);
        loadPeers();
    }

    return { init: init };
})();
