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
        LoadLaunch();
        AutoUpdateCheck.IsChecked = _c.CheckForUpdates;
        RefreshAutoStart();
        if (UpdateState.Text.Length == 0) UpdateState.Text = $"This is maison {AppInfo.Version}.";
        _loading = false;
        RefreshSharing();
        UpdatePreview();
        BuildSwatches();
        RefreshPortal();
    }

    // ------------------------------------------------------------------ AI pool

    /// <summary>Scrolls so the "AI pool" card starts at the top.</summary>
    public void ShowAiCard() => Dispatcher.BeginInvoke(() =>
    {
        var y = AiCard.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y;
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + y - 8);
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    private void RefreshSharing()
    {
        var s = AiSharing.Check();
        ShareAiToggle.IsChecked = _c.ShareAi;
        SharePanel.Visibility = _c.ShareAi ? Visibility.Visible : Visibility.Collapsed;
        PairCode.Text = _c.ShareAi ? Services.Pool.PoolHost.PairCode.Insert(3, " ") : "";
        PairedList.ItemsSource = _c.ShareAi ? _c.PoolClients.OrderByDescending(x => x.LastSeen).ToList() : null;
        NoPaired.Visibility = _c.ShareAi && _c.PoolClients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShareState.Text = !_c.ShareAi
            ? (s.OllamaExposed
                ? "Off in maison, but Ollama itself accepts connections from the network with no password (its \"Expose Ollama to the network\" setting or OLLAMA_HOST). Turn that off in Ollama, and share through maison instead."
                : "Off: only this PC can use its AI.")
            : s.Working ? "On: paired PCs at home can use this PC's AI."
            : "On, but " + string.Join(", ", new[]
              {
                  s.ServiceRunning ? null : "maison's pool service isn't running",
                  s.FirewallOpen ? null : "the firewall rule is missing",
              }.Where(x => x is not null)) + ". Turn it off and on again to fix.";

        UseRemoteToggle.IsChecked = _c.UseRemoteAi;
        IncludeThisToggle.IsChecked = _c.PoolIncludeThisPc;
        IncludeThisToggle.IsEnabled = SystemProbe.OllamaInstalled;
        var n = _c.PoolMembers.Count(m => m.Enabled);
        RemoteState.Text = !_c.UseRemoteAi
            ? "Off: this PC runs its own AI."
            : n == 0 && !_c.PoolIncludeThisPc ? "On, but no AI PCs are added yet. Open the Pool page to find them."
            : $"On: using {n} AI PC{(n == 1 ? "" : "s")}{(_c.PoolIncludeThisPc && SystemProbe.OllamaInstalled ? " and this PC" : "")}.";
    }

    private async void ShareAi_Click(object sender, RoutedEventArgs e)
    {
        bool on = ShareAiToggle.IsChecked == true;
        if (on && !SystemProbe.OllamaInstalled)
        {
            ShareAiToggle.IsChecked = false;
            ShareState.Text = "Ollama isn't installed on this PC, so there's nothing to share. Set it up on the Setup page first.";
            return;
        }
        ShareAiToggle.IsEnabled = false;
        try
        {
            bool ok; string message;
            if (on)
            {
                ShareState.Text = "Checking Windows Firewall and your network…";
                var survey = await AiSharing.SurveyAsync();
                if (!await Shell.ConfirmAsync("Make this PC an AI PC?",
                        "PCs at home that you pair (with a code shown here) can use this PC's models through maison, encrypted. Ollama itself stays private to this PC.\n\n" +
                        $"maison opens its ports ({Services.Pool.PoolPorts.Service} and {Services.Pool.PoolPorts.Discovery}) in Windows Firewall for your home network only (and Tailscale), never the internet." +
                        (survey.BroadOllamaRules.Count > 0
                            ? "\n\nWindows currently lets anyone reach Ollama directly on Public networks (a rule made when \"Allow\" was clicked on its prompt). maison switches that rule off."
                            : "") +
                        "\n\nWindows asks for admin permission once.", "Share"))
                {
                    ShareAiToggle.IsChecked = false;
                    return;
                }
                bool makePrivate = survey.PublicNetworks.Count > 0 && await Shell.ConfirmAsync("Set your network to Private?",
                    $"Windows treats \"{survey.PublicNetworks[0].Name}\" as a Public network (like café Wi-Fi), and the sharing rule only works on Private networks.\n\n" +
                    "If this PC is on your home network, set it to Private: that's the normal setting at home. Choose Keep Public if this is a public network.",
                    "Set to Private", "Keep Public");
                (ok, message) = await AiSharing.EnableAsync(survey, makePrivate, Log);
            }
            else (ok, message) = await AiSharing.DisableAsync(Log);
            Shell.Log(message);
            Shell.Toast(ok ? (on ? "This PC is an AI PC" : "AI is private again") : "Sharing wasn't turned on", message,
                ok ? ControlAppearance.Success : ControlAppearance.Danger, ok ? SymbolRegular.Share24 : SymbolRegular.ErrorCircle24);
        }
        finally
        {
            ShareAiToggle.IsEnabled = true;
            RefreshSharing();
            await Shell.OnModelsChangedAsync();
        }
    }

    private void Log(string line) => Dispatcher.Invoke(() => { ShareState.Text = line; Shell.Log(line); });

    private void NewCode_Click(object sender, RoutedEventArgs e)
    {
        Services.Pool.PoolHost.RenewPairCode();
        RefreshSharing();
    }

    private async void Unpair_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PoolClient c) return;
        if (!await Shell.ConfirmAsync($"Remove {c.Name}?", $"{c.Name} won't be able to use this PC's AI until it's paired again with the code.", "Remove")) return;
        Services.Pool.PoolHost.Revoke(c);
        RefreshSharing();
    }

    private async void UseRemote_Click(object sender, RoutedEventArgs e) => await Shell.SetUsePoolAsync(UseRemoteToggle.IsChecked == true);

    private async void IncludeThis_Click(object sender, RoutedEventArgs e) => await Shell.SetIncludeThisPcAsync(IncludeThisToggle.IsChecked == true);

    private void OpenPool_Click(object sender, RoutedEventArgs e) => Shell.ShowPool();

    /// <summary>Called by the shell after the pool is switched on or off elsewhere.</summary>
    public void RefreshPool() => RefreshSharing();
    // ------------------------------------------------------------------ start with Windows

    private void RefreshAutoStart()
    {
        var on = AutoStart.IsOn;
        StartWithWindows.IsChecked = on;
        StartMinimized.IsChecked = AutoStart.StartsMinimized;
        StartMinimized.IsEnabled = on;
        AutoStartState.Text = on ? "On: starts " + (AutoStart.StartsMinimized ? "minimised " : "") + "from " + AutoStart.Command : "";
    }

    internal void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        try { AutoStart.Set(StartWithWindows.IsChecked == true, StartMinimized.IsChecked == true); }
        catch (Exception ex) { Shell.Toast("Couldn't change start-up", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
        RefreshAutoStart();
    }

    // ------------------------------------------------------------------ updates

    private void AutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        _c.CheckForUpdates = AutoUpdateCheck.IsChecked == true;
        _c.Save();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        UpdateState.Text = "Checking…";
        try { UpdateState.Text = await Shell.CheckForUpdatesAsync(manual: true); }
        finally { BtnCheckUpdate.IsEnabled = true; }
    }

    // ------------------------------------------------------------------ start commands

    private static readonly (string Key, string Label)[] ThinkChoices = [("default", "Model default"), ("on", "Always think"), ("off", "Answer straight away")];

    private void LoadLaunch()
    {
        var o = _c.Launch;
        LTellCtx.IsChecked = o.TellContextWindow;
        LMaxOut.Value = o.MaxOutputTokens;
        LClaudeContinue.IsChecked = o.ClaudeContinue;
        LClaudeNoThink.IsChecked = o.ClaudeDisableThinking;
        LClaudeNoTraffic.IsChecked = o.ClaudeNoNonessentialTraffic;
        LClaudeNoCache.IsChecked = o.ClaudeNoPromptCaching;
        LClaudePct.Value = o.ClaudeCompactPercent;
        LClaudeArgs.Text = o.ClaudeExtraArgs;
        LClaudeEnv.Text = o.ClaudeExtraEnv;
        LQwenContinue.IsChecked = o.QwenContinue;
        LQwenBrowser.IsChecked = o.QwenBrowser;
        LQwenReader.IsChecked = o.QwenReader;
        LQwenPct.Value = Math.Round(o.QwenCompactThreshold * 100);
        LQwenTimeout.Value = o.QwenTimeoutSeconds;
        LQwenArgs.Text = o.QwenExtraArgs;
        LQwenEnv.Text = o.QwenExtraEnv;
        LChatVerbose.IsChecked = o.ChatVerbose;
        if (LChatThink.Items.Count == 0) foreach (var (_, label) in ThinkChoices) LChatThink.Items.Add(label);
        LChatThink.SelectedIndex = Math.Max(0, Array.FindIndex(ThinkChoices, t => t.Key == o.ChatThink));
        LChatArgs.Text = o.ChatExtraArgs;
    }

    private void Launch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        var o = _c.Launch;
        o.TellContextWindow = LTellCtx.IsChecked == true;
        o.MaxOutputTokens = (int)(LMaxOut.Value ?? 0);
        o.ClaudeContinue = LClaudeContinue.IsChecked == true;
        o.ClaudeDisableThinking = LClaudeNoThink.IsChecked == true;
        o.ClaudeNoNonessentialTraffic = LClaudeNoTraffic.IsChecked == true;
        o.ClaudeNoPromptCaching = LClaudeNoCache.IsChecked == true;
        o.ClaudeCompactPercent = (int)(LClaudePct.Value ?? 0);
        o.ClaudeExtraArgs = LClaudeArgs.Text.Trim();
        o.ClaudeExtraEnv = LClaudeEnv.Text;
        o.QwenContinue = LQwenContinue.IsChecked == true;
        o.QwenBrowser = LQwenBrowser.IsChecked == true;
        o.QwenReader = LQwenReader.IsChecked == true;
        o.QwenCompactThreshold = Math.Clamp((LQwenPct.Value ?? 85) / 100.0, 0.3, 1.0);
        o.QwenTimeoutSeconds = (int)(LQwenTimeout.Value ?? 600);
        o.QwenExtraArgs = LQwenArgs.Text.Trim();
        o.QwenExtraEnv = LQwenEnv.Text;
        o.ChatVerbose = LChatVerbose.IsChecked == true;
        o.ChatThink = ThinkChoices[Math.Max(0, LChatThink.SelectedIndex)].Key;
        o.ChatExtraArgs = LChatArgs.Text.Trim();
        _c.Save();
        UpdatePreview();
        Shell.UpdateCommands();
    }

    private void LTabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == LTabs) UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (Shell is null) return;
        var kind = (LTabs.SelectedItem as TabItem)?.Tag as string ?? "claude";
        LPreview.Text = Shell.StartCommandPreview(kind);
    }

    private void ResetLaunch_Click(object sender, RoutedEventArgs e)
    {
        _c.Launch = new LaunchOptions();
        _c.Save();
        _loading = true;
        LoadLaunch();
        AutoUpdateCheck.IsChecked = _c.CheckForUpdates;
        RefreshAutoStart();
        if (UpdateState.Text.Length == 0) UpdateState.Text = $"This is maison {AppInfo.Version}.";
        _loading = false;
        RefreshSharing();
        UpdatePreview();
        Shell.UpdateCommands();
        Shell.Toast("Start commands reset", "Every option is back to maison's defaults.", ControlAppearance.Success, SymbolRegular.ArrowReset24);
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
