using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Maison.Models;
using Maison.Services;
using Maison.Services.Pool;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Maison.Views;

/// <summary>
/// The AI pool page: finds AI PCs, pairs with them, and shows each one's status, the agents using it and its models,
/// with remote install / remove / set up.
/// </summary>
public partial class PoolView : UserControl
{
    public IShell Shell { get; set; } = null!;
    private readonly Dictionary<string, MemberCard> _cards = new();
    private bool _finding;
    private List<FoundPc> _found = [];

    public PoolView()
    {
        InitializeComponent();
        AiPool.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Update(); });
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) AiPool.StartPolling();
            else if (!PoolRelay.IsRunning) AiPool.StopPolling();
        };
    }

    public async Task RefreshAsync()
    {
        Update();
        await AiPool.RefreshAsync();
        if (_found.Count == 0 && !_finding) await FindAsync();
    }

    private void Update()
    {
        var c = AppConfig.Current;
        UsePoolToggle.IsChecked = c.UseRemoteAi;
        IncludeThisToggle.IsChecked = c.PoolIncludeThisPc;
        IncludeThisToggle.IsEnabled = SystemProbe.OllamaInstalled;
        var members = AiPool.Members;
        var online = members.Count(m => m.Member.Enabled && m.Online);
        PoolState.Text = !c.UseRemoteAi
            ? "Off: agents on this PC use only this PC's AI. You can still manage AI PCs here."
            : members.Count == 0 ? "On, but there's nothing in the pool yet. Add an AI PC below."
            : $"On: {online} of {members.Count} PC{(members.Count == 1 ? "" : "s")} ready.";

        // member cards, updated in place
        var ids = members.Select(m => m.Member.Id).ToHashSet();
        foreach (var gone in _cards.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            MemberList.Children.Remove(_cards[gone]);
            _cards.Remove(gone);
        }
        foreach (var m in members)
        {
            if (!_cards.TryGetValue(m.Member.Id, out var card))
            {
                card = new MemberCard(this, m);
                _cards[m.Member.Id] = card;
                MemberList.Children.Add(card);
            }
            card.Update(m);
        }
        NoMembers.Visibility = members.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowFound();
        ShowSessions(members);
    }

    // ------------------------------------------------------------------ finding and pairing

    private async void Find_Click(object sender, RoutedEventArgs e) => await FindAsync();

    private async Task FindAsync()
    {
        _finding = true;
        BtnFind.IsEnabled = false;
        FindState.Text = "Looking for AI PCs on your network…";
        try
        {
            _found = await AiPool.DiscoverAsync();
            var paired = AppConfig.Current.PoolMembers.Select(m => m.Id).ToHashSet();
            var fresh = _found.Count(f => !paired.Contains(f.Id));
            FindState.Text = _found.Count == 0
                ? "None found. On the AI PC, turn on Settings → AI pool → Share this PC's AI, and make sure maison is running there and both PCs are on the same network."
                : fresh == 0 ? "Every AI PC found is already in your pool." : $"Found {fresh} AI PC{(fresh == 1 ? "" : "s")} to add. Enter the pairing code shown on it.";
        }
        finally { _finding = false; BtnFind.IsEnabled = true; ShowFound(); }
    }

    private async void Manual_Click(object sender, RoutedEventArgs e) => await LookupAsync();
    private async void ManualAddress_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) await LookupAsync();
    }

    private async Task LookupAsync()
    {
        var a = ManualAddress.Text.Trim();
        if (a.Length == 0) return;
        FindState.Text = $"Asking {a}…";
        var pc = await AiPool.LookupAsync(a);
        if (pc is null) { FindState.Text = $"No AI PC answered at {a}. Check sharing is on there and maison is running."; return; }
        if (pc.Id == AppConfig.Current.MachineId) { FindState.Text = "That's this PC."; return; }
        _found.RemoveAll(f => f.Id == pc.Id);
        _found.Add(pc);
        FindState.Text = $"Found {pc.Name}. Enter the pairing code shown on it.";
        ShowFound();
    }

    private string _foundKey = "";

    private void ShowFound()
    {
        var paired = AppConfig.Current.PoolMembers.Select(m => m.Id).ToHashSet();
        var list = _found.Where(f => !paired.Contains(f.Id)).ToList();
        var key = string.Join("|", list.Select(f => f.Id + f.Address));
        if (key == _foundKey) return;   // don't wipe a code being typed
        _foundKey = key;
        FoundList.Children.Clear();
        foreach (var pc in list)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            name.Inlines.Add(new System.Windows.Documents.Run(pc.Name) { FontWeight = FontWeights.SemiBold });
            name.Inlines.Add(new System.Windows.Documents.Run($"  ·  {pc.Address}  ·  maison {pc.Version}") { Foreground = Brush("TextFillColorTertiaryBrush") });
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            var code = new TextBox { PlaceholderText = "Pairing code", Width = 140, MaxLength = 7, ToolTip = $"The 6-digit code shown on {pc.Name} under Settings → AI pool" };
            var add = new Button { Content = "Add to pool", Appearance = ControlAppearance.Primary, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(code, 1); Grid.SetColumn(add, 2);
            row.Children.Add(name); row.Children.Add(code); row.Children.Add(add);
            async Task Pair()
            {
                var c = code.Text.Replace(" ", "");
                if (c.Length != 6) { FindState.Text = "The code has 6 digits."; code.Focus(); return; }
                add.IsEnabled = false;
                FindState.Text = $"Pairing with {pc.Name}…";
                var (ok, msg) = await AiPool.PairAsync(pc, c);
                FindState.Text = msg;
                add.IsEnabled = true;
                if (ok)
                {
                    Shell.Toast("AI PC added", msg, ControlAppearance.Success, SymbolRegular.Server24);
                    _foundKey = "";
                    await AiPool.RefreshAsync();
                    if (AppConfig.Current.UseRemoteAi) await Shell.OnModelsChangedAsync();
                    else if (await Shell.ConfirmAsync("Use the AI pool now?",
                                 $"{pc.Name} is in your pool. Switch this PC's agents to the pool now?", "Use the pool", "Not yet"))
                        await Shell.SetUsePoolAsync(true);
                }
            }
            add.Click += async (_, _) => await Pair();
            code.KeyUp += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await Pair(); };
            FoundList.Children.Add(row);
        }
    }

    // ------------------------------------------------------------------ toggles

    private async void UsePool_Click(object sender, RoutedEventArgs e) => await Shell.SetUsePoolAsync(UsePoolToggle.IsChecked == true);

    private async void IncludeThis_Click(object sender, RoutedEventArgs e)
    {
        AppConfig.Current.PoolIncludeThisPc = IncludeThisToggle.IsChecked == true;
        AppConfig.Current.Save();
        await AiPool.RefreshAsync();
        if (AppConfig.Current.UseRemoteAi) await Shell.OnModelsChangedAsync();
    }

    // ------------------------------------------------------------------ sessions

    private void ShowSessions(IReadOnlyList<MemberState> members)
    {
        SessionList.Children.Clear();
        var routes = PoolRelay.Routes.Where(r => r.Active > 0 || (DateTime.Now - r.LastUsed).TotalMinutes < 30)
            .OrderByDescending(r => r.LastUsed).ToList();
        if (routes.Count == 0)
        {
            SessionList.Children.Add(Dim(AppConfig.Current.UseRemoteAi ? "None yet. Start Claude Code or Qwen Code on the Dashboard." : "The pool is off on this PC."));
            return;
        }
        foreach (var r in routes)
        {
            var t = new TextBlock { Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            t.Inlines.Add(new System.Windows.Documents.Run(r.Title) { FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new System.Windows.Documents.Run(
                $"  →  {r.MemberName ?? "not started yet"}{(r.Active > 0 ? "  ·  working" : "")}{(r.Moves > 0 ? $"  ·  moved {r.Moves}×  (its PC was busy)" : "")}")
                { Foreground = Brush("TextFillColorSecondaryBrush") });
            SessionList.Children.Add(t);
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    internal static TextBlock Dim(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Brush("TextFillColorTertiaryBrush"), Margin = new Thickness(0, 2, 0, 2),
    };

    internal IShell ShellRef => Shell;

    /// <summary>One AI PC: status, GPU, agents, models and what you can do with it.</summary>
    private sealed class MemberCard : Card
    {
        private readonly PoolView _page;
        private MemberState _s;
        private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };
        private readonly Border _pill = new() { CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 1, 9, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _pillText = new() { FontSize = 12, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _sub = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        private readonly StackPanel _agents = new();
        private readonly StackPanel _models = new();
        private readonly TextBlock _task = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly ProgressBar _progress = new() { Height = 4, Margin = new Thickness(0, 4, 0, 0), Maximum = 100, Visibility = Visibility.Collapsed };
        private readonly ComboBox _install = new() { IsEditable = true, Width = 240, ToolTip = "Pick a model sized for this PC, or type any Ollama model name (e.g. qwen3.5:9b)" };
        private readonly Button _btnInstall = new() { Content = "Install on this PC", Margin = new Thickness(8, 0, 0, 0), Icon = new SymbolIcon(SymbolRegular.ArrowDownload24) };
        private readonly Button _btnSetup = new() { Content = "Set it up", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 8, 0, 0), Icon = new SymbolIcon(SymbolRegular.Wrench24),
            ToolTip = "Installs Ollama there if needed, starts it, and downloads a model sized for its GPU" };
        private readonly ToggleSwitch _enabled = new() { Content = "Use", ToolTip = "Send work to this PC. Turn off to keep it out of the pool for now." };
        private readonly Button _btnRemove = new() { Content = "Remove", Appearance = ControlAppearance.Transparent, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Unpair: take this PC out of the pool" };
        private readonly StackPanel _installRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        private string _modelsKey = "";

        public MemberCard(PoolView page, MemberState s)
        {
            _page = page;
            _s = s;
            Margin = new Thickness(0, 0, 0, 12);
            _title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            _sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _task.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _pill.Child = _pillText;

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(_title); titleRow.Children.Add(_pill);
            var left = new StackPanel(); left.Children.Add(titleRow); left.Children.Add(_sub);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            right.Children.Add(_enabled);
            if (!s.IsThisPc) right.Children.Add(_btnRemove);
            else _enabled.Visibility = Visibility.Collapsed;
            Grid.SetColumn(right, 1);
            head.Children.Add(left); head.Children.Add(right);

            var cols = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var a = new StackPanel(); a.Children.Add(Label("Agents using it")); a.Children.Add(_agents);
            var m = new StackPanel(); m.Children.Add(Label("Models")); m.Children.Add(_models);
            Grid.SetColumn(m, 2);
            cols.Children.Add(a); cols.Children.Add(m);

            _installRow.Children.Add(_install); _installRow.Children.Add(_btnInstall);
            if (s.IsThisPc)
            {
                _installRow.Children.Clear();
                var open = new Button { Content = "Manage on the Models page", Icon = new SymbolIcon(SymbolRegular.Cube24) };
                open.Click += (_, _) => _page.ShellRef.Navigate("models");
                _installRow.Children.Add(open);
            }

            var body = new StackPanel();
            body.Children.Add(head); body.Children.Add(cols); body.Children.Add(_installRow);
            body.Children.Add(_btnSetup); body.Children.Add(_task); body.Children.Add(_progress);
            Content = body;

            _enabled.Click += async (_, _) =>
            {
                _s.Member.Enabled = _enabled.IsChecked == true;
                AppConfig.Current.Save();
                await AiPool.RefreshAsync();
            };
            _btnRemove.Click += async (_, _) =>
            {
                if (!await _page.ShellRef.ConfirmAsync($"Remove {_s.Member.Name}?",
                        $"This PC stops using {_s.Member.Name}'s AI. To add it again you'll need its pairing code.", "Remove")) return;
                await AiPool.RemoveAsync(_s.Member);
                if (AppConfig.Current.UseRemoteAi) await _page.ShellRef.OnModelsChangedAsync();
            };
            _btnInstall.Click += async (_, _) =>
            {
                var tag = (_install.Text ?? "").Split(' ')[0].Trim();
                if (tag.Length == 0) { _task.Text = "Pick or type a model first."; return; }
                await Run(() => AiPool.InstallModelAsync(_s.Member, tag), $"Downloading {tag} onto {_s.Member.Name}…");
            };
            _btnSetup.Click += async (_, _) => await Run(() => AiPool.SetupAsync(_s.Member), $"Setting up {_s.Member.Name}…");
        }

        private async Task Run(Func<Task<(bool Ok, string Message)>> work, string starting)
        {
            _task.Text = starting;
            var (ok, msg) = await work();
            if (!ok) _task.Text = msg;
        }

        private static TextBlock Label(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 0, 0, 3) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            return t;
        }

        public void Update(MemberState s)
        {
            _s = s;
            var i = s.Info;
            _title.Text = s.Member.Name;
            _enabled.IsChecked = s.Member.Enabled;

            var (text, colour) = !s.Member.Enabled ? ("Not used", "#808080")
                : !s.Online ? ("Offline", "#D9534F")
                : i is { OllamaRunning: false } ? ("Not ready", "#E5B454")
                : i?.Task is { Running: true } ? ("Installing", "#5B9BD5")
                : s.BusyForUs ? ("Busy", "#E5B454")
                : s.OurActive > 0 ? ("Working for you", "#5B9BD5")
                : ("Free", "#4CAF50");
            var c = (Color)ColorConverter.ConvertFromString(colour);
            _pill.Background = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
            _pillText.Foreground = new SolidColorBrush(c);
            _pillText.Text = text;

            if (i is null)
                _sub.Text = s.Problem ?? "Checking…";
            else
            {
                var gpu = i.Gpu is { } g ? $"{g.Name} · {g.UsedGb:0.#} of {g.TotalGb:0.#} GB in use · {g.LoadPercent}% busy" : "No NVIDIA GPU found (runs on the processor)";
                _sub.Text = $"{gpu}  ·  {i.RamGb:0} GB RAM  ·  " +
                            (i.OllamaRunning ? $"Ollama {i.OllamaVersion}" : i.OllamaInstalled ? "Ollama isn't running" : "Ollama isn't installed") +
                            (s.IsThisPc ? "" : $"  ·  maison {i.Version}  ·  {s.Member.Address}") +
                            (s.Problem is { } p && s.Online ? $"  ·  {p}" : "");
                if (!s.Online && s.Problem is { } p2) _sub.Text = p2 + "  ·  last seen: " + _sub.Text;
            }

            // agents
            _agents.Children.Clear();
            var agents = i?.Agents ?? [];
            if (agents.Count == 0) _agents.Children.Add(Dim(s.Online ? "None right now." : "—"));
            foreach (var a in agents)
            {
                var kind = a.Kind switch { "claude" => "Claude Code", "qwen" => "Qwen Code", "chat" => "Chat", _ => "maison" };
                var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                t.Inlines.Add(new System.Windows.Documents.Run($"{kind} from {a.Client}") { FontWeight = FontWeights.SemiBold });
                t.Inlines.Add(new System.Windows.Documents.Run($"  ·  {a.Model}  ·  {(a.Working ? "working" : Ago(a.LastSeen))}") { Foreground = Brush("TextFillColorSecondaryBrush") });
                _agents.Children.Add(t);
            }

            // models (rebuilt only when they change)
            var models = i?.Models ?? [];
            var loaded = i?.Loaded ?? [];
            var key = string.Join("|", models.Select(x => x.Name)) + "#" + string.Join("|", loaded.Select(x => x.Name));
            if (key != _modelsKey)
            {
                _modelsKey = key;
                _models.Children.Clear();
                var shown = models.Where(x => !System.Text.RegularExpressions.Regex.IsMatch(x.Name, @"-ctx\d+k(:latest)?$")).ToList();
                if (shown.Count == 0) _models.Children.Add(Dim(i is null ? "—" : "None installed."));
                foreach (var mo in shown)
                {
                    var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var isLoaded = loaded.Any(l => MemberState.Same(l.Name, mo.Name) || l.Name.StartsWith(mo.Name.Replace(":latest", "") + "-ctx"));
                    var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                    t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                    t.Inlines.Add(new System.Windows.Documents.Run(mo.Name));
                    t.Inlines.Add(new System.Windows.Documents.Run($"  ·  {mo.SizeGb:0.#} GB{(isLoaded ? "  ·  loaded" : "")}") { Foreground = Brush("TextFillColorTertiaryBrush") });
                    row.Children.Add(t);
                    if (!s.IsThisPc)
                    {
                        var del = new Button { Content = "Remove", Appearance = ControlAppearance.Transparent, Padding = new Thickness(8, 1, 8, 2), ToolTip = $"Delete {mo.Name} from {s.Member.Name} to free its disk space" };
                        var name = mo.Name;
                        del.Click += async (_, _) =>
                        {
                            if (!await _page.ShellRef.ConfirmAsync($"Remove {name}?", $"Deletes {name} from {_s.Member.Name}. It can be downloaded again later.", "Remove")) return;
                            var (ok, msg) = await AiPool.DeleteModelAsync(_s.Member, name);
                            _task.Text = ok ? $"Removed {name} from {_s.Member.Name}." : msg;
                            await AiPool.RefreshAsync();
                        };
                        Grid.SetColumn(del, 1);
                        row.Children.Add(del);
                    }
                    _models.Children.Add(row);
                }
                // install choices sized for this PC's GPU
                if (!s.IsThisPc && i is not null)
                {
                    var keep = _install.Text;
                    _install.Items.Clear();
                    var vram = i.Gpu?.TotalGb ?? 0;
                    foreach (var cm in ModelCatalog.All.Where(x => !models.Any(y => MemberState.Same(y.Name, x.Tag))))
                        _install.Items.Add($"{cm.Tag}  ({cm.SizeGb:0.#} GB, {ModelCatalog.FitText(ModelCatalog.FitFor(cm, vram, i.RamGb)).ToLowerInvariant()})");
                    _install.Text = keep.Length > 0 ? keep : i.Models.Count == 0 ? i.RecommendedModel : "";
                }
            }

            bool ready = s.Online && i is { OllamaRunning: true };
            _installRow.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
            _btnInstall.IsEnabled = i?.Task is not { Running: true };
            _btnSetup.Visibility = s.Online && !s.IsThisPc && i is not null && (!i.OllamaRunning || i.Models.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
            _btnSetup.IsEnabled = i?.Task is not { Running: true };

            if (i?.Task is { } task)
            {
                _task.Text = $"{task.Title}: {task.Status}";
                _progress.Visibility = task.Running ? Visibility.Visible : Visibility.Collapsed;
                _progress.Value = task.Percent;
            }
            else _progress.Visibility = Visibility.Collapsed;
        }

        private static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            return d.TotalMinutes < 1 ? "just now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago" : $"{(int)d.TotalHours} h ago";
        }
    }
}
