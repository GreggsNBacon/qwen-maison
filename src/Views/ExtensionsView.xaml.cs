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

    public async Task RefreshAsync()
    {
        if (SystemProbe.FindOnPath("qwen") is null)
        {
            Installed.Text = "Qwen Code isn't installed. Install it on the Setup page first.";
            return;
        }
        Installed.Text = "Loading…";
        try
        {
            var (_, list) = await QwenExtensions.ListInstalledAsync();
            var (_, sources) = await QwenExtensions.ListSourcesAsync();
            Installed.Text = (string.IsNullOrWhiteSpace(list) ? "No extensions installed." : list) +
                             Environment.NewLine + Environment.NewLine + "Marketplace sources: " + (string.IsNullOrWhiteSpace(sources) ? "none" : sources);
        }
        catch (Exception ex) { Installed.Text = "Could not read extensions: " + ex.Message; }
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
        var name = ExtName.Text.Trim();
        if (name.Length == 0) return;
        if (!await Shell.ConfirmAsync("Uninstall extension", $"Remove '{name}' from Qwen Code?", "Uninstall")) return;
        await Shell.RunInTerminalAsync($"Uninstall {name}", $"qwen extensions uninstall {Launcher.Q(name)}");
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var name = ExtName.Text.Trim();
        await Shell.RunInTerminalAsync("Update extensions", name.Length == 0 ? "qwen extensions update --all" : $"qwen extensions update {Launcher.Q(name)}");
    }
}
