// Push-to-talk from a live tile to the camera's speaker (two-way audio). Hold the 🎙️ button — or
// focus it and hold Space/Enter — to talk; release to stop. The ▾ next to it picks the microphone
// (headset, webcam, …), remembered per user.
//
// Audio path: getUserMedia → AudioWorklet (talk-worklet.js: 8 kHz 16-bit PCM, 40 ms chunks) →
// WebSocket /talk/{cameraId} on this site → recorder node → the camera's ONVIF audio backchannel.
// Wire protocol (see the node's TalkSessionHandler): text {"type":"start"} / {"type":"stop"} around
// each press, binary PCM only while pressed; the server answers {"type":"ready"} once the camera's
// speaker is open, {"type":"error","message"} when it can't be, and {"type":"recorded"} after each
// press it saved.
//
// The connection and microphone stay open for a minute after a release, so talking again starts
// immediately; after that both close (the browser's mic indicator goes away and the camera's
// backchannel is freed for someone else).
//
// Browsers only allow microphone access on a secure page (HTTPS, or localhost).
window.larisvmsTalk = (function () {
    'use strict';

    var MIC_PREF = 'talkMicDeviceId';
    var IDLE_CLOSE_MS = 60000;
    var READY_TIMEOUT_MS = 15000;

    function supported() {
        return !!(window.isSecureContext && navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.AudioWorkletNode);
    }

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    /// Markup for a tile's talk controls; empty when the camera can't take audio or this user can't
    /// talk through it. Call sites drop it into the tile's controls row and pass that row to wire().
    function html(canTalk) {
        if (!canTalk) return '';
        var disabled = supported() ? '' : ' disabled';
        var title = supported() ? 'Hold to talk' : 'Talking needs this site to be opened over HTTPS';
        return '<span class="talk-controls d-inline-flex align-items-center position-relative">' +
                '<button type="button" class="btn btn-sm btn-outline-light talk-button" style="padding:.1rem .35rem;" ' +
                    'title="' + title + '" aria-label="' + title + '"' + disabled + '>🎙️</button>' +
                '<button type="button" class="btn btn-sm btn-outline-light talk-mic" style="padding:.1rem .2rem;" ' +
                    'title="Choose microphone" aria-label="Choose microphone"' + disabled + '>▾</button>' +
                '<select class="form-select form-select-sm talk-mic-select d-none position-absolute bottom-100 end-0 mb-1" ' +
                    'style="width:auto; max-width:16rem;" aria-label="Microphone"></select>' +
                '<span class="talk-status badge text-bg-dark position-absolute bottom-100 end-0 mb-1 d-none"></span>' +
            '</span>';
    }

    function wire(containerEl, cameraId) {
        if (!containerEl) return;
        var button = containerEl.querySelector('.talk-button');
        if (!button || button.disabled) return;
        var micBtn = containerEl.querySelector('.talk-mic');
        var micSelect = containerEl.querySelector('.talk-mic-select');
        var statusEl = containerEl.querySelector('.talk-status');

        var socket = null;
        var ready = null;          // promise resolving once the server says the camera is ready
        var stream = null, context = null, node = null;
        var pressing = false;
        var idleTimer = null;
        var statusTimer = null;

        function showStatus(text, isError) {
            clearTimeout(statusTimer);
            statusEl.textContent = text;
            statusEl.className = 'talk-status badge position-absolute bottom-100 end-0 mb-1 ' + (isError ? 'text-bg-danger' : 'text-bg-dark');
            statusEl.classList.toggle('d-none', !text);
            if (text && isError) statusTimer = setTimeout(function () { showStatus(''); }, 6000);
        }

        function setTalking(on) {
            button.classList.toggle('btn-danger', on);
            button.classList.toggle('btn-outline-light', !on);
            button.setAttribute('aria-pressed', on ? 'true' : 'false');
        }

        function openMic() {
            if (stream) return Promise.resolve();
            var deviceId = window.larisvmsPreferences.get(MIC_PREF, '');
            var audio = { echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 };
            if (deviceId) audio.deviceId = { exact: deviceId };
            return navigator.mediaDevices.getUserMedia({ audio: audio })
                .catch(function (e) {
                    // A remembered microphone that's been unplugged: fall back to the default one.
                    if (deviceId && e && e.name === 'OverconstrainedError') {
                        delete audio.deviceId;
                        return navigator.mediaDevices.getUserMedia({ audio: audio });
                    }
                    throw e;
                })
                .then(function (s) {
                    stream = s;
                    context = new AudioContext();
                    return context.audioWorklet.addModule('/js/talk-worklet.js');
                })
                .then(function () {
                    node = new AudioWorkletNode(context, 'talk-downsampler');
                    node.port.onmessage = function (e) {
                        if (pressing && socket && socket.readyState === WebSocket.OPEN) socket.send(e.data);
                    };
                    context.createMediaStreamSource(stream).connect(node);
                });
        }

        function closeMic() {
            if (node) { node.port.onmessage = null; node.disconnect(); node = null; }
            if (context) { context.close(); context = null; }
            if (stream) { stream.getTracks().forEach(function (t) { t.stop(); }); stream = null; }
        }

        function openSocket() {
            if (ready) return ready;
            var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
            socket = new WebSocket(proto + '//' + location.host + '/talk/' + cameraId);
            socket.binaryType = 'arraybuffer';
            var current = socket;
            ready = new Promise(function (resolve, reject) {
                var timer = setTimeout(function () { reject(new Error('The camera didn\'t answer in time.')); }, READY_TIMEOUT_MS);
                current.onmessage = function (e) {
                    if (typeof e.data !== 'string') return;
                    var msg;
                    try { msg = JSON.parse(e.data); } catch (err) { return; }
                    if (msg.type === 'ready') { clearTimeout(timer); resolve(); }
                    else if (msg.type === 'error') { clearTimeout(timer); reject(new Error(msg.message || 'Two-way audio failed.')); showStatus(msg.message || 'Two-way audio failed.', true); }
                };
                current.onclose = function () {
                    clearTimeout(timer);
                    reject(new Error('Two-way audio disconnected.'));
                    if (socket === current) resetSession();
                };
            });
            ready.catch(function () { /* reported where it's awaited */ });
            return ready;
        }

        function resetSession() {
            if (pressing) { pressing = false; setTalking(false); }
            if (socket && socket.readyState <= WebSocket.OPEN) socket.close();
            socket = null;
            ready = null;
            closeMic();
        }

        function scheduleIdleClose() {
            clearTimeout(idleTimer);
            idleTimer = setTimeout(resetSession, IDLE_CLOSE_MS);
        }

        function press() {
            if (pressing) return;
            pressing = true;
            clearTimeout(idleTimer);
            setTalking(true);
            showStatus(ready ? '' : 'Connecting…');
            Promise.all([openMic(), openSocket()])
                .then(function () {
                    showStatus('');
                    // Released before the camera was ready: nothing to send.
                    if (!pressing) return;
                    socket.send(JSON.stringify({ type: 'start' }));
                    showStatus('Talking');
                })
                .catch(function (e) {
                    var message = e && e.name === 'NotAllowedError' ? 'Microphone access was blocked.'
                        : e && e.name === 'NotFoundError' ? 'No microphone found.'
                        : (e && e.message) || 'Two-way audio failed.';
                    showStatus(message, true);
                    resetSession();
                });
        }

        function release() {
            if (!pressing) return;
            pressing = false;
            setTalking(false);
            if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type: 'stop' }));
            if (statusEl.textContent === 'Talking' || statusEl.textContent === 'Connecting…') showStatus('');
            scheduleIdleClose();
        }

        button.addEventListener('pointerdown', function (e) {
            if (e.button !== 0) return;
            e.preventDefault();
            button.setPointerCapture(e.pointerId);
            press();
        });
        button.addEventListener('pointerup', release);
        button.addEventListener('pointercancel', release);
        button.addEventListener('lostpointercapture', release);
        button.addEventListener('contextmenu', function (e) { e.preventDefault(); }); // long-press on touch
        button.addEventListener('keydown', function (e) {
            if ((e.key === ' ' || e.key === 'Enter') && !e.repeat) { e.preventDefault(); press(); }
        });
        button.addEventListener('keyup', function (e) {
            if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); release(); }
        });
        button.addEventListener('blur', release);

        // Microphone picker. Device names are only visible once the page has had mic permission,
        // so the list says so until then.
        micBtn.addEventListener('click', function () {
            if (!micSelect.classList.contains('d-none')) { micSelect.classList.add('d-none'); return; }
            navigator.mediaDevices.enumerateDevices().then(function (devices) {
                var current = window.larisvmsPreferences.get(MIC_PREF, '');
                var mics = devices.filter(function (d) { return d.kind === 'audioinput'; });
                micSelect.innerHTML = '<option value="">Default microphone</option>' + mics
                    .filter(function (d) { return d.deviceId && d.deviceId !== 'default'; })
                    .map(function (d, i) {
                        return '<option value="' + escapeHtml(d.deviceId) + '"' + (d.deviceId === current ? ' selected' : '') + '>' +
                            escapeHtml(d.label || ('Microphone ' + (i + 1) + ' (talk once to see names)')) + '</option>';
                    }).join('');
                micSelect.classList.remove('d-none');
                micSelect.focus();
            });
        });
        micSelect.addEventListener('change', function () {
            window.larisvmsPreferences.set(MIC_PREF, micSelect.value);
            micSelect.classList.add('d-none');
            // Next press opens the newly chosen microphone.
            if (!pressing) closeMic();
        });
        micSelect.addEventListener('blur', function () { micSelect.classList.add('d-none'); });
    }

    return { html: html, wire: wire, supported: supported };
})();
