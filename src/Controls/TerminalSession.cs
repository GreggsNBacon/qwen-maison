using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QwenLauncher.Services;

namespace QwenLauncher.Controls;

/// <summary>
/// One in-app terminal: xterm.js (in WebView2) wired to a ConPTY running PowerShell → Claude Code / Qwen Code / chat.
/// </summary>
public sealed class TerminalSession : Grid, IDisposable
{
    private static Task<CoreWebView2Environment>? _env;
    private static string? _html;

    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1B, 0x1B, 0x1B) };
    private readonly StringBuilder _pending = new();
    private readonly DispatcherTimer _flush = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private PseudoConsole? _pty;
    private bool _disposed;

    public Launcher.Session Spec { get; }
    public bool IsRunning { get; private set; }
    public string? ShellTitle { get; private set; }

    public event Action<TerminalSession>? StateChanged;

    public TerminalSession(Launcher.Session spec)
    {
        Spec = spec;
        Children.Add(_web);
        _flush.Tick += (_, _) => Flush();
    }

    public async Task StartAsync()
    {
        _env ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QwenLauncher", "WebView2"));
        await _web.EnsureCoreWebView2Async(await _env);
        var s = _web.CoreWebView2.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;   // keep Ctrl+R/F5/Ctrl+F etc. for the terminal app
        _web.CoreWebView2.WebMessageReceived += OnMessage;
        _web.NavigateToString(_html ??= BuildHtml());
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("t").GetString())
        {
            case "ready":
                StartProcess((short)m.GetProperty("cols").GetInt32(), (short)m.GetProperty("rows").GetInt32());
                break;
            case "i":
                _pty?.Write(m.GetProperty("d").GetString() ?? "");
                break;
            case "r":
                _pty?.Resize(m.GetProperty("cols").GetInt32(), m.GetProperty("rows").GetInt32());
                break;
            case "copy":
                try { Clipboard.SetText(m.GetProperty("d").GetString() ?? ""); } catch { /* clipboard busy */ }
                break;
            case "paste":
                try { if (Clipboard.ContainsText()) Post(new { t = "paste", d = Clipboard.GetText() }); } catch { }
                break;
            case "title":
                ShellTitle = m.GetProperty("d").GetString();
                StateChanged?.Invoke(this);
                break;
        }
    }

    private void StartProcess(short cols, short rows)
    {
        if (_pty is not null) return;
        if (!Directory.Exists(Spec.WorkDir))
        {
            Write($"\x1b[31mWorking folder not found: {Spec.WorkDir}\x1b[0m\r\n");
            return;
        }
        try
        {
            _pty = PseudoConsole.Start(Spec.CommandLine, Spec.WorkDir, Math.Max((short)20, cols), Math.Max((short)5, rows));
        }
        catch (Exception ex)
        {
            Write($"\x1b[31mCould not start the session: {ex.Message}\x1b[0m\r\n");
            return;
        }
        _pty.Output += text => { lock (_pending) _pending.Append(text); };
        _pty.Exited += code => Dispatcher.BeginInvoke(() =>
        {
            Flush();
            Write($"\r\n\x1b[90m[session ended — exit code {code}. Close this tab or launch a new one.]\x1b[0m\r\n");
            IsRunning = false;
            StateChanged?.Invoke(this);
        });
        IsRunning = true;
        _flush.Start();
        StateChanged?.Invoke(this);
        FocusTerminal();
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
        Write(text);
    }

    private void Write(string text) => Post(new { t = "o", d = text });

    private void Post(object message)
    {
        if (_disposed || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    public void FocusTerminal()
    {
        _web.Focus();
        Post(new { t = "focus" });
    }

    /// <summary>Types text as if from the keyboard (goes through xterm.js onData, like real input). Used by the self-test.</summary>
    public Task TypeAsync(string text) =>
        _web.CoreWebView2.ExecuteScriptAsync($"term.input({JsonSerializer.Serialize(text)}, true)");

    /// <summary>Visible text of the terminal buffer (self-test).</summary>
    public async Task<string> ReadScreenAsync()
    {
        Flush();
        await Task.Delay(300);
        var json = await _web.CoreWebView2.ExecuteScriptAsync(
            "(() => { const b = term.buffer.active; const out = []; for (let i = 0; i < b.length; i++) out.push(b.getLine(i).translateToString(true)); return out.join('\\n').trim(); })()");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    /// <summary>PNG snapshot of the terminal (WebView2 content isn't captured by RenderTargetBitmap).</summary>
    public async Task CaptureAsync(string file)
    {
        Flush();
        await Task.Delay(300);
        await using var f = File.Create(file);
        await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, f);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flush.Stop();
        _pty?.Dispose();
        _web.Dispose();
    }

    // ------------------------------------------------------------------ page

    private static string Res(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("terminal/" + name)
                      ?? throw new FileNotFoundException("Missing embedded resource terminal/" + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd().Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildHtml() => $$"""
        <!DOCTYPE html>
        <html><head><meta charset="utf-8">
        <style>
        {{Res("xterm.css")}}
        html, body { margin: 0; height: 100%; background: #1b1b1b; overflow: hidden; }
        #t { position: absolute; top: 8px; left: 12px; right: 4px; bottom: 6px; }
        .xterm-viewport::-webkit-scrollbar { width: 10px; }
        .xterm-viewport::-webkit-scrollbar-thumb { background: #3a3a3a; border-radius: 5px; }
        </style></head>
        <body><div id="t"></div>
        <script>{{Res("xterm.js")}}</script>
        <script>{{Res("addon-fit.js")}}</script>
        <script>{{Res("addon-unicode11.js")}}</script>
        <script>
        const term = new Terminal({
          allowProposedApi: true, cursorBlink: true, scrollback: 10000,
          fontFamily: "'Cascadia Mono', 'Cascadia Code', Consolas, monospace", fontSize: 14,
          theme: {
            background: '#1b1b1b', foreground: '#e6e6e6', cursor: '#e6e6e6', selectionBackground: '#3a5a8c99',
            black: '#0c0c0c', red: '#e5534b', green: '#4cc38a', yellow: '#e5b454', blue: '#6ca0dc', magenta: '#b48ead', cyan: '#56b6c2', white: '#cccccc',
            brightBlack: '#767676', brightRed: '#ff6b63', brightGreen: '#6fdca5', brightYellow: '#f0c674', brightBlue: '#8fb8f0', brightMagenta: '#c9a0dc', brightCyan: '#7fd6e0', brightWhite: '#f2f2f2'
          }
        });
        const fit = new FitAddon.FitAddon();
        term.loadAddon(fit);
        term.loadAddon(new Unicode11Addon.Unicode11Addon());
        term.unicode.activeVersion = '11';
        term.open(document.getElementById('t'));
        const post = m => window.chrome.webview.postMessage(m);
        const doFit = () => { try { fit.fit(); } catch (e) {} };
        doFit();

        // Ctrl+Shift+C copies; Ctrl+C copies when text is selected (otherwise it interrupts, as usual).
        term.attachCustomKeyEventHandler(e => {
          if (e.type !== 'keydown') return true;
          if (e.ctrlKey && e.code === 'KeyC' && (e.shiftKey || term.hasSelection())) {
            if (term.hasSelection()) { post({ t: 'copy', d: term.getSelection() }); term.clearSelection(); }
            return false;
          }
          return true;
        });
        // Right-click: copy the selection, or paste when nothing is selected (like Windows Terminal).
        document.addEventListener('contextmenu', e => {
          e.preventDefault();
          if (term.hasSelection()) { post({ t: 'copy', d: term.getSelection() }); term.clearSelection(); }
          else post({ t: 'paste' });
        });

        term.onData(d => post({ t: 'i', d }));
        term.onResize(s => post({ t: 'r', cols: s.cols, rows: s.rows }));
        term.onTitleChange(t => post({ t: 'title', d: t }));
        new ResizeObserver(doFit).observe(document.getElementById('t'));
        window.chrome.webview.addEventListener('message', e => {
          const m = e.data;
          if (m.t === 'o') term.write(m.d);
          else if (m.t === 'paste') term.paste(m.d);
          else if (m.t === 'focus') term.focus();
        });
        post({ t: 'ready', cols: term.cols, rows: term.rows });
        term.focus();
        </script></body></html>
        """;
}
