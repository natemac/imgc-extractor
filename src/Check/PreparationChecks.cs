using ImgcExtractor;

internal static class PreparationChecks
{
    public static void Run(string wrappedPath, string elfPath, string root)
    {
        root = Path.Combine(Path.GetFullPath(root), "prep-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        byte[] original = File.ReadAllBytes(wrappedPath);
        using var input = new MemoryStream(original);
        var plan = ExecutablePreparation.Inspect(input, CancellationToken.None);
        if (!plan.RequiresDecode || plan.AdditionalFinalBytes != new FileInfo(elfPath).Length) throw new Exception("Wrong preparation size estimate");
        using var expectedStream = File.OpenRead(elfPath); string expectedHash = Extractor.Hash(expectedStream);
        if (plan.OutputSha256 != expectedHash) throw new Exception("Decoded hash differs from known working file");
        int checks = 2;
        string Stage(string name) { string p = Path.Combine(root, name); Directory.CreateDirectory(p); File.WriteAllBytes(Path.Combine(p, "game.bin"), original); return p; }
        string good = Stage("good");
        var receipt = ExecutablePreparation.PrepareOwnedStaging(good, plan, CancellationToken.None);
        if (receipt.Status != "Prepared" || !receipt.OriginalPreserved || receipt.OutputSha256 != expectedHash) throw new Exception("Bad preparation receipt");
        using (var backup = File.OpenRead(Path.Combine(good, "game.bin.encrypted")))
            if (Extractor.Hash(backup) != plan.InputSha256) throw new Exception("Original was not preserved");
        using (var prepared = File.OpenRead(Path.Combine(good, "game.bin")))
        {
            if (Extractor.Hash(prepared) != expectedHash) throw new Exception("Prepared bytes incorrect");
            prepared.Position = 0;
            var elf = ExecutablePreparation.Inspect(prepared, CancellationToken.None);
            if (elf.Status != "AlreadyElf" || elf.AdditionalFinalBytes != 0) throw new Exception("Already-ELF detection failed");
        }
        checks += 4;
        string same = Stage("same-backup"); File.WriteAllBytes(Path.Combine(same, "game.bin.encrypted"), original);
        ExecutablePreparation.PrepareOwnedStaging(same, plan, CancellationToken.None); checks++;
        string collision = Stage("different-backup"); File.WriteAllText(Path.Combine(collision, "game.bin.encrypted"), "keep existing");
        try { ExecutablePreparation.PrepareOwnedStaging(collision, plan, CancellationToken.None); throw new Exception("Backup collision accepted"); }
        catch (PreparationException) { checks++; }
        if (File.ReadAllText(Path.Combine(collision, "game.bin.encrypted")) != "keep existing") throw new Exception("Backup was overwritten");
        checks++;
        byte[] corrupted = (byte[])original.Clone(); corrupted[^16] ^= 1;
        try { using var b = new MemoryStream(corrupted); ExecutablePreparation.Inspect(b, CancellationToken.None); throw new Exception("Corrupt executable accepted"); }
        catch (PreparationException) { checks++; }
        byte[] legacy = (byte[])original.Clone(); legacy[2] = 1; legacy[3] = 0;
        using (var old = new MemoryStream(legacy))
        {
            var unsupported = ExecutablePreparation.Inspect(old, CancellationToken.None);
            if (unsupported.Status != "Unsupported" || unsupported.AdditionalFinalBytes != 0) throw new Exception("Legacy executable mislabeled prepared");
            string only = Path.Combine(root, "extraction-only"); Directory.CreateDirectory(only); File.WriteAllBytes(Path.Combine(only, "game.bin"), legacy);
            if (ExecutablePreparation.PrepareOwnedStaging(only, unsupported, CancellationToken.None).Status != "Unsupported" || File.Exists(Path.Combine(only, "game.bin.encrypted"))) throw new Exception("Unsupported executable modified");
        }
        checks += 2;
        string cancelled = Stage("cancelled"); using var cts = new CancellationTokenSource(); cts.Cancel();
        try { ExecutablePreparation.PrepareOwnedStaging(cancelled, plan, cts.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { checks++; }
        if (File.Exists(Path.Combine(cancelled, "game.bin.encrypted"))) throw new Exception("Cancellation committed files");
        using (var unchanged = File.OpenRead(Path.Combine(cancelled, "game.bin")))
            if (Extractor.Hash(unchanged) != plan.InputSha256) throw new Exception("Cancelled original changed");
        checks++;
        Console.WriteLine($"PASS: {checks} executable preparation, preservation, collision, corruption, legacy, cancellation and size checks");
        Console.WriteLine($"Prepared SHA256: {expectedHash}; preserved SHA256: {plan.InputSha256}; additional final bytes: {plan.AdditionalFinalBytes}");
    }
}
