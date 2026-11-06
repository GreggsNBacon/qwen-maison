using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QwenLauncher.Models;
using QwenLauncher.Services;
using Wpf.Ui;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace QwenLauncher;

public partial class MainWindow : FluentWindow
{
    private readonly ObservableCollection<StatusItem> _status = [];
    private readonly AppConfig _config = AppConfig.Load();
    private readonly SnackbarService _snackbar = new();
    private readonly DispatcherTimer _autoRefresh = new() { Interval = TimeSpan.FromSeconds(10) };
    private List<ModelInfo> _models = [];
    private List<LoadedModel> _loaded = [];
    private bool _refreshing;
    private bool _initializing = true;

    public MainWindow()
    {
        InitializeComponent();
        StatusList.ItemsSource = _status;
        _snackbar.SetSnackbarPresenter(Snackbar);

        foreach (var k in Launcher.ContextSizes.Keys) Context.Items.Add(k);
        MainModel.Text = _config.MainModel;
        FastModel.Text = _config.FastModel;
        Context.SelectedItem = Launcher.ContextSizes.ContainsKey(_config.Context) ? _config.Context : "64K";
        WorkDir.Text = _config.WorkDir;
        UseTerminal.IsChecked = _config.UseTerminal && SystemProbe.HasWindowsTerminal;
        UseTerminal.IsEnabled = SystemProbe.HasWindowsTerminal;
        SkipPerms.IsChecked = _config.SkipPermissions;
        _initializing = false;

        UpdateCommands();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            _autoRefresh.Tick += async (_, _) => await RefreshAsync(quiet: true);
            _autoRefresh.Start();
            await RunSelfTestIfRequestedAsync();
        };
        Closing += (_, _) => SaveConfig();
    }

    // ------------------------------------------------------------------ selection helpers

    private string BaseMain => string.IsNullOrWhiteSpace(MainModel.Text) ? "qwen3.8:27b" : MainModel.Text.Trim();
    private string BaseFast => string.IsNullOrWhiteSpace(FastModel.Text) ? BaseMain : FastModel.Text.Trim();
    private int ContextSize => Context.SelectedItem is string s && Launcher.ContextSizes.TryGetValue(s, out var n) ? n : 0;
    private bool Skip => SkipPerms.IsChecked == true;

    private void SaveConfig()
    {
        _config.MainModel = MainModel.Text.Trim();
        _config.FastModel = FastModel.Text.Trim();
        _config.Context = Context.SelectedItem as string ?? "64K";
        _config.WorkDir = WorkDir.Text.Trim();
        _config.UseTerminal = UseTerminal.IsChecked == true;
        _config.SkipPermissions = Skip;
        _config.Save();
    }

    private void UpdateCommands()
    {
        if (_initializing) return;
        var main = Launcher.EffectiveModel(BaseMain, ContextSize);
        var fast = Launcher.EffectiveModel(BaseFast, ContextSize);
        Commands.Text = Launcher.CommandsText(BaseMain, main, fast, ContextSize, Skip);
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
            var versionTask = OllamaClient.GetVersionAsync();
            var gpuTask = SystemProbe.QueryGpuAsync();
            var version = await versionTask;
            var gpu = await gpuTask;
            bool running = version is not null;

            if (running)
            {
                try { _models = await OllamaClient.GetModelsAsync(); } catch { _models = []; }
                try { _loaded = await OllamaClient.GetLoadedAsync(); } catch { _loaded = []; }
            }
            else { _models = []; _loaded = []; }

            var items = new List<StatusItem>();
            if (SystemProbe.OllamaInstalled)
                items.Add(new() { Level = Health.Ok, Title = "Ollama installed", Detail = SystemProbe.OllamaDir });
            else
                items.Add(new() { Level = Health.Bad, Title = "Ollama is not installed", Detail = "Use Repair Ollama to install it with winget." });

            if (SystemProbe.OllamaInstalled)
            {
                var cuda = SystemProbe.CudaDirs;
                items.Add(SystemProbe.RuntimeOk
                    ? new() { Level = Health.Ok, Title = "Runtime files present", Detail = "llama-server.exe" + (cuda.Length > 0 ? " · " + string.Join(", ", cuda) : "") }
                    : new() { Level = Health.Bad, Title = "llama-server.exe is missing", Detail = "Models will fail with \"binary not found\" (usually a failed auto-update). Click Repair Ollama." });
            }

            items.Add(running
                ? new() { Level = Health.Ok, Title = $"Server responding · v{version}", Detail = OllamaClient.BaseUrl }
                : new() { Level = Health.Bad, Title = "Server not responding", Detail = "Click Start." });

            foreach (var m in _loaded)
                items.Add(new()
                {
                    Level = m.GpuPercent >= 100 ? Health.Ok : Health.Warn,
                    Title = $"Loaded: {m.Name}",
                    Detail = $"{m.SizeGb} GB · {m.GpuPercent}% GPU · context {m.Context:N0}" + (m.GpuPercent < 100 ? " — partly on CPU, will be slow; pick a smaller context" : ""),
                });

            if (running)
            {
                var real = _models.Where(m => !m.Name.StartsWith("claude-") && !Launcher.IsVariant(m.Name)).ToList();
                var visible = real.Select(m => $"{m.Name} ({m.SizeGb} GB)");
                items.Add(new() { Level = Health.Info, Title = $"{real.Count} models installed", Detail = string.Join(" · ", visible) });

                var legacy = _models.Where(m => m.Name.StartsWith("claude-")).Select(m => m.Name).ToList();
                if (legacy.Count > 0)
                    items.Add(new() { Level = Health.Warn, Title = "Old claude-* alias models found", Detail = string.Join(", ", legacy) + " — not needed by this launcher." });
                BtnAliases.Visibility = legacy.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                if (!_models.Any(m => m.Name == BaseMain))
                    items.Add(new() { Level = Health.Warn, Title = $"Model '{BaseMain}' is not installed", Detail = $"ollama pull {BaseMain}" });

                UpdateModelLists();
            }

            var claude = SystemProbe.FindOnPath("claude");
            var qwen = SystemProbe.FindOnPath("qwen");
            items.Add(claude is not null
                ? new() { Level = Health.Ok, Title = "Claude Code CLI found", Detail = claude }
                : new() { Level = Health.Warn, Title = "Claude Code CLI not found", Detail = "irm https://claude.ai/install.ps1 | iex" });
            items.Add(qwen is not null
                ? new() { Level = Health.Ok, Title = "Qwen Code CLI found", Detail = qwen }
                : new() { Level = Health.Warn, Title = "Qwen Code CLI not found", Detail = "npm install -g @qwen-code/qwen-code" });

            _status.Clear();
            foreach (var i in items) _status.Add(i);

            UpdateMetrics(running, version, gpu);
            if (!quiet) Log(running ? $"Health check OK · Ollama v{version}" : "Health check · Ollama server not responding");
        }
        catch (Exception ex) { Log("Health check failed: " + ex.Message); }
        finally { _refreshing = false; }
    }

    private void UpdateMetrics(bool running, string? version, GpuInfo? gpu)
    {
        var red = (Color)ColorConverter.ConvertFromString("#E5534B");
        var green = (Color)ColorConverter.ConvertFromString("#4CC38A");
        var ok = running && SystemProbe.RuntimeOk;
        ServerDot.Fill = new SolidColorBrush(ok ? green : red);
        ServerPill.Background = new SolidColorBrush(Color.FromArgb(0x33, (ok ? green : red).R, (ok ? green : red).G, (ok ? green : red).B));
        ServerPillText.Text = !running ? "Ollama offline" : SystemProbe.RuntimeOk ? "Ollama ready" : "Ollama broken";

        MServer.Text = running ? "Running" : "Offline";
        MServerSub.Text = running ? $"v{version} · 127.0.0.1:11434" : "click Start";

        if (gpu is not null)
        {
            MVram.Text = $"{gpu.UsedGb:0.0} / {gpu.TotalGb:0} GB";
            MVramBar.Value = gpu.TotalGb > 0 ? 100 * gpu.UsedGb / gpu.TotalGb : 0;
            MGpu.Text = $"{gpu.Name.Replace("NVIDIA GeForce ", "")} · load {gpu.LoadPercent}%";
        }
        else { MVram.Text = "No NVIDIA GPU"; MGpu.Text = "nvidia-smi not available"; MVramBar.Value = 0; }

        var top = _loaded.FirstOrDefault();
        MLoaded.Text = top?.Name ?? "None";
        MLoadedSub.Text = top is null ? "loads on first request" : $"{top.GpuPercent}% GPU · ctx {top.Context:N0}";
    }

    private void UpdateModelLists()
    {
        var names = _models.Select(m => m.Name).Where(n => !n.StartsWith("claude-") && !Launcher.IsVariant(n)).ToList();
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

    // ------------------------------------------------------------------ logging / feedback

    private void Log(string message)
    {
        Log_AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        LastActivity.Text = message;
    }

    private void Log_AppendLine(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void Toast(string title, string message, ControlAppearance appearance = ControlAppearance.Secondary, SymbolRegular icon = SymbolRegular.Info24) =>
        _snackbar.Show(title, message, appearance, new SymbolIcon(icon), TimeSpan.FromSeconds(4));

    private async Task<bool> ConfirmAsync(string title, string text, string yes)
    {
        var box = new MessageBox { Title = title, Content = text, PrimaryButtonText = yes, CloseButtonText = "Cancel" };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    private async Task<bool> EnsureServerAsync()
    {
        if (await OllamaClient.GetVersionAsync(2) is not null) return true;
        Toast("Ollama is not running", "Click Start first.", ControlAppearance.Caution, SymbolRegular.Warning24);
        return false;
    }

    private async Task WithBusy(Func<Task> work)
    {
        Cursor = Cursors.Wait;
        try { await work(); }
        catch (Exception ex) { Log("Error: " + ex.Message); Toast("Something went wrong", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
        finally { Cursor = null; }
    }

    // ------------------------------------------------------------------ launch buttons

    private async void Claude_Click(object sender, RoutedEventArgs e) => await WithBusy(async () =>
    {
        if (!await EnsureServerAsync()) return;
        var main = await ResolveAsync(BaseMain);
        var fast = BaseFast == BaseMain ? main : await ResolveAsync(BaseFast);
        Launcher.Open($"Claude Code · {main} (local)", WorkDir.Text.Trim(), Launcher.ClaudeEnv(main, fast),
            ["Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue"], Launcher.ClaudeCommand(Skip), UseTerminal.IsChecked == true);
        Launched("Claude Code", main);
    });

    private async void Qwen_Click(object sender, RoutedEventArgs e) => await WithBusy(async () =>
    {
        if (!await EnsureServerAsync()) return;
        var main = await ResolveAsync(BaseMain);
        Launcher.Open($"Qwen Code · {main} (local)", WorkDir.Text.Trim(), Launcher.QwenEnv(main), null, Launcher.QwenCommand(main, Skip), UseTerminal.IsChecked == true);
        Launched("Qwen Code", main);
    });

    private async void Chat_Click(object sender, RoutedEventArgs e) => await WithBusy(async () =>
    {
        if (!await EnsureServerAsync()) return;
        var main = await ResolveAsync(BaseMain);
        Launcher.Open($"Chat · {main}", WorkDir.Text.Trim(), null, null, Launcher.ChatCommand(main), UseTerminal.IsChecked == true);
        Launched("Chat", main);
    });

    private void Launched(string what, string model)
    {
        SaveConfig();
        Log($"Launched {what} with {model} in {WorkDir.Text.Trim()}");
        Toast($"{what} launched", $"{model} · {WorkDir.Text.Trim()}", ControlAppearance.Success, SymbolRegular.Rocket24);
    }

    // ------------------------------------------------------------------ ollama buttons

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Start_Click(object sender, RoutedEventArgs e) => await WithBusy(async () =>
    {
        Log("Starting Ollama...");
        var ok = await SystemProbe.StartOllamaAsync();
        Log(ok ? "Ollama is up" : "Ollama did not respond within 15s");
        await RefreshAsync(quiet: true);
    });

    private async void Restart_Click(object sender, RoutedEventArgs e) => await WithBusy(async () =>
    {
        Log("Restarting Ollama...");
        await SystemProbe.StopOllamaAsync();
        var ok = await SystemProbe.StartOllamaAsync();
        Log(ok ? "Ollama restarted" : "Ollama did not come back within 15s");
        await RefreshAsync(quiet: true);
    });

    private async void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureServerAsync()) return;
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
        if (_loaded.Count == 0) { Log("Nothing loaded"); return; }
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
    private void Skip_Click(object sender, RoutedEventArgs e) => UpdateCommands();

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
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QwenLauncher");
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Qwen Local Launcher.lnk");
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

    /// <summary>QwenLauncher.exe --selftest &lt;dir&gt; : renders a screenshot + status dump, then exits.</summary>
    private async Task RunSelfTestIfRequestedAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--selftest");
        if (i < 0 || i + 1 >= args.Length) return;
        var dir = args[i + 1];
        Directory.CreateDirectory(dir);
        await Task.Delay(1500);
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
        using (var f = File.Create(Path.Combine(dir, "screenshot.png"))) enc.Save(f);

        var lines = new List<string> { "STATUS:" };
        lines.AddRange(_status.Select(s => $"[{s.Level}] {s.Title} | {s.Detail}"));
        lines.Add($"METRICS: {MServer.Text} | {MVram.Text} | {MLoaded.Text} | {MSpeed.Text}");
        lines.Add("MAIN ITEMS: " + string.Join(", ", MainModel.Items.Cast<object>()));
        lines.Add("COMMANDS:\n" + Commands.Text);
        lines.Add("LOG:\n" + LogBox.Text);
        File.WriteAllLines(Path.Combine(dir, "selftest.txt"), lines);
        Application.Current.Shutdown();
    }
}
