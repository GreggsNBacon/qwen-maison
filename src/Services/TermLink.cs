using System.Text.Json;

namespace Maison.Services;

/// <summary>
/// Connects one viewer (a WebView2 page or a portal WebSocket) to a <see cref="TermSession"/>:
/// replays history on attach, streams output, forwards keys/resizes and keeps mirrors sized consistently.
/// </summary>
public sealed class TermLink : IDisposable
{
    private readonly TermSession _s;
    private readonly Action<string> _send;            // sends a JSON message to the viewer (thread-safe)
    private readonly Func<string?>? _readClipboard;   // host clipboard (app only)
    private readonly Action<string>? _writeClipboard;
    private bool _attached, _disposed;

    public string ViewerId { get; } = "v" + Guid.NewGuid().ToString("N")[..8];
    public TermSession Session => _s;

    public TermLink(TermSession session, Action<string> send, Func<string?>? readClipboard = null, Action<string>? writeClipboard = null)
    {
        _s = session;
        _send = send;
        _readClipboard = readClipboard;
        _writeClipboard = writeClipboard;
    }

    private void Post(object m) { if (!_disposed) _send(JsonSerializer.Serialize(m)); }

    private void OnResized(TermSession _, int cols, int rows) => Post(new { t = "size", cols, rows, ctl = _s.Controller == ViewerId });

    /// <summary>Handles one JSON message from the viewer.</summary>
    public void Receive(string json)
    {
        if (_disposed) return;
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        switch (m.GetProperty("t").GetString())
        {
            case "ready":
            {
                int cols = m.GetProperty("cols").GetInt32(), rows = m.GetProperty("rows").GetInt32();
                if (!_s.HasStarted)
                {
                    // First viewer starts the process at its own size.
                    _s.TakeControl(ViewerId);
                    _s.Start(cols, rows);
                }
                Attach();
                break;
            }
            case "i":
                _s.Input(ViewerId, m.GetProperty("d").GetString() ?? "");
                break;
            case "r":
                _s.Resize(ViewerId, m.GetProperty("cols").GetInt32(), m.GetProperty("rows").GetInt32());
                break;
            case "claim":
                _s.TakeControl(ViewerId);
                _s.Resize(ViewerId, m.GetProperty("cols").GetInt32(), m.GetProperty("rows").GetInt32());
                break;
            case "copy":
                _writeClipboard?.Invoke(m.GetProperty("d").GetString() ?? "");
                break;
            case "paste":
                var text = _readClipboard?.Invoke();
                if (!string.IsNullOrEmpty(text)) Post(new { t = "paste", d = text });
                break;
        }
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        _s.Resized += OnResized;
        lock (_sendLock)
        {
            // History and live output are handed over atomically; the reset is queued before any later chunk.
            var history = _s.Attach(OnOutputLocked);
            _send(JsonSerializer.Serialize(new { t = "reset", d = history, cols = _s.Cols, rows = _s.Rows, ctl = _s.Controller == ViewerId }));
            _ready = true;
            foreach (var q in _queued) _send(q);
            _queued.Clear();
        }
    }

    private readonly object _sendLock = new();
    private readonly List<string> _queued = [];
    private bool _ready;

    private void OnOutputLocked(TermSession _, string text)
    {
        var json = JsonSerializer.Serialize(new { t = "o", d = text });
        lock (_sendLock)
        {
            if (_disposed) return;
            if (_ready) _send(json); else _queued.Add(json);
        }
    }

    public void SendTheme(object theme, int fontSize) => Post(new { t = "theme", theme, fontSize });
    public void Focus() => Post(new { t = "focus" });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _s.Detach(OnOutputLocked);
        _s.Resized -= OnResized;
    }
}
