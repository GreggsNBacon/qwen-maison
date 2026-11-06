using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Maison.Services;

namespace Maison.Controls;

/// <summary>
/// A view onto a shared <see cref="TermSession"/>: xterm.js in WebView2. Several viewers (app tab, pop-out window)
/// can show the same session at once; closing a viewer does not end the session.
/// </summary>
public sealed class TerminalViewer : Grid, IDisposable
{
    private static Task<CoreWebView2Environment>? _env;

    private readonly WebView2 _web;
    private TermLink? _link;
    private bool _disposed;

    public TermSession Session { get; }
    public bool IsRunning => Session.IsRunning;

    public TerminalViewer(TermSession session)
    {
        Session = session;
        var bg = System.Drawing.ColorTranslator.FromHtml(TerminalPage.CurrentTheme["background"]);
        _web = new WebView2 { DefaultBackgroundColor = bg };
        Children.Add(_web);
    }

    public async Task StartAsync()
    {
        _env ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maison", "WebView2"));
        await _web.EnsureCoreWebView2Async(await _env);
        var s = _web.CoreWebView2.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;   // keep Ctrl+R/F5/Ctrl+F etc. for the terminal app

        _link = new TermLink(Session,
            json => Dispatcher.BeginInvoke(() => { if (!_disposed && _web.CoreWebView2 is not null) _web.CoreWebView2.PostWebMessageAsJson(json); }),
            readClipboard: () =>
            {
                try { return Dispatcher.Invoke(() => Clipboard.ContainsText() ? Clipboard.GetText() : null); } catch { return null; }
            },
            writeClipboard: text => Dispatcher.BeginInvoke(() => { try { Clipboard.SetText(text); } catch { /* clipboard busy */ } }));

        _web.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            try { _link?.Receive(e.WebMessageAsJson); } catch { /* malformed message */ }
        };
        _web.NavigateToString(TerminalPage.Build("webview"));
    }

    public void FocusTerminal()
    {
        _web.Focus();
        _link?.Focus();
    }

    public void ApplyTheme()
    {
        _link?.SendTheme(TerminalPage.CurrentTheme, Models.AppConfig.Current.TerminalFontSize);
    }

    /// <summary>Types text as if from the keyboard (goes through xterm.js onData, like real input). Used by the self-test.</summary>
    public Task TypeAsync(string text) =>
        _web.CoreWebView2.ExecuteScriptAsync($"term.input({JsonSerializer.Serialize(text)}, true)");

    /// <summary>Visible text of the terminal buffer (self-test).</summary>
    public async Task<string> ReadScreenAsync()
    {
        await Task.Delay(300);
        var json = await _web.CoreWebView2.ExecuteScriptAsync(
            "(() => { const b = term.buffer.active; const out = []; for (let i = 0; i < b.length; i++) out.push(b.getLine(i).translateToString(true)); return out.join('\\n').trim(); })()");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    /// <summary>Runs script in the page (self-test).</summary>
    public async Task<string> EvalAsync(string script) => await _web.CoreWebView2.ExecuteScriptAsync(script);

    /// <summary>PNG snapshot of the terminal (WebView2 content isn't captured by RenderTargetBitmap).</summary>
    public async Task CaptureAsync(string file)
    {
        await Task.Delay(300);
        await using var f = File.Create(file);
        await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, f);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _link?.Dispose();
        _web.Dispose();
    }
}
