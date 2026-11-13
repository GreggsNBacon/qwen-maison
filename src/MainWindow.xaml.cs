using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Json;
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
    private readonly ToastWindow _toasts;
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
        OllamaClient.UsePool(_config.UseRemoteAi);
        if (_config.UseRemoteAi) _ = StartPoolPartAsync("pool", Services.Pool.PoolRelay.StartAsync);
        if (_config.ShareAi) _ = StartPoolPartAsync("AI sharing", Services.Pool.PoolHost.StartAsync);
        InitializeComponent();
        StatusList.ItemsSource = _status;
        // Messages live in their own owned window so they show above the terminals (WebView2 draws over WPF content).
        _toasts = new ToastWindow(this);
        _snackbar.SetSnackbarPresenter(_toasts.Presenter);
        ModelsPage.Shell = this;
        SetupPage.Shell = this;
        SystemPage.Shell = this;
        SimplePage.Shell = this;
        ExtensionsPage.Shell = this;
        SettingsPage.Shell = this;
        PoolPage.Shell = this;
        VersionText.Text = "v" + AppInfo.Version;
        TitleVersion.Text = "v" + AppInfo.Version;
        if (Environment.GetCommandLineArgs().All(a => a != "--selftest")) _ = UpdateLoopAsync();
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

        // The title bar is a drag area to Windows; make the Advanced switch in it clickable.
        // Added after first render so this hook runs before the title bar's own one.
        ContentRendered += (_, _) =>
            (PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource)?.AddHook(TitleBarHitTest);
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

    private async Task StartPoolPartAsync(string what, Func<Task> start)
    {
        try { await start(); await RefreshAsync(quiet: true); }
        catch (Exception ex) { Log($"Couldn't start the {what}: {ex.Message}"); Toast($"The {what} didn't start", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
    }

    public async Task OnAiLocationChangedAsync()
    {
        Log(OllamaClient.IsRemote ? "Using your AI pool" : "Using this PC's AI");
        _models = []; _loaded = [];
        await RefreshAsync(quiet: true);
        await SetupPage.RefreshAsync();
        if (SimplePage.IsVisible) await SimplePage.ActivateAsync();
    }

    public void ShowAiSettings() => ShowPool();

    // ------------------------------------------------------------------ updates

    private UpdateInfo? _update;

    /// <summary>At start-up (after things settle) and then once a day, unless turned off in Settings.</summary>
    private async Task UpdateLoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (true)
        {
            if (_config.CheckForUpdates) await CheckForUpdatesAsync(manual: false);
            await Task.Delay(TimeSpan.FromHours(24));
        }
    }

    public Task<string> CheckForUpdatesAsync(bool manual) => CheckForUpdatesAsync(manual, null);

    /// <param name="pretendVersion">Self-test only: compare as if this were the running version.</param>
    private async Task<string> CheckForUpdatesAsync(bool manual, string? pretendVersion)
    {
        var mine = AppInfo.Version;
        try
        {
            _update = await UpdateCheck.CheckAsync(pretendVersion);
            mine = pretendVersion ?? AppInfo.Version;
        }
        catch (Exception ex)
        {
            if (manual) Log($"Update check failed: {ex.Message}");
            return "Couldn't reach GitHub to check. Try again later.";
        }
        if (_update is null)
        {
            UpdateButton.Visibility = Visibility.Collapsed;
            return $"You have the latest version ({mine}).";
        }
        UpdateButton.Content = $"Update to {_update.Version}";
        UpdateButton.ToolTip = $"maison {_update.Version} is out (you have {mine}). Opens the download page.";
        UpdateButton.Visibility = Visibility.Visible;
        Log($"maison {_update.Version} is available: {_update.PageUrl}");
        if (!manual) Toast($"maison {_update.Version} is available", "Click Update in the title bar to download it.", ControlAppearance.Info, SymbolRegular.ArrowDownload24);
        return $"maison {_update.Version} is available (you have {mine}).";
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } u) return;
        if (!await ConfirmAsync($"Download maison {u.Version}?",
                $"You have {AppInfo.Version}. Your browser opens the release page; download maison-{u.Version}.exe there.\n\n" +
                "To update: close maison, run the new exe and delete the old one. Your settings and models are kept.",
                "Open download page", "Later")) return;
        try { Process.Start(new ProcessStartInfo(u.PageUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Toast("Couldn't open the browser", u.PageUrl + " · " + ex.Message, ControlAppearance.Caution, SymbolRegular.Warning24); }
    }

    public void ShowPool()
    {
        SetSimpleMode(false);
        Navigate("pool");
    }

    public async Task SetUsePoolAsync(bool on)
    {
        if (on)
        {
            try { await Services.Pool.PoolRelay.StartAsync(); }
            catch (Exception ex)
            {
                Toast("The AI pool didn't start", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                on = false;
            }
        }
        else await Services.Pool.PoolRelay.StopAsync();
        _config.UseRemoteAi = on;
        _config.Save();
        OllamaClient.UsePool(on);
        SettingsPage.RefreshPool();
        await OnAiLocationChangedAsync();
        await PoolPage.RefreshAsync();
    }

    public void Navigate(string page)
    {
        _page = page;
        var pages = new (string Key, FrameworkElement View, Wpf.Ui.Controls.Button Nav)[]
        {
            ("dashboard", DashboardPage, NavDashboard), ("terminals", TerminalsPage, NavTerminals), ("models", ModelsPage, NavModels),
            ("setup", SetupPage, NavSetup), ("system", SystemPage, NavSystem), ("extensions", ExtensionsPage, NavExtensions),
            ("settings", SettingsPage, NavSettings), ("pool", PoolPage, NavPool),
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
        if (page == "pool") _ = PoolPage.RefreshAsync();
    }

    public void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
        LastActivity.Text = message;
    }

    public void Toast(string title, string message, ControlAppearance appearance = ControlAppearance.Secondary, SymbolRegular icon = SymbolRegular.Info24) =>
        _snackbar.Show(title, message, appearance, new SymbolIcon(icon), TimeSpan.FromSeconds(4));

    public async Task<bool> ConfirmAsync(string title, string text, string yes, string no = "Cancel")
    {
        var box = new MessageBox { Title = title, Content = text, PrimaryButtonText = yes, CloseButtonText = no };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page }) Navigate(page);
    }

    // ------------------------------------------------------------------ selection helpers

    private string BaseMain => MainModel.Text.Trim();
    private string BaseFast => string.IsNullOrWhiteSpace(FastModel.Text) ? BaseMain : FastModel.Text.Trim();
    /// <summary>
    /// The chosen context window (0 = Ollama's default). In the AI pool every PC must give the same window, and their
    /// defaults differ with their GPUs, so "Ollama default" means a fixed 32K there.
    /// </summary>
    private int ContextSize => Context.SelectedItem is string s && Launcher.ContextSizes.TryGetValue(s, out var n) ? (n == 0 && OllamaClient.IsRemote ? 32768 : n) : 0;
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

    /// <summary>The context window sessions will really get: the chosen size, or what Ollama uses by default.</summary>
    private int RealContext => ContextSize > 0 ? ContextSize
        : _loaded.FirstOrDefault(m => m.Name == BaseMain || m.Name == BaseMain + ":latest") is { Context: > 0 } l ? l.Context
        : OllamaClient.IsRemote ? 32768 : _ollamaDefaultCtx;
    private readonly int _ollamaDefaultCtx = OllamaClient.ServerDefaultContext();

    private Launcher.Plan PlanFor(string main, string fast, int ctx) =>
        new(main, fast, ctx, PermMode, _config.Launch,
            _models.Where(m => !m.Name.StartsWith("claude-")).Select(m => m.Name).ToList());

    public void UpdateCommands()
    {
        if (_initializing) return;
        var baseMain = BaseMain.Length > 0 ? BaseMain : "<model>";
        var main = Launcher.EffectiveModel(baseMain, ContextSize);
        var fast = Launcher.EffectiveModel(BaseFast.Length > 0 ? BaseFast : baseMain, ContextSize);
        var plan = PlanFor(main, fast, RealContext);
        Commands.Text = Launcher.CommandsText(plan, baseMain);
        ContextHint.Text = (ContextSize == 0
                ? OllamaClient.IsRemote && !_loaded.Any(m => m.Name == BaseMain || m.Name == BaseMain + ":latest")
                    ? "The exact window is read from the AI PC when you launch (about 32K is typical). "
                    : $"Ollama is giving this model {RealContext / 1024}K. "
                : "") +
            $"Agents are told the window is {RealContext / 1024}K and replies are capped at {plan.OutputTokens / 1024}K, so they compact instead of overflowing." +
            (RealContext < 49152 ? " Claude Code's own instructions and tools take ~20–25K, so 64K or more works much better for it." : "");
    }

    public string StartCommandPreview(string kind)
    {
        var baseMain = BaseMain.Length > 0 ? BaseMain : "<model>";
        var p = PlanFor(Launcher.EffectiveModel(baseMain, ContextSize),
                        Launcher.EffectiveModel(BaseFast.Length > 0 ? BaseFast : baseMain, ContextSize), RealContext);
        var sb = new System.Text.StringBuilder();
        void Env(IDictionary<string, string> env) { foreach (var (k, v) in env) sb.AppendLine($"$env:{k} = {Launcher.Q(v)}"); }
        sb.AppendLine($"# {p.Main} · context {p.Ctx:N0} tokens · replies up to {p.OutputTokens:N0} · permissions: {Launcher.Mode(PermMode).Label}");
        switch (kind)
        {
            case "claude":
                Env(Launcher.ClaudeEnv(p));
                sb.AppendLine("Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue");
                sb.Append(Launcher.ClaudeCommand(p));
                break;
            case "qwen":
                Env(Launcher.QwenEnv(p, p.O.TellContextWindow ? System.IO.Path.Combine(AppConfig.Folder, "qwen", "settings-….json") : null));
                sb.Append(Launcher.QwenCommand(p));
                if (p.O.TellContextWindow)
                    sb.Append($"\n# maison's settings file tells Qwen Code: contextWindowSize {p.Ctx}, max_tokens {p.OutputTokens}, autoCompactThreshold {p.O.QwenCompactThreshold:0.##}. Your ~/.qwen/settings.json isn't changed.");
                break;
            default:
                sb.Append(Launcher.ChatCommand(p));
                break;
        }
        return sb.ToString();
    }

    /// <summary>The launch plan for a real session (context variant created, real window detected).</summary>
    private async Task<Launcher.Plan> PlanAsync()
    {
        var main = await ResolveAsync(BaseMain);
        var fast = BaseFast == BaseMain ? main : await ResolveAsync(BaseFast);
        var ctx = ContextSize > 0 ? ContextSize : await OllamaClient.EffectiveContextAsync(main);
        return PlanFor(main, fast, ctx);
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
            if (OllamaClient.IsRemote)
                items.Add(new() { Level = Health.Info, Title = "Using your AI pool", Detail = "Models run on the AI PCs in your pool. See and manage them on the AI pool page." });
            else if (SystemProbe.OllamaInstalled)
                items.Add(new() { Level = Health.Ok, Title = "Ollama installed", Detail = SystemProbe.OllamaDir });
            else
                items.Add(new() { Level = Health.Bad, Title = "Ollama is not installed", Detail = "Open Setup to install it." });

            if (SystemProbe.OllamaInstalled && !OllamaClient.IsRemote)
            {
                var cuda = SystemProbe.CudaDirs;
                items.Add(SystemProbe.RuntimeOk
                    ? new() { Level = Health.Ok, Title = "Runtime files present", Detail = "llama-server.exe" + (cuda.Length > 0 ? " · " + string.Join(", ", cuda) : "") }
                    : new() { Level = Health.Bad, Title = "llama-server.exe is missing", Detail = "Models will fail with \"binary not found\" (usually a failed auto-update). Click Repair Ollama." });
            }

            items.Add(running
                ? new() { Level = Health.Ok, Title = $"Server responding · v{version}", Detail = OllamaClient.BaseUrl }
                : OllamaClient.IsRemote
                    ? new() { Level = Health.Bad, Title = "No AI PC in your pool is reachable", Detail = "Check your AI PCs are on with maison running, and that both are on the same network. The AI pool page shows each one's status." }
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
        bool remote = OllamaClient.IsRemote;
        var ok = running && (remote || SystemProbe.RuntimeOk);
        var c = ok ? green : red;
        ServerDot.Fill = new SolidColorBrush(c);
        ServerPill.Background = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
        ServerPillText.Text = remote ? (running ? $"AI on {new Uri(OllamaClient.BaseUrl).Host}" : $"Can't reach {new Uri(OllamaClient.BaseUrl).Host}")
            : !SystemProbe.OllamaInstalled ? "Ollama not installed" : !running ? "Ollama offline" : SystemProbe.RuntimeOk ? "Ollama ready" : "Ollama broken";

        MServer.Text = running ? "Running" : "Offline";
        MServerSub.Text = running ? $"v{version} · {new Uri(OllamaClient.BaseUrl).Authority}"
            : remote ? "check the AI PC" : SystemProbe.OllamaInstalled ? "click Start" : "install from Setup";
        // Start / Restart / Repair act on this PC's Ollama; with the AI on another PC they belong over there.
        foreach (var b in new[] { BtnStart, BtnRestart, BtnRepair })
        {
            b.Tag ??= b.ToolTip;   // the normal tooltip, to put back
            b.IsEnabled = !remote;
            b.ToolTip = remote ? "The AI runs on your AI pool: set up or fix its PCs on the AI pool page" : b.Tag;
        }
        bool noChat = remote && !SystemProbe.OllamaInstalled;
        BtnChat.Tag ??= BtnChat.ToolTip;
        BtnChat.IsEnabled = !noChat;
        BtnChat.ToolTip = noChat ? "Chat uses the ollama command, which isn't installed on this PC. Use Qwen Code or Claude Code, or install Ollama from Setup." : BtnChat.Tag;

        var top = _loaded.FirstOrDefault();
        MLoaded.Text = top?.Name ?? "None";
        MLoadedSub.Text = top is null ? "loads on first request" : $"{top.GpuPercent}% GPU · ctx {top.Context:N0}";
        UpdateCommands();   // the real window may have changed with what's loaded
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
        // The AI's part of each reading (Ollama and its model runner), drawn bright inside the dim total.
        bool ai = s.OllamaRamGb >= 0.05;
        AiShare(GCpu, ai, Math.Min(s.OllamaCpuPercent, s.CpuPercent), $"AI {s.OllamaCpuPercent:0}%");
        AiShare(GRam, ai, s.OllamaRamGb, $"AI {s.OllamaRamGb:0.0} GB");
        AiLegend.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;

        var warn = HealthAdvice.VramWarning(s);
        VramWarnBox.Visibility = warn is null ? Visibility.Collapsed : Visibility.Visible;
        VramWarnText.Text = warn ?? "";

        var g = s.Gpu;
        {
            if (g is null)
            {
                GpuTitle.Text = "Machine · no NVIDIA GPU detected (nvidia-smi unavailable)";
                GpuExtra.Text = s.CpuName;
                foreach (var gauge in new[] { GTemp, GLoad, GPower, GVram }) { gauge.Value = 0; gauge.Display = "—"; AiShare(gauge, false, 0, ""); }
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

            AiShare(GVram, ai, Math.Min(s.OllamaVramGb, g.UsedGb), $"AI {s.OllamaVramGb:0.0} GB");
            var aiLoad = Math.Min(s.OllamaGpuPercent ?? 0, g.LoadPercent);
            AiShare(GLoad, ai && s.OllamaGpuPercent is not null, aiLoad, $"AI {aiLoad:0}%");
            // Power isn't measured per app: estimate the AI's part from its share of the GPU's work.
            var aiPower = g.LoadPercent > 0 && g.PowerW is { } pw ? pw * aiLoad / g.LoadPercent : 0;
            AiShare(GPower, ai && s.OllamaGpuPercent is not null && g.PowerW is not null, aiPower, $"AI ≈{aiPower:0} W");
        }
    }

    private static void AiShare(Controls.ArcGauge gauge, bool show, double value, string text) => gauge.SetAi(show, value, text);

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
        var p = await PlanAsync();
        var main = p.Main;
        var folder = WorkingFolder;
        if (OllamaClient.IsRemote && kind != "chat")   // its own address on the pool relay, so its requests stay together
            p = p with { Url = Services.Pool.PoolRelay.SessionUrl(Services.Pool.PoolRelay.Register(kind, $"{(kind == "claude" ? "Claude Code" : "Qwen Code")} · {main}")) };
        Log($"{kind}: context {p.Ctx:N0} tokens, replies up to {p.OutputTokens:N0}");
        return kind switch
        {
            "claude" => new($"Claude Code · {main}", folder, Launcher.ClaudeEnv(p),
                ["Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue"], Launcher.ClaudeCommand(p)),
            "qwen" => new($"Qwen Code · {main}", folder, Launcher.QwenEnv(p, Launcher.WriteQwenSettings(p)), null, Launcher.QwenCommand(p)),
            _ => new($"Chat · {main}", folder, Launcher.ChatEnv(), null, Launcher.ChatCommand(p)),
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
        ModeToggle.IsChecked = !simple;
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
        if (OllamaClient.IsRemote) { await RefreshAsync(quiet: true); return; }
        if (!SystemProbe.OllamaInstalled) { Navigate("setup"); return; }
        Log("Starting Ollama...");
        var ok = await SystemProbe.StartOllamaAsync();
        Log(ok ? "Ollama is up" : "Ollama did not respond within 20s");
        await RefreshAsync(quiet: true);
    });

    private async void Restart_Click(object sender, RoutedEventArgs e) => await WithBusy(async () =>
    {
        if (OllamaClient.IsRemote) { await RefreshAsync(quiet: true); return; }
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

    private void ModeToggle_Click(object sender, RoutedEventArgs e) => SetSimpleMode(ModeToggle.IsChecked != true);

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    private IntPtr TitleBarHitTest(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084, HTCLIENT = 1;
        if (msg != WM_NCHITTEST || !ModeToggle.IsVisible) return IntPtr.Zero;
        long lp = lParam.ToInt64();
        var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        try
        {
            var local = ModeToggle.PointFromScreen(screen);
            if (local.X >= -4 && local.Y >= -4 && local.X <= ModeToggle.ActualWidth + 4 && local.Y <= ModeToggle.ActualHeight + 4)
            {
                handled = true;
                return new IntPtr(HTCLIENT);
            }
        }
        catch (InvalidOperationException) { /* not connected to a presentation source yet */ }
        return IntPtr.Zero;
    }

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
        foreach (var page in new[] { "models", "setup", "system", "extensions", "settings", "pool", "terminals" })
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
            var tp = (await PlanAsync()) with { Perm = "ask" };
            var main = tp.Main;
            var folder = args[ti + 1];
            var claude = await LaunchAsync(new Launcher.Session($"Claude Code · {main}", folder, Launcher.ClaudeEnv(tp),
                ["Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue"], Launcher.ClaudeCommand(tp)), "claude", "Claude Code", main, Launcher.TargetApp);
            await Task.Delay(15000);
            string claudeScreen = "";
            if (claude is not null) { await claude.CaptureAsync(Path.Combine(dir, "term-claude.png")); claudeScreen = await claude.ReadScreenAsync(); }
            Snap(Path.Combine(dir, "terminals.png"));
            var qwen = await LaunchAsync(new Launcher.Session($"Qwen Code · {main}", folder, Launcher.QwenEnv(tp, Launcher.WriteQwenSettings(tp)), null, Launcher.QwenCommand(tp)),
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

            // The Advanced switch lives in the title bar for both views. Ask the window what Windows would do with a click
            // there (HTCLIENT = 1 means the switch gets it; HTCAPTION = 2 would mean "drag the window").
            bool toggleOffInSimple = ModeToggle.IsChecked == false;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var tp = ModeToggle.PointToScreen(new Point(ModeToggle.ActualWidth / 2, ModeToggle.ActualHeight / 2));
            var lp = new IntPtr(((int)tp.Y << 16) | ((int)tp.X & 0xFFFF));
            long hit = SendMessage(hwnd, 0x0084, IntPtr.Zero, lp).ToInt64();
            simpleResult += $" toggleOffInSimple={toggleOffInSimple} titleBarHitTest={(hit == 1 ? "clickable" : hit == 2 ? "DRAG-AREA" : hit.ToString())}";

            // Switch both ways (a real mouse click only when maison is verifiably in front, so we never click other windows).
            Activate();
            await Task.Delay(400);
            bool front = GetForegroundWindow() == hwnd;
            if (front) await RealClickAsync(ModeToggle);
            else { ModeToggle.IsChecked = true; ModeToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
            await Task.Delay(1200);
            bool toAdvanced = AdvancedRoot.Visibility == Visibility.Visible && ModeToggle.IsChecked == true;
            Snap(Path.Combine(dir, "mode-advanced.png"));
            if (front && GetForegroundWindow() == hwnd) await RealClickAsync(ModeToggle);
            else { ModeToggle.IsChecked = false; ModeToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
            await Task.Delay(1200);
            bool toSimple = SimplePage.Visibility == Visibility.Visible && ModeToggle.IsChecked == false;
            Snap(Path.Combine(dir, "mode-simple.png"));
            simpleResult += $" switchToAdvanced={toAdvanced} switchToSimple={toSimple} via={(front ? "real-mouse" : "click-event (maison not in front)")}";
            foreach (var s in TerminalHub.Sessions) TerminalHub.Remove(s);
            SetSimpleMode(before == "simple");
            _config.UiMode = before;
            _config.Save();
            await SetupPage.RefreshAsync();
        }

        // Optional: --jitter <workdir> records every frame Qwen Code sends while it streams a long answer.
        var jitterResult = "";
        var ji = Array.IndexOf(args, "--jitter");
        if (ji >= 0 && ji + 1 < args.Length && BaseMain.Length > 0)
        {
            var jp = (await PlanAsync()) with { Perm = "ask" };
            var v = await LaunchAsync(new Launcher.Session($"Qwen Code · {jp.Main}", args[ji + 1], Launcher.QwenEnv(jp, Launcher.WriteQwenSettings(jp)), null, Launcher.QwenCommand(jp)),
                "qwen", "Qwen Code", jp.Main, Launcher.TargetApp);
            if (v is not null)
            {
                await Task.Delay(20000);
                var frames = new System.Text.StringBuilder();
                var sw = Stopwatch.StartNew();
                void Rec(TermSession _, string t)
                {
                    lock (frames) frames.Append($"{sw.ElapsedMilliseconds,7} {t.Length,6} ").Append(t.Replace("\x1b", "\\e").Replace("\r", "\\r").Replace("\n", "\\n")).Append('\n');
                }
                v.Session.Output += Rec;
                await v.TypeAsync("Write a numbered list of 60 different fruits, one per line, each with a short description. No tools.");
                await Task.Delay(500);
                await v.TypeAsync("\r");
                var scroll = new System.Text.StringBuilder();
                for (int i = 0; i < 300; i++)   // 60 s
                {
                    scroll.Append(sw.ElapsedMilliseconds).Append(' ').Append(await v.EvalAsync("term.buffer.active.viewportY + ',' + term.buffer.active.baseY + ',' + term.buffer.active.cursorY")).Append('\n');
                    await Task.Delay(200);
                }
                v.Session.Output -= Rec;
                File.WriteAllText(Path.Combine(dir, "jitter-frames.txt"), frames.ToString());
                File.WriteAllText(Path.Combine(dir, "jitter-scroll.txt"), scroll.ToString());
                await v.CaptureAsync(Path.Combine(dir, "jitter-end.png"));
                jitterResult = "JITTER: recorded";
            }
        }

        // Optional: --launchopts edits Settings → Start commands through its controls and checks preview + launch spec.
        var launchResult = "";
        if (args.Contains("--launchopts"))
        {
            Navigate("settings");
            await Task.Delay(800);
            var sp = SettingsPage;
            sp.LClaudeContinue.IsChecked = true;
            sp.LClaudeContinue.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            sp.LClaudeArgs.Text = "--verbose";
            sp.LClaudeEnv.Text = "# comment\nMY_TEST_VAR=hello\nbad name=x";
            sp.LMaxOut.Value = 12288;
            await Task.Delay(400);
            var claudePreview = sp.LPreview.Text;
            sp.LTabs.SelectedIndex = 1;
            sp.LQwenPct.Value = 70;
            await Task.Delay(400);
            var qwenPreview = sp.LPreview.Text;
            var spec = (await BuildSpecAsync("claude"))!;
            var qspec = (await BuildSpecAsync("qwen"))!;
            var qsettings = qspec.Env?.TryGetValue("QWEN_CODE_SYSTEM_SETTINGS_PATH", out var qp) == true && File.Exists(qp) ? File.ReadAllText(qp) : "";
            sp.LTabs.SelectedIndex = 0;
            sp.LPreview.BringIntoView();
            await Task.Delay(500);
            Snap(Path.Combine(dir, "launchopts.png"));
            launchResult = "LAUNCHOPTS: " +
                $"previewContinue={claudePreview.Contains("--continue")} previewArgs={claudePreview.Contains("--verbose")} " +
                $"previewEnv={claudePreview.Contains("MY_TEST_VAR = 'hello'")} badEnvSkipped={!claudePreview.Contains("bad name")} " +
                $"previewMaxOut={claudePreview.Contains("CLAUDE_CODE_MAX_OUTPUT_TOKENS = '12288'")} " +
                $"specCmd=[{spec.Command}] specEnv={(spec.Env is { } se && se.TryGetValue("MY_TEST_VAR", out var mv) ? mv : "")} " +
                $"qwenPreviewThreshold={qwenPreview.Contains("autoCompactThreshold 0.7")} qwenSettingsThreshold={qsettings.Contains("\"autoCompactThreshold\": 0.7")} " +
                $"qwenSettingsMaxTokens={qsettings.Contains("\"max_tokens\": 12288")} saved={_config.Launch.ClaudeContinue && _config.Launch.MaxOutputTokens == 12288} " +
                $"dashboardCommands={Commands.Text.Contains("--continue")}";
            sp.GetType().GetMethod("ResetLaunch_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(sp, [null, new RoutedEventArgs()]);
            await Task.Delay(300);
            launchResult += $" resetOk={!_config.Launch.ClaudeContinue && _config.Launch.MaxOutputTokens == 0 && !Commands.Text.Contains("--continue")}";
            Navigate("dashboard");
        }

        // Optional: --pool <port:code,port:code> pairs with AI PCs started with --pool-host on this machine and checks
        // discovery, pairing, routing, spreading work, moving off a busy PC, agents shown, remote install and keys.
        var poolResult = "";
        if (args.Contains("--pool"))
        {
            var spec = args[Array.IndexOf(args, "--pool") + 1].Split(',').Select(x => x.Split(':')).Select(x => (Port: int.Parse(x[0]), Code: x[1])).ToList();
            var pr = new List<string>();
            void PAdd(string s) { pr.Add(s); try { File.AppendAllText(Path.Combine(dir, "pool-progress.txt"), s + Environment.NewLine); } catch { } }   // progress file: shows where it stopped if it hangs
            try
            {
                _config.PoolIncludeThisPc = false;
                _config.Save();
                var found = await Services.Pool.AiPool.DiscoverAsync();
                PAdd($"discovered={found.Count}");
                var pcs = new List<Services.Pool.FoundPc>();
                foreach (var (port, _) in spec) if (await Services.Pool.AiPool.LookupAsync($"127.0.0.1:{port}") is { } pc) pcs.Add(pc);
                PAdd($"lookup={pcs.Count}/{spec.Count}");
                var (badOk, _) = await Services.Pool.AiPool.PairAsync(pcs[0], "000000");
                PAdd($"badCodeRejected={!badOk}");
                for (int k = 0; k < pcs.Count; k++)
                {
                    var (ok, msg) = await Services.Pool.AiPool.PairAsync(pcs[k], spec[k].Code);
                    PAdd($"pair{k}={ok}");
                    if (!ok) PAdd($"pairMsg={msg}");
                }
                await SetUsePoolAsync(true);
                await Services.Pool.AiPool.RefreshAsync();
                var members = Services.Pool.AiPool.Members.ToList();
                PAdd($"online={members.Count(m => m.Online)}/{members.Count}");
                using var relay = new System.Net.Http.HttpClient { BaseAddress = new Uri(Services.Pool.PoolRelay.Url), Timeout = TimeSpan.FromMinutes(5) };
                var tags = await relay.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/tags");
                var models = tags.GetProperty("models").EnumerateArray()
                    .Select(m => (Name: m.GetProperty("name").GetString()!, Size: m.GetProperty("size").GetInt64()))
                    .Where(m => !System.Text.RegularExpressions.Regex.IsMatch(m.Name, @"-ctx\d+k")).OrderBy(m => m.Size).ToList();
                PAdd($"relayTags={models.Count}");
                var small = models[0].Name;
                PAdd($"model={small}");
                object Gen(int n) => new { model = small, prompt = "Count from 1 upwards, one number per line.", stream = false, options = new { num_predict = n }, keep_alive = "10m" };

                // warm up (load the model) through a session-less request
                await relay.PostAsJsonAsync("/api/generate", Gen(1));
                // two sessions at once go to different PCs
                var a = Services.Pool.PoolRelay.Register("qwen", "Test A");
                var b = Services.Pool.PoolRelay.Register("claude", "Test B");
                var ta = relay.PostAsJsonAsync($"/s/{a}/api/generate", Gen(400));
                await Task.Delay(800);
                var rb = await relay.PostAsJsonAsync($"/s/{b}/api/generate", Gen(5));
                await ta;
                var routes = Services.Pool.PoolRelay.Routes.ToDictionary(r => r.Id);
                PAdd($"genOk={rb.IsSuccessStatusCode}");
                PAdd($"spread={routes[a].MemberId != routes[b].MemberId}");

                // someone else keeps A's PC busy: A's next request moves to the other PC
                var busyMember = members.First(m => m.Member.Id == routes[a].MemberId);
                var handler = new System.Net.Http.HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
                using var other = new System.Net.Http.HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{busyMember.Member.Port}"), Timeout = TimeSpan.FromMinutes(5) };
                var code = spec[members.IndexOf(busyMember)].Code;
                var pairResp = await other.PostAsJsonAsync("/maison/pair", new Services.Pool.PairRequest(code, "OTHER-PC", "other-pc-id"));
                var otherToken = (await pairResp.Content.ReadFromJsonAsync<Services.Pool.PairReply>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!.Token;
                other.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", otherToken);
                var busy = other.PostAsJsonAsync("/api/generate", Gen(600));
                await Task.Delay(300);
                await Services.Pool.AiPool.RefreshAsync();
                PAdd($"seenBusy={busyMember.BusyForUs}");
                var agentsSeen = busyMember.Info?.Agents.Select(x => $"{x.Kind}@{x.Client}").ToList() ?? [];
                PAdd($"agents=[{string.Join(" ", agentsSeen)}]");
                await relay.PostAsJsonAsync($"/s/{a}/api/generate", Gen(5));
                routes = Services.Pool.PoolRelay.Routes.ToDictionary(r => r.Id);
                PAdd($"movedOffBusy={routes[a].MemberId != busyMember.Member.Id && routes[a].Moves == 1}");
                await busy;

                // bad key is refused
                using var bad = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
                    { BaseAddress = new Uri($"https://127.0.0.1:{busyMember.Member.Port}") };
                bad.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-real-key-not-a-real-key");
                PAdd($"badKey={(int)(await bad.GetAsync("/api/tags")).StatusCode}");

                // remote install (a model it already has: quick) and its progress
                var target = members[^1];
                var (pullOk, _) = await Services.Pool.AiPool.InstallModelAsync(target.Member, small);
                Services.Pool.PoolTask? task = null;
                for (int k = 0; k < 60 && pullOk; k++)
                {
                    await Task.Delay(1000);
                    await Services.Pool.AiPool.RefreshAsync();
                    task = target.Info?.Task;
                    if (task is { Running: false }) break;
                }
                PAdd($"remotePull={pullOk && task is { Running: false, Error: null }}");
                if (task?.Error is { } te) PAdd($"pullErr={te}");

                Navigate("pool");
                await Task.Delay(2500);
                Snap(Path.Combine(dir, "pool-test.png"));
                await SetUsePoolAsync(false);
            }
            catch (Exception ex) { PAdd($"error={ex.GetType().Name}: {ex.Message}"); }
            poolResult = string.Join(" ", pr);
        }

        // Optional: --update checks GitHub for real: as this version (expect no update unless one is released), then as
        // 0.0.1 (expect the title-bar button), and the version comparison rules.
        var updateResult = "";
        if (args.Contains("--update"))
        {
            var ur = new List<string>();
            try
            {
                var now = await CheckForUpdatesAsync(manual: true);
                ur.Add($"current=\"{now}\"");
                var old = await CheckForUpdatesAsync(true, "0.0.1");
                ur.Add($"as001=\"{old}\" button={UpdateButton.Visibility == Visibility.Visible} label=\"{UpdateButton.Content}\" exe={_update?.DownloadUrl}");
                Navigate("dashboard");
                await Task.Delay(600);
                Snap(Path.Combine(dir, "update-button.png"));
                ur.Add($"rules={UpdateCheck.IsNewer("v0.0.10", "0.0.9") && !UpdateCheck.IsNewer("v0.0.5", "0.0.5") && !UpdateCheck.IsNewer("v0.0.4", "0.0.5") && UpdateCheck.IsNewer("v0.1.0-beta", "0.0.9") && !UpdateCheck.IsNewer("garbage", "0.0.1")}");
                await CheckForUpdatesAsync(manual: true);   // back to the real state
                ur.Add($"hiddenAgain={UpdateButton.Visibility != Visibility.Visible}");
            }
            catch (Exception ex) { ur.Add($"error={ex.Message}"); }
            updateResult = "UPDATE: " + string.Join(" ", ur);
        }

        // Optional: --toast opens a terminal tab, shows a message and checks Windows really puts it above the terminal.
        var toastResult = "";
        if (args.Contains("--toast"))
        {
            Navigate("terminals");
            await TerminalsPage.OpenAsync(new Launcher.Session("Toast test", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null, null, "Get-ChildItem $env:WINDIR | Select-Object -First 40"), "shell");
            await Task.Delay(4000);
            Activate();
            Toast("Toast test", "This message should float above the terminal.", ControlAppearance.Success, SymbolRegular.Alert24);
            await Task.Delay(1200);
            var bar = FindDescendant<Snackbar>(_toasts.Presenter);
            var toastHwnd = new System.Windows.Interop.WindowInteropHelper(_toasts).Handle;
            var mainHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            string Owner(Point screen)
            {
                var h = GetAncestor(WindowFromPoint(new PT { X = (int)screen.X, Y = (int)screen.Y }), 2);
                return h == toastHwnd ? "toast" : h == mainHwnd ? "main" : "other";
            }
            var onToast = bar is { ActualWidth: > 0 } ? Owner(bar.PointToScreen(new Point(bar.ActualWidth / 2, bar.ActualHeight / 2))) : "no-snackbar";
            var viewer = FindViewers(Root).FirstOrDefault(v => v.IsVisible);
            var onTerminalTop = viewer is not null ? Owner(viewer.PointToScreen(new Point(viewer.ActualWidth / 2, 20))) : "no-terminal";
            var topLeft = PointToScreen(new Point(0, 0));
            var size = PointToScreen(new Point(ActualWidth, ActualHeight));
            using (var shot = new System.Drawing.Bitmap((int)(size.X - topLeft.X), (int)(size.Y - topLeft.Y)))
            {
                using (var g = System.Drawing.Graphics.FromImage(shot)) g.CopyFromScreen((int)topLeft.X, (int)topLeft.Y, 0, 0, shot.Size);
                shot.Save(Path.Combine(dir, "toast-screen.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            toastResult = $"TOAST: underToast={onToast} terminalTop={onTerminalTop} foreground={GetForegroundWindow() == mainHwnd}";
            Navigate("dashboard");
        }

        // Optional: --accent switches accent colours live and reads the colour the switches are actually drawn in.
        var accentResult = "";
        if (args.Contains("--accent"))
        {
            var before = _config.Accent;
            Navigate("settings");
            await Task.Delay(800);
            var parts = new List<string>();
            foreach (var name in new[] { "Coral", "Blue", "Green" })
            {
                Theming.ApplyAccent(name);
                await Task.Delay(700);
                var want = (Color)ColorConverter.ConvertFromString(Theming.Accents.First(a => a.Name == name).Hex);
                var toggle = DominantColour(ModeToggle);
                var idle = DominantColour(SettingsPage.IdleNotify);
                parts.Add($"{name}: want={want.R},{want.G},{want.B} titleToggle={toggle} settingsToggle={idle} " +
                          $"match={(Near(toggle, want) && Near(idle, want))}");
                Snap(Path.Combine(dir, $"accent-{name.ToLowerInvariant()}.png"));
            }
            Theming.ApplyAccent(before);
            Navigate("dashboard");
            accentResult = "ACCENT: " + string.Join(" | ", parts);
        }

        // Optional: --portal <password> starts the web portal and exercises it over real HTTPS (use with MAISON_CONFIG_DIR).
        var po = Array.IndexOf(args, "--portal");
        var portalResult = "";
        if (po >= 0 && po + 1 < args.Length)
        {
            try { portalResult = await Services.PortalSelfTest.RunAsync(args[po + 1], dir); }
            catch (Exception ex) { portalResult = "PORTAL: exception " + ex; }
        }

        var lines = new List<string> { "STATUS:", pullResult, termResult, simpleResult, accentResult, toastResult, jitterResult, launchResult, poolResult, updateResult, portalResult };
        lines.AddRange(_status.Select(s => $"[{s.Level}] {s.Title} | {s.Detail}"));
        lines.Add($"GAUGES: temp={GTemp.Display} load={GLoad.Display} power={GPower.Display} vram={GVram.Display} cpu={GCpu.Display} ram={GRam.Display} | ai: load={GLoad.AiDisplay} power={GPower.AiDisplay} vram={GVram.AiDisplay} cpu={GCpu.AiDisplay} ram={GRam.AiDisplay} | {GpuTitle.Text} | {GpuExtra.Text}");
        if (_stats is { } st)
            lines.Add("VRAM USERS: " + string.Join(", ", st.VramUsers.Select(p => $"{p.Name} {p.VramGb:0.0}GB")) + $" | warning: {HealthAdvice.VramWarning(st) ?? "none"}");
        lines.Add($"VERSION: {AppInfo.Version} | PERMS: {_config.Permissions} | ACCENT: {_config.Accent} | UIMODE: {_config.UiMode}");
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

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    private struct PT { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindDescendant<T>(c) is { } d) return d;
        }
        return null;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    /// <summary>Self-test: a genuine left click in the middle of an element (not a synthetic Click event).</summary>
    private async Task RealClickAsync(FrameworkElement el)
    {
        Activate();
        await Task.Delay(300);
        var p = el.PointToScreen(new Point(el.ActualWidth / 2, el.ActualHeight / 2));
        SetCursorPos((int)p.X, (int)p.Y);
        await Task.Delay(150);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // left down
        await Task.Delay(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // left up
    }

    /// <summary>Self-test: the most saturated colour an element is drawn with (e.g. a switch's "on" fill).</summary>
    private static Color? DominantColour(FrameworkElement? el)
    {
        if (el is null || el.ActualWidth < 1) return null;
        var bmp = new RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        // Through a brush: Render(el) would apply el's offset in its parent and miss it.
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        bmp.Render(dv);
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        Color? best = null; int bestSat = 40;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] < 200) continue;
            int b = px[i], g = px[i + 1], r = px[i + 2];
            int sat = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            if (sat > bestSat) { bestSat = sat; best = Color.FromRgb((byte)r, (byte)g, (byte)b); }
        }
        return best;
    }

    private static bool Near(Color? a, Color b) =>
        a is { } x && Math.Abs(x.R - b.R) + Math.Abs(x.G - b.G) + Math.Abs(x.B - b.B) < 60;

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
