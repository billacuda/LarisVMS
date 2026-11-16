using System.Buffers.Binary;
using System.Text;

namespace LarisVMS.Core;

/// <summary>
/// Writes a ZIP archive front to back onto a stream that can't seek (an HTTP response), so an export
/// package of many gigabytes reaches the browser as it's read from the recorders, never staged on
/// the server. Entries are stored, not compressed: video doesn't compress, and the rest is small.
///
/// Laid out the way Go's archive/zip does it, which every unzip tool reads: each entry's CRC and sizes
/// follow its data in a data descriptor (flag bit 3), names are UTF-8 (flag bit 11), and ZIP64
/// records are used only where a size, offset or count needs them — an entry of 4 GiB or more, or an
/// archive that large — so a small package is a plain ZIP.
/// </summary>
public sealed class StreamingZipWriter(Stream output)
{
    private const uint Max32 = uint.MaxValue;
    private const ushort Max16 = ushort.MaxValue;
    private const ushort FlagDataDescriptor = 0x0008;
    private const ushort FlagUtf8 = 0x0800;
    private const ushort Version20 = 20;
    private const ushort Version45 = 45; // ZIP64

    private sealed record Entry(byte[] Name, uint Crc, ulong Size, ulong Offset, ushort DosTime, ushort DosDate);

    private readonly List<Entry> _entries = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private ulong _written;
    private bool _finished;

    /// <summary>Bytes written so far.</summary>
    public ulong BytesWritten => _written;

    /// <summary>Adds an entry from bytes in memory.</summary>
    public Task AddAsync(string name, ReadOnlyMemory<byte> content, DateTime modifiedLocal, CancellationToken ct = default) =>
        AddAsync(name, new MemoryStream(content.ToArray(), writable: false), modifiedLocal, ct);

    /// <summary>Adds an entry, copying <paramref name="content"/> to its end. Returns the entry's size.</summary>
    public async Task<ulong> AddAsync(string name, Stream content, DateTime modifiedLocal, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        name = name.Replace('\\', '/');
        if (name.Length == 0 || name.StartsWith('/') || name.Split('/').Any(p => p is "" or "." or ".."))
            throw new ArgumentException($"Not a valid entry name: '{name}'.", nameof(name));
        if (!_names.Add(name)) throw new ArgumentException($"Duplicate entry name: '{name}'.", nameof(name));

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var (dosTime, dosDate) = DosDateTime(modifiedLocal);
        var offset = _written;

        // Local file header: CRC and sizes are zero here and follow the data in a descriptor.
        var header = new byte[30 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), Version20);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), FlagDataDescriptor | FlagUtf8);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 0); // stored
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), dosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), dosDate);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)nameBytes.Length);
        nameBytes.CopyTo(header, 30);
        await WriteAsync(header, ct);

        var crc = Crc32.Initial;
        ulong size = 0;
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            crc = Crc32.Update(crc, buffer.AsSpan(0, read));
            await WriteAsync(buffer.AsMemory(0, read), ct);
            size += (ulong)read;
        }
        var finalCrc = Crc32.Finish(crc);

        // Data descriptor: 8-byte sizes when the entry is too big for 4 bytes (ZIP64).
        var zip64 = size >= Max32;
        var descriptor = new byte[zip64 ? 24 : 16];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(0), 0x08074b50);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), finalCrc);
        if (zip64)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), size);
            BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(16), size);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(8), (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(12), (uint)size);
        }
        await WriteAsync(descriptor, ct);

        _entries.Add(new Entry(nameBytes, finalCrc, size, offset, dosTime, dosDate));
        return size;
    }

    /// <summary>Writes the central directory. Nothing can be added afterwards.</summary>
    public async Task FinishAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;
        var directoryStart = _written;

        foreach (var e in _entries)
        {
            var bigSize = e.Size >= Max32;
            var bigOffset = e.Offset >= Max32;
            var extraLength = (bigSize ? 16 : 0) + (bigOffset ? 8 : 0);
            var record = new byte[46 + e.Name.Length + (extraLength > 0 ? 4 + extraLength : 0)];
            var span = record.AsSpan();
            var version = extraLength > 0 ? Version45 : Version20;
            BinaryPrimitives.WriteUInt32LittleEndian(span[0..], 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], version); // made by (MS-DOS)
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], version); // needed
            BinaryPrimitives.WriteUInt16LittleEndian(span[8..], FlagDataDescriptor | FlagUtf8);
            BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[12..], e.DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(span[14..], e.DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(span[16..], e.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(span[20..], bigSize ? Max32 : (uint)e.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(span[24..], bigSize ? Max32 : (uint)e.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)e.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(span[30..], (ushort)(extraLength > 0 ? 4 + extraLength : 0));
            // comment length, disk number, internal attributes: 0; external attributes: 0.
            BinaryPrimitives.WriteUInt32LittleEndian(span[42..], bigOffset ? Max32 : (uint)e.Offset);
            e.Name.CopyTo(span[46..]);
            if (extraLength > 0)
            {
                var x = span[(46 + e.Name.Length)..];
                BinaryPrimitives.WriteUInt16LittleEndian(x, 0x0001); // ZIP64 extended information
                BinaryPrimitives.WriteUInt16LittleEndian(x[2..], (ushort)extraLength);
                var at = 4;
                if (bigSize)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(x[at..], e.Size);
                    BinaryPrimitives.WriteUInt64LittleEndian(x[(at + 8)..], e.Size);
                    at += 16;
                }
                if (bigOffset) BinaryPrimitives.WriteUInt64LittleEndian(x[at..], e.Offset);
            }
            await WriteAsync(record, ct);
        }

        var directorySize = _written - directoryStart;
        var count = (ulong)_entries.Count;
        if (count >= Max16 || directorySize >= Max32 || directoryStart >= Max32)
        {
            var zip64End = _written;
            var record = new byte[56];
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0), 0x06064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(4), 44); // size of the rest
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), Version45);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), Version45);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), count);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), count);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40), directorySize);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(48), directoryStart);
            await WriteAsync(record, ct);

            var locator = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(0), 0x07064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(locator.AsSpan(8), zip64End);
            BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(16), 1);
            await WriteAsync(locator, ct);
        }

        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(0), 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)Math.Min(count, Max16));
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)Math.Min(count, Max16));
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)Math.Min(directorySize, Max32));
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)Math.Min(directoryStart, Max32));
        await WriteAsync(end, ct);
        await output.FlushAsync(ct);
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await output.WriteAsync(bytes, ct);
        _written += (ulong)bytes.Length;
    }

    /// <summary>MS-DOS time and date (2-second precision; 1980 to 2107).</summary>
    internal static (ushort Time, ushort Date) DosDateTime(DateTime local)
    {
        if (local.Year < 1980) local = new DateTime(1980, 1, 1);
        if (local.Year > 2107) local = new DateTime(2107, 12, 31, 23, 59, 58);
        var time = (ushort)((local.Hour << 11) | (local.Minute << 5) | (local.Second / 2));
        var date = (ushort)(((local.Year - 1980) << 9) | (local.Month << 5) | local.Day);
        return (time, date);
    }

    /// <summary>CRC-32 (IEEE 802.3, as ZIP uses), table-driven.</summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();
        public const uint Initial = 0xFFFFFFFF;

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        public static uint Finish(uint crc) => crc ^ 0xFFFFFFFF;
    }
}
