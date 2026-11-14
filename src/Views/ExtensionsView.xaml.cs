using System.Windows;
using System.Windows.Controls;
using Maison.Services;
using Wpf.Ui.Controls;

namespace Maison.Views;

public partial class ExtensionsView : UserControl
{
    public IShell Shell { get; set; } = null!;
    private readonly List<MarketplaceInfo> _markets = [.. QwenExtensions.Defaults];
    private List<MarketPlugin> _plugins = [];
    private bool _loaded;

    public ExtensionsView()
    {
        InitializeComponent();
        Markets.ItemsSource = _markets;
    }

    /// <summary>One installed extension as the list shows it.</summary>
    private sealed class ExtRow
    {
        public string Name { get; init; } = "";
        public string Title { get; init; } = "";
        public string Badge { get; init; } = "";
        public System.Windows.Media.Brush BadgeBrush { get; init; } = System.Windows.Media.Brushes.Gray;
        public string Detail { get; init; } = "";
        public string Contents { get; init; } = "";
        public string ContentsFull { get; init; } = "";
        public string Source { get; init; } = "";
        public Visibility UpdateVisibility { get; init; } = Visibility.Collapsed;
    }

    private static string Names(List<string> items, int show)
        => items.Count <= show ? string.Join(", ", items) : string.Join(", ", items.Take(show)) + $" +{items.Count - show} more";

    private static ExtRow ToRow(QwenExtensions.ExtensionInfo e)
    {
        var parts = new List<string>();
        var full = new List<string>();
        void Add(string label, List<string> items)
        {
            if (items.Count == 0) return;
            parts.Add($"{label} ({items.Count}): {Names(items, 6)}");
            full.Add($"{label}: {string.Join(", ", items)}");
        }
        Add("Skills", e.Skills); Add("Commands", e.Commands); Add("MCP tools", e.Mcp); Add("Agents", e.Agents);
        var newer = e.NewerVersion is { Length: > 0 };
        var (badge, brush) = !e.Enabled ? ("disabled", System.Windows.Media.Brushes.Gray)
            : newer ? ($"update available: {e.NewerVersion}", System.Windows.Media.Brushes.Orange)
            : e.NewerVersion is null ? ("active", System.Windows.Media.Brushes.LightGreen)
            : ("active · up to date", System.Windows.Media.Brushes.LightGreen);
        return new ExtRow
        {
            Name = e.Name,
            Title = e.Version.Length > 0 ? $"{e.Name} {e.Version}" : e.Name,
            Badge = badge,
            BadgeBrush = brush,
            Detail = e.Description,
            Contents = parts.Count > 0 ? string.Join("  ·  ", parts) : "No skills, commands or tools of its own (language support or context only).",
            ContentsFull = string.Join("\n", full),
            Source = e.Source + (e.Type.Length > 0 ? $"  ({e.Type})" : ""),
            UpdateVisibility = newer ? Visibility.Visible : Visibility.Collapsed,
        };
    }

    private void ShowWebTools()
    {
        var lines = new List<string>();
        var cfg = Maison.Models.AppConfig.Current.Launch;
        var servers = Launcher.QwenMcpServers(cfg);
        lines.Add(servers.ContainsKey("maison-browser")
            ? "Browser: on. Qwen Code has its own hidden Edge window (maison-browser), separate from yours."
            : !cfg.QwenBrowser ? "Browser: off (Settings → Start commands → Qwen Code)."
            : SystemProbe.FindOnPath("npx") is null ? "Browser: needs Node.js, which isn't installed (winget install OpenJS.NodeJS.LTS)."
            : "Browser: needs Microsoft Edge, which wasn't found.");
        lines.Add(servers.ContainsKey("maison-reader")
            ? "Page reader: on (maison-reader). Fetches pages and online files as text, no JavaScript."
            : "Page reader: off (Settings → Start commands → Qwen Code).");
        WebTools.Text = string.Join(Environment.NewLine, lines);
    }

    public async Task RefreshAsync()
    {
        ShowWebTools();
        // Marketplaces already added to Qwen Code come back every time, not only the ones added in this session.
        var selected = Markets.SelectedItem as MarketplaceInfo;
        foreach (var m in QwenExtensions.SavedSources())
            if (_markets.All(x => !x.Repo.Equals(m.Repo, StringComparison.OrdinalIgnoreCase))) _markets.Add(m);
        Markets.Items.Refresh();
        if (selected is not null) Markets.SelectedItem = _markets.FirstOrDefault(x => x.Repo == selected.Repo);

        if (SystemProbe.FindOnPath("qwen") is null)
        {
            InstalledNote.Text = "Qwen Code isn't installed. Install it on the Setup page first.";
            Installed.ItemsSource = null;
            return;
        }
        InstalledNote.Text = "Loading…";
        try
        {
            var list = await QwenExtensions.InstalledAsync();
            Installed.ItemsSource = list.Select(ToRow).ToList();
            InstalledNote.Text = list.Count == 0
                ? "Nothing installed yet. Pick something from a marketplace below."
                : $"{list.Count(e => e.Enabled)} active of {list.Count}. Checking for updates…";
            await Task.WhenAll(list.Select(QwenExtensions.CheckUpdateAsync));
            Installed.ItemsSource = list.Select(ToRow).ToList();
            var n = list.Count(e => e.NewerVersion is { Length: > 0 });
            var checkedCount = list.Count(e => e.NewerVersion is not null);
            InstalledNote.Text = list.Count == 0 ? InstalledNote.Text
                : $"{list.Count(e => e.Enabled)} active of {list.Count}. " + (n > 0 ? $"{n} can be updated."
                    : checkedCount == 0 ? "Couldn't check for updates from here; Update all covers everything."
                    : $"{checkedCount} checked and up to date. The other {list.Count - checkedCount} can't be checked from here; Update all covers them.");
        }
        catch (Exception ex) { InstalledNote.Text = "Could not read extensions: " + ex.Message; }
        if (!_loaded) { _loaded = true; Markets.SelectedIndex = 0; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Markets_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Markets.SelectedItem is not MarketplaceInfo m) return;
        MarketDesc.Text = $"{m.Description}  ·  github.com/{m.Repo}";
        PluginList.ItemsSource = null;
        try
        {
            _plugins = await QwenExtensions.BrowseAsync(m.Repo);
            ApplyFilter();
            if (_plugins.Count == 0) MarketDesc.Text += "  ·  no plugins listed";
        }
        catch (Exception ex)
        {
            _plugins = [];
            MarketDesc.Text = $"Could not load github.com/{m.Repo}: {ex.Message}";
        }
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = Filter.Text.Trim();
        PluginList.ItemsSource = f.Length == 0
            ? _plugins
            : _plugins.Where(p => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Description.Contains(f, StringComparison.OrdinalIgnoreCase)
                                  || p.Category.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async void AddMarket_Click(object sender, RoutedEventArgs e)
    {
        var repo = NewMarket.Text.Trim().Replace("https://github.com/", "").Trim('/');
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$"))
        {
            Shell.Toast("Use owner/repo", "For example anthropics/skills", ControlAppearance.Caution, SymbolRegular.Warning24);
            return;
        }
        if (_markets.All(m => !m.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)))
        {
            _markets.Add(new MarketplaceInfo(repo, repo, "Added by you.", "claude"));
            Markets.Items.Refresh();
        }
        Markets.SelectedItem = _markets.First(m => m.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));
        try
        {
            var (code, output) = await QwenExtensions.AddSourceAsync(repo);
            Shell.Log($"qwen extensions sources add {repo}: {(code == 0 ? "ok" : output)}");
            if (code != 0)
            {
                _markets.RemoveAll(m => m.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));
                Markets.Items.Refresh();
                Markets.SelectedIndex = 0;
                Shell.Toast("Couldn't add it", output.Length > 200 ? output[..200] : output, ControlAppearance.Caution, SymbolRegular.Warning24);
            }
        }
        catch (Exception ex) { Shell.Log("Adding marketplace source failed: " + ex.Message); }
        NewMarket.Text = "";
        await RefreshAsync();
    }

    private async void InstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MarketPlugin p }) return;
        if (!await Shell.ConfirmAsync($"Install {p.Name}?",
                $"From github.com/{p.Marketplace}\n\n{p.Description}\n\nExtensions can run code on this PC. It installs in a terminal tab where you can review and confirm.", "Install"))
            return;
        await Shell.RunInTerminalAsync($"Install {p.Name}", $"qwen extensions install {Launcher.Q(QwenExtensions.InstallSource(p))}");
    }

    private async void InstallCustom_Click(object sender, RoutedEventArgs e)
    {
        var src = CustomSource.Text.Trim();
        if (src.Length == 0) return;
        await Shell.RunInTerminalAsync("Install extension", $"qwen extensions install {Launcher.Q(src)}");
        CustomSource.Text = "";
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        if (!await Shell.ConfirmAsync("Uninstall extension", $"Remove '{name}' from Qwen Code?", "Uninstall")) return;
        await Shell.RunInTerminalAsync($"Uninstall {name}", $"qwen extensions uninstall {Launcher.Q(name)}");
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        await Shell.RunInTerminalAsync($"Update {name}", $"qwen extensions update {Launcher.Q(name)}");
    }

    private async void UpdateAll_Click(object sender, RoutedEventArgs e)
        => await Shell.RunInTerminalAsync("Update extensions", "qwen extensions update --all");
}
