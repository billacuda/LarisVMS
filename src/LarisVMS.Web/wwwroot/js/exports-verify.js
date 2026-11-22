// Verify an export (Components/Pages/Exports/Verify): checks an exported video against its signed
// manifest, entirely in the browser. The checks themselves are in export-verify-core.js, shared with
// the standalone export player.
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
    var core = window.larisvmsExportVerify;

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function update() { runBtn.disabled = !manifestInput.files.length || !videoInput.files.length; }
    manifestInput.addEventListener('change', update);
    videoInput.addEventListener('change', update);

    function lineHtml(l) {
        var icon = l.state === 'ok' ? '✓' : l.state === 'warn' ? '!' : '✗'; // text marks, not emoji, so they take the color
        var cls = l.state === 'ok' ? 'text-success' : l.state === 'warn' ? 'text-warning' : 'text-danger';
        return '<li class="list-group-item d-flex gap-2"><span class="' + cls + ' fw-bold" aria-hidden="true">' + icon + '</span><div><div class="fw-semibold">' +
            escapeHtml(l.title) + '</div>' + (l.detail ? '<div class="small text-muted">' + escapeHtml(l.detail) + '</div>' : '') + '</div></li>';
    }

    function hashFile(file) {
        var bar = progress.querySelector('.progress-bar');
        progress.classList.remove('d-none');
        return core.hashFile(file, function (fraction) { bar.style.width = Math.round(fraction * 100) + '%'; })
            .then(function (hex) { progress.classList.add('d-none'); return hex; });
    }

    async function run() {
        resultEl.innerHTML = '';
        runBtn.disabled = true;
        var lines = [];
        var verdictOk = true;
        var otherKey = false;
        try {
            var signed = core.readSigned(await manifestInput.files[0].text());
            var manifest = signed.manifest, payload = signed.payload;

            // 1. Signature
            var valid = await core.verifySignature(manifest.publicKeyPem, manifest.signedPayload, manifest.signature);
            var keys = await fetch('/api/signing-keys').then(function (r) { return r.ok ? r.json() : []; }).catch(function () { return []; });
            var ours = keys.some(function (k) { return core.pemBody(k.publicKeyPem) === core.pemBody(manifest.publicKeyPem); });
            if (!valid) { verdictOk = false; lines.push(core.line('bad', larisvmsI18n.t('js.exportsVerify.manifestInvalid'), larisvmsI18n.t('js.exportsVerify.manifestInvalidDetail'))); }
            else if (ours) lines.push(core.line('ok', larisvmsI18n.t('js.exportsVerify.manifestOurs'), larisvmsI18n.t('js.exportsVerify.signedWithKey', { key: manifest.keyId })));
            else { otherKey = true; lines.push(core.line('warn', larisvmsI18n.t('js.exportsVerify.manifestOtherKey'), larisvmsI18n.t('js.exportsVerify.manifestOtherKeyDetail', { key: manifest.keyId }))); }

            // 2. File
            var file = videoInput.files[0];
            if (payload.file.sizeBytes != null && file.size !== payload.file.sizeBytes) {
                verdictOk = false;
                lines.push(core.line('bad', larisvmsI18n.t('js.exportsVerify.notTheFile'), larisvmsI18n.t('js.exportsVerify.sizeMismatch', { size: file.size, expected: payload.file.sizeBytes })));
            } else {
                var hex = await hashFile(file);
                if (hex === payload.file.sha256) lines.push(core.line('ok', larisvmsI18n.t('js.exportsVerify.exactFile'), 'SHA-256 ' + hex + '.'));
                else { verdictOk = false; lines.push(core.line('bad', larisvmsI18n.t('js.exportsVerify.videoChanged'), larisvmsI18n.t('js.exportsVerify.videoChangedDetail'))); }
            }

            // 3–4. Footage chain
            var chainLines = await core.checkChain(payload);
            if (chainLines.some(function (l) { return l.state === 'bad'; })) verdictOk = false;
            lines = lines.concat(chainLines);

            var range = new Date(payload.requested.fromUtc).toLocaleString(larisvmsI18n.locale) + ' – ' + new Date(payload.requested.toUtc).toLocaleString(larisvmsI18n.locale);
            // eslint-disable-next-line no-unsanitized/property -- reviewed: text is escaped (escapeHtml/escHtml/larisvmsI18n.html); the rest is fixed markup, numbers, or server IDs
            resultEl.innerHTML =
                // Intact but signed by a key that isn't this server's: anyone can sign a package they
                // changed with a key of their own, so that isn't shown as verified.
                '<div class="alert ' + (!verdictOk ? 'alert-danger' : otherKey ? 'alert-warning' : 'alert-success') + '"><strong>' +
                (!verdictOk ? larisvmsI18n.html('js.exportsVerify.notVerified')
                    : otherKey ? larisvmsI18n.html('js.exportsVerify.manifestOtherKey') + '.'
                    : larisvmsI18n.html('js.exportsVerify.verified')) + '</strong> ' + escapeHtml(payload.camera.name) + ', ' + escapeHtml(range) +
                (payload.redacted ? ' ' + larisvmsI18n.html('js.exportsVerify.redactedNote') : '') + '.</div>' +
                '<ul class="list-group">' + lines.map(lineHtml).join('') + '</ul>';
        } catch (e) {
            resultEl.innerHTML = '<div class="alert alert-danger">' + escapeHtml(e.message || larisvmsI18n.t('js.exportsVerify.couldntRead')) + '</div>';
        } finally {
            progress.classList.add('d-none');
            update();
        }
    }

    runBtn.addEventListener('click', run);
})();
