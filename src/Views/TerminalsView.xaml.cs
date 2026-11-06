using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Maison.Controls;
using Maison.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Maison.Views;

public partial class TerminalsView : UserControl
{
    public IShell Shell { get; set; } = null!;

    /// <summary>Working folder for a plain PowerShell tab.</summary>
    public Func<string> WorkDirProvider { get; set; } = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public event Action? SessionsChanged;

    private readonly List<PopOutWindow> _popouts = [];

    public TerminalsView()
    {
        InitializeComponent();
        UpdateEmpty();
        TerminalHub.Changed += () => Dispatcher.BeginInvoke(SyncTabs);
        StatsService.Updated += s => Dispatcher.BeginInvoke(() => ShowStats(s));
    }

    public int Count => Tabs.Items.Count;
    public int RunningCount => TerminalHub.Sessions.Count(s => s.IsRunning);
    public IEnumerable<TerminalViewer> Viewers => Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<TerminalViewer>();
    public TerminalViewer? Current => (Tabs.SelectedItem as TabItem)?.Content as TerminalViewer;

    public static SymbolRegular IconFor(string kind) => kind switch
    {
        "claude" => SymbolRegular.Sparkle24,
        "qwen" => SymbolRegular.Code24,
        "chat" => SymbolRegular.Chat24,
        _ => SymbolRegular.WindowConsole20,
    };

    /// <summary>Starts a session and shows it in a new, selected tab.</summary>
    public async Task<TerminalViewer> OpenAsync(Launcher.Session spec, string kind)
    {
        var session = TerminalHub.Create(spec, kind);
        return await AddTabAsync(session, select: true);
    }

    private TabItem? TabFor(TermSession s) =>
        Tabs.Items.OfType<TabItem>().FirstOrDefault(t => (t.Content as TerminalViewer)?.Session == s);

    /// <summary>Adds tabs for sessions created elsewhere (the web portal) and refreshes tab states.</summary>
    private void SyncTabs()
    {
        foreach (var s in TerminalHub.Sessions)
            if (TabFor(s) is null) _ = AddTabAsync(s, select: Tabs.Items.Count == 0);
        foreach (var t in Tabs.Items.OfType<TabItem>().ToList())
            if (t.Content is TerminalViewer v && !TerminalHub.Sessions.Contains(v.Session)) RemoveTab(t);
        foreach (var t in Tabs.Items.OfType<TabItem>()) (t.Tag as Action)?.Invoke();
        SessionsChanged?.Invoke();
    }

    private async Task<TerminalViewer> AddTabAsync(TermSession session, bool select)
    {
        var existing = TabFor(session);
        if (existing is not null) { if (select) Tabs.SelectedItem = existing; return (TerminalViewer)existing.Content; }

        var spec = session.Spec;
        var viewer = new TerminalViewer(session);
        var title = new TextBlock { Text = spec.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        var idleDot = new Ellipse
        {
            Width = 8, Height = 8, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)FindResource("BrandGradient"), Visibility = Visibility.Collapsed,
            ToolTip = "Idle: the agent stopped producing output and is probably waiting for you",
        };
        var close = new Button
        {
            Icon = new SymbolIcon(SymbolRegular.Dismiss16), Appearance = ControlAppearance.Transparent,
            Padding = new Thickness(4), Margin = new Thickness(8, 0, -4, 0), BorderThickness = new Thickness(0),
            ToolTip = "End this session and close the tab",
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new SymbolIcon(IconFor(session.Kind)) { Margin = new Thickness(0, 0, 6, 0), FontSize = 14 });
        header.Children.Add(title);
        header.Children.Add(idleDot);
        header.Children.Add(close);

        var tab = new TabItem { Header = header, Content = viewer, ToolTip = $"{spec.Title}\n{spec.WorkDir}" };
        string lastTip = "";
        // Refresh only what changed: re-laying out the tab strip on every title tick made the terminal jitter.
        tab.Tag = (Action)(() =>
        {
            var text = session.IsRunning ? spec.Title : spec.Title + " (ended)";
            if (title.Text != text) title.Text = text;
            var op = session.IsRunning ? 1.0 : 0.6;
            if (title.Opacity != op) title.Opacity = op;
            var vis = session.IsRunning && session.IsIdle ? Visibility.Visible : Visibility.Collapsed;
            if (idleDot.Visibility != vis) idleDot.Visibility = vis;
            var tip = $"{spec.Title}\n{spec.WorkDir}" + (session.IsIdle ? "\nIdle — waiting for you" : "");
            if (tip != lastTip) { tab.ToolTip = tip; lastTip = tip; }
        });
        close.Click += (_, _) => CloseTab(tab);

        Tabs.Items.Add(tab);
        if (select) Tabs.SelectedItem = tab;
        UpdateEmpty();
        try { await viewer.StartAsync(); }
        catch (Exception ex)
        {
            Shell.Toast("Terminal could not start", ex.Message + "\nIs the Microsoft Edge WebView2 Runtime installed?", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            CloseTab(tab);
        }
        SessionsChanged?.Invoke();
        return viewer;
    }

    /// <summary>Ends the session (and its mirrors) and removes the tab.</summary>
    public void CloseTab(TabItem tab)
    {
        if (tab.Content is TerminalViewer v)
        {
            foreach (var w in _popouts.Where(p => p.Session == v.Session).ToList()) w.Close();
            TerminalHub.Remove(v.Session);
        }
        RemoveTab(tab);
    }

    private void RemoveTab(TabItem tab)
    {
        if (tab.Content is TerminalViewer v) v.Dispose();
        Tabs.Items.Remove(tab);
        UpdateEmpty();
        SessionsChanged?.Invoke();
    }

    public void CloseAll()
    {
        foreach (var w in _popouts.ToList()) w.Close();
        foreach (var t in Tabs.Items.OfType<TabItem>().ToList()) CloseTab(t);
        foreach (var s in TerminalHub.Sessions) TerminalHub.Remove(s);
    }

    public void Select(TermSession s)
    {
        if (TabFor(s) is { } t) Tabs.SelectedItem = t;
    }

    public void ApplyTheme()
    {
        foreach (var v in Viewers) v.ApplyTheme();
        foreach (var w in _popouts) w.ApplyTheme();
    }

    private void UpdateEmpty()
    {
        bool empty = Tabs.Items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Tabs.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        BtnCloseTab.IsEnabled = BtnPopOut.IsEnabled = !empty;
    }

    private void ShowStats(StatsSnapshot s)
    {
        if (!IsVisible) return;
        var g = s.Gpu;
        SGpu.Text = g is null ? "—" : $"{g.LoadPercent}%";
        STemp.Text = g?.TempC is { } t ? $"{t:0}°C" : "—";
        SVram.Text = g is null ? "—" : $"{g.UsedGb:0.0} / {g.TotalGb:0} GB";
        SCpu.Text = $"{s.CpuPercent:0}%";
        SRam.Text = $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0} GB";
        SOllama.Text = $"{s.OllamaCpuPercent:0}% CPU · {s.OllamaRamGb:0.0} GB RAM";
        var warn = HealthAdvice.VramWarning(s);
        SWarnPill.Visibility = warn is null ? Visibility.Collapsed : Visibility.Visible;
        SWarn.Text = warn ?? "";
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs) Dispatcher.BeginInvoke(() => Current?.FocusTerminal(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is TabItem t) CloseTab(t);
    }

    private void PopOut_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } v) return;
        var w = new PopOutWindow(v.Session) { Owner = null };
        w.Closed += (_, _) => _popouts.Remove(w);
        _popouts.Add(w);
        w.Show();
        Shell.Log($"Mirrored {v.Session.Spec.Title} in a separate window");
    }

    private async void NewShell_Click(object sender, RoutedEventArgs e)
    {
        var dir = WorkDirProvider();
        await OpenAsync(new Launcher.Session("PowerShell", dir, null, null, "Get-Location | Out-Null"), "shell");
    }
}
