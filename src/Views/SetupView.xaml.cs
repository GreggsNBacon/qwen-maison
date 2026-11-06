using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QwenLauncher.Models;
using QwenLauncher.Services;
using Wpf.Ui.Controls;

namespace QwenLauncher.Views;

public partial class SetupView : UserControl
{
    private readonly List<SetupStep> _steps;
    private readonly SetupStep _ollama, _server, _model, _claude, _node, _qwen;
    private CatalogModel _recommended = ModelCatalog.All[0];
    private bool _runningAll;

    public IShell Shell { get; set; } = null!;

    public SetupView()
    {
        InitializeComponent();
        _ollama = new SetupStep("Ollama", "Runs the models on this PC. Downloads the official installer from ollama.com (per-user, no admin needed).", "Install");
        _server = new SetupStep("Ollama server", "The local service Claude Code and Qwen Code talk to (127.0.0.1:11434).", "Start");
        _model = new SetupStep("Qwen model", "", "Download");
        _claude = new SetupStep("Claude Code", "Anthropic's coding agent. Installed with the official script from claude.ai; the launcher points it at your local model.", "Install");
        _node = new SetupStep("Node.js", "Needed by Qwen Code. Installed with winget; Windows may ask for admin approval once.", "Install");
        _qwen = new SetupStep("Qwen Code", "Qwen's own coding agent (npm package @qwen-code/qwen-code).", "Install");
        _steps = [_ollama, _server, _model, _claude, _node, _qwen];
        StepList.ItemsSource = _steps;
    }

    /// <summary>True when the essentials (Ollama + at least one model) are missing — used to open Setup on first run.</summary>
    public static async Task<bool> NeedsSetupAsync()
    {
        if (!SystemProbe.OllamaInstalled) return true;
        if (await OllamaClient.GetVersionAsync(2) is null) return false; // installed but stopped: Dashboard handles it
        try { return (await OllamaClient.GetModelsAsync()).Count(m => !m.Name.StartsWith("claude-")) == 0; }
        catch { return false; }
    }

    public async Task RefreshAsync()
    {
        var gpu = await SystemProbe.QueryGpuAsync();
        double vram = gpu?.TotalGb ?? 0, ram = SystemProbe.RamGb;
        _recommended = ModelCatalog.Recommend(vram, ram);

        SysGpu.Text = gpu is null ? "No NVIDIA GPU" : gpu.Name.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "");
        SysGpuSub.Text = gpu is null ? "models will run on the CPU" : $"{vram:N0} GB VRAM";
        SysRam.Text = $"{ram:N0} GB";
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(OllamaClient.ModelsDir))!);
            SysDisk.Text = $"{drive.AvailableFreeSpace / 1073741824.0:N0} GB free";
            SysDiskSub.Text = $"on {drive.Name.TrimEnd('\\')} (model storage)";
        }
        catch { SysDisk.Text = "—"; }
        SysRec.Text = _recommended.Title;
        SysRecSub.Text = $"{_recommended.Tag} · {_recommended.SizeGb:0.#} GB";

        bool installed = SystemProbe.OllamaInstalled, runtime = SystemProbe.RuntimeOk;
        bool running = await OllamaClient.GetVersionAsync(2) is not null;
        List<ModelInfo> models = [];
        if (running) { try { models = await OllamaClient.GetModelsAsync(); } catch { } }

        if (!_ollama.Busy)
        {
            if (installed && runtime) _ollama.SetDone("Installed");
            else if (installed) _ollama.SetMissing("Broken install — llama-server.exe missing", "Repair", "Reinstall Ollama and fix locked files (opens a PowerShell window)");
            else _ollama.SetMissing("Not installed", "Install", "Download and install Ollama (~1.6 GB download)");
        }
        if (!_server.Busy)
        {
            if (running) _server.SetDone("Running");
            else _server.SetMissing(installed ? "Not running" : "Needs Ollama", "Start", "Start the Ollama server", enabled: installed);
        }
        if (!_model.Busy)
        {
            _model.Title = $"Qwen model · {_recommended.Tag}";
            _model.Description = $"{_recommended.Description} Chosen for {(vram > 0 ? $"{vram:N0} GB of GPU memory" : "a CPU-only PC")}; pick others on the Models page.";
            var have = models.Any(m => m.Name.Equals(_recommended.Tag, StringComparison.OrdinalIgnoreCase));
            var other = models.FirstOrDefault(m => !m.Name.StartsWith("claude-") && !Launcher.IsVariant(m.Name));
            if (have) _model.SetDone("Installed");
            else if (other is not null) _model.SetDone($"Using {other.Name}", optionalButton: "Download", tip: $"Also download {_recommended.Tag} ({_recommended.SizeGb:0.#} GB)");
            else _model.SetMissing(running ? "Not downloaded" : "Needs the Ollama server", "Download", $"Download {_recommended.Tag} ({_recommended.SizeGb:0.#} GB)", enabled: running);
        }
        if (!_claude.Busy)
        {
            if (Installer.ClaudePath is { } c) _claude.SetDone("Installed", detail: c);
            else _claude.SetMissing("Not installed", "Install", "Install Claude Code with Anthropic's official installer");
        }
        if (!_node.Busy)
        {
            if (Installer.NpmPath is not null) _node.SetDone("Installed");
            else _node.SetMissing("Not installed", "Install", Installer.HasWinget ? "Install Node.js LTS with winget" : "winget missing — install from nodejs.org", enabled: Installer.HasWinget);
        }
        if (!_qwen.Busy)
        {
            if (Installer.QwenPath is { } q) _qwen.SetDone("Installed", detail: q);
            else _qwen.SetMissing(Installer.NpmPath is null ? "Needs Node.js" : "Not installed", "Install", "npm install -g @qwen-code/qwen-code", enabled: Installer.NpmPath is not null);
        }

        BtnAll.IsEnabled = !_runningAll && _steps.Any(s => s.State == StepState.Missing);
        BtnAll.Content = _steps.All(s => s.State == StepState.Done) ? "All set" : "Set up everything";
    }

    // ------------------------------------------------------------------ running steps

    private void Out(string line)
    {
        Dispatcher.Invoke(() =>
        {
            Output.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
            Output.ScrollToEnd();
            LastLine.Text = line;
        });
    }

    private async Task<bool> RunStepAsync(SetupStep step)
    {
        if (step.Busy) return false;
        step.Begin();
        try
        {
            if (step == _ollama)
            {
                if (SystemProbe.OllamaInstalled && !SystemProbe.RuntimeOk)
                {
                    SystemProbe.RunRepair();
                    Out("Repair started in a separate window. Refresh this page when it finishes.");
                    return false;
                }
                await Installer.InstallOllamaAsync(Out, new Progress<double>(p => step.Progress = p), CancellationToken.None);
            }
            else if (step == _server)
            {
                Out("Starting Ollama…");
                if (!await SystemProbe.StartOllamaAsync()) throw new InvalidOperationException("Ollama did not start within 20 seconds.");
                Out("Ollama is running.");
            }
            else if (step == _model)
            {
                var tag = _recommended.Tag;
                Out($"Downloading {tag} ({_recommended.SizeGb:0.#} GB)…");
                string lastStatus = "";
                await OllamaClient.PullAsync(tag, new Progress<PullProgress>(p =>
                {
                    if (p.Total > 0) { step.Progress = 100.0 * p.Completed / p.Total; step.StatusText = $"{p.Completed / 1073741824.0:N1} of {p.Total / 1073741824.0:N1} GB"; }
                    if (p.Status != lastStatus && !p.Status.StartsWith("pulling ")) { Out(p.Status); lastStatus = p.Status; }
                }), CancellationToken.None);
                if (string.IsNullOrWhiteSpace(Shell.CurrentMainModel) || !await IsInstalledAsync(Shell.CurrentMainModel)) Shell.UseModel(tag);
                Out($"{tag} installed and selected as the main model.");
            }
            else if (step == _claude) await Installer.InstallClaudeAsync(Out, CancellationToken.None);
            else if (step == _node) await Installer.InstallNodeAsync(Out, CancellationToken.None);
            else if (step == _qwen) await Installer.InstallQwenAsync(Out, CancellationToken.None);

            Shell.Log($"Setup: {step.Title} done");
            return true;
        }
        catch (Exception ex)
        {
            Out($"FAILED: {ex.Message}");
            Shell.Log($"Setup: {step.Title} failed: {ex.Message}");
            step.SetFailed(ex.Message);
            return false;
        }
        finally
        {
            step.End();
            if (step.State != StepState.Failed) await RefreshAsync();
            await Shell.OnModelsChangedAsync();
        }
    }

    private static async Task<bool> IsInstalledAsync(string model)
    {
        try { return (await OllamaClient.GetModelsAsync()).Any(m => m.Name.Equals(model, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    private async void Step_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SetupStep s }) await RunStepAsync(s);
    }

    private async void All_Click(object sender, RoutedEventArgs e)
    {
        _runningAll = true;
        BtnAll.IsEnabled = false;
        Out("=== Setting up everything ===");
        try
        {
            foreach (var s in _steps)
            {
                await RefreshAsync();
                if (s.State == StepState.Done) continue;
                if (!s.CanRun) { Out($"Skipping {s.Title}: {s.StatusText}"); continue; }
                if (!await RunStepAsync(s))
                {
                    Shell.Toast("Setup stopped", $"{s.Title} did not finish — see Installer output", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    return;
                }
            }
            Out("=== All done ===");
            Shell.Toast("All set", "Ollama, your Qwen model, Claude Code and Qwen Code are ready", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
        }
        finally
        {
            _runningAll = false;
            await RefreshAsync();
        }
    }
}

public enum StepState { Missing, Done, Failed }

public sealed class SetupStep(string title, string description, string buttonText) : Observable
{
    private string _title = title, _description = description, _status = "", _buttonText = buttonText, _buttonTip = "";
    private StepState _state = StepState.Missing;
    private bool _busy, _canRun = true, _showButton = true;
    private double _progress;
    private bool _showProgress;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string StatusText { get => _status; set => Set(ref _status, value); }
    public string ButtonText { get => _buttonText; set => Set(ref _buttonText, value); }
    public string ButtonTip { get => _buttonTip; set => Set(ref _buttonTip, value); }
    public double Progress { get => _progress; set { Set(ref _progress, value); ShowProgress = true; } }
    public bool ShowProgress { get => _showProgress; set => Set(ref _showProgress, value); }
    public bool ShowButton { get => _showButton; set => Set(ref _showButton, value); }
    public bool CanRun { get => _canRun && !_busy; set => Set(ref _canRun, value); }

    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) { Raise(nameof(NotBusy)); Raise(nameof(CanRun)); } } }
    public bool NotBusy => !_busy;

    public StepState State
    {
        get => _state;
        private set { if (Set(ref _state, value)) { Raise(nameof(Symbol)); Raise(nameof(Brush)); } }
    }

    public SymbolRegular Symbol => State switch
    {
        StepState.Done => SymbolRegular.CheckmarkCircle24,
        StepState.Failed => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.Circle24,
    };

    public Brush Brush => State switch
    {
        StepState.Done => Palette.Green,
        StepState.Failed => Palette.Red,
        _ => Palette.Grey,
    };

    public void SetDone(string status, string? optionalButton = null, string? tip = null, string? detail = null)
    {
        State = StepState.Done;
        StatusText = detail is null ? status : $"{status} · {detail}";
        ShowProgress = false;
        ShowButton = optionalButton is not null;
        if (optionalButton is not null) ButtonText = optionalButton;
        ButtonTip = tip ?? "";
        CanRun = true;
    }

    public void SetMissing(string status, string button, string tip, bool enabled = true)
    {
        State = StepState.Missing;
        StatusText = status;
        ButtonText = button;
        ButtonTip = tip;
        ShowButton = true;
        CanRun = enabled;
    }

    public void SetFailed(string message)
    {
        State = StepState.Failed;
        StatusText = "Failed: " + message;
        ButtonText = "Retry";
        ButtonTip = "Try this step again";
        ShowButton = true;
        CanRun = true;
    }

    public void Begin() { Busy = true; StatusText = "Working…"; }
    public void End() => Busy = false;
}
