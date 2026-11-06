using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Maison.Models;
using Maison.Services;
using Maison.Views;
using Wpf.Ui;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Maison;

public partial class MainWindow : FluentWindow, IShell
{
    private readonly ObservableCollection<StatusItem> _status = [];
    private readonly AppConfig _config = AppConfig.Current;
    private readonly SnackbarService _snackbar = new();
    private readonly DispatcherTimer _autoRefresh = new() { Interval = TimeSpan.FromSeconds(10) };
    private List<ModelInfo> _models = [];
    private List<LoadedModel> _loaded = [];
    private bool _refreshing;
    private StatsSnapshot? _stats;

    /// <summary>The main window, for the web portal.</summary>
    public static MainWindow? Instance { get; private set; }
    private bool _initializing = true;
    private string _page = "dashboard";

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        StatusList.ItemsSource = _status;
        _snackbar.SetSnackbarPresenter(Snackbar);
        ModelsPage.Shell = this;
        SetupPage.Shell = this;
        SystemPage.Shell = this;
        SimplePage.Shell = this;
        ExtensionsPage.Shell = this;
        SettingsPage.Shell = this;
        VersionText.Text = "v" + AppInfo.Version;
        Theming.ApplyAccent(_config.Accent);

        foreach (var k in Launcher.ContextSizes.Keys) Context.Items.Add(k);
        MainModel.Text = _config.MainModel;
        FastModel.Text = _config.FastModel;
        Context.SelectedItem = Launcher.ContextSizes.ContainsKey(_config.Context) ? _config.Context : "64K";
        WorkDir.Text = Directory.Exists(_config.WorkDir) ? _config.WorkDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var t in Launcher.Targets)
            if (t != Launcher.TargetTerminal || SystemProbe.HasWindowsTerminal) OpenIn.Items.Add(t);
        OpenIn.SelectedItem = OpenIn.Items.Contains(_config.OpenIn) ? _config.OpenIn : Launcher.TargetApp;
        Perms.ItemsSource = Launcher.PermissionModes.Select(m => new PermChoice(m.Key, m.Label, m.Tip)).ToList();
        Perms.SelectedItem = ((List<PermChoice>)Perms.ItemsSource).FirstOrDefault(p => p.Key == _config.Permissions) ?? ((List<PermChoice>)Perms.ItemsSource)[0];
        TerminalsPage.Shell = this;
        TerminalsPage.WorkDirProvider = () => Directory.Exists(WorkDir.Text.Trim()) ? WorkDir.Text.Trim() : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        TerminalsPage.SessionsChanged += () =>
        {
            var n = TerminalsPage.Count;
            NavTerminals.Content = n > 0 ? $"Terminals ({n})" : "Terminals";
        };
        _initializing = false;
        UpdateCommands();
        Navigate("dashboard");

        TerminalHub.Idle += s => Dispatcher.BeginInvoke(() => Notifier.Idle(this, s));
        StatsService.Updated += s => Dispatcher.BeginInvoke(() => ShowStats(s));

        Loaded += async (_, _) =>
        {
            StatsService.Start();
            await RefreshAsync();
            _autoRefresh.Tick += async (_, _) => await RefreshAsync(quiet: true);
            _autoRefresh.Start();
            if (_config.PortalEnabled && await SelfTestDir() is null)
            {
                try { await WebPortal.StartAsync(); Log($"Web portal on {string.Join(", ", WebPortal.Urls)}"); }
                catch (Exception ex) { Log("Web portal could not start: " + ex.Message); }
            }

            if (_config.UiMode == "simple" && await SelfTestDir() is null) SetSimpleMode(true);
            else if (await SelfTestDir() is null && await SetupView.NeedsSetupAsync())
            {
                Navigate("setup");
                Toast("Welcome to maison", "Let's get everything installed. Click 'Set up everything' to start.", ControlAppearance.Info, SymbolRegular.Rocket24);
            }
            await RunSelfTestIfRequestedAsync();
        };
        Closing += async (_, e) =>
        {
            SaveConfig();
            if (TerminalsPage.RunningCount > 0 && !_confirmedClose && await SelfTestDir() is null)
            {
                e.Cancel = true;
                if (await ConfirmAsync("Close the launcher?", $"{TerminalsPage.RunningCount} terminal session(s) are still running and will be ended.", "Close"))
                {
                    _confirmedClose = true;
                    Close();
                }
                return;
            }
            TerminalsPage.CloseAll();
            Notifier.Dispose();
            await WebPortal.StopAsync();
        };
    }

    private sealed record PermChoice(string Key, string Label, string Tip);

    private bool _confirmedClose;

    // ------------------------------------------------------------------ IShell

    public string CurrentMainModel => MainModel.Text.Trim();

    public void UseModel(string name)
    {
        MainModel.Text = name;
        SaveConfig();
        UpdateCommands();
        _ = RefreshAsync(quiet: true);
    }

    public async Task OnModelsChangedAsync() => await RefreshAsync(quiet: true);

    public void Navigate(string page)
    {
        _page = page;
        var pages = new (string Key, FrameworkElement View, Wpf.Ui.Controls.Button Nav)[]
        {
            ("dashboard", DashboardPage, NavDashboard), ("terminals", TerminalsPage, NavTerminals), ("models", ModelsPage, NavModels),
            ("setup", SetupPage, NavSetup), ("system", SystemPage, NavSystem), ("extensions", ExtensionsPage, NavExtensions),
            ("settings", SettingsPage, NavSettings),
        };
        foreach (var (key, view, nav) in pages)
        {
            view.Visibility = key == page ? Visibility.Visible : Visibility.Collapsed;
            nav.Appearance = ControlAppearance.Transparent;
            if (key == page)
            {
                nav.SetResourceReference(BackgroundProperty, "MaisonAccentSoftBrush");
                nav.SetResourceReference(ForegroundProperty, "MaisonAccentTextBrush");
                nav.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                nav.ClearValue(BackgroundProperty);
                nav.ClearValue(ForegroundProperty);
                nav.ClearValue(FontWeightProperty);
            }
        }
        StatsService.Detailed = page == "system";
        if (page == "terminals") Dispatcher.BeginInvoke(() => TerminalsPage.Current?.FocusTerminal(), DispatcherPriority.Input);
        if (page == "models") _ = ModelsPage.RefreshAsync();
        if (page == "setup") _ = SetupPage.RefreshAsync();
        if (page == "system") SystemPage.Refresh();
        if (page == "extensions") _ = ExtensionsPage.RefreshAsync();
        if (page == "settings") SettingsPage.Refresh();
    }

    public void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
        LastActivity.Text = message;
    }

    public void Toast(string title, string message, ControlAppearance appearance = ControlAppearance.Secondary, SymbolRegular icon = SymbolRegular.Info24) =>
        _snackbar.Show(title, message, appearance, new SymbolIcon(icon), TimeSpan.FromSeconds(4));

    public async Task<bool> ConfirmAsync(string title, string text, string yes)
    {
        var box = new MessageBox { Title = title, Content = text, PrimaryButtonText = yes, CloseButtonText = "Cancel" };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page }) Navigate(page);
    }

    // ------------------------------------------------------------------ selection helpers

    private string BaseMain => MainModel.Text.Trim();
    private string BaseFast => string.IsNullOrWhiteSpace(FastModel.Text) ? BaseMain : FastModel.Text.Trim();
    private int ContextSize => Context.SelectedItem is string s && Launcher.ContextSizes.TryGetValue(s, out var n) ? n : 0;
    private string PermMode => (Perms.SelectedItem as PermChoice)?.Key ?? "ask";

    private void SaveConfig()
    {
        _config.MainModel = MainModel.Text.Trim();
        _config.FastModel = FastModel.Text.Trim();
        _config.Context = Context.SelectedItem as string ?? "64K";
        _config.WorkDir = WorkDir.Text.Trim();
        _config.OpenIn = Target;
        _config.Permissions = PermMode;
        _config.SkipPermissions = PermMode == "skip";
        _config.Save();
    }

    public string WorkingFolder => Directory.Exists(WorkDir.Text.Trim()) ? WorkDir.Text.Trim() : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Records that a model was used now (drives the "stale" badge on the Models page).</summary>
    public void MarkUsed(string model)
    {
        var baseName = System.Text.RegularExpressions.Regex.Replace(model, @"-ctx\d+k$", "");
        _config.ModelLastUsed[baseName] = DateTime.Now;
        _config.Save();
    }

    private void UpdateCommands()
    {
        if (_initializing) return;
        var baseMain = BaseMain.Length > 0 ? BaseMain : "<model>";
        var main = Launcher.EffectiveModel(baseMain, ContextSize);
        var fast = Launcher.EffectiveModel(BaseFast.Length > 0 ? BaseFast : baseMain, ContextSize);
        Commands.Text = Launcher.CommandsText(baseMain, main, fast, ContextSize, PermMode);
    }

    /// <summary>Returns the model to launch, creating the context variant when needed.</summary>
    private async Task<string> ResolveAsync(string baseModel)
    {
        var ctx = ContextSize;
        var eff = Launcher.EffectiveModel(baseModel, ctx);
        if (eff == baseModel) return baseModel;
        try
        {
            await OllamaClient.CreateVariantAsync(eff, baseModel, ctx);
            Log($"Using {eff} (context {ctx:N0})");
            return eff;
        }
        catch (Exception ex)
        {
            Log($"Could not create {eff}: {ex.Message}. Falling back to {baseModel}");
            return baseModel;
        }
    }

    // ------------------------------------------------------------------ health

    private async Task RefreshAsync(bool quiet = false)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var version = await OllamaClient.GetVersionAsync();
            bool running = version is not null;

            if (running)
            {
                try { _models = await OllamaClient.GetModelsAsync(); } catch { _models = []; }
                try { _loaded = await OllamaClient.GetLoadedAsync(); } catch { _loaded = []; }
            }
            else { _models = []; _loaded = []; }

            var real = _models.Where(m => !m.Name.StartsWith("claude-") && !Launcher.IsVariant(m.Name)).ToList();
            if (running && BaseMain.Length == 0 && real.Count > 0)
            {
                // First run / fresh config: pick the best installed model.
                var pick = ModelCatalog.All.Select(c => c.Tag).FirstOrDefault(t => real.Any(m => m.Name == t)) ?? real[0].Name;
                MainModel.Text = pick;
                SaveConfig();
            }

            var items = new List<StatusItem>();
            if (SystemProbe.OllamaInstalled)
                items.Add(new() { Level = Health.Ok, Title = "Ollama installed", Detail = SystemProbe.OllamaDir });
            else
                items.Add(new() { Level = Health.Bad, Title = "Ollama is not installed", Detail = "Open Setup to install it." });

            if (SystemProbe.OllamaInstalled)
            {
                var cuda = SystemProbe.CudaDirs;
                items.Add(SystemProbe.RuntimeOk
                    ? new() { Level = Health.Ok, Title = "Runtime files present", Detail = "llama-server.exe" + (cuda.Length > 0 ? " · " + string.Join(", ", cuda) : "") }
                    : new() { Level = Health.Bad, Title = "llama-server.exe is missing", Detail = "Models will fail with \"binary not found\" (usually a failed auto-update). Click Repair Ollama." });
            }

            items.Add(running
                ? new() { Level = Health.Ok, Title = $"Server responding · v{version}", Detail = OllamaClient.BaseUrl }
                : new() { Level = Health.Bad, Title = "Server not responding", Detail = SystemProbe.OllamaInstalled ? "Click Start." : "Install Ollama from the Setup page." });

            foreach (var m in _loaded)
            {
                MarkUsed(m.Name);   // a model sitting in memory is in use
                items.Add(new()
                {
                    Level = m.GpuPercent >= 100 ? Health.Ok : Health.Warn,
                    Title = $"Loaded: {m.Name}",
                    Detail = $"{m.SizeGb} GB · {m.GpuPercent}% GPU · context {m.Context:N0}" + (m.GpuPercent < 100 ? " — partly on CPU, will be slow; pick a smaller context" : ""),
                });
            }
            if (_stats is not null && HealthAdvice.VramWarning(_stats) is { } vw)
                items.Add(new() { Level = Health.Warn, Title = "Another app is crowding the GPU", Detail = vw });

            if (running)
            {
                items.Add(real.Count > 0
                    ? new() { Level = Health.Info, Title = $"{real.Count} models installed", Detail = string.Join(" · ", real.Select(m => $"{m.Name} ({m.SizeGb} GB)")) }
                    : new() { Level = Health.Warn, Title = "No models installed", Detail = "Install one on the Models page." });

                var legacy = _models.Where(m => m.Name.StartsWith("claude-")).Select(m => m.Name).ToList();
                if (legacy.Count > 0)
                    items.Add(new() { Level = Health.Warn, Title = "Old claude-* alias models found", Detail = string.Join(", ", legacy) + " — not needed by this launcher." });
                BtnAliases.Visibility = legacy.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                if (BaseMain.Length > 0 && !_models.Any(m => m.Name == BaseMain))
                    items.Add(new() { Level = Health.Warn, Title = $"Model '{BaseMain}' is not installed", Detail = "Install it on the Models page or pick another." });

                UpdateModelLists(real.Select(m => m.Name).ToList());
            }

            var claude = SystemProbe.FindOnPath("claude");
            var qwen = SystemProbe.FindOnPath("qwen");
            items.Add(claude is not null
                ? new() { Level = Health.Ok, Title = "Claude Code CLI found", Detail = claude }
                : new() { Level = Health.Warn, Title = "Claude Code CLI not found", Detail = "Install it from the Setup page." });
            items.Add(qwen is not null
                ? new() { Level = Health.Ok, Title = "Qwen Code CLI found", Detail = qwen }
                : new() { Level = Health.Warn, Title = "Qwen Code CLI not found", Detail = "Install it from the Setup page." });

            _status.Clear();
            foreach (var i in items) _status.Add(i);

            UpdateMetrics(running, version);
            UpdateCommands();
            if (!quiet) Log(running ? $"Health check OK · Ollama v{version}" : "Health check · Ollama server not responding");
        }
        catch (Exception ex) { Log("Health check failed: " + ex.Message); }
        finally { _refreshing = false; }
    }

    private void UpdateMetrics(bool running, string? version)
    {
        var red = (Color)ColorConverter.ConvertFromString("#E5534B");
        var green = (Color)ColorConverter.ConvertFromString("#4CC38A");
        var ok = running && SystemProbe.RuntimeOk;
        var c = ok ? green : red;
        ServerDot.Fill = new SolidColorBrush(c);
        ServerPill.Background = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
        ServerPillText.Text = !SystemProbe.OllamaInstalled ? "Ollama not installed" : !running ? "Ollama offline" : SystemProbe.RuntimeOk ? "Ollama ready" : "Ollama broken";

        MServer.Text = running ? "Running" : "Offline";
        MServerSub.Text = running ? $"v{version} · 127.0.0.1:11434" : SystemProbe.OllamaInstalled ? "click Start" : "install from Setup";

        var top = _loaded.FirstOrDefault();
        MLoaded.Text = top?.Name ?? "None";
        MLoadedSub.Text = top is null ? "loads on first request" : $"{top.GpuPercent}% GPU · ctx {top.Context:N0}";
    }

    private void ShowStats(StatsSnapshot s)
    {
        _stats = s;
        if (WindowState == WindowState.Minimized || _page != "dashboard") return;

        GCpu.Value = s.CpuPercent;
        GCpu.Display = $"{s.CpuPercent:0}%";
        GCpu.Caption = $"CPU · {s.Cores}C/{s.Threads}T";
        GRam.Maximum = s.RamTotalGb > 0 ? s.RamTotalGb : 1;
        GRam.Warn = GRam.Maximum * 0.85;
        GRam.Danger = GRam.Maximum * 0.95;
        GRam.Value = s.RamUsedGb;
        GRam.Display = $"{s.RamUsedGb:0.0} GB";
        GRam.Caption = $"RAM of {s.RamTotalGb:0} GB";

        var warn = HealthAdvice.VramWarning(s);
        VramWarnBox.Visibility = warn is null ? Visibility.Collapsed : Visibility.Visible;
        VramWarnText.Text = warn ?? "";

        var g = s.Gpu;
        {
            if (g is null)
            {
                GpuTitle.Text = "Machine · no NVIDIA GPU detected (nvidia-smi unavailable)";
                GpuExtra.Text = s.CpuName;
                foreach (var gauge in new[] { GTemp, GLoad, GPower, GVram }) { gauge.Value = 0; gauge.Display = "—"; }
                return;
            }
            GpuTitle.Text = "Machine · " + g.Name.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "") + " · " + ShortCpu(s.CpuName);
            GpuExtra.Text = string.Join("   ·   ", new[]
            {
                g.FanPercent is { } f ? $"Fan {f}%" : null,
                g.ClockMhz is { } mhz ? $"Core {mhz:N0} MHz" : null,
                s.OllamaCpuPercent >= 1 ? $"Ollama CPU {s.OllamaCpuPercent:0}%" : null,
            }.Where(x => x is not null));

            GTemp.Value = g.TempC ?? 0;
            GTemp.Display = g.TempC is { } t ? $"{t:0}°C" : "—";

            GLoad.Value = g.LoadPercent;
            GLoad.Display = $"{g.LoadPercent}%";

            var limit = g.PowerLimitW is > 0 ? g.PowerLimitW.Value : 450;
            GPower.Maximum = limit;
            GPower.Warn = limit * 0.8;
            GPower.Danger = limit * 0.95;
            GPower.Value = g.PowerW ?? 0;
            GPower.Display = g.PowerW is { } p ? $"{p:0} W" : "—";
            GPower.Caption = $"Power · {limit:0} W max";

            GVram.Maximum = g.TotalGb > 0 ? g.TotalGb : 1;
            GVram.Warn = GVram.Maximum * 0.9;
            GVram.Danger = GVram.Maximum * 0.97;
            GVram.Value = g.UsedGb;
            GVram.Display = $"{g.UsedGb:0.0} GB";
            GVram.Caption = $"VRAM of {g.TotalGb:0} GB";
        }
    }

    private static string ShortCpu(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"\(R\)|\(TM\)|CPU|Processor|\d+-Core|@.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Replace("  ", " ").Trim();

    private void UpdateModelLists(List<string> names)
    {
        _initializing = true;
        foreach (var (cb, allowBlank) in new[] { (MainModel, false), (FastModel, true) })
        {
            var keep = cb.Text;
            var current = cb.Items.Cast<object>().Select(o => o.ToString()).ToList();
            var wanted = (allowBlank ? new List<string> { "" } : []).Concat(names).ToList();
            if (!current.SequenceEqual(wanted))
            {
                cb.Items.Clear();
                foreach (var n in wanted) cb.Items.Add(n);
            }
            cb.Text = keep;
        }
        _initializing = false;
    }

    private async Task<bool> EnsureReadyAsync()
    {
        if (await OllamaClient.GetVersionAsync(2) is null)
        {
            Toast("Ollama is not running", SystemProbe.OllamaInstalled ? "Click Start first." : "Install it from the Setup page.", ControlAppearance.Caution, SymbolRegular.Warning24);
            return false;
        }
        if (BaseMain.Length == 0)
        {
            Toast("No model selected", "Install or pick a model first.", ControlAppearance.Caution, SymbolRegular.Warning24);
            return false;
        }
        return true;
    }

    private async Task WithBusy(Func<Task> work)
    {
        Cursor = Cursors.Wait;
        try { await work(); }
        catch (Exception ex) { Log("Error: " + ex.Message); Toast("Something went wrong", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
        finally { Cursor = null; }
    }

    // ------------------------------------------------------------------ launch buttons

    private async void Claude_Click(object sender, RoutedEventArgs e) => await WithBusy(() => LaunchKindAsync("claude", Target));
    private async void Qwen_Click(object sender, RoutedEventArgs e) => await WithBusy(() => LaunchKindAsync("qwen", Target));
    private async void Chat_Click(object sender, RoutedEventArgs e) => await WithBusy(() => LaunchKindAsync("chat", Target));

    /// <summary>Builds and launches a Claude Code / Qwen Code / Chat session with the current Dashboard selection.</summary>
    public async Task<TermSession?> LaunchKindAsync(string kind, string target)
    {
        if (kind == "shell")
        {
            var v = await TerminalsPage.OpenAsync(new Launcher.Session("PowerShell", WorkingFolder, null, null, "Get-Location | Out-Null"), "shell");
            return v.Session;
        }
        var spec = await BuildSpecAsync(kind);
        if (spec is null) return null;
        var what = kind switch { "claude" => "Claude Code", "qwen" => "Qwen Code", _ => "Chat" };
        MarkUsed(BaseMain);
        return (await LaunchAsync(spec, kind, what, Launcher.EffectiveModel(BaseMain, ContextSize), target))?.Session;
    }

    /// <summary>The session spec for Claude Code / Qwen Code / Chat with the current selection (null if something is missing).</summary>
    public async Task<Launcher.Session?> BuildSpecAsync(string kind)
    {
        if (!await EnsureReadyAsync()) return null;
        if (kind == "claude" && SystemProbe.FindOnPath("claude") is null) { Toast("Claude Code is not installed", "Install it from the Setup page.", ControlAppearance.Caution, SymbolRegular.Warning24); return null; }
        if (kind == "qwen" && SystemProbe.FindOnPath("qwen") is null) { Toast("Qwen Code is not installed", "Install it from the Setup page.", ControlAppearance.Caution, SymbolRegular.Warning24); return null; }
        var main = await ResolveAsync(BaseMain);
        var folder = WorkingFolder;
        return kind switch
        {
            "claude" => new($"Claude Code · {main}", folder,
                Launcher.ClaudeEnv(main, BaseFast == BaseMain ? main : await ResolveAsync(BaseFast)),
                ["Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue"], Launcher.ClaudeCommand(PermMode)),
            "qwen" => new($"Qwen Code · {main}", folder, Launcher.QwenEnv(main), null, Launcher.QwenCommand(main, PermMode)),
            _ => new($"Chat · {main}", folder, null, null, Launcher.ChatCommand(main)),
        };
    }

    /// <summary>Starts a session in the hub without opening a tab here (the simple view shows it; Terminals adds a tab too).</summary>
    public async Task<TermSession?> StartSessionAsync(string kind)
    {
        var spec = await BuildSpecAsync(kind);
        if (spec is null) return null;
        SaveConfig();
        MarkUsed(BaseMain);
        Log($"Started {spec.Title} in {spec.WorkDir}");
        return TerminalHub.Create(spec, kind);
    }

    public void SetWorkingFolder(string folder)
    {
        WorkDir.Text = folder;
        SaveConfig();
        UpdateCommands();
    }

    public string MainModelName => BaseMain;
    public SetupView Setup => SetupPage;
    public async Task RefreshHealthAsync() => await RefreshAsync(quiet: true);

    /// <summary>Simple view ↔ full app.</summary>
    public void SetSimpleMode(bool simple)
    {
        _config.UiMode = simple ? "simple" : "advanced";
        _config.Save();
        SimplePage.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;
        AdvancedRoot.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        if (simple) _ = SimplePage.ActivateAsync();
        else Navigate(_page);
    }

    private string Target => OpenIn.SelectedItem as string ?? Launcher.TargetApp;

    /// <summary>Opens the session in an app tab, Windows Terminal or a PowerShell window, per "Open in".</summary>
    private async Task<Controls.TerminalViewer?> LaunchAsync(Launcher.Session session, string kind, string what, string model, string? target = null)
    {
        target ??= Target;
        if (!Directory.Exists(session.WorkDir)) throw new DirectoryNotFoundException($"Working folder not found: {session.WorkDir}");
        SaveConfig();
        Controls.TerminalViewer? tab = null;
        if (target == Launcher.TargetApp)
        {
            Navigate("terminals");
            tab = await TerminalsPage.OpenAsync(session, kind);
        }
        else
        {
            Launcher.OpenExternal(session, useWindowsTerminal: target == Launcher.TargetTerminal);
            Toast($"{what} launched", $"{model} · {session.WorkDir}", ControlAppearance.Success, SymbolRegular.Rocket24);
        }
        Log($"Launched {what} with {model} in {session.WorkDir} ({target}, permissions: {Launcher.Mode(PermMode).Label})");
        return tab;
    }

    // ------------------------------------------------------------------ ollama buttons

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Start_Click(object sender, RoutedEventArgs e) => await WithBusy(async () =>
    {
        if (!SystemProbe.OllamaInstalled) { Navigate("setup"); return; }
        Log("Starting Ollama...");
        var ok = await SystemProbe.StartOllamaAsync();
        Log(ok ? "Ollama is up" : "Ollama did not respond within 20s");
        await RefreshAsync(quiet: true);
    });

    private async void Restart_Click(object sender, RoutedEventArgs e) => await WithBusy(async () =>
    {
        Log("Restarting Ollama...");
        await SystemProbe.StopOllamaAsync();
        var ok = await SystemProbe.StartOllamaAsync();
        Log(ok ? "Ollama restarted" : "Ollama did not come back within 20s");
        await RefreshAsync(quiet: true);
    });

    private async void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureReadyAsync()) return;
        BtnSpeed.IsEnabled = false;
        MSpeed.Text = "Testing…";
        MSpeedSub.Text = "first run includes load time";
        try
        {
            var model = await ResolveAsync(BaseMain);
            var r = await OllamaClient.SpeedTestAsync(model);
            MSpeed.Text = $"{r.TokensPerSecond} tok/s";
            MSpeedSub.Text = $"{r.Model} · {r.Tokens} tokens";
            Log($"Speed test {r.Model}: {r.Tokens} tokens at {r.TokensPerSecond} tok/s (load {r.LoadSeconds:0.0}s, total {r.TotalSeconds:0.0}s)");
        }
        catch (Exception ex) { MSpeed.Text = "Failed"; MSpeedSub.Text = ex.Message; Log("Speed test failed: " + ex.Message); }
        finally { BtnSpeed.IsEnabled = true; await RefreshAsync(quiet: true); }
    }

    private async void Unload_Click(object sender, RoutedEventArgs e) => await WithBusy(async () =>
    {
        if (_loaded.Count == 0) { Log("Nothing loaded"); Toast("Nothing to unload", "No model is in GPU memory."); return; }
        foreach (var m in _loaded) { await OllamaClient.UnloadAsync(m.Name); Log($"Unloaded {m.Name}"); }
        await RefreshAsync(quiet: true);
    });

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Repair Ollama",
                "This stops Ollama, moves aside any locked runtime files and reinstalls Ollama with winget.\n\nYour downloaded models are kept.",
                "Repair")) return;
        SystemProbe.RunRepair();
        Log("Repair started in a separate window — Refresh when it finishes");
    }

    private async void Aliases_Click(object sender, RoutedEventArgs e)
    {
        var legacy = _models.Where(m => m.Name.StartsWith("claude-")).Select(m => m.Name).ToList();
        if (legacy.Count == 0) return;
        if (!await ConfirmAsync("Remove alias models",
                "Remove these from Ollama?\n\n" + string.Join("\n", legacy) + "\n\nThey are name copies; the real Qwen models stay installed.",
                "Remove")) return;
        await WithBusy(async () =>
        {
            foreach (var n in legacy) { await OllamaClient.DeleteAsync(n); Log($"Removed {n}"); }
            await RefreshAsync(quiet: true);
        });
    }

    // ------------------------------------------------------------------ misc

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateCommands, DispatcherPriority.Background);

    private void Selection_KeyUp(object sender, KeyEventArgs e) => UpdateCommands();

    private void Perms_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Perms.SelectedItem is PermChoice p) Perms.ToolTip = p.Tip + "\n\nClaude Code's 'auto' mode isn't offered: its safety check runs on Anthropic's servers and can't work with a local model.";
        if (_initializing) return;
        SaveConfig();
        UpdateCommands();
    }

    public async Task RunInTerminalAsync(string title, string command)
    {
        Navigate("terminals");
        await TerminalsPage.OpenAsync(new Launcher.Session(title, WorkingFolder, null, null, command), "shell");
        Log($"{title}: {command}");
    }

    public IReadOnlyDictionary<string, DateTime> ModelLastUsed => _config.ModelLastUsed;

    private void SimpleMode_Click(object sender, RoutedEventArgs e) => SetSimpleMode(true);

    public void ApplyTerminalTheme() { TerminalsPage.ApplyTheme(); SimplePage.ApplyTheme(); }
    public void SelectSession(TermSession s) => TerminalsPage.Select(s);

    public string CurrentStatusSummary => ServerPillText.Text;
    public IReadOnlyList<StatusItem> StatusItems => _status.ToList();
    public IReadOnlyList<LoadedModel> LoadedModels => _loaded;
    public IReadOnlyList<ModelInfo> InstalledModels => _models;
    public string SelectedContext => Context.SelectedItem as string ?? "";
    public string SelectedPermissions => Launcher.Mode(PermMode).Label;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(WorkDir.Text) ? WorkDir.Text : null };
        if (dlg.ShowDialog(this) == true) { WorkDir.Text = dlg.FolderName; SaveConfig(); }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(Commands.Text);
        Toast("Copied", "Start commands copied to the clipboard", ControlAppearance.Success, SymbolRegular.Copy24);
    }

    private void ConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        SaveConfig();
        Process.Start(new ProcessStartInfo("explorer.exe", AppConfig.Folder) { UseShellExecute = true });
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "maison.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = exe;
            sc.WorkingDirectory = Path.GetDirectoryName(exe);
            sc.Description = "Check Ollama and launch Claude Code / Qwen Code on local Qwen";
            sc.Save();
            Log("Desktop shortcut created: " + lnk);
            Toast("Shortcut created", lnk, ControlAppearance.Success, SymbolRegular.Desktop24);
        }
        catch (Exception ex) { Log("Shortcut failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ self-test

    private static Task<string?> SelfTestDir()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--selftest");
        return Task.FromResult(i >= 0 && i + 1 < args.Length ? args[i + 1] : null);
    }

    /// <summary>Maison.exe --selftest &lt;dir&gt; : screenshots every page + dumps status, then exits.</summary>
    private async Task RunSelfTestIfRequestedAsync()
    {
        var dir = await SelfTestDir();
        if (dir is null) return;
        Directory.CreateDirectory(dir);
        await Task.Delay(7000);   // first stats sample + gauge animation
        Snap(Path.Combine(dir, "dashboard.png"));
        foreach (var page in new[] { "models", "setup", "system", "extensions", "settings", "terminals" })
        {
            Navigate(page);
            await Task.Delay(3000);
            Snap(Path.Combine(dir, page + ".png"));
        }
        Navigate("dashboard");

        // Optional: --pull <model> exercises the real Models-page install path, then removes the model.
        var args = Environment.GetCommandLineArgs();
        var pi = Array.IndexOf(args, "--pull");
        var pullResult = "";
        if (pi >= 0 && pi + 1 < args.Length)
        {
            var model = args[pi + 1];
            Navigate("models");
            var install = ModelsPage.InstallAsync(model);
            await Task.Delay(4000);
            Snap(Path.Combine(dir, "models-downloading.png"));
            await install;
            var installed = (await OllamaClient.GetModelsAsync()).Any(m => m.Name == model);
            await Task.Delay(1000);
            Snap(Path.Combine(dir, "models-installed.png"));
            if (installed) await OllamaClient.DeleteAsync(model);
            var gone = !(await OllamaClient.GetModelsAsync()).Any(m => m.Name == model);
            pullResult = $"PULL {model}: installed={installed} deletedAfter={gone}";
            Navigate("dashboard");
        }

        // Optional: --term <workdir> opens Claude Code and Qwen Code in app tabs and captures what they render.
        var ti = Array.IndexOf(args, "--term");
        var termResult = "";
        if (ti >= 0 && ti + 1 < args.Length && BaseMain.Length > 0)
        {
            var main = await ResolveAsync(BaseMain);
            var folder = args[ti + 1];
            var claude = await LaunchAsync(new Launcher.Session($"Claude Code · {main}", folder, Launcher.ClaudeEnv(main, main),
                ["Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue"], Launcher.ClaudeCommand("ask")), "claude", "Claude Code", main, Launcher.TargetApp);
            await Task.Delay(15000);
            string claudeScreen = "";
            if (claude is not null) { await claude.CaptureAsync(Path.Combine(dir, "term-claude.png")); claudeScreen = await claude.ReadScreenAsync(); }
            Snap(Path.Combine(dir, "terminals.png"));
            var qwen = await LaunchAsync(new Launcher.Session($"Qwen Code · {main}", folder, Launcher.QwenEnv(main), null, Launcher.QwenCommand(main, "ask")),
                "qwen", "Qwen Code", main, Launcher.TargetApp);
            await Task.Delay(15000);
            if (qwen is not null) await qwen.CaptureAsync(Path.Combine(dir, "term-qwen.png"));

            // Keyboard path: type into a plain PowerShell tab through xterm.js.
            var ps = await TerminalsPage.OpenAsync(new Launcher.Session("PowerShell", folder, null, null, "Get-Location | Out-Null"), "shell");
            await Task.Delay(4000);
            await ps.TypeAsync("Write-Output ('typed-' + (6*7))\r");
            await Task.Delay(2500);
            var screen = await ps.ReadScreenAsync();
            var jsErrors = await ps.EvalAsync("JSON.stringify({ errs: window.__errs, term: typeof term, cols: typeof term === 'object' ? term.cols : -1 })");
            termResult += "JS: " + jsErrors + " | ";

            // Mirror: a pop-out window on the same session shows the same live output, and input from it reaches the shell.
            var pop = new PopOutWindow(ps.Session);
            pop.Show();
            await Task.Delay(4000);
            var mirrorHistory = (await pop.Viewer.ReadScreenAsync()).Contains("typed-42");
            await pop.Viewer.TypeAsync("Write-Output ('mirror-' + (5*5))\r");
            await Task.Delay(2500);
            var mainSeesMirrorInput = (await ps.ReadScreenAsync()).Contains("mirror-25");
            await pop.Viewer.CaptureAsync(Path.Combine(dir, "term-popout.png"));
            pop.Close();

            // Paste: one Ctrl+V pastes exactly once.
            Clipboard.SetText("echo paste-once");
            ps.FocusTerminal();
            await ps.EvalAsync("document.querySelector('.xterm-helper-textarea').dispatchEvent(new KeyboardEvent('keydown', {key:'v', code:'KeyV', ctrlKey:true, bubbles:true}))");
            await Task.Delay(1500);
            var afterPaste = await ps.ReadScreenAsync();
            int pastes = System.Text.RegularExpressions.Regex.Matches(afterPaste, "paste-once").Count;
            await ps.TypeAsync("\x1b");   // clear the line
            await ps.CaptureAsync(Path.Combine(dir, "term-ps.png"));

            // Idle notification fires once per request, never for typing or idle redraws.
            var shell = TerminalHub.Sessions.Last(s => s.Kind == "shell");
            int idleEvents = 0;
            shell.BecameIdle += _ => Interlocked.Increment(ref idleEvents);
            var wait = TimeSpan.FromSeconds(Math.Max(3, _config.IdleSeconds) + 3);
            await ps.TypeAsync("1..250 | % { 'working line ' + $_; Start-Sleep -Milliseconds 16 }\r");   // ~4 s of output
            for (int i = 0; i < 60 && idleEvents == 0; i++) await Task.Delay(500);   // the work takes ~4-8 s, then the idle delay
            await Task.Delay(wait);                                                    // nothing more should follow
            int afterWork = idleEvents;
            await ps.TypeAsync("x");                                                                      // typing only
            await Task.Delay(wait);
            int afterTyping = idleEvents;
            await ps.TypeAsync("\x1b");                                                                   // clear the line
            await Task.Delay(300);
            await ps.TypeAsync("\r");                                                                     // empty Enter: quick prompt redraw
            await Task.Delay(wait);
            int afterQuickEnter = idleEvents;

            await ps.TypeAsync("exit\r");
            await Task.Delay(2500);
            termResult += $"TERM: claudeStarted={claudeScreen.Length > 50} claudeAutoModeWarning={claudeScreen.Contains("Auto mode")} typedOutputSeen={screen.Contains("typed-42")} " +
                         $"mirrorShowsHistory={mirrorHistory} mirrorInputReachesShell={mainSeesMirrorInput} ctrlVPasteCount={pastes} " +
                         $"idleAfterWork={afterWork} idleAfterTyping={afterTyping} idleAfterQuickEnter={afterQuickEnter} " +
                         $"psEndedAfterExit={!ps.IsRunning} tabs={TerminalsPage.Count} running={TerminalsPage.RunningCount}";
            TerminalsPage.CloseAll();
            termResult += $" afterClose={TerminalsPage.Count} hubSessions={TerminalHub.Sessions.Count}";
        }

        // Optional: --simple exercises the simple view: ready tick + live Qwen terminal, and previews of the progress screen.
        var simpleResult = "";
        if (args.Contains("--simple"))
        {
            var before = _config.UiMode;
            SetSimpleMode(true);
            Activate();
            for (int i = 0; i < 40 && TerminalHub.Sessions.All(s => !s.IsRunning); i++) await Task.Delay(500);
            await Task.Delay(12000);   // Qwen Code's UI comes up
            await SnapWithTerminalsAsync(Path.Combine(dir, "simple-ready.png"));
            var qs = TerminalHub.Sessions.FirstOrDefault(s => s.Kind == "qwen");
            simpleResult = $"SIMPLE: readyShown={SimplePage.FindName("ReadyPanel") is FrameworkElement { Visibility: Visibility.Visible }} " +
                           $"qwenSessionRunning={qs?.IsRunning == true} qwenOutput={qs?.History.Length ?? 0}chars tabMirrored={TerminalsPage.Count > 0}";
            SimplePage.PreviewProgress(SetupPage.ModelStep, 37, "6.1 of 16.5 GB", "pulling 3f1a8c2e9b47… 37% (98 MB/s)");
            await Task.Delay(800);
            Snap(Path.Combine(dir, "simple-setup-model.png"));
            SimplePage.PreviewProgress(SetupPage.NodeStep, 0, "Working…", "Found Node.js LTS [OpenJS.NodeJS.LTS] Version 24.11.0");
            await Task.Delay(800);
            Snap(Path.Combine(dir, "simple-setup-node.png"));
            foreach (var s in TerminalHub.Sessions) TerminalHub.Remove(s);
            SetSimpleMode(before == "simple");
            _config.UiMode = before;
            _config.Save();
            await SetupPage.RefreshAsync();
        }

        // Optional: --portal <password> starts the web portal and exercises it over real HTTPS (use with MAISON_CONFIG_DIR).
        var po = Array.IndexOf(args, "--portal");
        var portalResult = "";
        if (po >= 0 && po + 1 < args.Length)
        {
            try { portalResult = await Services.PortalSelfTest.RunAsync(args[po + 1], dir); }
            catch (Exception ex) { portalResult = "PORTAL: exception " + ex; }
        }

        var lines = new List<string> { "STATUS:", pullResult, termResult, simpleResult, portalResult };
        lines.AddRange(_status.Select(s => $"[{s.Level}] {s.Title} | {s.Detail}"));
        lines.Add($"GAUGES: temp={GTemp.Display} load={GLoad.Display} power={GPower.Display} vram={GVram.Display} cpu={GCpu.Display} ram={GRam.Display} | {GpuTitle.Text} | {GpuExtra.Text}");
        if (_stats is { } st)
            lines.Add("VRAM USERS: " + string.Join(", ", st.VramUsers.Select(p => $"{p.Name} {p.VramGb:0.0}GB")) + $" | warning: {HealthAdvice.VramWarning(st) ?? "none"}");
        lines.Add($"VERSION: {AppInfo.Version} | PERMS: {_config.Permissions} | ACCENT: {_config.Accent}");
        lines.Add($"MAIN: {MainModel.Text} | ITEMS: " + string.Join(", ", MainModel.Items.Cast<object>()));
        lines.Add("COMMANDS:\n" + Commands.Text);
        lines.Add("LOG:\n" + LogBox.Text);
        File.WriteAllLines(Path.Combine(dir, "selftest.txt"), lines);
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Screenshot of the app with any visible terminals composited in (WebView2 isn't captured by RenderTargetBitmap,
    /// and copying the screen would pick up other windows on top).
    /// </summary>
    private async Task SnapWithTerminalsAsync(string file)
    {
        var basePng = file + ".base.png";
        Snap(basePng);
        var dpi = VisualTreeHelper.GetDpi(this);
        using var canvas = new System.Drawing.Bitmap(basePng);
        using var g = System.Drawing.Graphics.FromImage(canvas);
        foreach (var v in FindViewers(Root).Where(v => v.IsVisible && v.ActualWidth > 0))
        {
            var tmp = file + ".term.png";
            await v.CaptureAsync(tmp);
            var p = v.TransformToAncestor(Root).Transform(new Point(0, 0));
            using var img = System.Drawing.Image.FromFile(tmp);
            g.DrawImage(img, (float)(p.X * dpi.DpiScaleX), (float)(p.Y * dpi.DpiScaleY), (float)(v.ActualWidth * dpi.DpiScaleX), (float)(v.ActualHeight * dpi.DpiScaleY));
        }
        g.Flush();
        canvas.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static IEnumerable<Controls.TerminalViewer> FindViewers(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is Controls.TerminalViewer v) yield return v;
            else foreach (var x in FindViewers(c)) yield return x;
        }
    }

    private void Snap(string file)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)(Root.ActualWidth * dpi.DpiScaleX);
        var h = (int)(Root.ActualHeight * dpi.DpiScaleY);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(Root), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        }
        var bmp = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(file);
        enc.Save(f);
    }
}
