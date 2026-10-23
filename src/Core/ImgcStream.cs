using System.Buffers.Binary;
using System.Text;

namespace ImgcExtractor;

// Format and modified LZO semantics based on shiz/unimgc (WTFPL).
// Unlike unimgc, all input/output bounds are checked. No disk is mounted or written.
public sealed class ImgcStream : Stream
{
    private record Block(long Logical, long Physical, int Packed, int Unpacked, long Length, bool Zero);
    private readonly FileStream source;
    private readonly List<Block> blocks = [];
    private readonly Dictionary<int, byte[]> cache = [];
    private readonly Queue<int> cacheOrder = [];
    private long cacheBytes;
    private long position;
    private long length;
    public int SectorSize { get; }
    public int BlockCount => blocks.Count;
    private const int MaxBlock = 64 * 1024 * 1024;
    private const long CacheLimit = 256L * 1024 * 1024;
    public ImgcStream(string path, Action<string, double>? progress = null, CancellationToken token = default)
    {
        source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        try
        {
            byte[] h = new byte[4096]; source.ReadExactly(h);
            if (Encoding.ASCII.GetString(h, 1, h[0]) != "HDD Raw Copy Tool")
                throw new InvalidDataException("Not an HDD Raw Copy Tool IMGC image.");
            var sectors = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(0x500));
            var sectorSize = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(0x508));
            if (sectorSize is not (512 or 4096) || sectors == 0 || sectors > long.MaxValue / sectorSize)
                throw new InvalidDataException("Invalid IMGC disk geometry.");
            SectorSize = (int)sectorSize;
            long expected = checked((long)(sectors * sectorSize));
            byte[] bh = new byte[12];
            long lastUpdate = 0;
            while (source.Position < source.Length)
            {
                token.ThrowIfCancellationRequested();
                source.ReadExactly(bh.AsSpan(0, 8));
                var kind = Encoding.ASCII.GetString(bh, 0, 4);
                long size = BinaryPrimitives.ReadUInt32LittleEndian(bh.AsSpan(4));
                if (size < 10 || size > MaxBlock || size - 8 > source.Length - source.Position)
                    throw new InvalidDataException("Truncated or oversized IMGC block.");
                long physical = source.Position;
                long count;
                if (kind == "omg!")
                {
                    if (size != 16) throw new InvalidDataException("Invalid zero block.");
                    source.ReadExactly(bh.AsSpan(0, 8));
                    ulong zero = BinaryPrimitives.ReadUInt64LittleEndian(bh);
                    if (zero > long.MaxValue) throw new InvalidDataException("Oversized zero block.");
                    count = (long)zero;
                }
                else if (kind == "lol!")
                {
                    source.ReadExactly(bh.AsSpan(0, 2));
                    int n = BinaryPrimitives.ReadUInt16LittleEndian(bh);
                    int prefix = 2;
                    if ((n & 0x8000) != 0)
                    {
                        if (size < 13) throw new InvalidDataException("Truncated compressed size.");
                        source.ReadExactly(bh.AsSpan(0, 2));
                        n = (n & 0x7fff) | (BinaryPrimitives.ReadUInt16LittleEndian(bh) << 15);
                        prefix = 4;
                    }
                    if (n <= 0 || n > MaxBlock || size <= 8 + prefix)
                        throw new InvalidDataException("Invalid decompressed block size.");
                    count = n;
                    physical += prefix;
                    size -= prefix;
                }
                else throw new InvalidDataException($"Unknown IMGC block type '{kind}'.");
                if (count <= 0 || count > expected - length)
                    throw new InvalidDataException("IMGC blocks exceed the advertised disk size.");
                blocks.Add(new Block(length, physical, (int)size - 8, kind == "lol!" ? (int)count : 0, count, kind == "omg!"));
                length += count;
                source.Position = physical + size - 8;
                if (blocks.Count > 5_000_000) throw new InvalidDataException("Too many IMGC blocks.");
                if (Environment.TickCount64 - lastUpdate > 250)
                {
                    progress?.Invoke($"Indexing compressed image: {blocks.Count:N0} blocks", (double)source.Position / source.Length);
                    lastUpdate = Environment.TickCount64;
                }
            }
            if (length != expected) throw new InvalidDataException($"Incomplete IMGC: expected {expected:N0} bytes, found {length:N0}.");
            progress?.Invoke($"Indexed {blocks.Count:N0} blocks; logical disk {length / 1073741824.0:N2} GiB", 1);
        }
        catch { source.Dispose(); throw; }
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0 && position < length)
        {
            int lo = 0, hi = blocks.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (blocks[mid].Logical <= position) lo = mid; else hi = mid - 1;
            }
            var b = blocks[lo];
            long within = position - b.Logical;
            int n = (int)Math.Min(buffer.Length, b.Length - within);
            if (b.Zero) buffer[..n].Clear();
            else
            {
                if (!cache.TryGetValue(lo, out var data))
                {
                    source.Position = b.Physical;
                    byte[] packed = new byte[b.Packed]; source.ReadExactly(packed);
                    data = ModifiedLzo.Decode(packed, b.Unpacked);
                    while (cacheBytes + data.Length > CacheLimit && cacheOrder.Count > 0)
                    {
                        int old = cacheOrder.Dequeue(); cacheBytes -= cache[old].Length; cache.Remove(old);
                    }
                    cache.Add(lo, data); cacheOrder.Enqueue(lo); cacheBytes += data.Length;
                }
                data.AsSpan((int)within, n).CopyTo(buffer);
            }
            position += n; total += n; buffer = buffer[n..];
        }
        return total;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long next = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        if (next < 0 || next > length) throw new IOException("Seek outside image.");
        return position = next;
    }
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override long Length => length;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
}

public static class ModifiedLzo
{
    public static byte[] Decode(byte[] input, int size)
    {
        if (size <= 0 || size > 64 * 1024 * 1024) throw new InvalidDataException("Invalid LZO output size.");
        byte[] output = new byte[size];
        int p = 0, o = 0, state = 0;
        int Byte() => p < input.Length ? input[p++] : throw new InvalidDataException("Truncated LZO instruction.");
        int Word() { int low = Byte(); return low | (Byte() << 8); }
        int Length(int bits)
        {
            int mask = (1 << bits) - 1, n = Byte() & mask;
            if (n == 0)
            {
                int v;
                while ((v = Byte()) == 0)
                { n = checked(n + 255); if (n > size) throw new InvalidDataException("LZO length exceeds output."); }
                n = checked(n + v + mask);
            }
            return n;
        }
        void Literal(int n)
        {
            if (n > input.Length - p || n > size - o) throw new InvalidDataException("LZO literal exceeds block.");
            Buffer.BlockCopy(input, p, output, o, n); p += n; o += n;
        }
        while (p < input.Length)
        {
            int instr = input[p];
            if (p == 0 && instr > 17) { Literal(instr - 17); state = 4; continue; }
            int n, distance, follow;
            if (instr >= 64)
            { p++; n = ((instr >> 5) & 7) + 1; follow = Byte(); distance = (follow << 3) + ((instr >> 2) & 7) + 1; state = instr & 3; }
            else if (instr >= 32)
            { n = Length(5) + 2; follow = Word(); distance = (follow >> 2) + 1; state = follow & 3; }
            else if (instr >= 16)
            {
                n = Length(3) + 2; follow = Word(); distance = (((instr >> 3) & 1) << 14) + (follow >> 2) + 0x4000; state = follow & 3;
                // The reference decoder accepts the conventional trailing end marker after the output is full.
                if (instr == 17 && follow == 0 && o == size && p == input.Length) break;
            }
            else if (state == 0) { Literal(Length(4) + 3); state = 4; continue; }
            else throw new InvalidDataException("Invalid LZO state.");
            if (distance <= 0 || distance > o || n > size - o) throw new InvalidDataException("Invalid LZO back reference.");
            if (n < 16)
                for (int i = 0; i < n; i++) { output[o] = output[o - distance]; o++; }
            else
            {
                // Copy an initialized period, then double the initialized span. This
                // preserves LZO's overlapping-match semantics without bytewise runs.
                int start = o, remaining = n, available = distance;
                while (remaining > 0)
                {
                    int chunk = Math.Min(remaining, available);
                    Buffer.BlockCopy(output, start - distance, output, o, chunk);
                    o += chunk; remaining -= chunk; available += chunk;
                }
            }
            if (state > 0 && state < 4) Literal(state);
        }
        if (o != size) throw new InvalidDataException($"LZO length mismatch: {o} instead of {size}.");
        return output;
    }
}
