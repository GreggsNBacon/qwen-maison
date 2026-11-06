using System.Windows;
using Maison.Controls;
using Maison.Services;
using Wpf.Ui.Controls;

namespace Maison.Views;

/// <summary>
/// A separate window that mirrors a running session (same process, same screen, live in both places).
/// Typing in either window works; the window you type in sets the terminal size. Closing it keeps the session.
/// </summary>
public sealed class PopOutWindow : FluentWindow
{
    private readonly TerminalViewer _viewer;
    private readonly TermSession _session;
    private readonly TitleBar _titleBar;

    public PopOutWindow(TermSession session)
    {
        _session = session;
        Title = session.Spec.Title + " · maison";
        Width = 1100;
        Height = 720;
        MinWidth = 480;
        MinHeight = 300;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowCornerPreference = WindowCornerPreference.Round;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/brand/maison.ico"));

        var root = new System.Windows.Controls.Grid();
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        _titleBar = new TitleBar { Title = Title + "  ·  mirrored" };
        root.Children.Add(_titleBar);
        _viewer = new TerminalViewer(session) { Margin = new Thickness(6, 0, 6, 6) };
        System.Windows.Controls.Grid.SetRow(_viewer, 1);
        root.Children.Add(_viewer);
        Content = root;

        Loaded += async (_, _) => { await _viewer.StartAsync(); _viewer.FocusTerminal(); };
        Activated += (_, _) => _viewer.FocusTerminal();
        Closed += (_, _) => { _session.StateChanged -= OnState; _viewer.Dispose(); };
        _session.StateChanged += OnState;
    }

    public TermSession Session => _session;
    public TerminalViewer Viewer => _viewer;

    private void OnState(TermSession s) => Dispatcher.BeginInvoke(() =>
        _titleBar.Title = s.Spec.Title + (s.IsRunning ? s.IsIdle ? "  ·  idle" : "  ·  mirrored" : "  ·  ended"));

    public void ApplyTheme() => _viewer.ApplyTheme();
}
