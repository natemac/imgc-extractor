using System.Buffers.Binary;
using System.Text;
using DiscUtils.Ext;

namespace ImgcExtractor;

public sealed record ImagePartition(int Number, long Offset, long Size, string Label);
public sealed class GameImage : IDisposable
{
    private readonly Stream disk;
    private readonly List<Stream> slices = [];
    public List<ImagePartition> Partitions { get; } = [];
    public Dictionary<string, ExtFileSystem> FileSystems { get; } = new(StringComparer.Ordinal);
    public string BackupDiscovery { get; private set; } = "";
    public long Length => disk.Length;
    public GameImage(string path, Action<string, double>? progress = null, CancellationToken token = default)
    {
        disk = Path.GetExtension(path).Equals(".imgc", StringComparison.OrdinalIgnoreCase)
            ? new ImgcStream(path, progress, token)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            int sector = disk is ImgcStream imgc ? imgc.SectorSize : 512;
            byte[] h = new byte[sector]; disk.Position = sector; disk.ReadExactly(h);
            if (Encoding.ASCII.GetString(h, 0, 8) != "EFI PART" && disk is not ImgcStream)
            {
                sector = 4096; h = new byte[sector]; disk.Position = sector; disk.ReadExactly(h);
            }
            if (Encoding.ASCII.GetString(h, 0, 8) != "EFI PART")
                throw new InvalidDataException("This version supports GPT images with game_dynamic and game_static ext partitions and a backup partition (the supported merged-content layout). No GPT header was found.");
            int headerSize = checked((int)U32(h, 12));
            if (headerSize < 92 || headerSize > sector) throw new InvalidDataException("Invalid GPT header size.");
            uint crc = U32(h, 16); Array.Clear(h, 16, 4);
            if (Crc(h.AsSpan(0, headerSize)) != crc) throw new InvalidDataException("GPT header checksum failed.");
            if (U64(h, 24) != 1) throw new InvalidDataException("Invalid primary GPT location.");
            long tableOffset = checked((long)U64(h, 72) * sector);
            int count = checked((int)U32(h, 80)), entrySize = checked((int)U32(h, 84));
            if (count < 1 || count > 4096 || entrySize < 128 || entrySize > 4096 || entrySize % 8 != 0)
                throw new InvalidDataException("Invalid GPT partition table dimensions.");
            int tableLength = checked(count * entrySize);
            if (tableOffset < sector * 2L || tableOffset > disk.Length - tableLength) throw new InvalidDataException("GPT table outside image.");
            byte[] table = new byte[tableLength]; disk.Position = tableOffset; disk.ReadExactly(table);
            if (Crc(table) != U32(h, 88)) throw new InvalidDataException("GPT partition table checksum failed.");
            var ranges = new List<(long Start, long End)>();
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var e = table.AsSpan(i * entrySize, entrySize);
                if (e[..16].IndexOfAnyExcept((byte)0) == -1) continue;
                long start = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(e[32..]) * sector);
                long end = checked(((long)BinaryPrimitives.ReadUInt64LittleEndian(e[40..]) + 1) * sector);
                if (start < tableOffset + tableLength || end <= start || end > disk.Length || ranges.Any(r => start < r.End && end > r.Start))
                    throw new InvalidDataException($"Partition {i + 1} overlaps metadata/another partition or extends beyond the image.");
                ranges.Add((start, end));
                if (end - start < 2048) continue;
                byte[] sb = new byte[1024]; disk.Position = start + 1024; disk.ReadExactly(sb);
                string label = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(56)) == 0xef53
                    ? Encoding.UTF8.GetString(sb, 120, 16).TrimEnd('\0') : "(not ext)";
                Partitions.Add(new ImagePartition(i + 1, start, end - start, label));
                progress?.Invoke($"Partition {i + 1}: {(label.Length == 0 ? "(unlabelled ext)" : label)} ({(end - start) / 1073741824.0:N2} GiB)", 1);
                if (label is not ("game_static" or "game_dynamic" or "db_backup")) continue;
                if (FileSystems.ContainsKey(label)) throw new InvalidDataException($"Duplicate partition label: {label}");
                var slice = new ImageSlice(disk, start, end - start); slices.Add(slice);
                FileSystems.Add(label, new ExtFileSystem(slice));
            }
            foreach (string label in new[] { "game_static", "game_dynamic" })
                if (!FileSystems.ContainsKey(label)) throw new InvalidDataException($"Required ext partition '{label}' was not found. Detected: {string.Join(", ", Partitions.Select(p => $"p{p.Number}={p.Label}"))}. Partition numbers are never assumed.");
            if (FileSystems.ContainsKey("db_backup"))
                BackupDiscovery = $"Backup partition p{Partitions.Single(p => p.Label == "db_backup").Number}: db_backup label.";
            else
            {
                // Some images omit only the backup filesystem label. Identify its
                // contents explicitly; never infer a role from a partition number.
                var candidates = new List<ImagePartition>();
                foreach (var partition in Partitions.Where(p => p.Label.Length == 0))
                {
                    token.ThrowIfCancellationRequested();
                    using var fs = OpenPartition(partition);
                    if (fs.DirectoryExists("pg_backup")) candidates.Add(partition);
                }
                if (candidates.Count != 1)
                    throw new InvalidDataException(candidates.Count > 1
                        ? $"Ambiguous backup layout: multiple unlabelled ext partitions contain pg_backup ({string.Join(", ", candidates.Select(p => $"p{p.Number}"))}). Refusing to choose a backup."
                        : $"Required backup not found: no db_backup label or unique unlabelled ext partition containing pg_backup. Detected: {string.Join(", ", Partitions.Select(p => $"p{p.Number}={(p.Label.Length == 0 ? "(unlabelled ext)" : p.Label)}"))}.");
                FileSystems.Add("db_backup", OpenPartition(candidates[0]));
                BackupDiscovery = $"Backup partition p{candidates[0].Number}: unlabelled ext filesystem identified by its pg_backup directory.";
            }
            progress?.Invoke(BackupDiscovery, 1);
        }
        catch { Dispose(); throw; }
    }
    private ExtFileSystem OpenPartition(ImagePartition partition)
    {
        var slice = new ImageSlice(disk, partition.Offset, partition.Size);
        slices.Add(slice);
        return new ExtFileSystem(slice);
    }
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));
    public static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint n = 0xffffffff;
        foreach (byte b in bytes) { n ^= b; for (int i = 0; i < 8; i++) n = (n >> 1) ^ ((n & 1) != 0 ? 0xedb88320u : 0); }
        return ~n;
    }
    public void Dispose() { foreach (var fs in FileSystems.Values) fs.Dispose(); foreach (var s in slices) s.Dispose(); disk.Dispose(); }
}

internal sealed class ImageSlice(Stream parent, long start, long size) : Stream
{
    private long position;
    public override int Read(byte[] b, int offset, int count) => Read(b.AsSpan(offset, count));
    public override int Read(Span<byte> b)
    {
        int count = (int)Math.Min(b.Length, size - position);
        if (count <= 0) return 0;
        parent.Position = start + position; int n = parent.Read(b[..count]); position += n; return n;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long next = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => size, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        if (next < 0 || next > size) throw new IOException("Seek outside partition.");
        return position = next;
    }
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override long Length => size;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
