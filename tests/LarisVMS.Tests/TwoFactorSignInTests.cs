using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class TwoFactorSignInTests
{
    // Checked with a separate decoder (zxing-cpp) when this was written: it reads back "LarisVMS",
    // level M.
    private static readonly string[] LarisVmsQr =
    [
        "#######.##....#######",
        "#.....#.#.##..#.....#",
        "#.###.#..#.##.#.###.#",
        "#.###.#.#.##..#.###.#",
        "#.###.#..#....#.###.#",
        "#.....#..##...#.....#",
        "#######.#.#.#.#######",
        "........#.#..........",
        "#.##.###.##...#..#.##",
        ".###...#.#..#.....#.#",
        ".##...#..##.##.#...##",
        ".#.###..#...##.###.#.",
        ".##.####.#.#....##.##",
        "........#....#...#..#",
        "#######.#.#..####.#..",
        "#.....#.##.########..",
        "#.###.#..#.#..##.##..",
        "#.###.#.#.#..#.##.##.",
        "#.###.#.##.#.#...##..",
        "#.....#....##.###...#",
        "#######.#.###...###..",
    ];

    [Fact]
    public void TheQrCodeMatchesOneADecoderRead()
    {
        var qr = QrCode.Encode("LarisVMS");
        Assert.Equal(1, qr.Version);
        for (var y = 0; y < qr.Size; y++)
            for (var x = 0; x < qr.Size; x++)
                Assert.True(LarisVmsQr[y][x] == '#' == qr[x, y], $"module ({x},{y})");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    [InlineData(150)]
    [InlineData(300)]
    public void EveryQrCodeHasItsFinderPatternsAndReadableFormat(int length)
    {
        var qr = QrCode.Encode(new string('x', length));
        Assert.Equal(qr.Version * 4 + 17, qr.Size);

        // The three 7×7 finder squares: a dark ring, a light ring, a dark 3×3 centre.
        foreach (var (cx, cy) in new[] { (3, 3), (qr.Size - 4, 3), (3, qr.Size - 4) })
            for (var dy = -3; dy <= 3; dy++)
                for (var dx = -3; dx <= 3; dx++)
                    Assert.Equal(Math.Max(Math.Abs(dx), Math.Abs(dy)) != 2, qr[cx + dx, cy + dy]);

        // Format information around the top-left finder: error correction M (00) and the mask used.
        var bits = 0;
        int[] xs = [8, 8, 8, 8, 8, 8, 8, 8, 7, 5, 4, 3, 2, 1, 0];
        int[] ys = [0, 1, 2, 3, 4, 5, 7, 8, 8, 8, 8, 8, 8, 8, 8];
        for (var i = 0; i < 15; i++) if (qr[xs[i], ys[i]]) bits |= 1 << i;
        bits ^= 0x5412;
        Assert.Equal(0, bits >> 13);
        Assert.Equal(qr.Mask, (bits >> 10) & 7);
    }

    [Fact]
    public void AnAuthenticatorLinkFitsAndTheSvgIsJustShapes()
    {
        var uri = TwoFactorSignIn.AuthenticatorUri("LarisVMS", "jane.doe@example.com", "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567");
        var svg = QrCode.Encode(uri).ToSvg();
        Assert.StartsWith("<svg ", svg);
        Assert.DoesNotContain("script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('x', 1000)));
    }

    [Fact]
    public void TheLinkEscapesTheNameAndAccount()
    {
        var uri = TwoFactorSignIn.AuthenticatorUri("Acme: HQ & Co", "jane@example.com", "KEY234");
        Assert.Equal("otpauth://totp/Acme%3A%20HQ%20%26%20Co:jane%40example.com?secret=KEY234&issuer=Acme%3A%20HQ%20%26%20Co&digits=6", uri);
    }

    [Theory]
    [InlineData("123 456", "123456")]
    [InlineData(" 123-456 ", "123456")]
    [InlineData("abcd-efgh", "abcdefgh")]
    [InlineData(null, "")]
    public void CodesAreTakenAsTyped(string? typed, string expected) =>
        Assert.Equal(expected, TwoFactorSignIn.NormalizeCode(typed));

    [Fact]
    public void TheKeyIsShownInGroupsOfFour() =>
        Assert.Equal("abcd efgh ij", TwoFactorSignIn.FormatKey("ABCDEFGHIJ"));
}
