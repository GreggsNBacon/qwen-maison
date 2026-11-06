using System.IO;
using System.Reflection;
using System.Text.Json;
using Maison.Models;

namespace Maison.Services;

/// <summary>Builds the xterm.js page shared by the in-app viewers (WebView2) and the web portal (WebSocket).</summary>
public static class TerminalPage
{
    public static readonly Dictionary<string, Dictionary<string, string>> Themes = new()
    {
        ["maison"] = Theme("#18181d", "#e8e6ef", "#a98bff", "#7c5cff55",
            "#0c0c10", "#e5534b", "#4cc38a", "#e5b454", "#7aa2f7", "#b48cff", "#56b6c2", "#cfcfd8",
            "#6e6e7a", "#ff6b63", "#6fdca5", "#f0c674", "#9ab8ff", "#cdb4ff", "#7fd6e0", "#f4f3f8"),
        ["Campbell"] = Theme("#0c0c0c", "#cccccc", "#ffffff", "#ffffff40",
            "#0c0c0c", "#c50f1f", "#13a10e", "#c19c00", "#0037da", "#881798", "#3a96dd", "#cccccc",
            "#767676", "#e74856", "#16c60c", "#f9f1a5", "#3b78ff", "#b4009e", "#61d6d6", "#f2f2f2"),
        ["One Half Dark"] = Theme("#282c34", "#dcdfe4", "#a3b3cc", "#ffffff40",
            "#282c34", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#dcdfe4",
            "#5a6374", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#dcdfe4"),
        ["Solarized Dark"] = Theme("#002b36", "#839496", "#93a1a1", "#ffffff30",
            "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5",
            "#586e75", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3"),
        ["Midnight Violet"] = Theme("#120f1f", "#e4defa", "#c76ad8", "#c76ad855",
            "#0b0914", "#ff6b8b", "#5fd4a0", "#f5c26b", "#8c9cff", "#c76ad8", "#64d2e8", "#d9d3f0",
            "#5d5778", "#ff8aa3", "#7fe6b8", "#ffd68a", "#a9b5ff", "#e08cf0", "#8ae4f5", "#f6f3ff"),
        ["Light"] = Theme("#fafafa", "#383a42", "#526eff", "#526eff33",
            "#383a42", "#e45649", "#50a14f", "#c18401", "#0184bc", "#a626a4", "#0997b3", "#fafafa",
            "#4f525e", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#ffffff"),
    };

    private static Dictionary<string, string> Theme(string bg, string fg, string cursor, string sel, params string[] ansi)
    {
        string[] names = ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
                          "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite"];
        var d = new Dictionary<string, string> { ["background"] = bg, ["foreground"] = fg, ["cursor"] = cursor, ["cursorAccent"] = bg, ["selectionBackground"] = sel };
        for (int i = 0; i < names.Length; i++) d[names[i]] = ansi[i];
        return d;
    }

    public static Dictionary<string, string> CurrentTheme =>
        Themes.TryGetValue(AppConfig.Current.TerminalTheme, out var t) ? t : Themes["maison"];

    public static string ThemeJson => JsonSerializer.Serialize(CurrentTheme);

    private static readonly Dictionary<string, string> Cache = [];

    public static string Res(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var v)) return v;
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("terminal/" + name)
                          ?? throw new FileNotFoundException("Missing embedded resource terminal/" + name);
            using var r = new StreamReader(s);
            return Cache[name] = r.ReadToEnd().Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The terminal page. <paramref name="transport"/> is "webview" (WPF host) or "ws" (web portal, connects to wsUrl).
    /// </summary>
    public static string Build(string transport, string wsUrl = "", string extraHead = "", string extraBody = "", string extraScript = "")
    {
        var bg = CurrentTheme["background"];
        return $$"""
        <!DOCTYPE html>
        <html><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">
        <style>
        {{Res("xterm.css")}}
        html, body { margin: 0; height: 100%; background: {{bg}}; overflow: hidden; }
        #wrap { position: absolute; top: 8px; left: 12px; right: 4px; bottom: 6px; overflow: hidden; }
        #t { position: absolute; inset: 0; transform-origin: 0 0; }
        #follow { position: absolute; right: 10px; bottom: 8px; font: 12px 'Segoe UI', sans-serif; color: #cfc6ff;
                  background: #2a2140cc; border: 1px solid #7c5cff66; padding: 4px 10px; border-radius: 12px; display: none; cursor: pointer; z-index: 5; }
        .xterm-viewport::-webkit-scrollbar { width: 10px; }
        .xterm-viewport::-webkit-scrollbar-thumb { background: #3a3a46; border-radius: 5px; }
        {{extraHead}}
        </style></head>
        <body><script>window.__errs = []; window.addEventListener('error', e => window.__errs.push(String(e.message) + ' @' + e.lineno));</script>
        {{extraBody}}<div id="wrap"><div id="t"></div><div id="follow" title="This session is sized for another window. Click (or type) to fit it here.">mirrored · tap to fit here</div></div>
        <script>{{Res("xterm.js")}}</script>
        <script>{{Res("addon-fit.js")}}</script>
        <script>{{Res("addon-unicode11.js")}}</script>
        <script>{{Res("addon-webgl.js")}}</script>
        <script>
        const TRANSPORT = '{{transport}}';
        const term = new Terminal({
          allowProposedApi: true, cursorBlink: true, scrollback: 10000, smoothScrollDuration: 0,
          fontFamily: "'Cascadia Mono', 'Cascadia Code', Consolas, Menlo, monospace", fontSize: {{AppConfig.Current.TerminalFontSize}},
          theme: {{ThemeJson}}
        });
        const fit = new FitAddon.FitAddon();
        term.loadAddon(fit);
        term.loadAddon(new Unicode11Addon.Unicode11Addon());
        term.unicode.activeVersion = '11';
        term.open(document.getElementById('t'));
        try { const gl = new WebglAddon.WebglAddon(); gl.onContextLoss(() => gl.dispose()); term.loadAddon(gl); } catch (e) { /* DOM renderer fallback */ }

        // ---- transport
        let send = () => {};
        let ws = null;
        if (TRANSPORT === 'webview') {
          send = m => window.chrome.webview.postMessage(m);
          window.chrome.webview.addEventListener('message', e => handle(e.data));
        } else {
          const connect = () => {
            ws = new WebSocket('{{wsUrl}}'.replace(/^http/, 'ws'));
            ws.onopen = () => send({ t: 'ready', cols: term.cols, rows: term.rows });
            ws.onmessage = e => handle(JSON.parse(e.data));
            ws.onclose = () => { term.write('\r\n\x1b[90m[disconnected — reconnecting…]\x1b[0m\r\n'); setTimeout(connect, 2000); };
          };
          send = m => { if (ws && ws.readyState === 1) ws.send(JSON.stringify(m)); };
          connect();
        }

        // ---- sizing: the window the user is using drives the PTY; other mirrors follow and scale to fit.
        let controller = true;
        const followEl = document.getElementById('follow');
        const tEl = document.getElementById('t');
        const applyScale = () => {
          if (controller) { tEl.style.transform = ''; tEl.style.right = '0'; tEl.style.bottom = '0'; return; }
          const screen = tEl.querySelector('.xterm-screen');
          if (!screen) return;
          const wrap = document.getElementById('wrap');
          const s = Math.min(1, wrap.clientWidth / screen.offsetWidth, wrap.clientHeight / screen.offsetHeight);
          tEl.style.right = 'auto'; tEl.style.bottom = 'auto';
          tEl.style.width = screen.offsetWidth + 'px'; tEl.style.height = screen.offsetHeight + 'px';
          tEl.style.transform = s < 1 ? `scale(${s})` : '';
        };
        const claim = () => {
          if (controller) return;
          controller = true;
          followEl.style.display = 'none';
          tEl.style.width = ''; tEl.style.height = ''; applyScale();
          const d = fit.proposeDimensions();
          if (d && d.cols && d.rows) { term.resize(d.cols, d.rows); }
          send({ t: 'claim', cols: term.cols, rows: term.rows });
        };
        const doFit = () => { if (!controller) { applyScale(); return; } try { fit.fit(); } catch (e) {} };
        followEl.addEventListener('click', () => { claim(); term.focus(); });

        // ---- clipboard
        let lastPaste = { text: '', at: 0 };
        const pasteText = text => {
          if (!text) return;
          const now = Date.now();
          if (text === lastPaste.text && now - lastPaste.at < 500) return;   // one paste per gesture
          lastPaste = { text, at: now };
          claim();
          term.paste(text);
        };
        const copySel = () => { const s = term.getSelection(); if (s) send({ t: 'copy', d: s }); term.clearSelection(); };

        term.attachCustomKeyEventHandler(e => {
          if (e.type !== 'keydown') return true;
          const k = e.code;
          // Copy: Ctrl+Shift+C, or Ctrl+C with a selection (otherwise Ctrl+C interrupts as usual).
          if (e.ctrlKey && k === 'KeyC' && (e.shiftKey || term.hasSelection())) { copySel(); e.preventDefault(); return false; }
          // Paste: Ctrl+V / Ctrl+Shift+V / Shift+Insert.
          if ((e.ctrlKey && k === 'KeyV') || (e.shiftKey && k === 'Insert')) {
            e.preventDefault();
            if (TRANSPORT === 'webview') send({ t: 'paste' });
            else if (navigator.clipboard && navigator.clipboard.readText) navigator.clipboard.readText().then(pasteText).catch(() => {});
            return false;
          }
          return true;
        });
        // Swallow the browser's own paste event in the app (the host reads the clipboard) so it can't double up.
        document.addEventListener('paste', e => {
          if (TRANSPORT === 'webview') { e.preventDefault(); e.stopImmediatePropagation(); return; }
          const text = e.clipboardData && e.clipboardData.getData('text');
          e.preventDefault(); e.stopImmediatePropagation();
          pasteText(text);
        }, true);
        // Right-click: copy the selection, or paste when nothing is selected (like Windows Terminal).
        document.addEventListener('contextmenu', e => {
          e.preventDefault();
          if (term.hasSelection()) copySel();
          else if (TRANSPORT === 'webview') send({ t: 'paste' });
        });

        // xterm.js answers terminal queries (cursor position, device attributes, focus) by itself. Every mirror would
        // answer, so only the window in control forwards those, and they never take control.
        const AUTO = /^(\x1b\[[\d;?>]*[Rcn]|\x1b\[[IO]|\x1b\][^\x07\x1b]*(\x07|\x1b\\))+$/;
        term.onData(d => {
          if (AUTO.test(d)) { if (controller) send({ t: 'i', d }); return; }
          claim(); send({ t: 'i', d });
        });
        term.onBinary(d => send({ t: 'i', d }));
        term.onResize(s => { if (controller) send({ t: 'r', cols: s.cols, rows: s.rows }); });
        term.textarea && term.textarea.addEventListener('focus', () => send({ t: 'focus' }));
        new ResizeObserver(doFit).observe(document.getElementById('wrap'));

        function handle(m) {
          switch (m.t) {
            case 'o': term.write(m.d); break;
            case 'reset':
              term.reset();
              controller = !!m.ctl;
              if (!controller) { term.resize(m.cols, m.rows); followEl.style.display = 'block'; }
              else { followEl.style.display = 'none'; doFit(); }
              term.write(m.d, () => { applyScale(); term.scrollToBottom(); });
              break;
            case 'size':
              controller = !!m.ctl;
              followEl.style.display = controller ? 'none' : 'block';
              if (!controller) { tEl.style.width = ''; tEl.style.height = ''; term.resize(m.cols, m.rows); requestAnimationFrame(applyScale); }
              else doFit();
              break;
            case 'paste': pasteText(m.d); break;
            case 'focus': term.focus(); break;
            case 'theme':
              term.options.theme = m.theme; term.options.fontSize = m.fontSize;
              document.body.style.background = m.theme.background; doFit(); break;
          }
        }
        doFit();
        if (TRANSPORT === 'webview') send({ t: 'ready', cols: term.cols, rows: term.rows });
        term.focus();
        {{extraScript}}
        </script></body></html>
        """;
    }
}
