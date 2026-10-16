// Incremental SHA-256 (FIPS 180-4). WebCrypto's digest() needs the whole input in memory at once,
// which an exported video (often gigabytes) doesn't fit; this one takes the file a chunk at a time.
// window.larisvmsSha256.create() -> { update(Uint8Array), digestHex() }.
window.larisvmsSha256 = (function () {
    'use strict';

    var K = new Uint32Array([
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
    ]);

    function create() {
        var h = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
        var w = new Uint32Array(64);
        var buffer = new Uint8Array(64);
        var buffered = 0;
        var length = 0; // total bytes; fine up to 2^53

        function block(data, offset) {
            for (var i = 0; i < 16; i++) {
                var j = offset + i * 4;
                w[i] = (data[j] << 24) | (data[j + 1] << 16) | (data[j + 2] << 8) | data[j + 3];
            }
            for (i = 16; i < 64; i++) {
                var x = w[i - 15], y = w[i - 2];
                var s0 = ((x >>> 7) | (x << 25)) ^ ((x >>> 18) | (x << 14)) ^ (x >>> 3);
                var s1 = ((y >>> 17) | (y << 15)) ^ ((y >>> 19) | (y << 13)) ^ (y >>> 10);
                w[i] = (w[i - 16] + s0 + w[i - 7] + s1) | 0;
            }
            var a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
            for (i = 0; i < 64; i++) {
                var S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
                var ch = (e & f) ^ (~e & g);
                var t1 = (hh + S1 + ch + K[i] + w[i]) | 0;
                var S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
                var maj = (a & b) ^ (a & c) ^ (b & c);
                var t2 = (S0 + maj) | 0;
                hh = g; g = f; f = e; e = (d + t1) | 0; d = c; c = b; b = a; a = (t1 + t2) | 0;
            }
            h[0] = (h[0] + a) | 0; h[1] = (h[1] + b) | 0; h[2] = (h[2] + c) | 0; h[3] = (h[3] + d) | 0;
            h[4] = (h[4] + e) | 0; h[5] = (h[5] + f) | 0; h[6] = (h[6] + g) | 0; h[7] = (h[7] + hh) | 0;
        }

        function update(data) {
            var i = 0;
            length += data.length;
            if (buffered > 0) {
                while (buffered < 64 && i < data.length) buffer[buffered++] = data[i++];
                if (buffered === 64) { block(buffer, 0); buffered = 0; }
            }
            for (; i + 64 <= data.length; i += 64) block(data, i);
            while (i < data.length) buffer[buffered++] = data[i++];
        }

        function digestHex() {
            var bits = length * 8;
            var pad = new Uint8Array(((buffered < 56) ? 56 : 120) - buffered + 8);
            pad[0] = 0x80;
            var hi = Math.floor(bits / 0x100000000), lo = bits >>> 0;
            var n = pad.length;
            pad[n - 8] = hi >>> 24; pad[n - 7] = hi >>> 16; pad[n - 6] = hi >>> 8; pad[n - 5] = hi;
            pad[n - 4] = lo >>> 24; pad[n - 3] = lo >>> 16; pad[n - 2] = lo >>> 8; pad[n - 1] = lo;
            update(pad); // length already captured in `bits`; the padding itself isn't counted again
            var out = '';
            for (var i = 0; i < 8; i++) out += ('00000000' + (h[i] >>> 0).toString(16)).slice(-8);
            return out;
        }

        return { update: update, digestHex: digestHex };
    }

    return { create: create };
})();
