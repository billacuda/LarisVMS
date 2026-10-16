// Verify an export (Components/Pages/Exports/Verify): checks an exported video against its signed
// manifest, entirely in the browser.
//
//   1. The manifest's signature (ECDSA P-256 / SHA-256 over the exact signedPayload text), and
//      whether the signing key is one of this server's (GET /api/signing-keys).
//   2. The video's size and SHA-256 against the manifest (streamed through sha256-stream.js).
//   3. Every source segment: its chain link recomputed (same formula as LarisVMS.Core.SegmentChain)
//      and linked to the one before; and whether the node found it unchanged since recording.
//   4. The chain from the last exported segment on to the signed checkpoint, and that checkpoint's
//      own signature.
(function () {
    'use strict';

    var manifestInput = document.getElementById('verifyManifest');
    var videoInput = document.getElementById('verifyVideo');
    var runBtn = document.getElementById('verifyRun');
    var progress = document.getElementById('verifyProgress');
    var resultEl = document.getElementById('verifyResult');
    if (!manifestInput || !runBtn) return;

    var CHUNK = 4 * 1024 * 1024;
    var EPOCH_TICKS = 621355968000000000n; // .NET ticks at 1970-01-01

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function update() { runBtn.disabled = !manifestInput.files.length || !videoInput.files.length; }
    manifestInput.addEventListener('change', update);
    videoInput.addEventListener('change', update);

    // ── Crypto helpers ──────────────────────────────────────────────────────

    function b64ToBytes(b64) {
        var bin = atob(b64), out = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
        return out;
    }

    function hexToBytes(hex) {
        var out = new Uint8Array(hex.length / 2);
        for (var i = 0; i < out.length; i++) out[i] = parseInt(hex.substr(i * 2, 2), 16);
        return out;
    }

    function bytesToHex(bytes) {
        return Array.prototype.map.call(bytes, function (b) { return ('0' + b.toString(16)).slice(-2); }).join('');
    }

    function pemBody(pem) { return (pem || '').replace(/-----[^-]+-----/g, '').replace(/\s+/g, ''); }

    function verifySignature(pem, text, signatureB64) {
        return crypto.subtle.importKey('spki', b64ToBytes(pemBody(pem)), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify'])
            .then(function (key) {
                return crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, b64ToBytes(signatureB64), new TextEncoder().encode(text));
            })
            .catch(function () { return false; });
    }

    // "2026-10-14T18:00:00.1234567Z" -> .NET ticks (BigInt), to the 100 ns the server hashed.
    function ticks(iso) {
        var m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?Z$/.exec(iso || '');
        if (!m) return null;
        var ms = Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]);
        var frac = ((m[7] || '') + '0000000').slice(0, 7);
        return EPOCH_TICKS + BigInt(ms) * 10000n + BigInt(frac);
    }

    // Guid.ToByteArray(): the first three groups little-endian, the rest as written.
    function guidBytes(guid) {
        var hex = guid.replace(/-/g, '');
        var b = hexToBytes(hex);
        return new Uint8Array([b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]]);
    }

    function int64Le(v) {
        var out = new Uint8Array(8);
        for (var i = 0; i < 8; i++) { out[i] = Number(v & 0xffn); v >>= 8n; }
        return out;
    }

    // Same as LarisVMS.Core.SegmentChain.Link.
    function chainLink(prevHex, contentHex, cameraId, startIso, endIso) {
        var buf = new Uint8Array(96);
        if (prevHex) buf.set(hexToBytes(prevHex), 0);
        buf.set(hexToBytes(contentHex), 32);
        buf.set(guidBytes(cameraId), 64);
        buf.set(int64Le(ticks(startIso)), 80);
        buf.set(int64Le(ticks(endIso)), 88);
        return crypto.subtle.digest('SHA-256', buf).then(function (d) { return bytesToHex(new Uint8Array(d)); });
    }

    function hashFile(file) {
        var hasher = window.larisvmsSha256.create();
        var bar = progress.querySelector('.progress-bar');
        progress.classList.remove('d-none');
        function next(offset) {
            bar.style.width = Math.round(offset / Math.max(1, file.size) * 100) + '%';
            if (offset >= file.size) return Promise.resolve(hasher.digestHex());
            return file.slice(offset, offset + CHUNK).arrayBuffer().then(function (buf) {
                hasher.update(new Uint8Array(buf));
                return next(offset + CHUNK);
            });
        }
        return next(0).then(function (hex) { progress.classList.add('d-none'); return hex; });
    }

    // ── Checks ──────────────────────────────────────────────────────────────

    function line(state, title, detail) {
        var icon = state === 'ok' ? '✓' : state === 'warn' ? '!' : '✗'; // text marks, not emoji, so they take the color
        var cls = state === 'ok' ? 'text-success' : state === 'warn' ? 'text-warning' : 'text-danger';
        return '<li class="list-group-item d-flex gap-2"><span class="' + cls + ' fw-bold" aria-hidden="true">' + icon + '</span><div><div class="fw-semibold">' +
            escapeHtml(title) + '</div>' + (detail ? '<div class="small text-muted">' + escapeHtml(detail) + '</div>' : '') + '</div></li>';
    }

    async function checkChain(payload) {
        var lines = [];
        var segments = payload.segments || [];
        var cameraId = payload.camera.id;
        var hashed = segments.filter(function (s) { return s.sha256 && s.chainHash; });
        var bad = 0, broken = 0, prev = null;
        for (var i = 0; i < hashed.length; i++) {
            var s = hashed[i];
            if (await chainLink(s.prevChainHash, s.sha256, cameraId, s.startUtc, s.endUtc) !== s.chainHash) bad++;
            if (prev && s.prevChainHash !== prev.chainHash) broken++;
            prev = s;
        }
        var unhashed = segments.length - hashed.length;
        if (hashed.length === 0) {
            lines.push(line('warn', 'No recorded hashes for this footage', 'It was recorded before tamper evidence existed, so it can only be checked against the export itself.'));
        } else if (bad === 0 && broken === 0) {
            lines.push(line('ok', 'Recorded footage chain is intact', hashed.length + ' segment' + (hashed.length === 1 ? '' : 's') + ' link up exactly as they were recorded' +
                (unhashed ? ' (' + unhashed + ' older one' + (unhashed === 1 ? '' : 's') + ' without a hash)' : '') + '.'));
        } else {
            lines.push(line('bad', 'Recorded footage chain doesn\'t add up', bad + ' link(s) don\'t match their contents and ' + broken + ' don\'t follow the one before.'));
        }

        var changed = segments.filter(function (s) { return s.unchangedAtExport === false; }).length;
        var checked = segments.filter(function (s) { return s.unchangedAtExport === true; }).length;
        if (changed > 0) lines.push(line('bad', 'Some footage had changed on disk', changed + ' segment' + (changed === 1 ? '' : 's') + ' no longer matched the hash recorded when written, at the time of export.'));
        else if (checked > 0) lines.push(line('ok', 'Footage was unchanged when exported', 'Every source file still matched the hash recorded when it was written.'));

        // On to the signed checkpoint.
        var cp = payload.checkpoint;
        if (!cp) {
            if (hashed.length) lines.push(line('warn', 'No signed checkpoint', 'The manifest doesn\'t include a chain checkpoint.'));
            return lines;
        }
        var at = hashed.length ? hashed[hashed.length - 1].chainHash : null;
        var linksOk = true;
        var links = payload.chainToCheckpoint || [];
        for (var j = 0; j < links.length; j++) {
            var l = links[j];
            if (l.prevChainHash !== at || await chainLink(l.prevChainHash, l.sha256, cameraId, l.startUtc, l.endUtc) !== l.chainHash) { linksOk = false; break; }
            at = l.chainHash;
        }
        var reaches = linksOk && at === cp.chainHash;
        var cpText = 'LarisVMS-chain-v1|' + cameraId + '|' + cp.segmentId + '|' + cp.chainHash + '|' + cp.segmentEndUtc + '|' + cp.signedUtc;
        var cpSigned = await verifySignature(cp.publicKeyPem, cpText, cp.signature);
        var when = new Date(cp.signedUtc).toLocaleString();
        if (reaches && cpSigned) lines.push(line('ok', 'Chain reaches a signed checkpoint', 'Signed ' + when + (links.length ? ', ' + links.length + ' later segment' + (links.length === 1 ? '' : 's') + ' on.' : '.')));
        else if (!cpSigned) lines.push(line('bad', 'Checkpoint signature is not valid', 'Signed ' + when + '.'));
        else lines.push(line('bad', 'Chain doesn\'t reach its checkpoint', 'The links after the export don\'t lead to the signed checkpoint.'));
        return lines;
    }

    async function run() {
        resultEl.innerHTML = '';
        runBtn.disabled = true;
        var lines = [];
        var verdictOk = true;
        try {
            var manifest = JSON.parse(await manifestInput.files[0].text());
            if (!manifest.signedPayload || !manifest.signature) throw new Error('This isn\'t a LarisVMS export manifest.');
            var payload = JSON.parse(manifest.signedPayload);

            // 1. Signature
            var signed = await verifySignature(manifest.publicKeyPem, manifest.signedPayload, manifest.signature);
            var keys = await fetch('/api/signing-keys').then(function (r) { return r.ok ? r.json() : []; }).catch(function () { return []; });
            var ours = keys.some(function (k) { return pemBody(k.publicKeyPem) === pemBody(manifest.publicKeyPem); });
            if (!signed) { verdictOk = false; lines.push(line('bad', 'Manifest signature is not valid', 'The manifest has been edited, or wasn\'t signed by LarisVMS.')); }
            else if (ours) lines.push(line('ok', 'Manifest signed by this server', 'Signed with key ' + manifest.keyId + '.'));
            else { lines.push(line('warn', 'Manifest signature is valid, but the key isn\'t this server\'s', 'It was signed by key ' + manifest.keyId + ' — check that key belongs to the system the export came from.')); }

            // 2. File
            var file = videoInput.files[0];
            if (payload.file.sizeBytes != null && file.size !== payload.file.sizeBytes) {
                verdictOk = false;
                lines.push(line('bad', 'The video is not the exported file', 'Size is ' + file.size.toLocaleString() + ' bytes; the export was ' + payload.file.sizeBytes.toLocaleString() + '.'));
            } else {
                var hex = await hashFile(file);
                if (hex === payload.file.sha256) lines.push(line('ok', 'The video is exactly the exported file', 'SHA-256 ' + hex + '.'));
                else { verdictOk = false; lines.push(line('bad', 'The video has been changed since it was exported', 'Its SHA-256 doesn\'t match the manifest.')); }
            }

            // 3–4. Footage chain
            var chainLines = await checkChain(payload);
            if (chainLines.some(function (l) { return l.indexOf('text-danger') >= 0; })) verdictOk = false;
            lines = lines.concat(chainLines);

            var range = new Date(payload.requested.fromUtc).toLocaleString() + ' – ' + new Date(payload.requested.toUtc).toLocaleString();
            resultEl.innerHTML =
                '<div class="alert ' + (verdictOk ? 'alert-success' : 'alert-danger') + '"><strong>' +
                (verdictOk ? 'Verified.' : 'Not verified.') + '</strong> ' + escapeHtml(payload.camera.name) + ', ' + escapeHtml(range) +
                (payload.redacted ? ' (areas were blacked out in this export)' : '') + '.</div>' +
                '<ul class="list-group">' + lines.join('') + '</ul>';
        } catch (e) {
            resultEl.innerHTML = '<div class="alert alert-danger">' + escapeHtml(e.message || 'Couldn\'t read those files.') + '</div>';
        } finally {
            progress.classList.add('d-none');
            update();
        }
    }

    runBtn.addEventListener('click', run);
})();
