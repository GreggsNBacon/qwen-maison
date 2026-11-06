using System.IO;
using System.Text;
using Maison.Models;

namespace Maison.Services;

/// <summary>
/// One live terminal session (ConPTY + output history) that any number of viewers can show at once:
/// the app tab, a pop-out window and the web portal all mirror the same session.
/// </summary>
public sealed class TermSession : IDisposable
{
    private const int HistoryLimit = 2_000_000;   // characters of VT output kept for late joiners

    private readonly StringBuilder _history = new();
    private readonly StringBuilder _pending = new();
    private readonly AutoResetEvent _dataArrived = new(false);
    private readonly object _lock = new();
    private PseudoConsole? _pty;
    private Thread? _pump;
    private bool _disposed;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
    public Launcher.Session Spec { get; }
    public string Kind { get; }                 // claude | qwen | chat | shell
    public DateTime Started { get; } = DateTime.Now;

    public int Cols { get; private set; } = 120;
    public int Rows { get; private set; } = 30;
    public bool IsRunning { get; private set; }
    public int? ExitCode { get; private set; }
    public string? ShellTitle { get; private set; }

    /// <summary>Viewer whose window size drives the PTY (the one the user last typed in or focused).</summary>
    public string? Controller { get; private set; }

    // ---- activity / idle
    public DateTime LastOutput { get; private set; } = DateTime.Now;
    public DateTime LastInput { get; private set; } = DateTime.MinValue;
    /// <summary>True when the agent went quiet after working on something the user asked.</summary>
    public bool IsIdle { get; private set; }
    // Idle notification: armed by a submitted request (Enter), fired once when the work it started goes quiet.
    private bool _armed;
    private DateTime _armedAt;
    private long _workChars;

    /// <summary>Coalesced output chunks (background thread). Each chunk is a complete-ish frame.</summary>
    public event Action<TermSession, string>? Output;
    public event Action<TermSession, int, int>? Resized;
    public event Action<TermSession>? StateChanged;
    public event Action<TermSession>? BecameIdle;

    public TermSession(Launcher.Session spec, string kind)
    {
        Spec = spec;
        Kind = kind;
    }

    /// <summary>True once the first viewer has asked for the process to start (even if starting failed).</summary>
    public bool HasStarted { get; private set; }

    public void Start(int cols, int rows)
    {
        lock (_lock)
        {
            if (HasStarted || _disposed) return;
            HasStarted = true;
            Cols = Math.Max(20, cols);
            Rows = Math.Max(5, rows);
        }
        if (!Directory.Exists(Spec.WorkDir))
        {
            Append($"\x1b[31mWorking folder not found: {Spec.WorkDir}\x1b[0m\r\n");
            Flush();
            return;
        }
        try
        {
            _pty = PseudoConsole.Start(Spec.CommandLine, Spec.WorkDir, (short)Cols, (short)Rows);
        }
        catch (Exception ex)
        {
            Append($"\x1b[31mCould not start the session: {ex.Message}\x1b[0m\r\n");
            Flush();
            return;
        }
        _pty.Output += text => { Append(text); };
        _pty.Exited += code =>
        {
            Flush();
            Append($"\r\n\x1b[90m[session ended — exit code {code}. Close this tab or launch a new one.]\x1b[0m\r\n");
            Flush();
            IsRunning = false;
            ExitCode = code;
            StateChanged?.Invoke(this);
        };
        IsRunning = true;
        _pump = new Thread(Pump) { IsBackground = true, Name = "term-pump-" + Id };
        _pump.Start();
        StateChanged?.Invoke(this);
    }

    private void Append(string text)
    {
        lock (_pending) _pending.Append(text);
        _dataArrived.Set();
    }

    /// <summary>
    /// Sends output in whole frames: waits for a short pause in the stream (or a cap), and holds back while a
    /// synchronized-output block (CSI ?2026h … ?2026l) is open. Splitting a TUI redraw across two paints is what
    /// makes Claude Code / Qwen Code jitter.
    /// </summary>
    private void Pump()
    {
        while (!_disposed)
        {
            if (!_dataArrived.WaitOne(500)) { CheckIdle(); continue; }
            var first = DateTime.UtcNow;
            while (!_disposed)
            {
                var waited = (DateTime.UtcNow - first).TotalMilliseconds;
                bool inSync;
                lock (_pending) inSync = OpenSyncBlock(_pending);
                var cap = inSync ? 200 : 40;
                if (waited >= cap) break;
                if (!_dataArrived.WaitOne(inSync ? 10 : 6)) { if (!inSync) break; }
            }
            Flush();
        }
    }

    private static bool OpenSyncBlock(StringBuilder sb)
    {
        if (sb.Length == 0) return false;
        var s = sb.ToString();
        int open = s.LastIndexOf("\x1b[?2026h", StringComparison.Ordinal);
        int close = s.LastIndexOf("\x1b[?2026l", StringComparison.Ordinal);
        return open >= 0 && open > close;
    }

    private void Flush()
    {
        string text;
        lock (_pending)
        {
            if (_pending.Length == 0) return;
            text = _pending.ToString();
            _pending.Clear();
        }
        lock (_lock)
        {
            _history.Append(text);
            if (_history.Length > HistoryLimit)
            {
                // Trim at a line start so we don't cut an escape sequence in half.
                var cut = _history.Length - HistoryLimit * 3 / 4;
                var s = _history.ToString(cut, Math.Min(4096, _history.Length - cut));
                var nl = s.IndexOf('\n');
                _history.Remove(0, cut + (nl >= 0 ? nl + 1 : 0));
            }
            // Under the same lock as Attach(): a new viewer gets history + live output with no gap or overlap.
            Output?.Invoke(this, text);
        }
        LastOutput = DateTime.Now;
        if (IsIdle) { IsIdle = false; StateChanged?.Invoke(this); }
        if (_armed) _workChars += VisibleLength(text);
        TrackTitle(text);
    }

    /// <summary>Subscribes a viewer and returns the history up to that instant, atomically.</summary>
    public string Attach(Action<TermSession, string> onOutput)
    {
        lock (_lock)
        {
            Output += onOutput;
            return _history.ToString();
        }
    }

    public void Detach(Action<TermSession, string> onOutput)
    {
        lock (_lock) Output -= onOutput;
    }

    private void TrackTitle(string text)
    {
        // OSC 0/2 ; title BEL — the PTY title (Claude Code animates a spinner here).
        int i = text.LastIndexOf("\x1b]0;", StringComparison.Ordinal);
        if (i < 0) i = text.LastIndexOf("\x1b]2;", StringComparison.Ordinal);
        if (i < 0) return;
        int end = text.IndexOf('\a', i);
        if (end < 0) return;
        var t = text[(i + 4)..end];
        if (t != ShellTitle) ShellTitle = t;
    }

    private void CheckIdle()
    {
        var secs = Math.Max(3, AppConfig.Current.IdleSeconds);
        if (!IsRunning || IsIdle || !_armed) return;
        if ((DateTime.Now - LastOutput).TotalSeconds < secs) return;
        // Only real work counts: output kept coming for a few seconds after the request and amounted to something
        // (a menu pick or a quick redraw after Enter is not "the AI finished").
        bool worked = (LastOutput - _armedAt).TotalSeconds >= 3 && _workChars >= 200;
        _armed = false;                       // one notification per request, never repeated by idle redraws
        if (!worked) return;
        IsIdle = true;
        StateChanged?.Invoke(this);
        BecameIdle?.Invoke(this);
    }

    // Output length without title updates (OSC) — Claude Code animates a spinner in the title even when idle.
    private static readonly System.Text.RegularExpressions.Regex Osc = new(@"\x1b\][^\x07\x1b]*(\x07|\x1b\\)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static int VisibleLength(string text) => text.IndexOf("\x1b]", StringComparison.Ordinal) < 0 ? text.Length : Osc.Replace(text, "").Length;

    /// <summary>Everything printed so far (for a viewer that just attached).</summary>
    public string History { get { lock (_lock) return _history.ToString(); } }

    // Replies xterm.js sends on its own (cursor position, device attributes, focus in/out, colour queries) —
    // not the user typing, so they must not count as activity.
    private static readonly System.Text.RegularExpressions.Regex AutoReply = new(
        @"^(\x1b\[[\d;?>]*[Rcn]|\x1b\[[IO]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1bP[^\x1b]*\x1b\\)+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public void Input(string viewer, string data)
    {
        if (!IsRunning) return;
        if (!AutoReply.IsMatch(data))
        {
            TakeControl(viewer);
            LastInput = DateTime.Now;
            if (data.Contains('\r')) { _armed = true; _armedAt = DateTime.Now; _workChars = 0; }   // a request was sent
            if (IsIdle) { IsIdle = false; StateChanged?.Invoke(this); }
        }
        _pty?.Write(data);
    }

    public void TakeControl(string viewer)
    {
        if (Controller == viewer) return;
        Controller = viewer;
        Resized?.Invoke(this, Cols, Rows);   // tells every mirror who is in control now
    }

    /// <summary>A viewer's preferred size. Applied only when it is the controller (or nobody is).</summary>
    public void Resize(string viewer, int cols, int rows)
    {
        cols = Math.Clamp(cols, 20, 500);
        rows = Math.Clamp(rows, 5, 200);
        Controller ??= viewer;
        if (Controller != viewer) return;
        if (cols == Cols && rows == Rows) return;
        Cols = cols;
        Rows = rows;
        _pty?.Resize(cols, rows);
        Resized?.Invoke(this, cols, rows);
    }

    public void Kill() => _pty?.Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dataArrived.Set();
        _pty?.Dispose();
        IsRunning = false;
    }
}

/// <summary>All sessions in the app; the UI and the web portal both read from here.</summary>
public static class TerminalHub
{
    private static readonly List<TermSession> _sessions = [];

    public static event Action? Changed;
    public static event Action<TermSession>? Idle;

    public static IReadOnlyList<TermSession> Sessions { get { lock (_sessions) return _sessions.ToList(); } }

    public static TermSession? Find(string id) { lock (_sessions) return _sessions.FirstOrDefault(s => s.Id == id); }

    public static TermSession Create(Launcher.Session spec, string kind)
    {
        var s = new TermSession(spec, kind);
        s.StateChanged += _ => Changed?.Invoke();
        s.BecameIdle += x => Idle?.Invoke(x);
        lock (_sessions) _sessions.Add(s);
        Changed?.Invoke();
        return s;
    }

    public static void Remove(TermSession s)
    {
        s.Dispose();
        lock (_sessions) _sessions.Remove(s);
        Changed?.Invoke();
    }
}
