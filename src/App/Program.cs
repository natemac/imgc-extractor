using ImgcExtractor;

namespace ImgcExtractorApp;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}

internal sealed class MainForm : Form
{
    private readonly TextBox source = new() { Dock = DockStyle.Fill };
    private readonly TextBox parent = new() { Dock = DockStyle.Fill };
    private readonly TextBox name = new() { Dock = DockStyle.Fill };
    private readonly Button browseSource = new() { Text = "Browse...", AutoSize = true };
    private readonly Button browseParent = new() { Text = "Browse...", AutoSize = true };
    private readonly Button inspect = new() { Text = "1. Inspect image", AutoSize = true, Padding = new Padding(12, 6, 12, 6) };
    private readonly Button extract = new() { Text = "2. Extract and prepare", AutoSize = true, Padding = new Padding(12, 6, 12, 6), Enabled = false };
    private readonly Button cancel = new() { Text = "Cancel", AutoSize = true, Padding = new Padding(8, 6, 8, 6), Enabled = false };
    private readonly Button open = new() { Text = "Open output", AutoSize = true, Padding = new Padding(8, 6, 8, 6), Enabled = false };
    private readonly Label status = new() { Text = "Choose an IMGC or IMG file to begin.", AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label summary = new() { Text = "Profile: merged content folder\nPartitions are identified by label, not partition number.", AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label space = new() { Text = "Disk space: inspect an image to calculate final size and check the selected drive.\nExtra temporary IMG space: 0 GiB. In-progress files become the final folder.", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.FromArgb(242, 246, 250) };
    private readonly ProgressBar bar = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 22 };
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.FromArgb(246, 247, 249), Font = new Font("Consolas", 9) };
    private GameImage? image;
    private ExtractionPlan? plan;
    private CancellationTokenSource? cts;
    private bool busy;
    private string? completed;
    private string lastStage = "";

    public MainForm(string? initial)
    {
        Text = "IMGC Extractor";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;
        Size = new Size(940, 870); MinimumSize = new Size(820, 820);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 3, RowCount = 13 };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 118)); layout.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.ColumnStyles.Add(new(SizeType.AutoSize));
        for (int i = 0; i < 13; i++) layout.RowStyles.Add(new(i == 11 ? SizeType.Percent : SizeType.AutoSize, i == 11 ? 100 : 0));
        void Wide(Control c, int row) { layout.Controls.Add(c, 0, row); layout.SetColumnSpan(c, 3); }
        Wide(new Label { Text = "IMGC to game folder", AutoSize = true, Font = new Font("Segoe UI Semibold", 23), Margin = new Padding(0, 0, 0, 8) }, 0);
        Wide(new Label { Text = "Extract directly on Windows. No Ubuntu, disk mounting, or intermediate IMG needed.", AutoSize = true, Margin = new Padding(0, 0, 0, 22) }, 1);
        void Field(string title, TextBox box, Button? button, int row)
        {
            layout.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 6, 9) }, 0, row);
            box.Margin = new Padding(0, 4, 8, 10); layout.Controls.Add(box, 1, row);
            if (button != null) layout.Controls.Add(button, 2, row); else layout.SetColumnSpan(box, 2);
        }
        Field("Source image", source, browseSource, 2); Field("Save inside", parent, browseParent, 3); Field("New folder", name, null, 4);
        summary.Margin = new Padding(0, 12, 0, 16); Wide(summary, 5);
        space.Margin = new Padding(0, 0, 0, 16); Wide(space, 6);
        Wide(new Label { Text = "Merge rule: static content takes precedence on matching paths.\nEvery copied file is checked with SHA-256. Existing folders are never overwritten.", AutoSize = true, ForeColor = Color.FromArgb(65, 71, 80), Margin = new Padding(0, 0, 0, 16) }, 7);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 16) };
        buttons.Controls.AddRange([inspect, extract, cancel, open]); Wide(buttons, 8);
        status.Margin = new Padding(0, 0, 0, 6); status.AutoEllipsis = true; Wide(status, 9);
        bar.Margin = new Padding(0, 0, 0, 12); Wide(bar, 10); Wide(details, 11);
        Wide(new Label { Text = "Extraction prepares files only. Playing a game requires a compatible Windows launcher/runtime.  •  v1.0.0", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 12, 0, 0), Font = new Font("Segoe UI", 9) }, 12);
        Controls.Add(layout);
        parent.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        name.Text = "Extracted Content";
        source.TextChanged += (_, _) => InvalidateImage();
        parent.TextChanged += (_, _) => { if (!busy) UpdateSpace(); };
        name.TextChanged += (_, _) => { if (!busy) UpdateSpace(); };
        browseSource.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Disk images (*.imgc;*.img)|*.imgc;*.img", Title = "Choose the disk image", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) SetSource(dialog.FileName);
        };
        browseParent.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose where to create the new game folder", UseDescriptionForTitle = true, SelectedPath = parent.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) parent.Text = dialog.SelectedPath;
        };
        inspect.Click += async (_, _) => await Inspect(); extract.Click += async (_, _) => await Extract();
        cancel.Click += (_, _) => { cts?.Cancel(); cancel.Enabled = false; status.Text = "Cancelling after the current read..."; };
        open.Click += (_, _) => { if (completed != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(completed) { UseShellExecute = true }); };
        FormClosing += (_, e) =>
        {
            if (busy) { e.Cancel = true; cts?.Cancel(); status.Text = "Cancelling safely. Close the window when the operation stops."; }
            else image?.Dispose();
        };
        AllowDrop = true;
        DragEnter += (_, e) => { if (!busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) => { if (!busy && e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1) SetSource(paths[0]); };
        if (!string.IsNullOrWhiteSpace(initial)) SetSource(initial);
    }
    private void SetSource(string value)
    {
        source.Text = value;
        string stem = Path.GetFileNameWithoutExtension(value);
        int shrink = stem.IndexOf(".shrink", StringComparison.OrdinalIgnoreCase);
        if (shrink >= 0) stem = stem[..shrink];
        name.Text = stem + " Extracted";
    }
    private void InvalidateImage() { if (busy) return; image?.Dispose(); image = null; plan = null; extract.Enabled = false; UpdateSpace(); }
    private string OutputPath()
    {
        string folder = name.Text.Trim(); ExtractionPlan.ValidateRelative(folder);
        if (folder.Contains('\\')) throw new IOException("New folder must be a single folder name.");
        return Path.Combine(Path.GetFullPath(parent.Text.Trim().Trim('"')), folder);
    }
    private void UpdateSpace()
    {
        if (plan == null)
        {
            space.ForeColor = Color.FromArgb(45, 55, 65);
            space.Text = completed != null ? "Output verified and saved. The space preflight is recorded in Extraction-report.json.\nNo intermediate IMG was created." : "Disk space: inspect an image to calculate final size and check the selected drive.\nExtra temporary IMG space: 0 GiB. In-progress files become the final folder.";
            extract.Enabled = false; return;
        }
        try
        {
            var s = ImgcExtractor.Extractor.EstimateDestination(OutputPath(), plan.FinalBytes, plan.FinalFileCount + plan.Directories.Count + 1);
            space.ForeColor = s.Enough ? Color.FromArgb(27, 93, 53) : Color.Firebrick;
            space.Text = $"Destination drive {s.Drive} ({s.Format}) — {s.FreeBytes / 1073741824.0:N2} GiB free\n" +
                $"Final data (including executable preparation): {s.FinalFileBytes / 1073741824.0:N2} GiB\n" +
                "Extra temporary space beyond final data: 0 GiB (no IMG; staging is renamed in place).\n" +
                $"Total required: {s.RequiredBytes / 1073741824.0:N2} GiB (includes allocation allowance + 1 GiB safety margin)\n" +
                (s.Enough ? "Enough space. In-progress files are renamed into the final folder; no second copy." : $"NOT ENOUGH SPACE — free another {(s.RequiredBytes - s.FreeBytes) / 1073741824.0:N2} GiB or choose another drive.");
            // Keep Extract available so a fresh click can recheck after space is freed
            // externally; CheckDestination blocks copying when space remains insufficient.
            extract.Enabled = !busy;
        }
        catch (Exception ex) { space.ForeColor = Color.Firebrick; space.Text = "Destination check: " + ex.Message; extract.Enabled = !busy; }
    }
    private void SetBusy(bool value)
    {
        busy = value; source.Enabled = parent.Enabled = name.Enabled = browseSource.Enabled = browseParent.Enabled = inspect.Enabled = !value;
        cancel.Enabled = value; extract.Enabled = !value && plan != null; open.Enabled = !value && completed != null;
        if (value) { cts = new(); lastStage = ""; } else { cts?.Dispose(); cts = null; UpdateSpace(); }
    }
    private void Append(string text) { details.AppendText(text + Environment.NewLine); }
    private void ShowProgress(ProgressUpdate p)
    {
        status.Text = p.Stage + ": " + p.Message;
        bar.Style = p.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
        if (p.Total > 0) bar.Value = (int)Math.Clamp(p.Done * 1000.0 / p.Total, 0, 1000);
        if (p.Stage != lastStage && p.Stage != "Verifying" && p.Stage != "Copying") { Append(status.Text); lastStage = p.Stage; }
    }
    private async Task Inspect()
    {
        InvalidateImage(); details.Clear(); completed = null; SetBusy(true);
        var progress = new Progress<ProgressUpdate>(ShowProgress);
        GameImage? candidate = null;
        try
        {
            string path = Path.GetFullPath(source.Text.Trim().Trim('"'));
            var token = cts!.Token;
            var result = await Task.Run(() =>
            {
                candidate = new GameImage(path, (message, ratio) => ((IProgress<ProgressUpdate>)progress).Report(new("Indexing", message, (long)(ratio * 1000), 1000)), token);
                ((IProgress<ProgressUpdate>)progress).Report(new("Inspecting", "Reading the game directory trees..."));
                return ExtractionPlan.Create(candidate, token, message => ((IProgress<ProgressUpdate>)progress).Report(new("Inspecting", message)));
            });
            image = candidate; plan = result;
            foreach (var part in image!.Partitions) Append($"Partition {part.Number}: {part.Label} ({part.Size / 1073741824.0:N2} GiB)");
            summary.Text = $"Ready: {plan.FinalFileCount:N0} final files • {plan.FinalBytes / 1073741824.0:N2} GiB of file data\n{plan.Collisions.Count:N0} static/dynamic collisions • {plan.Files.Count(f => f.MaterializedLink):N0} file links to materialize\n" + plan.Preparation.Message;
            extract.Text = plan.Preparation.Status == "Unsupported" ? "2. Extract only (unsupported)" : "2. Extract and prepare";
            Append(summary.Text); foreach (var c in plan.Collisions.Take(50)) Append("Static wins: " + c.Kept.Relative);
            status.Text = "Image inspected. Review space and executable status, then choose Extract.";
            bar.Style = ProgressBarStyle.Continuous; bar.Value = 1000;
        }
        catch (Exception ex) { candidate?.Dispose(); image = null; plan = null; Error(ex); }
        finally { SetBusy(false); }
    }
    private async Task Extract()
    {
        if (image == null || plan == null) return;
        try
        {
            string output = OutputPath();
            ImgcExtractor.Extractor.CheckDestination(output, plan.FinalBytes, plan.FinalFileCount + plan.Directories.Count + 1);
            string input = source.Text.Trim().Trim('"');
            SetBusy(true); var token = cts!.Token; var progress = new Progress<ProgressUpdate>(ShowProgress);
            completed = await Task.Run(() => ImgcExtractor.Extractor.Run(image, plan, input, output, p => ((IProgress<ProgressUpdate>)progress).Report(p), token));
            Append("Verified output: " + completed);
            Append("Details: Extraction-report.json and Extraction.log inside the output folder.");
            status.Text = plan.Preparation.Status == "Unsupported" ? "Extraction complete — executable NOT prepared (unsupported format)." : "Complete — files verified and executable prepared/already ELF.";
            bar.Style = ProgressBarStyle.Continuous; bar.Value = 1000;
            image.Dispose(); image = null; plan = null;
        }
        catch (Exception ex) { Error(ex); }
        finally { SetBusy(false); }
    }
    private void Error(Exception ex)
    {
        bar.Style = ProgressBarStyle.Continuous; bar.Value = 0;
        status.Text = ex is OperationCanceledException ? "Cancelled." : "Stopped — see details below.";
        Append(ex.Message);
        if (ex is not OperationCanceledException) MessageBox.Show(this, ex.Message, "Extraction stopped", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
