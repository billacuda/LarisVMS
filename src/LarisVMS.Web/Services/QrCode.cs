using System.Text;

namespace LarisVMS.Web.Services;

/// <summary>
/// A small QR code encoder (ISO/IEC 18004): byte mode, error correction level M, versions 1–20,
/// the mask with the lowest penalty. Enough for an authenticator-app <c>otpauth://</c> link, drawn as
/// an inline SVG so the page needs no script or image endpoint. Written here rather than adding a
/// package for one picture.
/// </summary>
public sealed class QrCode
{
    public const int MaxVersion = 20;

    // Level M, indexed by version (index 0 unused).
    private static readonly int[] EccCodewordsPerBlock =
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26];
    private static readonly int[] EccBlocks =
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16];
    private const int EccFormatBits = 0; // level M

    public int Version { get; }
    public int Size { get; }
    public int Mask { get; }
    private readonly bool[,] _modules;   // [y, x]
    private readonly bool[,] _isFunction;

    public bool this[int x, int y] => x >= 0 && y >= 0 && x < Size && y < Size && _modules[y, x];

    private QrCode(int version, byte[] codewords, int? forcedMask)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];

        DrawFunctionPatterns();
        DrawCodewords(AddEccAndInterleave(codewords));

        if (forcedMask is { } m)
        {
            Mask = m;
        }
        else
        {
            var best = 0;
            var bestPenalty = int.MaxValue;
            for (var i = 0; i < 8; i++)
            {
                ApplyMask(i);
                DrawFormatBits(i);
                var penalty = Penalty();
                if (penalty < bestPenalty) { best = i; bestPenalty = penalty; }
                ApplyMask(i); // XOR again undoes it
            }
            Mask = best;
        }
        ApplyMask(Mask);
        DrawFormatBits(Mask);
    }

    /// <summary>Encodes <paramref name="text"/> as UTF-8 in the smallest version that holds it.</summary>
    /// <exception cref="ArgumentException">Too long for version 20.</exception>
    public static QrCode Encode(string text) => Encode(Encoding.UTF8.GetBytes(text), null);

    internal static QrCode Encode(byte[] data, int? mask)
    {
        for (var version = 1; version <= MaxVersion; version++)
        {
            var countBits = version <= 9 ? 8 : 16;
            var capacityBits = DataCodewords(version) * 8;
            var usedBits = 4 + countBits + data.Length * 8;
            if (usedBits > capacityBits) continue;

            var bits = new List<bool>(capacityBits);
            Append(bits, 0b0100, 4);
            Append(bits, data.Length, countBits);
            foreach (var b in data) Append(bits, b, 8);
            Append(bits, 0, Math.Min(4, capacityBits - bits.Count));
            Append(bits, 0, (8 - bits.Count % 8) % 8);
            for (var pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) Append(bits, pad, 8);

            var codewords = new byte[bits.Count / 8];
            for (var i = 0; i < bits.Count; i++)
                if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            return new QrCode(version, codewords, mask);
        }
        throw new ArgumentException("Too long for a QR code.", nameof(data));
    }

    /// <summary>An SVG of the code with the standard four-module quiet zone, in dark-on-white whatever
    /// the page theme (scanners want that).</summary>
    public string ToSvg(int pixelsPerModule = 4)
    {
        var full = Size + 8;
        var path = new StringBuilder();
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                if (_modules[y, x]) path.Append('M').Append(x + 4).Append(',').Append(y + 4).Append("h1v1h-1z");
        var px = full * pixelsPerModule;
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {full} {full}\" width=\"{px}\" height=\"{px}\" shape-rendering=\"crispEdges\" role=\"img\">"
            + $"<rect width=\"{full}\" height=\"{full}\" fill=\"#fff\"/><path d=\"{path}\" fill=\"#000\"/></svg>";
    }

    private static void Append(List<bool> bits, int value, int length)
    {
        for (var i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
    }

    private static int RawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    internal static int DataCodewords(int version) =>
        RawDataModules(version) / 8 - EccCodewordsPerBlock[version] * EccBlocks[version];

    // ── Function patterns ───────────────────────────────────────────────────
    private void Set(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var align = AlignmentPositions();
        var n = align.Length;
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0))
                    DrawAlignment(align[i], align[j]);

        DrawFormatBits(0); // reserves the area; redrawn once the mask is chosen
        DrawVersion();
    }

    private void DrawFinder(int cx, int cy)
    {
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size) Set(x, y, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                Set(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1) return [];
        var n = Version / 7 + 2;
        var step = (Version * 8 + n * 3 + 5) / (n * 4 - 4) * 2;
        var result = new int[n];
        result[0] = 6;
        for (int i = n - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private void DrawFormatBits(int mask)
    {
        var data = EccFormatBits << 3 | mask;
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = (data << 10 | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;

        for (var i = 0; i <= 5; i++) Set(8, i, Bit(i));
        Set(8, 7, Bit(6));
        Set(8, 8, Bit(7));
        Set(7, 8, Bit(8));
        for (var i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));

        for (var i = 0; i < 8; i++) Set(Size - 1 - i, 8, Bit(i));
        for (var i = 8; i < 15; i++) Set(8, Size - 15 + i, Bit(i));
        Set(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        var rem = Version;
        for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        var bits = Version << 12 | rem;
        for (var i = 0; i < 18; i++)
        {
            var bit = ((bits >> i) & 1) != 0;
            int a = Size - 11 + i % 3, b = i / 3;
            Set(a, b, bit);
            Set(b, a, bit);
        }
    }

    // ── Data ────────────────────────────────────────────────────────────────
    private byte[] AddEccAndInterleave(byte[] data)
    {
        var numBlocks = EccBlocks[Version];
        var eccLen = EccCodewordsPerBlock[Version];
        var raw = RawDataModules(Version) / 8;
        var numShort = numBlocks - raw % numBlocks;
        var shortLen = raw / numBlocks;

        var divisor = ReedSolomonDivisor(eccLen);
        var blocks = new List<byte[]>();
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            var datLen = shortLen - eccLen + (i < numShort ? 0 : 1);
            var dat = data.AsSpan(k, datLen).ToArray();
            k += datLen;
            var ecc = ReedSolomonRemainder(dat, divisor);
            var block = new byte[shortLen + 1];
            dat.CopyTo(block, 0);
            // Short blocks get a placeholder byte where the long ones have one more data byte.
            ecc.CopyTo(block, shortLen + 1 - eccLen);
            blocks.Add(block);
        }

        var result = new List<byte>(raw);
        for (var i = 0; i < shortLen + 1; i++)
            for (var j = 0; j < blocks.Count; j++)
                if (i != shortLen - eccLen || j >= numShort) result.Add(blocks[j][i]);
        return [.. result];
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    private static byte Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < Size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (_isFunction[y, x]) continue;
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert) _modules[y, x] = !_modules[y, x];
            }
    }

    // The four penalty rules of the standard (N1 = 3, N2 = 3, N3 = 40, N4 = 10).
    private int Penalty()
    {
        var penalty = 0;
        for (var line = 0; line < Size; line++)
        {
            penalty += LinePenalty(i => _modules[line, i]);
            penalty += LinePenalty(i => _modules[i, line]);
        }
        for (var y = 0; y < Size - 1; y++)
            for (var x = 0; x < Size - 1; x++)
            {
                var c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) penalty += 3;
            }
        var dark = 0;
        foreach (var m in _modules) if (m) dark++;
        var total = Size * Size;
        var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return penalty + Math.Max(0, k) * 10;
    }

    private int LinePenalty(Func<int, bool> at)
    {
        var penalty = 0;
        var run = 1;
        for (var i = 1; i <= Size; i++)
        {
            if (i < Size && at(i) == at(i - 1)) { run++; continue; }
            if (run >= 5) penalty += 3 + run - 5;
            run = 1;
        }
        // 1:1:3:1:1 finder look-alikes with four light modules on either side (outside counts as light).
        bool Dark(int i) => i >= 0 && i < Size && at(i);
        for (var i = 0; i + 7 <= Size; i++)
        {
            if (!(Dark(i) && !Dark(i + 1) && Dark(i + 2) && Dark(i + 3) && Dark(i + 4) && !Dark(i + 5) && Dark(i + 6))) continue;
            var lightBefore = !Dark(i - 1) && !Dark(i - 2) && !Dark(i - 3) && !Dark(i - 4);
            var lightAfter = !Dark(i + 7) && !Dark(i + 8) && !Dark(i + 9) && !Dark(i + 10);
            if (lightBefore || lightAfter) penalty += 40;
        }
        return penalty;
    }
}
