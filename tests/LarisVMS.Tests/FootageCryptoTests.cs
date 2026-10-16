using System.Security.Cryptography;
using LarisVMS.Core;

namespace LarisVMS.Tests;

/// <summary>The encrypted footage file format: round trips, seeking, and tamper detection.</summary>
public class FootageCryptoTests
{
    private static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    private static byte[] Data(int n)
    {
        var data = new byte[n];
        for (var i = 0; i < n; i++) data[i] = (byte)(i * 31 + 7);
        return data;
    }

    private static byte[] Encrypt(byte[] plain, Guid keyId, byte[] key, int chunk = FootageCrypto.DefaultChunkSize)
    {
        using var output = new MemoryStream();
        FootageCrypto.EncryptAsync(new MemoryStream(plain), output, keyId, key, chunkSize: chunk).GetAwaiter().GetResult();
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(300_000)]
    public void RoundTripsAnySize(int size)
    {
        var key = Key();
        var keyId = Guid.NewGuid();
        var plain = Data(size);
        var sealedBytes = Encrypt(plain, keyId, key);

        Assert.Equal(FootageCrypto.EncryptedLength(size), sealedBytes.Length);
        Assert.True(FootageCrypto.IsEncrypted(new MemoryStream(sealedBytes)));
        using var reader = new FootageDecryptStream(new MemoryStream(sealedBytes), key);
        Assert.Equal(keyId, reader.KeyId);
        Assert.Equal(size, reader.Length);
        var back = new MemoryStream();
        reader.CopyTo(back);
        Assert.Equal(plain, back.ToArray());
    }

    [Fact]
    public void SeeksAcrossChunks()
    {
        var key = Key();
        var plain = Data(10_000);
        using var reader = new FootageDecryptStream(new MemoryStream(Encrypt(plain, Guid.NewGuid(), key, chunk: 1024)), key);

        foreach (var at in new[] { 9_999, 0, 1023, 1024, 5_000, 2047 })
        {
            reader.Position = at;
            var buffer = new byte[300];
            var n = reader.ReadAtLeast(buffer, Math.Min(300, plain.Length - at), throwOnEndOfStream: false);
            Assert.Equal(plain.AsSpan(at, n).ToArray(), buffer.AsSpan(0, n).ToArray());
        }
        reader.Seek(-10, SeekOrigin.End);
        Assert.Equal(plain[^10], (byte)reader.ReadByte());
    }

    [Fact]
    public void ATamperedChunkOrHeaderOrWrongKeyFails()
    {
        var key = Key();
        var sealedBytes = Encrypt(Data(5000), Guid.NewGuid(), key, chunk: 1024);

        var flipped = (byte[])sealedBytes.Clone();
        flipped[FootageCrypto.HeaderSize + 1500] ^= 1; // inside chunk 1
        using (var reader = new FootageDecryptStream(new MemoryStream(flipped), key))
        {
            reader.Position = 0;
            reader.ReadExactly(new byte[100]); // chunk 0 is fine
            reader.Position = 1100;
            Assert.ThrowsAny<CryptographicException>(() => reader.ReadExactly(new byte[10]));
        }

        var headerEdit = (byte[])sealedBytes.Clone();
        headerEdit[60] ^= 1; // reserved header byte is still covered by every chunk's AAD
        using (var reader = new FootageDecryptStream(new MemoryStream(headerEdit), key))
            Assert.ThrowsAny<CryptographicException>(() => reader.ReadExactly(new byte[10]));

        using (var reader = new FootageDecryptStream(new MemoryStream(sealedBytes), Key()))
            Assert.ThrowsAny<CryptographicException>(() => reader.ReadExactly(new byte[10]));
    }

    [Fact]
    public void PlainFilesAreNotMistakenForEncryptedOnes()
    {
        Assert.False(FootageCrypto.IsEncrypted(new MemoryStream(Data(100))));
        Assert.False(FootageCrypto.IsEncrypted(new MemoryStream([])));
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encrypt(Data(10), Guid.NewGuid(), Key()));
            Assert.True(FootageCrypto.IsEncrypted(path));
            Assert.NotNull(FootageCrypto.ReadKeyId(path));
        }
        finally { File.Delete(path); }
    }
}
