using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ImgcExtractor;

public sealed record ExecutablePlan(string Status, string Message, long InputBytes, long DecodedBytes, string InputSha256, string? OutputSha256, int? Revision, string? Version)
{
    public bool RequiresDecode => Status == "WrappedSupported";
    public long AdditionalFinalBytes => RequiresDecode ? DecodedBytes : 0;
}
public sealed record PreparationReceipt(string Status, string Message, string InputSha256, string? OutputSha256, long InputBytes, long OutputBytes, int? Revision, string? Version, bool OriginalPreserved);
public sealed class PreparationException(string message, Exception? inner = null) : IOException("Executable preparation failed: " + message, inner);

public static class ExecutablePreparation
{
    private const int MaxExecutable = 128 * 1024 * 1024;
    public static ExecutablePlan Inspect(Stream input, CancellationToken token)
    {
        long length = input.Length;
        byte[] prefix = new byte[(int)Math.Min(100, length)]; input.ReadExactly(prefix); input.Position = 0;
        if (prefix.Length >= 4 && prefix.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 69, 76, 70 }))
        {
            string hash = Extractor.Hash(input, token);
            bool elf32 = prefix.Length >= 52 && prefix[4] == 1 && prefix[5] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(18)) == 3;
            return new(elf32 ? "AlreadyElf" : "Unsupported", elf32 ? "Already ELF32 i386; copied without decryption." : "ELF format is not the supported ELF32 i386 format; extraction only.", length, 0, hash, elf32 ? hash : null, null, null);
        }
        bool container = prefix.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(prefix) == 100;
        int revision = prefix.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(2)) : 0;
        if (!container || revision is not (2 or 3))
            return new("Unsupported", container ? $"Container revision {revision} is unsupported; extraction only." : "Unknown executable format; extraction only.", length, 0, Extractor.Hash(input, token), null, container ? revision : null, null);
        if (prefix.Length < 100) throw new PreparationException("Truncated supported executable container.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(84)) != 14)
            return new("Unsupported", "Container product is not supported product 14; extraction only.", length, 0, Extractor.Hash(input, token), null, revision, null);
        if (length > MaxExecutable || length < 600) throw new PreparationException("Supported executable container must be between 600 bytes and 128 MiB.");
        var decoded = Decode(ReadBounded(input, token), token);
        return new("WrappedSupported", $"Revision {decoded.Revision}, version {decoded.Version}: decode verified. Original will be preserved as game.bin.encrypted.", length, decoded.Bytes.Length, decoded.InputSha256.ToUpperInvariant(), decoded.OutputSha256.ToUpperInvariant(), decoded.Revision, decoded.Version);
    }
    private static byte[] ReadBounded(Stream input, CancellationToken token)
    {
        if (input.Length > MaxExecutable) throw new PreparationException("Executable exceeds the 128 MiB preparation limit.");
        byte[] bytes = new byte[checked((int)input.Length)]; int at = 0;
        while (at < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            int n = input.Read(bytes, at, Math.Min(1024 * 1024, bytes.Length - at));
            if (n == 0) throw new PreparationException("Truncated executable."); at += n;
        }
        return bytes;
    }
    private static GameBinResult Decode(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { var result = GameBinDecoder.Decode(bytes); token.ThrowIfCancellationRequested(); return result; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or ArgumentException)
        { throw new PreparationException(ex.Message, ex); }
    }
    // The caller owns a fresh, private staging folder. Never call this on a user's existing game folder.
    public static PreparationReceipt PrepareOwnedStaging(string staging, ExecutablePlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string game = Path.Combine(staging, "game.bin"), backup = Path.Combine(staging, "game.bin.encrypted");
        string currentHash;
        using (var stream = File.OpenRead(game)) currentHash = Extractor.Hash(stream, token);
        if (currentHash != plan.InputSha256) throw new PreparationException("Staged game.bin no longer matches the inspected source.");
        if (!plan.RequiresDecode)
            return new(plan.Status == "AlreadyElf" ? "AlreadyElf" : "Unsupported", plan.Message, currentHash, plan.OutputSha256, plan.InputBytes, plan.InputBytes, plan.Revision, plan.Version, File.Exists(backup));
        bool backupExists = File.Exists(backup);
        if (backupExists)
        {
            using var existing = File.OpenRead(backup);
            if (Extractor.Hash(existing, token) != currentHash) throw new PreparationException("Existing game.bin.encrypted differs from the original; refusing to replace it.");
        }
        GameBinResult decoded;
        using (var input = File.OpenRead(game)) decoded = Decode(ReadBounded(input, token), token);
        if (!decoded.OutputSha256.Equals(plan.OutputSha256, StringComparison.OrdinalIgnoreCase) || decoded.Bytes.Length != plan.DecodedBytes)
            throw new PreparationException("Decoded executable differs from the inspected result.");
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(staging))!);
        if (drive.AvailableFreeSpace < decoded.Bytes.Length + 64L * 1024 * 1024)
            throw new PreparationException($"Not enough free space on {drive.Name} for the decoded executable.");
        string pending = Path.Combine(staging, ".game.bin.preparing-" + Guid.NewGuid().ToString("N"));
        using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            for (int at = 0; at < decoded.Bytes.Length; at += 1024 * 1024)
            { token.ThrowIfCancellationRequested(); output.Write(decoded.Bytes, at, Math.Min(1024 * 1024, decoded.Bytes.Length - at)); }
            output.Flush(true);
        }
        using (var verify = File.OpenRead(pending))
            if (!Extractor.Hash(verify, token).Equals(decoded.OutputSha256, StringComparison.OrdinalIgnoreCase)) throw new PreparationException("Decoded executable disk verification failed.");
        token.ThrowIfCancellationRequested();
        // Commit this pair without interruption. Original remains recoverable even if
        // the second rename fails, and the outer folder is not yet published.
        if (backupExists) File.Delete(game); else File.Move(game, backup);
        File.Move(pending, game);
        return new("Prepared", "Decoded and verified; original preserved as game.bin.encrypted.", currentHash, decoded.OutputSha256.ToUpperInvariant(), plan.InputBytes, decoded.Bytes.Length, decoded.Revision, decoded.Version, true);
    }
}
