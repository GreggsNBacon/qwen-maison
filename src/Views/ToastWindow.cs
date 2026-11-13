using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Maison.Views;

/// <summary>
/// Hosts the in-app pop-up messages. Terminals are WebView2 (a native child window that WPF content can't draw over),
/// so messages shown inside the main window ended up behind them. A borderless, transparent window owned by maison sits
/// above its owner (and only its owner); its fully transparent areas don't take clicks, so it never blocks the app.
/// </summary>
public sealed class ToastWindow : Window
{
    private const double Height_ = 170;
    private readonly Window _owner;
    public SnackbarPresenter Presenter { get; } = new() { VerticalAlignment = VerticalAlignment.Bottom };

    public ToastWindow(Window owner)
    {
        _owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "maison notifications";
        Content = Presenter;
        Height = Height_;

        owner.ContentRendered += (_, _) => { Owner = owner; Show(); Follow(); };
        owner.LocationChanged += (_, _) => Follow();
        owner.SizeChanged += (_, _) => Follow();
        owner.StateChanged += (_, _) => { if (owner.WindowState == WindowState.Minimized) Hide(); else { Show(); Follow(); } };
        owner.Closed += (_, _) => Close();
    }

    /// <summary>Keep the strip over the bottom of maison's client area.</summary>
    public void Follow()
    {
        if (_owner.WindowState == WindowState.Minimized || !_owner.IsLoaded) return;
        if (_owner.Content is not FrameworkElement root || root.ActualWidth < 1) return;
        var topLeft = root.PointToScreen(new Point(0, 0));
        var bottomRight = root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight));
        // Screen pixels → WPF units for this monitor.
        var m = PresentationSource.FromVisual(root)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var tl = m.Transform(topLeft);
        var br = m.Transform(bottomRight);
        Left = tl.X;
        Width = Math.Max(100, br.X - tl.X);
        Top = br.Y - Height_;
    }
}
