using System.Windows;
using System.Windows.Controls;
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

    public TerminalsView()
    {
        InitializeComponent();
        UpdateEmpty();
    }

    public int Count => Tabs.Items.Count;
    public int RunningCount => Sessions.Count(s => s.IsRunning);
    public IEnumerable<TerminalSession> Sessions => Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<TerminalSession>();
    public TerminalSession? Current => (Tabs.SelectedItem as TabItem)?.Content as TerminalSession;

    /// <summary>Opens a session in a new tab and selects it.</summary>
    public async Task<TerminalSession> OpenAsync(Launcher.Session spec, SymbolRegular icon)
    {
        var session = new TerminalSession(spec);
        var title = new TextBlock { Text = spec.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button
        {
            Icon = new SymbolIcon(SymbolRegular.Dismiss16), Appearance = ControlAppearance.Transparent,
            Padding = new Thickness(4), Margin = new Thickness(8, 0, -4, 0), BorderThickness = new Thickness(0),
            ToolTip = "End this session and close the tab",
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new SymbolIcon(icon) { Margin = new Thickness(0, 0, 6, 0), FontSize = 14 });
        header.Children.Add(title);
        header.Children.Add(close);

        var tab = new TabItem { Header = header, Content = session, ToolTip = $"{spec.Title}\n{spec.WorkDir}" };
        close.Click += (_, _) => CloseTab(tab);
        session.StateChanged += s =>
        {
            title.Text = s.IsRunning ? spec.Title : spec.Title + " (ended)";
            title.Opacity = s.IsRunning ? 1 : 0.6;
            if (!string.IsNullOrWhiteSpace(s.ShellTitle)) tab.ToolTip = $"{spec.Title}\n{s.ShellTitle}\n{spec.WorkDir}";
            SessionsChanged?.Invoke();
        };

        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateEmpty();
        try { await session.StartAsync(); }
        catch (Exception ex)
        {
            Shell.Toast("Terminal could not start", ex.Message + "\nIs the Microsoft Edge WebView2 Runtime installed?", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            CloseTab(tab);
        }
        SessionsChanged?.Invoke();
        return session;
    }

    public void CloseTab(TabItem tab)
    {
        if (tab.Content is TerminalSession s) s.Dispose();
        Tabs.Items.Remove(tab);
        UpdateEmpty();
        SessionsChanged?.Invoke();
    }

    public void CloseAll()
    {
        foreach (var t in Tabs.Items.OfType<TabItem>().ToList()) CloseTab(t);
    }

    private void UpdateEmpty()
    {
        bool empty = Tabs.Items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Tabs.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        BtnCloseTab.IsEnabled = BtnPopOut.IsEnabled = !empty;
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
        if (Current is not { } s) return;
        try { Launcher.OpenExternal(s.Spec, useWindowsTerminal: true); Shell.Log($"Opened {s.Spec.Title} in an external window"); }
        catch (Exception ex) { Shell.Toast("Could not open", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
    }

    private async void NewShell_Click(object sender, RoutedEventArgs e)
    {
        var dir = WorkDirProvider();
        await OpenAsync(new Launcher.Session("PowerShell", dir, null, null, "Get-Location | Out-Null"), SymbolRegular.WindowConsole20);
    }
}
