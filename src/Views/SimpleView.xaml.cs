using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Maison.Controls;
using Maison.Models;
using Maison.Services;

namespace Maison.Views;

/// <summary>
/// The simple view: a Start button that runs the real setup steps with friendly messages, then one tick,
/// four plain stats and a Qwen Code terminal. The full app is one toggle away.
/// </summary>
public partial class SimpleView : UserControl
{
    public MainWindow Shell { get; set; } = null!;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private TerminalViewer? _viewer;
    private TermSession? _session;
    private bool _checking, _settingUp;
    private SetupStep? _shownStep;
    private DateTime _stepShownAt;
    private int _phrase;

    public sealed record Pip(string Label, Brush Fill, Brush TextBrush, string Tip);

    public SimpleView()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => ShowProgress();
        StatsService.Updated += s => Dispatcher.BeginInvoke(() => { if (IsVisible) ShowStats(s); });
        TerminalHub.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible && _session is not null && !TerminalHub.Sessions.Contains(_session)) DetachTerminal(); });
    }

    // ------------------------------------------------------------------ friendly messages, one set per real step

    private Dictionary<SetupStep, (string Short, string[] Phrases, string? Hint)> Messages => new()
    {
        [Shell.Setup.OllamaStep] = ("Engine", ["Getting things set up for you", "Bringing the AI engine home", "Unpacking the engine — it's a big one"], null),
        [Shell.Setup.ServerStep] = ("Power", ["Turning the lights on", "Waking the engine up"], null),
        [Shell.Setup.ModelStep] = ("Qwen", ["Moving Qwen in — this is the big download", "Carrying Qwen's brain up the stairs", "Unpacking Qwen's boxes"], null),
        [Shell.Setup.ClaudeStep] = ("Tools", ["Unpacking the toolbox", "Putting Claude Code on the shelf"], null),
        [Shell.Setup.NodeStep] = ("Basics", ["Laying the foundations", "Pouring the foundations for Qwen Code"],
            "Windows may ask for permission to install Node.js — click Yes."),
        [Shell.Setup.QwenStep] = ("Qwen Code", ["Hanging the Qwen-tissential pictures", "Putting Qwen Code in its place"], null),
    };

    private static readonly Dictionary<string, double> Weight = new()
    {
        ["Engine"] = 15, ["Power"] = 2, ["Qwen"] = 58, ["Tools"] = 8, ["Basics"] = 9, ["Qwen Code"] = 8,
    };

    // ------------------------------------------------------------------ state

    public async Task ActivateAsync()
    {
        AdvancedToggle.IsChecked = false;
        await CheckAsync();
    }

    /// <summary>What's missing before Qwen can run (empty = ready). Starts the server quietly if that's all it needs.</summary>
    private async Task<List<string>> MissingAsync()
    {
        var missing = new List<string>();
        if (!SystemProbe.OllamaInstalled || !SystemProbe.RuntimeOk) missing.Add("the AI engine (Ollama)");
        bool running = await OllamaClient.GetVersionAsync(2) is not null;
        if (!running && SystemProbe.OllamaInstalled && SystemProbe.RuntimeOk)
        {
            ShowBusy("Waking things up", "Starting the AI engine (Ollama server)…");
            running = await SystemProbe.StartOllamaAsync();
        }
        if (!running && missing.Count == 0) missing.Add("the AI engine to start");
        if (running)
        {
            try
            {
                var models = (await OllamaClient.GetModelsAsync()).Where(m => !m.Name.StartsWith("claude-") && !Launcher.IsVariant(m.Name)).ToList();
                if (models.Count == 0) missing.Add("a Qwen model");
                else if (Shell.MainModelName.Length == 0 || models.All(m => m.Name != Shell.MainModelName)) Shell.UseModel(models[0].Name);
            }
            catch { missing.Add("a Qwen model"); }
        }
        else if (missing.Count == 1 && missing[0].Contains("engine")) missing.Add("a Qwen model");
        if (Installer.QwenPath is null) missing.Add("Qwen Code");
        return missing;
    }

    private async Task CheckAsync()
    {
        if (_checking || _settingUp) return;
        _checking = true;
        try
        {
            var missing = await MissingAsync();
            if (missing.Count == 0) await ShowReadyAsync();
            else ShowSetup(missing);
        }
        finally { _checking = false; }
    }

    private void ShowBusy(string headline, string detail)
    {
        SetupPanel.Visibility = Visibility.Visible;
        ReadyPanel.Visibility = Visibility.Collapsed;
        StartButton.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        Ring.Visibility = Visibility.Visible;
        Logo.Width = Logo.Height = 52;
        Headline.Text = headline + "…";
        Subline.Visibility = Visibility.Collapsed;
        Overall.IsIndeterminate = true;
        Detail.Text = detail;
        Hint.Visibility = Visibility.Collapsed;
        Pips.ItemsSource = null;
    }

    private void ShowSetup(List<string> missing)
    {
        SetupPanel.Visibility = Visibility.Visible;
        ReadyPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        StartButton.Visibility = Visibility.Visible;
        Ring.Visibility = Visibility.Collapsed;
        Logo.Width = Logo.Height = 72;
        Subline.Visibility = Visibility.Visible;
        bool fresh = missing.Count >= 3;
        Headline.Text = fresh ? "Let's set up your AI at home" : "Almost there";
        Subline.Text = fresh
            ? "One click installs everything Qwen needs to run privately on this PC. No account, no subscription. Grab a coffee — the first setup downloads a lot."
            : "Still to do: " + string.Join(", ", missing) + ". Press Start and maison takes care of it.";
        StartButton.Content = fresh ? "Start" : "Finish setup";
    }

    // ------------------------------------------------------------------ setup run

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_settingUp) return;
        _settingUp = true;
        _shownStep = null;
        ShowBusy("Getting things set up for you", "Checking what this PC already has…");
        Overall.IsIndeterminate = false;
        Overall.Value = 0;
        _tick.Start();
        (bool Ok, string? FailedStep, string? Error) result;
        try { result = await Shell.Setup.SetupEverythingAsync(); }
        finally { _tick.Stop(); _settingUp = false; }

        if (result.Ok)
        {
            Shell.Log("Simple view: setup finished");
            Overall.Value = 100;
            Headline.Text = "All moved in!";
            Detail.Text = "Everything is installed. Starting Qwen…";
            Pips.ItemsSource = BuildPips();
            await Task.Delay(1200);
            await Shell.RefreshHealthAsync();
            await CheckAsync();
        }
        else
        {
            Ring.Visibility = Visibility.Collapsed;
            Logo.Width = Logo.Height = 72;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            Headline.Text = "That didn't quite work";
            ErrorText.Text = $"{result.FailedStep ?? "Setup"} couldn't finish: {result.Error}";
        }
    }

    private void ShowProgress()
    {
        var setup = Shell.Setup;
        var s = setup.CurrentStep;
        var msgs = Messages;
        if (s is not null && msgs.TryGetValue(s, out var m))
        {
            if (s != _shownStep) { _shownStep = s; _stepShownAt = DateTime.Now; _phrase = 0; }
            else if ((DateTime.Now - _stepShownAt).TotalSeconds > 8) { _stepShownAt = DateTime.Now; _phrase = (_phrase + 1) % m.Phrases.Length; }
            Headline.Text = m.Phrases[_phrase] + "…";

            var idx = setup.Steps.ToList().IndexOf(s) + 1;
            var status = s.StatusText is "Working…" or "" ? "" : " · " + s.StatusText;
            var real = setup.LastOutput.Length > 110 ? setup.LastOutput[..110] + "…" : setup.LastOutput;
            Detail.Text = $"Step {idx} of {setup.Steps.Count}: {StepAction(s)}{status}" + (real.Length > 0 ? "\n" + real : "");
            Hint.Text = m.Hint ?? "";
            Hint.Visibility = m.Hint is null ? Visibility.Collapsed : Visibility.Visible;
        }

        // Overall progress: finished steps count fully, the current one by its own progress.
        double total = 0, done = 0;
        foreach (var st in setup.Steps)
        {
            var w = msgs.TryGetValue(st, out var mm) ? Weight[mm.Short] : 5;
            total += w;
            if (st.State == StepState.Done && st != s) done += w;
            else if (st == s) done += w * (st.ShowProgress ? st.Progress / 100 : 0.3);
        }
        Overall.IsIndeterminate = false;
        Overall.Value = total > 0 ? 100 * done / total : 0;
        Pips.ItemsSource = BuildPips();
    }

    /// <summary>The real action behind each step, in plain words.</summary>
    private string StepAction(SetupStep s)
    {
        var setup = Shell.Setup;
        if (s == setup.OllamaStep) return "downloading and installing Ollama, the engine that runs AI models";
        if (s == setup.ServerStep) return "starting the Ollama server";
        if (s == setup.ModelStep) return $"downloading the {setup.RecommendedModel} model ({setup.RecommendedSizeGb:0.#} GB)";
        if (s == setup.ClaudeStep) return "installing Claude Code";
        if (s == setup.NodeStep) return "installing Node.js (needed by Qwen Code)";
        if (s == setup.QwenStep) return "installing Qwen Code";
        return s.Title;
    }

    private List<Pip> BuildPips()
    {
        var setup = Shell.Setup;
        var msgs = Messages;
        var dim = (Brush)FindResource("TextFillColorTertiaryBrush");
        var text = (Brush)FindResource("TextFillColorSecondaryBrush");
        return setup.Steps.Select(st =>
        {
            var label = msgs.TryGetValue(st, out var m) ? m.Short : st.Title;
            if (st == setup.CurrentStep) return new Pip(label, (Brush)FindResource("BrandGradient"), (Brush)FindResource("TextFillColorPrimaryBrush"), st.Title + " — in progress");
            return st.State switch
            {
                StepState.Done => new Pip(label, Palette.Green, text, st.Title + " — done"),
                StepState.Failed => new Pip(label, Palette.Red, text, st.Title + " — failed"),
                _ => new Pip(label, Palette.Grey, dim, st.Title + " — to do"),
            };
        }).ToList();
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        Shell.SetSimpleMode(false);
        Shell.Navigate("setup");
    }

    // ------------------------------------------------------------------ ready

    private async Task ShowReadyAsync()
    {
        SetupPanel.Visibility = Visibility.Collapsed;
        ReadyPanel.Visibility = Visibility.Visible;
        var gpu = StatsService.Latest?.Gpu?.Name.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "");
        ReadySub.Text = $"Running {Shell.MainModelName} on {(gpu is null ? "this PC" : "your " + gpu)} · working in {Shell.WorkingFolder}";
        if (StatsService.Latest is { } st) ShowStats(st);

        // Show the newest running Qwen Code session, or start one.
        if (_session is { IsRunning: true } && TerminalHub.Sessions.Contains(_session)) return;
        var existing = TerminalHub.Sessions.LastOrDefault(x => x.Kind == "qwen" && (x.IsRunning || !x.HasStarted));
        await AttachAsync(existing ?? await Shell.StartSessionAsync("qwen"));
    }

    private async Task AttachAsync(TermSession? s)
    {
        DetachTerminal();
        if (s is null)
        {
            TerminalStarting.Visibility = Visibility.Visible;
            TerminalStartingText.Text = "Qwen couldn't start — check the Advanced view's Dashboard.";
            return;
        }
        _session = s;
        TerminalStarting.Visibility = Visibility.Visible;
        TerminalStartingText.Text = "Starting Qwen…";
        _viewer = new TerminalViewer(s);
        TerminalHost.Children.Add(_viewer);
        await _viewer.StartAsync();
        TerminalStarting.Visibility = Visibility.Collapsed;
        _viewer.FocusTerminal();
    }

    private void DetachTerminal()
    {
        if (_viewer is not null)
        {
            TerminalHost.Children.Remove(_viewer);
            _viewer.Dispose();
            _viewer = null;
        }
        _session = null;
        TerminalStarting.Visibility = Visibility.Visible;
        TerminalStartingText.Text = "No Qwen session — click New session.";
    }

    private void ShowStats(StatsSnapshot s)
    {
        if (ReadyPanel.Visibility != Visibility.Visible) return;
        var g = s.Gpu;
        TGpu.Text = g is null ? "—" : $"{g.LoadPercent}%" + (g.TempC is { } t ? $" · {t:0}°C" : "");
        TVram.Text = g is null ? "—" : $"{g.UsedGb:0.0} / {g.TotalGb:0} GB";
        TCpu.Text = $"{s.CpuPercent:0}%";
        TRam.Text = $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0} GB";
        var hog = HealthAdvice.VramHog(s);
        WarnBox.Visibility = hog is null ? Visibility.Collapsed : Visibility.Visible;
        WarnText.Text = hog is { } h ? $"{h.Name} is using {h.Gb:0.0} GB of graphics memory, so Qwen may be very slow. Closing it should speed things up." : "";
    }

    private async void NewSession_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } old) TerminalHub.Remove(old);
        await AttachAsync(await Shell.StartSessionAsync("qwen"));
    }

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder Qwen should work in", InitialDirectory = Shell.WorkingFolder };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Shell.SetWorkingFolder(dlg.FolderName);
        ReadySub.Text = $"Running {Shell.MainModelName} · working in {Shell.WorkingFolder}";
        if (_session is { } old) TerminalHub.Remove(old);
        await AttachAsync(await Shell.StartSessionAsync("qwen"));
    }

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        if (AdvancedToggle.IsChecked == true) Shell.SetSimpleMode(false);
    }

    public void ApplyTheme() => _viewer?.ApplyTheme();

    /// <summary>Self-test: paint the progress screen for a given step without running installers.</summary>
    public void PreviewProgress(SetupStep step, double stepProgress, string status, string output)
    {
        ShowBusy("Getting things set up for you", "");
        step.Progress = stepProgress;
        step.StatusText = status;
        var m = Messages[step];
        Headline.Text = m.Phrases[0] + "…";
        var idx = Shell.Setup.Steps.ToList().IndexOf(step) + 1;
        Detail.Text = $"Step {idx} of {Shell.Setup.Steps.Count}: {StepAction(step)} · {status}\n{output}";
        Hint.Text = m.Hint ?? "";
        Hint.Visibility = m.Hint is null ? Visibility.Collapsed : Visibility.Visible;
        Overall.IsIndeterminate = false;
        Overall.Value = 42;
        Pips.ItemsSource = Shell.Setup.Steps.Select(st => new Pip(Messages[st].Short,
            st == step ? (Brush)FindResource("BrandGradient") : Shell.Setup.Steps.ToList().IndexOf(st) < idx - 1 ? Palette.Green : Palette.Grey,
            (Brush)FindResource("TextFillColorSecondaryBrush"), st.Title)).ToList();
    }
}
