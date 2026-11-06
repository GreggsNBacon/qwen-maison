using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Maison.Models;
using Maison.Services;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Maison.Views;

public partial class SettingsView : UserControl
{
    public IShell Shell { get; set; } = null!;
    private readonly AppConfig _c = AppConfig.Current;
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        BuildSwatches();
    }

    public void Refresh()
    {
        _loading = true;
        FontSizeSlider.Value = _c.TerminalFontSize;
        FontSizeText.Text = _c.TerminalFontSize.ToString();
        IdleNotify.IsChecked = _c.IdleNotify;
        IdleSeconds.Value = _c.IdleSeconds;
        IdleSound.IsChecked = _c.IdleSound;
        StaleDays.Value = _c.StaleDays;
        Port.Value = _c.PortalPort;
        AllowInternet.IsChecked = _c.PortalAllowInternet;
        PortalOn.IsChecked = WebPortal.IsRunning;
        ModelsDir.Text = ModelStorage.Current;
        var disk = StatsService.Disk(ModelStorage.Current);
        ModelsDirSub.Text = disk is { } d ? $"{d.FreeGb:N0} GB free of {d.TotalGb:N0} GB on {d.Drive}" : "";
        _loading = false;
        BuildSwatches();
        RefreshPortal();
    }

    // ------------------------------------------------------------------ appearance

    private void BuildSwatches()
    {
        AccentPanel.Children.Clear();
        foreach (var (name, hex) in Theming.Accents)
        {
            var color = string.IsNullOrEmpty(hex) ? SystemParameters.WindowGlassColor : (Color)ColorConverter.ConvertFromString(hex);
            var selected = _c.Accent == name;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 8, 0) });
            content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var b = new Wpf.Ui.Controls.Button
            {
                Content = content, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 7, 14, 7),
                Appearance = selected ? ControlAppearance.Primary : ControlAppearance.Secondary,
                ToolTip = string.IsNullOrEmpty(hex) ? "Follow your Windows accent colour" : $"{name} ({hex})",
            };
            b.Click += (_, _) => { _c.Accent = name; _c.Save(); Theming.ApplyAccent(name); BuildSwatches(); };
            AccentPanel.Children.Add(b);
        }

        ThemePanel.Children.Clear();
        foreach (var (name, t) in TerminalPage.Themes)
        {
            var sample = new Border
            {
                Width = 150, Height = 54, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 6),
                Background = Brush(t["background"]),
                BorderBrush = _c.TerminalTheme == name ? (Brush)FindResource("BrandGradient") : Brush("#40ffffff"),
                BorderThickness = new Thickness(_c.TerminalTheme == name ? 2 : 1),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = Brush(t["foreground"]), FontFamily = new FontFamily("Cascadia Mono, Consolas") });
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var k in new[] { "red", "green", "yellow", "blue", "magenta", "cyan" })
                dots.Children.Add(new Rectangle { Width = 14, Height = 10, Margin = new Thickness(0, 0, 4, 0), Fill = Brush(t[k]), RadiusX = 2, RadiusY = 2 });
            sp.Children.Add(dots);
            sample.Child = sp;
            var b = new Button
            {
                Content = sample, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(0), BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = $"Use the {name} terminal colours",
            };
            b.Click += (_, _) => { _c.TerminalTheme = name; _c.Save(); BuildSwatches(); (Window.GetWindow(this) as MainWindow)?.ApplyTerminalTheme(); };
            ThemePanel.Children.Add(b);
        }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private void FontSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || FontSizeText is null) return;
        _c.TerminalFontSize = (int)FontSizeSlider.Value;
        FontSizeText.Text = _c.TerminalFontSize.ToString();
        _c.Save();
        (Window.GetWindow(this) as MainWindow)?.ApplyTerminalTheme();
    }

    // ------------------------------------------------------------------ notifications / models

    private void Notify_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _c.IdleNotify = IdleNotify.IsChecked == true;
        _c.IdleSeconds = (int)(IdleSeconds.Value ?? 8);
        _c.IdleSound = IdleSound.IsChecked == true;
        _c.Save();
    }

    private void Stale_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _c.StaleDays = (int)(StaleDays.Value ?? 14);
        _c.Save();
    }

    private void OpenModels_Click(object sender, RoutedEventArgs e)
    {
        var dir = ModelStorage.Current;
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private async void MoveModels_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the new model folder" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var target = dlg.FolderName;
        if (TerminalHub.Sessions.Any(s => s.IsRunning) &&
            !await Shell.ConfirmAsync("Sessions are running", "Moving models restarts Ollama; running agents will lose their connection for a moment.", "Continue"))
            return;
        if (!await Shell.ConfirmAsync("Move models", $"Move every model from\n{ModelStorage.Current}\nto\n{target}\n\nOllama is stopped while the files move.", "Move"))
            return;
        BtnMoveModels.IsEnabled = false;
        MoveLog.Visibility = Visibility.Visible;
        MoveLog.Clear();
        void Log(string s) => Dispatcher.Invoke(() => { MoveLog.AppendText(s + Environment.NewLine); MoveLog.ScrollToEnd(); });
        try
        {
            await Task.Run(() => ModelStorage.MoveAsync(target, moveExisting: true, Log));
            Shell.Toast("Models moved", target, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
            Shell.Log("Model folder moved to " + target);
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex.Message);
            Shell.Toast("Move failed", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
        }
        finally
        {
            BtnMoveModels.IsEnabled = true;
            Refresh();
            await Shell.OnModelsChangedAsync();
        }
    }

    // ------------------------------------------------------------------ portal

    private void RefreshPortal()
    {
        PwState.Text = PortalAuth.HasPassword ? "A password is set." : "No password yet — required before the portal can start.";
        PortalState.Text = WebPortal.IsRunning ? $"Running on port {WebPortal.Port}" : "Off";
        PortalInfo.Visibility = WebPortal.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        UrlList.ItemsSource = WebPortal.IsRunning ? WebPortal.Urls : [];
        CertText.Text = WebPortal.IsRunning
            ? $"Your phone will warn that the certificate isn't trusted — maison made it itself on this PC. Check the fingerprint starts with {WebPortal.Fingerprint} and continue. " +
              "To reach maison away from home, use a VPN such as Tailscale rather than opening the port on your router."
            : "";
        List<PortalDevice> devices;
        lock (_c.PortalDevices) devices = _c.PortalDevices.OrderByDescending(d => d.LastSeen).ToList();
        DeviceList.ItemsSource = devices;
        NoDevices.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        var a = Pw1.Password ?? "";
        var b = Pw2.Password ?? "";
        if (a.Length < 8) { Shell.Toast("Password too short", "Use at least 8 characters.", ControlAppearance.Caution, SymbolRegular.Warning24); return; }
        if (a != b) { Shell.Toast("Passwords don't match", "Type the same password twice.", ControlAppearance.Caution, SymbolRegular.Warning24); return; }
        PortalAuth.SetPassword(a);
        Pw1.Password = Pw2.Password = "";
        Shell.Toast("Password set", "Every device will need to sign in with the new password.", ControlAppearance.Success, SymbolRegular.Key24);
        RefreshPortal();
    }

    private async void PortalOn_Click(object sender, RoutedEventArgs e)
    {
        if (PortalOn.IsChecked == true)
        {
            if (!PortalAuth.HasPassword)
            {
                PortalOn.IsChecked = false;
                Shell.Toast("Set a password first", "The portal needs a password before it can start.", ControlAppearance.Caution, SymbolRegular.Key24);
                return;
            }
            try
            {
                await WebPortal.StartAsync();
                _c.PortalEnabled = true;
                Shell.Log("Web portal on: " + string.Join(", ", WebPortal.Urls));
            }
            catch (Exception ex)
            {
                PortalOn.IsChecked = false;
                Shell.Toast("Portal could not start", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }
        else
        {
            await WebPortal.StopAsync();
            _c.PortalEnabled = false;
            Shell.Log("Web portal off");
        }
        _c.Save();
        RefreshPortal();
    }

    private async void Port_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || Port.Value is not { } v) return;
        _c.PortalPort = (int)v;
        _c.Save();
        if (WebPortal.IsRunning) { await WebPortal.StopAsync(); try { await WebPortal.StartAsync(); } catch (Exception ex) { Shell.Toast("Portal could not restart", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); } }
        RefreshPortal();
    }

    private void RandomPort_Click(object sender, RoutedEventArgs e) => Port.Value = Random.Shared.Next(20000, 60000);

    private async void AllowInternet_Click(object sender, RoutedEventArgs e)
    {
        if (AllowInternet.IsChecked == true &&
            !await Shell.ConfirmAsync("Allow internet connections?",
                "Anyone on the internet who finds the port could try your password. Only turn this on if you've forwarded the port on your router on purpose. " +
                "A VPN like Tailscale is the safer way to reach maison from outside.", "Allow"))
        {
            AllowInternet.IsChecked = false;
            return;
        }
        _c.PortalAllowInternet = AllowInternet.IsChecked == true;
        _c.Save();
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) { Clipboard.SetText(url); Shell.Toast("Copied", url, ControlAppearance.Success, SymbolRegular.Copy24); }
    }

    private void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PortalDevice d }) { PortalAuth.Revoke(d); RefreshPortal(); Shell.Log($"Web portal: signed out {d.Name}"); }
    }
}
