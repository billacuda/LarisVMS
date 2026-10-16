namespace LarisVMS.Media.Talk;

/// <summary>G.711 encoders (ITU-T G.711) for two-way audio: the browser sends 16-bit PCM at 8 kHz and
/// the camera's backchannel takes µ-law (PCMU, RTP payload type 0) or A-law (PCMA, type 8) — the two
/// codecs every ONVIF Profile T/S audio-output camera supports.</summary>
public static class G711
{
    private const int MuLawBias = 0x84;
    private const int MuLawClip = 32635;

    public static byte EncodeMuLaw(short pcm)
    {
        int sample = pcm;
        var sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = -sample;
        if (sample > MuLawClip) sample = MuLawClip;
        sample += MuLawBias;

        var exponent = 7;
        for (var mask = 0x4000; (sample & mask) == 0 && exponent > 0; mask >>= 1) exponent--;
        var mantissa = (sample >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    public static byte EncodeALaw(short pcm)
    {
        int sample = pcm;
        int sign;
        if (sample >= 0) sign = 0xD5;
        else
        {
            sign = 0x55;
            sample = -sample - 1;
        }
        if (sample > 32767) sample = 32767;

        int encoded;
        if (sample < 256)
        {
            encoded = sample >> 4;
        }
        else
        {
            var exponent = 7;
            for (var mask = 0x4000; (sample & mask) == 0 && exponent > 1; mask >>= 1) exponent--;
            var mantissa = (sample >> (exponent + 3)) & 0x0F;
            encoded = (exponent << 4) | mantissa;
        }
        return (byte)(encoded ^ sign);
    }

    /// <summary>Encodes little-endian 16-bit PCM (the browser's wire format) into <paramref name="output"/>,
    /// one byte per sample. Returns the number of bytes written.</summary>
    public static int Encode(ReadOnlySpan<byte> pcm16le, Span<byte> output, bool aLaw)
    {
        var samples = pcm16le.Length / 2;
        for (var i = 0; i < samples; i++)
        {
            var s = (short)(pcm16le[2 * i] | (pcm16le[2 * i + 1] << 8));
            output[i] = aLaw ? EncodeALaw(s) : EncodeMuLaw(s);
        }
        return samples;
    }
}
