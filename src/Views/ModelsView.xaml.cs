using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Maison.Models;
using Maison.Services;
using Wpf.Ui.Controls;

namespace Maison.Views;

public partial class ModelsView : UserControl
{
    private readonly ObservableCollection<DownloadItem> _downloads = [];
    private List<ModelInfo> _models = [];
    private double _vramGb, _ramGb;
    private bool _gpuQueried;

    public IShell Shell { get; set; } = null!;

    public ModelsView()
    {
        InitializeComponent();
        DownloadList.ItemsSource = _downloads;
        _downloads.CollectionChanged += (_, _) => DownloadsCard.Visibility = _downloads.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool IsDownloading(string model) => _downloads.Any(d => d.Model == model);

    public async Task RefreshAsync()
    {
        if (!_gpuQueried)
        {
            var gpu = await SystemProbe.QueryGpuAsync();
            _vramGb = gpu?.TotalGb ?? 0;
            _ramGb = SystemProbe.RamGb;
            _gpuQueried = true;
        }

        bool running = await OllamaClient.GetVersionAsync(2) is not null;
        try { _models = running ? await OllamaClient.GetModelsAsync() : []; }
        catch (Exception ex) { _models = []; Shell.Log("Could not list models: " + ex.Message); }

        // Disk
        try
        {
            var dir = OllamaClient.ModelsDir;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
            DiskText.Text = $"{dir}  ·  {drive.AvailableFreeSpace / 1073741824.0:N0} GB free on {drive.Name.TrimEnd('\\')}"
                            + (running ? "" : "  ·  Ollama is not running");
        }
        catch { DiskText.Text = OllamaClient.ModelsDir; }

        // Installed
        var main = Shell.CurrentMainModel;
        var rows = _models.Select(m => InstalledRow.From(m, main, Shell.ModelLastUsed)).ToList();
        InstalledList.ItemsSource = rows;
        var stale = rows.Count(r => r.IsStale);
        InstalledHeader.Text = $"Installed ({rows.Count(r => !r.IsHelper)})" + (stale > 0 ? $"  ·  {stale} stale — unused for {AppConfig.Current.StaleDays}+ days, safe to uninstall if you don't need them" : "");
        InstalledEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Catalog
        var rec = ModelCatalog.Recommend(_vramGb, _ramGb);
        CatalogHint.Text = _vramGb > 0
            ? $"Fit is estimated for your GPU ({_vramGb:N0} GB) and {_ramGb:N0} GB RAM. ★ = recommended for this PC."
            : $"No NVIDIA GPU detected; models run on the CPU ({_ramGb:N0} GB RAM). ★ = recommended for this PC.";
        var names = _models.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CatalogList.ItemsSource = ModelCatalog.All.Select(c => new CatalogRow(c, ModelCatalog.FitFor(c, _vramGb, _ramGb),
            names.Contains(c.Tag), IsDownloading(c.Tag), c.Tag == rec.Tag)).ToList();
    }

    // ------------------------------------------------------------------ install

    public async Task InstallAsync(string model)
    {
        model = model.Trim();
        if (model.Length == 0) return;
        if (IsDownloading(model)) { Shell.Toast("Already downloading", model); return; }
        if (await OllamaClient.GetVersionAsync(2) is null)
        {
            Shell.Toast("Ollama is not running", "Start it from the Dashboard or run Setup first.", ControlAppearance.Caution, SymbolRegular.Warning24);
            return;
        }

        var cat = ModelCatalog.All.FirstOrDefault(c => c.Tag.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (cat is not null)
        {
            try
            {
                var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(OllamaClient.ModelsDir))!).AvailableFreeSpace / 1073741824.0;
                if (free < cat.SizeGb + 2 &&
                    !await Shell.ConfirmAsync("Low disk space", $"{model} needs about {cat.SizeGb:N0} GB but only {free:N0} GB is free. Try anyway?", "Download"))
                    return;
            }
            catch { /* unknown drive: just try */ }
        }

        var item = new DownloadItem { Model = model };
        _downloads.Add(item);
        Shell.Log($"Downloading {model}…");
        await RefreshAsync();

        long lastBytes = 0;
        var lastTime = Stopwatch.StartNew();
        double speed = 0;
        var progress = new Progress<PullProgress>(p =>
        {
            if (p.Total > 0)
            {
                item.Indeterminate = false;
                item.Percent = 100.0 * p.Completed / p.Total;
                if (lastTime.Elapsed.TotalSeconds >= 1)
                {
                    var inst = (p.Completed - lastBytes) / lastTime.Elapsed.TotalSeconds;
                    speed = speed == 0 ? inst : speed * 0.7 + inst * 0.3;
                    lastBytes = p.Completed;
                    lastTime.Restart();
                }
                var eta = speed > 0 ? TimeSpan.FromSeconds((p.Total - p.Completed) / speed) : (TimeSpan?)null;
                item.Detail = $"{Gb(p.Completed)} of {Gb(p.Total)}  ·  {speed / 1048576:N0} MB/s" + (eta is { } e ? $"  ·  {Eta(e)} left" : "");
            }
            else
            {
                item.Indeterminate = true;
                item.Detail = char.ToUpper(p.Status.FirstOrDefault()) + string.Concat(p.Status.Skip(1)) + "…";
            }
        });

        try
        {
            await OllamaClient.PullAsync(model, progress, item.Cancel.Token);
            Shell.Log($"Installed {model}");
            Shell.Toast("Model installed", model, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
            if (string.IsNullOrWhiteSpace(Shell.CurrentMainModel)) Shell.UseModel(model);
        }
        catch (OperationCanceledException) { Shell.Log($"Download of {model} cancelled (partial data kept; installing again resumes)"); }
        catch (Exception ex)
        {
            Shell.Log($"Download of {model} failed: {ex.Message}");
            Shell.Toast("Download failed", $"{model}: {ex.Message}", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
        }
        finally
        {
            _downloads.Remove(item);
            await RefreshAsync();
            await Shell.OnModelsChangedAsync();
        }
    }

    private static string Gb(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824.0:N1} GB" : $"{bytes / 1048576.0:N0} MB";
    private static string Eta(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.Seconds}s";

    // ------------------------------------------------------------------ handlers

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void PullByName_Click(object sender, RoutedEventArgs e) { var n = PullName.Text; PullName.Text = ""; await InstallAsync(n); }
    private async void PullName_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await InstallAsync(PullName.Text); }
    private async void CatalogInstall_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string t }) await InstallAsync(t); }
    private void CancelDownload_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: DownloadItem d }) d.Cancel.Cancel(); }
    private void Browse_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://ollama.com/library") { UseShellExecute = true });

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        Shell.UseModel(name);
        Shell.Toast("Main model set", $"{name} will be used for Claude Code, Qwen Code and Chat", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
        _ = RefreshAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        // Context variants share the weights; remove them too or no disk space is freed.
        var variants = _models.Where(m => Launcher.IsVariant(m.Name) && (m.ParentModel == name || m.Name.StartsWith(name + "-ctx"))).Select(m => m.Name).ToList();
        var text = $"Uninstall {name}?" + (variants.Count > 0 ? $"\n\nIts context variants will be removed too:\n{string.Join("\n", variants)}" : "")
                   + "\n\nYou can download it again any time.";
        if (!await Shell.ConfirmAsync("Delete model", text, "Delete")) return;
        try
        {
            foreach (var v in variants) await OllamaClient.DeleteAsync(v);
            await OllamaClient.DeleteAsync(name);
            Shell.Log($"Deleted {name}" + (variants.Count > 0 ? $" and {variants.Count} variant(s)" : ""));
            Shell.Toast("Model deleted", name, ControlAppearance.Success, SymbolRegular.Delete24);
        }
        catch (Exception ex) { Shell.Toast("Delete failed", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
        await RefreshAsync();
        await Shell.OnModelsChangedAsync();
    }

    // ------------------------------------------------------------------ rows

    private sealed class InstalledRow
    {
        public required string Name { get; init; }
        public required string Detail { get; init; }
        public string Badge { get; init; } = "";
        public Brush BadgeBrush { get; init; } = Palette.Grey;
        public Brush IconBrush { get; init; } = Palette.Blue;
        public bool HasBadge => Badge.Length > 0;
        public bool CanUse { get; init; }
        public bool IsHelper { get; init; }
        public bool IsStale { get; init; }
        public string? BadgeTip { get; init; }

        public static InstalledRow From(ModelInfo m, string main, IReadOnlyDictionary<string, DateTime> lastUsed)
        {
            bool alias = m.Name.StartsWith("claude-");
            bool variant = Launcher.IsVariant(m.Name);
            var bits = new List<string> { $"{m.SizeGb} GB" };
            if (m.ParameterSize.Length > 0) bits.Add(m.ParameterSize + " params");
            if (m.Quantization.Length > 0) bits.Add(m.Quantization);
            var caps = m.Capabilities.Where(c => c is "vision" or "tools" or "thinking").ToArray();
            if (caps.Length > 0) bits.Add(string.Join(", ", caps));
            if (m.Modified != default) bits.Add("added " + Ago(m.Modified));
            if (variant && m.ParentModel.Length > 0) bits.Add("context variant of " + m.ParentModel + " (no extra disk)");
            else if (alias) bits.Add("alias from the old setup (no extra disk)");

            // Usage: launched from maison or seen loaded in Ollama. Before tracking began, count from when it started.
            var cfg = AppConfig.Current;
            bool stale = false;
            string usage = "";
            if (!variant && !alias)
            {
                if (lastUsed.TryGetValue(m.Name, out var last))
                {
                    usage = "last used " + Ago(last.ToUniversalTime());
                    stale = (DateTime.Now - last).TotalDays > cfg.StaleDays;
                }
                else
                {
                    var since = new[] { m.Modified == default ? DateTime.MinValue : m.Modified.ToLocalTime(), cfg.UsageTrackingSince }.Max();
                    usage = "not used yet";
                    stale = (DateTime.Now - since).TotalDays > cfg.StaleDays;
                }
                bits.Insert(0, usage);
            }

            bool inUse = !variant && !alias && m.Name.Equals(main, StringComparison.OrdinalIgnoreCase);
            if (inUse) stale = false;
            return new InstalledRow
            {
                Name = m.Name,
                Detail = string.Join("  ·  ", bits),
                Badge = inUse ? "In use" : stale ? "Stale" : variant ? "Variant" : alias ? "Old alias" : "",
                BadgeBrush = inUse ? Palette.Green : stale ? Palette.Amber : Palette.Grey,
                BadgeTip = stale ? $"Not used for over {cfg.StaleDays} days ({usage}). Uninstall it to free {m.SizeGb} GB if you don't need it. Change the threshold in Settings." : null,
                IconBrush = variant || alias ? Palette.Grey : inUse ? Palette.Green : stale ? Palette.Amber : Palette.Blue,
                CanUse = !variant && !alias && !inUse,
                IsHelper = variant || alias,
                IsStale = stale,
            };
        }

        private static string Ago(DateTime t)
        {
            var d = DateTime.Now - t.ToLocalTime();
            return d.TotalMinutes < 60 ? "just now" : d.TotalHours < 24 ? $"{(int)d.TotalHours}h ago" : d.TotalDays < 60 ? $"{(int)d.TotalDays}d ago" : t.ToString("d MMM yyyy");
        }
    }

    private sealed class CatalogRow(CatalogModel m, Fit fit, bool installed, bool downloading, bool recommended)
    {
        public string Tag => m.Tag;
        public string Title => m.Title;
        public string SubTitle => $"{m.Tag}  ·  {m.SizeGb:0.#} GB download";
        public string Description => m.Description;
        public bool Recommended => recommended;
        public string FitText => ModelCatalog.FitText(fit);
        public Brush FitBrush => fit switch { Fit.Gpu => Palette.Green, Fit.Tight or Fit.Cpu => Palette.Amber, _ => Palette.Red };
        public bool CanInstall => !installed && !downloading;
        public string ButtonText => installed ? "Installed" : downloading ? "Downloading" : "Install";
        public string ButtonTip => installed ? "Already installed — manage it in the list above"
            : $"Download {m.Tag} ({m.SizeGb:0.#} GB). {ModelCatalog.FitText(fit)}.";
    }
}
