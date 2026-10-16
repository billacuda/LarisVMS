namespace LarisVMS.Media.Talk;

/// <summary>Writes a mono 16-bit PCM WAV file as audio arrives — the recording of what an operator said
/// through a camera's speaker. The RIFF/data sizes are placeholders until <see cref="Dispose"/>
/// patches them, so a file cut short by a crash still has a valid header shape (players read it to the
/// end of the file).</summary>
public sealed class WavWriter : IDisposable
{
    private readonly FileStream _stream;
    private long _dataBytes;
    private bool _disposed;

    public int SampleRate { get; }
    public string Path { get; }
    public long DataBytes => _dataBytes;
    public TimeSpan Duration => TimeSpan.FromSeconds(_dataBytes / 2.0 / SampleRate);

    public WavWriter(string path, int sampleRate)
    {
        Path = path;
        SampleRate = sampleRate;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _stream.Write(Header(sampleRate, uint.MaxValue - 36));
    }

    public void Write(ReadOnlySpan<byte> pcm16le)
    {
        var even = pcm16le.Length & ~1;
        _stream.Write(pcm16le[..even]);
        _dataBytes += even;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _stream.Position = 0;
            _stream.Write(Header(SampleRate, (uint)Math.Min(_dataBytes, uint.MaxValue - 36)));
            _stream.Flush();
        }
        finally
        {
            _stream.Dispose();
        }
    }

    internal static byte[] Header(int sampleRate, uint dataBytes)
    {
        var h = new byte[44];
        void Ascii(int at, string s) { for (var i = 0; i < 4; i++) h[at + i] = (byte)s[i]; }
        void U32(int at, uint v) => BitConverter.TryWriteBytes(h.AsSpan(at, 4), v);
        void U16(int at, ushort v) => BitConverter.TryWriteBytes(h.AsSpan(at, 2), v);

        Ascii(0, "RIFF");
        U32(4, 36 + dataBytes);
        Ascii(8, "WAVE");
        Ascii(12, "fmt ");
        U32(16, 16);                       // PCM fmt chunk size
        U16(20, 1);                        // PCM
        U16(22, 1);                        // mono
        U32(24, (uint)sampleRate);
        U32(28, (uint)sampleRate * 2);     // byte rate
        U16(32, 2);                        // block align
        U16(34, 16);                       // bits per sample
        Ascii(36, "data");
        U32(40, dataBytes);
        return h;
    }
}
