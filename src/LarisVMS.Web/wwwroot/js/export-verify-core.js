// The checks behind verifying an export, shared by the Verify page (exports-verify.js) and the
// standalone export player (export-player/player.js), which runs from disk with no server. Pure
// checks: nothing here touches the page. Results come back as { state: 'ok'|'warn'|'bad', title,
// detail } lines, worded through larisvmsI18n.
//
//   verifySignature(pem, text, signatureB64)  ECDSA P-256 / SHA-256 over the exact text (WebCrypto)
//   hashFile(file, onProgress)                SHA-256 of a File, a chunk at a time (sha256-stream.js)
//   checkChain(payload)                       an export manifest's segment chain, as lines
//   fingerprint(pem)                          a key's SHA-256 fingerprint, for comparing by eye
//   readSigned(text)                          { manifest, payload } from a signed manifest file
window.larisvmsExportVerify = (function () {
    'use strict';

    var CHUNK = 4 * 1024 * 1024;
    var EPOCH_TICKS = 621355968000000000n; // .NET ticks at 1970-01-01

    function i18n() { return window.larisvmsI18n; }

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
        try {
            return crypto.subtle.importKey('spki', b64ToBytes(pemBody(pem)), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify'])
                .then(function (key) {
                    return crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, b64ToBytes(signatureB64), new TextEncoder().encode(text));
                })
                .catch(function () { return false; });
        } catch (e) {
            return Promise.resolve(false);
        }
    }

    /// SHA-256 over the key's SPKI bytes, in groups of four, e.g. "3f2a 91c0 …".
    function fingerprint(pem) {
        try {
            return crypto.subtle.digest('SHA-256', b64ToBytes(pemBody(pem))).then(function (d) {
                return bytesToHex(new Uint8Array(d)).match(/.{4}/g).join(' ');
            });
        } catch (e) {
            return Promise.resolve('');
        }
    }

    // "2026-10-14T18:00:00.1234567Z" -> .NET ticks (BigInt), to the 100 ns the server hashed.
    function ticks(iso) {
        // eslint-disable-next-line security/detect-unsafe-regex -- reviewed: anchored, no nested repetition
        var m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?Z$/.exec(iso || '');
        if (!m) return null;
        var ms = Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]);
        var frac = ((m[7] || '') + '0000000').slice(0, 7);
        return EPOCH_TICKS + BigInt(ms) * 10000n + BigInt(frac);
    }

    // Guid.ToByteArray(): the first three groups little-endian, the rest as written.
    function guidBytes(guid) {
        var b = hexToBytes(guid.replace(/-/g, ''));
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

    function hashFile(file, onProgress) {
        var hasher = window.larisvmsSha256.create();
        function next(offset) {
            if (onProgress) onProgress(offset / Math.max(1, file.size));
            if (offset >= file.size) return Promise.resolve(hasher.digestHex());
            return file.slice(offset, offset + CHUNK).arrayBuffer().then(function (buf) {
                hasher.update(new Uint8Array(buf));
                return next(offset + CHUNK);
            });
        }
        return next(0);
    }

    function line(state, title, detail) { return { state: state, title: title, detail: detail || '' }; }

    /// A signed manifest file's text -> { manifest, payload }; throws when it isn't one.
    function readSigned(text) {
        var manifest = JSON.parse(text);
        if (!manifest || !manifest.signedPayload || !manifest.signature) throw new Error(i18n().t('js.exportsVerify.thisIsntALarisvmsExport'));
        return { manifest: manifest, payload: JSON.parse(manifest.signedPayload) };
    }

    async function checkChain(payload) {
        var t = i18n();
        var lines = [];
        var segments = payload.segments || [];
        var cameraId = payload.camera.id;
        var hashed = segments.filter(function (s) { return s.sha256 && s.chainHash; });
        var bad = 0, broken = 0, gaps = 0, prev = null;
        var chain = payload.chain || [];
        var lastHash = hashed.length ? hashed[hashed.length - 1].chainHash : null;
        if (chain.length) {
            // The whole chain from the first exported segment to the last, with the camera's other
            // recordings in between: every link recomputed, each following the one before, and every
            // exported segment on it.
            var onChain = {};
            for (var c = 0; c < chain.length; c++) {
                var link = chain[c];
                if (await chainLink(link.prevChainHash, link.sha256, cameraId, link.startUtc, link.endUtc) !== link.chainHash) bad++;
                if (c > 0 && link.prevChainHash !== chain[c - 1].chainHash) broken++;
                onChain[link.chainHash] = true;
            }
            hashed.forEach(function (s) { if (!onChain[s.chainHash]) broken++; });
            lastHash = chain[chain.length - 1].chainHash;
        } else {
            // A manifest from before the links in between were included: each exported segment's own
            // link is checked, but where another recording sits between two of them, that gap can't be.
            for (var i = 0; i < hashed.length; i++) {
                var s = hashed[i];
                if (await chainLink(s.prevChainHash, s.sha256, cameraId, s.startUtc, s.endUtc) !== s.chainHash) bad++;
                if (prev && s.prevChainHash !== prev.chainHash) gaps++;
                prev = s;
            }
        }
        var unhashed = segments.length - hashed.length;
        if (hashed.length === 0) {
            lines.push(line('warn', t.t('js.exportsVerify.noHashes'), t.t('js.exportsVerify.noHashesDetail')));
        } else if (bad === 0 && broken === 0 && gaps > 0) {
            lines.push(line('warn', t.t('js.exportsVerify.chainGaps'), t.plural('js.exportsVerify.chainGapsDetail', gaps)));
        } else if (bad === 0 && broken === 0) {
            lines.push(line('ok', t.t('js.exportsVerify.chainIntact'), unhashed
                ? t.plural('js.exportsVerify.chainIntactDetailOlder', hashed.length, { older: t.plural('js.exportsVerify.olderWithoutHash', unhashed) })
                : t.plural('js.exportsVerify.chainIntactDetail', hashed.length)));
        } else {
            lines.push(line('bad', t.t('js.exportsVerify.chainBroken'), t.t('js.exportsVerify.chainBrokenDetail', { bad: bad, broken: broken })));
        }

        var changed = segments.filter(function (s) { return s.unchangedAtExport === false; }).length;
        var checked = segments.filter(function (s) { return s.unchangedAtExport === true; }).length;
        if (changed > 0) lines.push(line('bad', t.t('js.exportsVerify.footageChanged'), t.plural('js.exportsVerify.footageChangedDetail', changed)));
        else if (checked > 0) lines.push(line('ok', t.t('js.exportsVerify.footageUnchanged'), t.t('js.exportsVerify.footageUnchangedDetail')));

        // On to the signed checkpoint.
        var cp = payload.checkpoint;
        if (!cp) {
            if (hashed.length) lines.push(line('warn', t.t('js.exportsVerify.noCheckpoint'), t.t('js.exportsVerify.noCheckpointDetail')));
            return lines;
        }
        var at = lastHash;
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
        var when = new Date(cp.signedUtc).toLocaleString(t.locale);
        if (reaches && cpSigned) lines.push(line('ok', t.t('js.exportsVerify.checkpointReached'), links.length ? t.plural('js.exportsVerify.signedLater', links.length, { when: when }) : t.t('js.exportsVerify.signedAt', { when: when })));
        else if (!cpSigned) lines.push(line('bad', t.t('js.exportsVerify.checkpointInvalid'), t.t('js.exportsVerify.signedAt', { when: when })));
        else lines.push(line('bad', t.t('js.exportsVerify.checkpointNotReached'), t.t('js.exportsVerify.checkpointNotReachedDetail')));
        return lines;
    }

    return {
        verifySignature: verifySignature,
        fingerprint: fingerprint,
        hashFile: hashFile,
        checkChain: checkChain,
        readSigned: readSigned,
        pemBody: pemBody,
        line: line
    };
})();
