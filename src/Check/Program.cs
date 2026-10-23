using System.Text;
using System.Text.Json;
using ImgcExtractor;

try
{
    if (args.Length == 0) { Console.WriteLine("Commands: self-test | inspect IMAGE | sample IMAGE REFERENCE_FOLDER | extract IMAGE NEW_FOLDER | compare-stream IMGC IMG"); return 0; }
    if (args[0] == "preparation-test") { PreparationChecks.Run(args[1], args[2], args[3]); return 0; }
    if (args[0] == "self-test")
    {
        int passed = 0;
        void Check(bool condition) { if (!condition) throw new Exception($"Check {passed + 1} failed"); passed++; }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException) { passed++; return; } throw new Exception("Expected rejection"); }
        Check(Encoding.ASCII.GetString(ModifiedLzo.Decode([1, 65, 66, 67, 68], 4)) == "ABCD");
        Check(Encoding.ASCII.GetString(ModifiedLzo.Decode([1, 65, 66, 67, 68, 76, 0], 7)) == "ABCDABC");
        Check(Encoding.ASCII.GetString(ModifiedLzo.Decode([1, 65, 66, 67, 68, 33, 12, 0], 7)) == "ABCDABC");
        Check(Encoding.ASCII.GetString(ModifiedLzo.Decode([1, 65, 66, 67, 68, 17, 0, 0], 4)) == "ABCD");
        Check(ModifiedLzo.Decode([21, 1, 2, 3], 4).SequenceEqual(new byte[] { 21, 1, 2, 3 }));
        Check(Encoding.ASCII.GetString(ModifiedLzo.Decode([1, 65, 65, 65, 65, 224, 0], 12)) == new string('A', 12));
        Reject(() => ModifiedLzo.Decode([1, 65], 4));
        Reject(() => ModifiedLzo.Decode([64, 0], 3));
        Reject(() => ModifiedLzo.Decode([1, 65, 66, 67, 68], 7));
        Reject(() => ModifiedLzo.Decode([0, 0], 10));
        foreach (string path in new[] { "..\\escape", "golf\\CON.txt", "golf\\foo.", "golf\\bad:name", "golf\\bad/name", "golf\\AUX", "\\absolute" })
            Reject(() => ExtractionPlan.ValidateRelative(path));
        ExtractionPlan.ValidateRelative("golf\\courses\\test.dat"); passed++;
        Check(GameImage.Crc(Encoding.ASCII.GetBytes("123456789")) == 0xcbf43926);
        Console.WriteLine($"PASS: {passed} decoder/path/CRC checks"); return 0;
    }
    if (args[0] == "compare-stream")
    {
        using var compressed = new ImgcStream(args[1]); using var raw = File.OpenRead(args[2]);
        if (Extractor.Hash(compressed) != Extractor.Hash(raw)) throw new Exception("Sequential stream mismatch");
        var rng = new Random(42); byte[] a = new byte[8192], b = new byte[8192];
        for (int i = 0; i < 1000; i++)
        {
            long at = rng.NextInt64(raw.Length - a.Length); raw.Position = at; compressed.Position = at;
            raw.ReadExactly(a); compressed.ReadExactly(b);
            if (!a.SequenceEqual(b)) throw new Exception($"Random read mismatch at {at}");
        }
        Console.WriteLine("PASS: full-stream SHA-256 and 1,000 random reads"); return 0;
    }
    if (args[0] == "integration-test")
    {
        string root = Path.GetFullPath(args[1]);
        string valid = Path.Combine(root, "valid.imgc");
        string scratch = Path.Combine(root, "checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        int checks = 0;
        void Reject(Action action)
        {
            try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { checks++; return; }
            throw new Exception("Expected failure was not rejected");
        }
        byte[] contents = File.ReadAllBytes(valid);
        foreach (int cut in new[] { 0, 100, 4095, 4100, contents.Length - 1 })
        {
            string truncated = Path.Combine(scratch, $"truncated-{cut}.imgc"); File.WriteAllBytes(truncated, contents[..cut]);
            Reject(() => { using var stream = new ImgcStream(truncated); });
        }
        byte[] invalid = (byte[])contents.Clone(); invalid[4096] = (byte)'X';
        string bad = Path.Combine(scratch, "unknown-block.imgc"); File.WriteAllBytes(bad, invalid);
        Reject(() => { using var stream = new ImgcStream(bad); });
        invalid = (byte[])contents.Clone(); Array.Clear(invalid, 4100, 4);
        bad = Path.Combine(scratch, "bad-block-length.imgc"); File.WriteAllBytes(bad, invalid);
        Reject(() => { using var stream = new ImgcStream(bad); });
        string badGpt = Path.Combine(scratch, "bad-gpt.img"); File.Copy(Path.Combine(root, "valid.img"), badGpt);
        using (var f = File.OpenWrite(badGpt)) { f.Position = 512 + 56; f.WriteByte(0xff); }
        Reject(() => { using var image = new GameImage(badGpt); });
        using var good = new GameImage(valid);
        var fixturePlan = ExtractionPlan.Create(good);
        using var cts = new CancellationTokenSource();
        string output = Path.Combine(scratch, "cancel-output");
        Reject(() => Extractor.Run(good, fixturePlan, valid, output, p => cts.Cancel(), cts.Token));
        if (Directory.Exists(output)) throw new Exception("Cancelled output was incorrectly published");
        var partial = Directory.GetDirectories(scratch, "cancel-output.partial-*").Single();
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(partial, "Extraction-report.json")));
        if (report.RootElement.GetProperty("status").GetString() != "Cancelled") throw new Exception("Missing cancellation receipt");
        checks++;
        string existing = Path.Combine(scratch, "existing"); Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "sentinel"), "keep me");
        Reject(() => Extractor.Run(good, fixturePlan, valid, existing, _ => { }, CancellationToken.None));
        if (File.ReadAllText(Path.Combine(existing, "sentinel")) != "keep me") throw new Exception("Existing output changed");
        checks++;
        var space = Extractor.CheckDestination(Path.Combine(scratch, "space-ok"), fixturePlan.Bytes, fixturePlan.Files.Count + fixturePlan.Directories.Count);
        if (!space.Enough || space.ExtraTemporaryBytes != 0 || space.RequiredBytes <= fixturePlan.Bytes || space.Drive != Path.GetPathRoot(scratch)) throw new Exception("Drive-space calculation failed");
        checks++;
        try { Extractor.CheckDestination(Path.Combine(scratch, "too-big"), 8L * 1024 * 1024 * 1024 * 1024, 1); throw new Exception("Insufficient space accepted"); }
        catch (IOException ex) when (ex.Message.StartsWith("Not enough space on")) { checks++; }
        Console.WriteLine($"PASS: {checks} truncation/corruption/cancellation/destination/space checks"); return 0;
    }
    using var image = new GameImage(args[1], (text, percent) => Console.WriteLine($"{percent:P0} {text}"));
    foreach (var p in image.Partitions) Console.WriteLine($"p{p.Number} {p.Label}: {p.Size:N0} bytes");
    if (args[0] == "sample")
    {
        var comparisons = new List<object>();
        foreach (var (label, path) in new[] { ("game_dynamic", "game.bin"), ("game_dynamic", "db_primary.tar.gz"), ("game_static", "assetlist.txt"), ("game_static", "dynamic_backup\\dynamic.tar.gz") })
        {
            using var src = image.FileSystems[label].OpenFile(path, FileMode.Open, FileAccess.Read);
            using var reference = File.OpenRead(Path.Combine(args[2], path));
            string imageHash = Extractor.Hash(src), referenceHash = Extractor.Hash(reference);
            bool match = imageHash == referenceHash;
            Console.WriteLine($"{(match ? "MATCH" : "DIFFERENT")} {path} {imageHash}");
            comparisons.Add(new { path, match, imageHash, referenceHash });
        }
        Console.WriteLine(JsonSerializer.Serialize(comparisons, new JsonSerializerOptions { WriteIndented = true })); return 0;
    }
    var plan = ExtractionPlan.Create(image, progress: Console.WriteLine);
    Console.WriteLine($"PLAN: {plan.FinalFileCount:N0} final files; {plan.Directories.Count:N0} directories; {plan.FinalBytes:N0} final bytes; {plan.Collisions.Count:N0} static/dynamic collisions; {plan.Files.Count(f => f.MaterializedLink)} materialized links");
    Console.WriteLine("EXECUTABLE: " + plan.Preparation.Message);
    foreach (var c in plan.Collisions.Take(30)) Console.WriteLine($"COLLISION: {c.Kept.Relative}");
    if (args[0] == "spot-check")
    {
        int missing = 0, changedSize = 0;
        foreach (var f in plan.Files)
        {
            var local = new FileInfo(Path.Combine(args[2], f.Relative));
            if (!local.Exists) { missing++; Console.WriteLine("MISSING: " + f.Relative); }
            else if (local.Length != f.Length) { changedSize++; Console.WriteLine("SIZE DIFFERENCE: " + f.Relative); }
        }
        Console.WriteLine($"TREE: {plan.Files.Count} expected paths; {missing} missing; {changedSize} different sizes");
        var selected = plan.Files.Where(f => f.Relative.StartsWith("golf\\")).OrderByDescending(f => f.Length).Take(3)
            .Concat(plan.Files.Where(f => f.Relative.StartsWith("lib\\")).Take(1))
            .Concat(plan.Files.Where(f => f.Relative.StartsWith("pg_backup\\")).Take(1));
        foreach (var f in selected)
        {
            using var src = image.FileSystems[f.Partition].OpenFile(f.Source, FileMode.Open, FileAccess.Read);
            using var local = File.OpenRead(Path.Combine(args[2], f.Relative));
            int size = (int)Math.Min(65536, f.Length);
            foreach (long offset in new[] { 0L, (f.Length - size) / 2, f.Length - size }.Distinct())
            {
                byte[] a = new byte[size], b = new byte[size]; src.Position = local.Position = offset;
                src.ReadExactly(a); local.ReadExactly(b);
                if (!a.SequenceEqual(b)) throw new Exception($"Sample mismatch: {f.Relative} at {offset}");
            }
            Console.WriteLine($"MATCH first/middle/last samples: {f.Relative} ({f.Length:N0} bytes total)");
        }
        return 0;
    }
    if (args[0] == "extract") Extractor.Run(image, plan, args[1], args[2], p => Console.WriteLine($"{p.Stage}: {p.Message} {p.Done}/{p.Total}"), CancellationToken.None);
    else if (args[0] != "inspect") throw new ArgumentException("Unknown command");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
