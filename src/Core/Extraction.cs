using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.ComponentModel;
using DiscUtils;
using DiscUtils.Ext;

namespace ImgcExtractor;

public sealed record PlannedFile(string Partition, string Source, string Relative, long Length, bool MaterializedLink);
public sealed record Collision(PlannedFile Kept, PlannedFile Skipped);
public sealed class ExtractionPlan
{
    public List<PlannedFile> Files { get; } = [];
    public List<string> Directories { get; } = [];
    public List<Collision> Collisions { get; } = [];
    public long Bytes => Files.Sum(f => f.Length);
    public ExecutablePlan Preparation { get; private set; } = null!;
    public string? SourceBackupStatus { get; private set; }
    public long FinalBytes => checked(Bytes + Preparation.AdditionalFinalBytes);
    public int FinalFileCount => Files.Count + (Preparation.RequiresDecode ? 1 : 0);
    public static ExtractionPlan Create(GameImage image, CancellationToken token = default, Action<string>? progress = null)
    {
        var plan = new ExtractionPlan();
        var names = new Dictionary<string, (string Spelling, bool Directory, PlannedFile? File)>(StringComparer.OrdinalIgnoreCase);
        void Add(string label, string source, string relative, bool merge, int depth = 0)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 128 || names.Count > 2_000_000) throw new InvalidDataException("Directory tree exceeds supported limits.");
            ValidateRelative(relative);
            ExtFileSystem fs = image.FileSystems[label];
            var info = fs.GetUnixFileInfo(source);
            bool directory = info.FileType == UnixFileType.Directory;
            bool link = info.FileType == UnixFileType.Link;
            if (!directory && !link && info.FileType != UnixFileType.Regular)
                throw new InvalidDataException($"Unsupported special file: {label}:{source}");
            PlannedFile? file = null;
            if (!directory)
            {
                // OpenFile resolves file symlinks inside the virtual filesystem only.
                // Materialize the target as a normal Windows file; never create host links.
                long length;
                if (link) { using var content = fs.OpenFile(source, FileMode.Open, FileAccess.Read); length = content.Length; }
                else length = fs.GetFileLength(source);
                file = new(label, source, relative, length, link);
            }
            if (names.TryGetValue(relative, out var existing))
            {
                if (existing.Spelling != relative || existing.Directory != directory)
                    throw new InvalidDataException($"Windows path/case collision: '{existing.Spelling}' and '{relative}'.");
                if (!directory)
                {
                    if (!merge || existing.File!.Partition != "game_static") throw new InvalidDataException($"Duplicate output path: {relative}");
                    plan.Collisions.Add(new(existing.File!, file!)); return;
                }
            }
            else
            {
                names.Add(relative, (relative, directory, file));
                if (directory) plan.Directories.Add(relative); else plan.Files.Add(file!);
                if (names.Count % 500 == 0) progress?.Invoke($"{plan.Files.Count:N0} files inventoried: {relative}");
            }
            if (directory)
            {
                foreach (string child in fs.GetFileSystemEntries(source).Order(StringComparer.Ordinal))
                {
                    string name = child[(child.LastIndexOf('\\') + 1)..];
                    Add(label, child, relative + "\\" + name, merge, depth + 1);
                }
            }
        }
        void Tree(string label, string path, bool merge = false)
        {
            if (!image.FileSystems[label].DirectoryExists(path)) throw new InvalidDataException($"Required directory missing: {label}:{path}");
            Add(label, "\\" + path, path, merge);
        }
        void FileItem(string label, string path)
        {
            if (!image.FileSystems[label].FileExists(path)) throw new InvalidDataException($"Required file missing: {label}:{path}");
            Add(label, "\\" + path, path, false);
        }
        Tree("game_static", "golf"); Tree("game_dynamic", "golf", true);
        FileItem("game_dynamic", "game.bin"); FileItem("game_dynamic", "db_primary.tar.gz"); Tree("game_dynamic", "lib");
        FileItem("game_static", "assetlist.txt"); Tree("game_static", "dynamic_backup"); Tree("db_backup", "pg_backup");
        progress?.Invoke("Checking executable format and preparation integrity...");
        var dynamicFs = image.FileSystems["game_dynamic"];
        using (var executable = dynamicFs.OpenFile("game.bin", FileMode.Open, FileAccess.Read))
            plan.Preparation = ExecutablePreparation.Inspect(executable, token);
        if (dynamicFs.FileExists("game.bin.encrypted"))
        {
            using var backup = dynamicFs.OpenFile("game.bin.encrypted", FileMode.Open, FileAccess.Read);
            if (plan.Preparation.RequiresDecode)
            {
                if (Extractor.Hash(backup, token) != plan.Preparation.InputSha256)
                    throw new PreparationException("Source game.bin.encrypted differs from the wrapped game.bin. Extraction stopped to preserve both identities.");
                plan.SourceBackupStatus = "Existing encrypted backup matches original; one copy will be retained.";
            }
            else
            {
                if (plan.Preparation.Status == "AlreadyElf")
                {
                    var backupPlan = ExecutablePreparation.Inspect(backup, token);
                    if (backupPlan.RequiresDecode && backupPlan.OutputSha256 != plan.Preparation.InputSha256)
                        throw new PreparationException("Source game.bin.encrypted decodes to a different game.bin.");
                }
                FileItem("game_dynamic", "game.bin.encrypted");
                plan.SourceBackupStatus = "Existing encrypted backup retained unchanged.";
            }
        }
        return plan;
    }
    public static void ValidateRelative(string path)
    {
        foreach (string segment in path.Split('\\'))
        {
            string stem = segment.Split('.')[0].TrimEnd(' ');
            bool reserved = stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(stem, "^(COM|LPT)[1-9¹²³]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (segment.Length == 0 || segment.Length > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.Any(c => c < 32 || "<>:\"/|?*".Contains(c)) || reserved)
                throw new InvalidDataException($"Cannot represent this image path safely on Windows: {path}");
        }
    }
}

public sealed record ProgressUpdate(string Stage, string Message, long Done = 0, long Total = 0);
public sealed record FileReceipt(string Path, string Partition, long Bytes, string Sha256, bool MaterializedLink);
public sealed record SpaceEstimate(string Drive, string Format, long FreeBytes, long FinalFileBytes, long AllocationAllowance, long SafetyMargin)
{
    public long ExtraTemporaryBytes => 0;
    public long RequiredBytes => checked(FinalFileBytes + AllocationAllowance + SafetyMargin);
    public bool Enough => FreeBytes >= RequiredBytes;
}
public static class Extractor
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
    public static SpaceEstimate EstimateDestination(string output, long bytes, int entryCount)
    {
        if (bytes < 0 || entryCount < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Choose a NEW output folder; existing folders are never merged or overwritten.");
        string parent = Path.GetDirectoryName(output) ?? throw new IOException("Choose an output folder below a drive root.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The parent of the output folder must already exist.");
        for (var d = new DirectoryInfo(parent); d != null; d = d.Parent)
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Choose an output path without junctions or symbolic links.");
        var drive = new DriveInfo(Path.GetPathRoot(output)!);
        if (drive.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase)) throw new IOException("Use NTFS or exFAT; FAT32 cannot hold large game files.");
        long cluster = 65536;
        if (OperatingSystem.IsWindows())
        {
            if (!GetDiskFreeSpaceW(drive.Name, out uint sectors, out uint sectorBytes, out _, out _))
                throw new IOException("Cannot determine the destination drive's allocation size.", new Win32Exception(Marshal.GetLastWin32Error()));
            cluster = checked((long)sectors * sectorBytes);
            if (cluster <= 0) throw new IOException("Invalid destination allocation size.");
        }
        return new(drive.Name, drive.DriveFormat, drive.AvailableFreeSpace, bytes, checked((long)entryCount * cluster), 1024L * 1024 * 1024);
    }
    public static SpaceEstimate CheckDestination(string output, long bytes, int entryCount)
    {
        var space = EstimateDestination(output, bytes, entryCount);
        if (!space.Enough)
            throw new IOException($"Not enough space on {space.Drive}: {space.RequiredBytes / 1073741824.0:N2} GiB required; {space.FreeBytes / 1073741824.0:N2} GiB free.\nFinal file data: {space.FinalFileBytes / 1073741824.0:N2} GiB. Extra temporary image: 0 GiB. Allocation allowance and safety margin included.\nChoose another drive or free at least {(space.RequiredBytes - space.FreeBytes) / 1073741824.0:N2} GiB, then retry.");
        return space;
    }
    public static string Run(GameImage image, ExtractionPlan plan, string source, string output, Action<ProgressUpdate> progress, CancellationToken token)
    {
        output = Path.GetFullPath(output);
        var initialSpace = CheckDestination(output, plan.FinalBytes, plan.FinalFileCount + plan.Directories.Count + 1);
        string staging = output + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(staging);
        string reportPath = Path.Combine(staging, "Extraction-report.json");
        string logPath = Path.Combine(staging, "Extraction.log");
        var receipts = new List<FileReceipt>();
        var collisionChecks = new List<object>();
        PreparationReceipt? preparation = null;
        using var log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Log(string text) => log.WriteLine($"{DateTimeOffset.Now:O} {text}");
        Log($"Source: {Path.GetFullPath(source)}"); Log($"Output: {output}");
        Log($"Drive {initialSpace.Drive} ({initialSpace.Format}): free={initialSpace.FreeBytes} bytes, final data={initialSpace.FinalFileBytes}, extra temporary image=0, required including margins={initialSpace.RequiredBytes}");
        Log("Executable plan: " + plan.Preparation.Message);
        Log(image.BackupDiscovery);
        Log("Profile: merged content folder. Static files win exact-path golf collisions. Source opened read-only.");
        void Report(string status, string? error = null) => System.IO.File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            tool = "IMGC Extractor 1.0.1", status, error, source = Path.GetFullPath(source), output,
            spaceBeforeExtraction = initialSpace, executablePreparation = preparation, executablePlan = plan.Preparation, sourceBackupStatus = plan.SourceBackupStatus,
            completedUtc = DateTimeOffset.UtcNow, partitions = image.Partitions, backupDiscovery = image.BackupDiscovery,
            plannedFiles = plan.FinalFileCount, plannedBytes = plan.FinalBytes, verifiedFiles = receipts,
            collisionPolicy = "game_static wins exact-path golf collisions", collisions = collisionChecks,
            note = "File bytes verified with SHA-256. File symlinks materialized. Linux ownership/mode and directory times are not reproduced. Extraction does not establish game runtime compatibility."
        }, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            foreach (string dir in plan.Directories) { token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.Combine(staging, dir)); }
            byte[] buffer = new byte[1024 * 1024]; long copied = 0, lastProgress = 0;
            foreach (var f in plan.Files)
            {
                token.ThrowIfCancellationRequested();
                // Other processes may consume space after the preflight. Recheck the
                // selected output drive before every file and during large file writes.
                var drive = new DriveInfo(Path.GetPathRoot(staging)!);
                if (drive.AvailableFreeSpace < f.Length + 64L * 1024 * 1024)
                    throw new IOException($"Free space on {drive.Name} fell below what is needed for {f.Relative}. Free space and retry into a new folder.");
                string dest = Path.Combine(staging, f.Relative);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var input = image.FileSystems[f.Partition].OpenFile(f.Source, FileMode.Open, FileAccess.Read))
                using (var target = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length))
                {
                    if (input.Length != f.Length) throw new IOException($"Source length changed: {f.Relative}");
                    long actual = 0, nextSpaceCheck = 256L * 1024 * 1024; int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        token.ThrowIfCancellationRequested(); target.Write(buffer, 0, n); hash.AppendData(buffer, 0, n); actual += n; copied += n;
                        if (actual >= nextSpaceCheck)
                        {
                            if (drive.AvailableFreeSpace < f.Length - actual + 64L * 1024 * 1024)
                                throw new IOException($"Free space on {drive.Name} dropped during copying. Partial output is retained.");
                            nextSpaceCheck = actual + 256L * 1024 * 1024;
                        }
                        if (Environment.TickCount64 - lastProgress > 200)
                        { progress(new("Copying", f.Relative, copied, plan.Bytes)); lastProgress = Environment.TickCount64; }
                    }
                    if (actual != f.Length) throw new IOException($"Truncated source file: {f.Relative}");
                    target.Flush(true);
                }
                string expected = Convert.ToHexString(hash.GetHashAndReset());
                // Reopen and reread the destination; compare to the bytes actually read from the image.
                using (var verify = System.IO.File.OpenRead(dest))
                {
                    string actual = Hash(verify, token, () => progress(new("Verifying", f.Relative, copied, plan.Bytes)));
                    if (actual != expected) throw new IOException($"SHA-256 verification failed: {f.Relative}");
                }
                receipts.Add(new(f.Relative, f.Partition, f.Length, expected, f.MaterializedLink));
                Log($"Verified {f.Relative} ({f.Length} bytes) SHA256={expected}" + (f.MaterializedLink ? " [materialized file symlink]" : ""));
            }
            var hashes = receipts.ToDictionary(r => r.Path, r => r.Sha256, StringComparer.Ordinal);
            foreach (var c in plan.Collisions)
            {
                token.ThrowIfCancellationRequested(); progress(new("Checking collisions", c.Skipped.Relative));
                using var stream = image.FileSystems[c.Skipped.Partition].OpenFile(c.Skipped.Source, FileMode.Open, FileAccess.Read);
                string skippedHash = Hash(stream, token);
                bool identical = skippedHash == hashes[c.Kept.Relative];
                collisionChecks.Add(new { path = c.Kept.Relative, keptPartition = c.Kept.Partition, skippedPartition = c.Skipped.Partition, identical, keptSha256 = hashes[c.Kept.Relative], skippedSha256 = skippedHash });
                Log($"Collision: {c.Kept.Relative}; static kept; identical={identical}");
            }
            token.ThrowIfCancellationRequested();
            progress(new("Preparing executable", plan.Preparation.Message));
            preparation = ExecutablePreparation.PrepareOwnedStaging(staging, plan.Preparation, token);
            if (preparation.Status == "Prepared")
            {
                int originalIndex = receipts.FindIndex(r => r.Path == "game.bin");
                receipts[originalIndex] = receipts[originalIndex] with { Path = "game.bin.encrypted" };
                receipts.Add(new("game.bin", "decoded from game_dynamic", preparation.OutputBytes, preparation.OutputSha256!, false));
            }
            Log("Executable status: " + preparation.Status + "; " + preparation.Message);
            token.ThrowIfCancellationRequested(); Report("Verified"); Log("SUCCESS: all selected output files passed SHA-256 verification.");
            log.Dispose();
            // Windows file indexers/scanners can briefly retain a just-written file.
            // Retry only the owned staging-folder rename, never overwrite a destination.
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Move(staging, output); break; }
                catch (IOException) when (attempt < 10 && !Directory.Exists(output) && !System.IO.File.Exists(output))
                { Thread.Sleep(300); }
            }
            progress(new("Complete", preparation.Status == "Unsupported" ? "Extraction complete; executable NOT prepared (unsupported format). " + output : "Extraction verified; executable " + preparation.Status + ". " + output, plan.FinalBytes, plan.FinalBytes));
            return output;
        }
        catch (Exception ex)
        {
            try { Log(ex.ToString()); Report(ex is OperationCanceledException ? "Cancelled" : ex is PreparationException ? "PreparationFailed" : "Failed", ex.Message); } catch { /* Preserve original failure, including disk-full. */ }
            if (ex is OperationCanceledException) throw new OperationCanceledException($"Cancelled. Partial files are retained at: {staging}", ex, token);
            throw new IOException($"{ex.Message}\nPartial files and diagnostics: {staging}", ex);
        }
    }
    public static string Hash(Stream stream, CancellationToken token = default, Action? progress = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] b = new byte[1024 * 1024]; int n; long last = 0;
        while ((n = stream.Read(b, 0, b.Length)) > 0)
        {
            token.ThrowIfCancellationRequested(); hash.AppendData(b, 0, n);
            if (Environment.TickCount64 - last > 250) { progress?.Invoke(); last = Environment.TickCount64; }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
