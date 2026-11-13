using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Maison.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Maison.Services;

public static class AppInfo
{
    /// <summary>e.g. "0.0.1" (from the project's &lt;Version&gt;).</summary>
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}

/// <summary>Accent colours. Purple (the logo's violet) is the default on the grey/Mica theme.</summary>
public static class Theming
{
    public static readonly (string Name, string Hex)[] Accents =
    [
        ("Purple", "#8B6CFF"), ("Orchid", "#C76AD8"), ("Coral", "#E8825F"), ("Blue", "#4C8DFF"),
        ("Teal", "#2BB5A0"), ("Green", "#4CC38A"), ("Gold", "#E5B454"), ("Windows accent", ""),
    ];

    private static readonly string[] AccentColorKeys = ["SystemAccentColor", "SystemAccentColorPrimary", "SystemAccentColorSecondary", "SystemAccentColorTertiary"];
    /// <summary>Theme brush key → which accent shade (index into AccentColorKeys) it was built from.</summary>
    private static readonly Dictionary<object, int> AccentBrushKeys = [];
    private static readonly List<Color[]> SeenShades = [];

    private static Color[] CurrentShades() =>
        AccentColorKeys.Select(k => System.Windows.Application.Current.TryFindResource(k) is Color c ? c : Colors.Transparent).ToArray();

    private static IEnumerable<ResourceDictionary> AllDictionaries(ResourceDictionary d)
    {
        yield return d;
        foreach (var m in d.MergedDictionaries)
            foreach (var x in AllDictionaries(m)) yield return x;
    }

    /// <summary>
    /// WPF UI builds control brushes (toggle switch fill, focus borders, slider thumbs, check boxes…) from the accent
    /// colour once, so changing the accent left them in the old colour until a restart. Find every theme brush whose
    /// colour is one of the accent shades seen so far and redefine it (at app level) in the matching new shade.
    /// </summary>
    private static void RetintThemeBrushes(Color[] newShades)
    {
        var app = System.Windows.Application.Current;
        foreach (var dict in AllDictionaries(app.Resources).ToList())
            foreach (var key in dict.Keys.Cast<object>().ToList())
            {
                if (AccentBrushKeys.ContainsKey(key) || key is string s && s.StartsWith("Maison")) continue;
                SolidColorBrush? b;
                try { b = dict[key] as SolidColorBrush; }
                catch { continue; }   // a lazily-built resource that can't be built out of its own context: not ours
                if (b is null) continue;
                foreach (var shades in SeenShades)
                {
                    int i = Array.FindIndex(shades, c => c.A > 0 && c.R == b.Color.R && c.G == b.Color.G && c.B == b.Color.B);
                    if (i >= 0) { AccentBrushKeys[key] = i; break; }
                }
            }
        foreach (var (key, i) in AccentBrushKeys)
        {
            var old = app.TryFindResource(key) as SolidColorBrush;
            var nc = newShades[i];
            var brush = new SolidColorBrush(Color.FromArgb(old?.Color.A ?? 255, nc.R, nc.G, nc.B)) { Opacity = old?.Opacity ?? 1 };
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }

    public static void ApplyAccent(string name)
    {
        SeenShades.Add(CurrentShades());
        var hex = Accents.FirstOrDefault(a => a.Name == name).Hex;
        Color c;
        if (string.IsNullOrEmpty(hex))
        {
            ApplicationAccentColorManager.ApplySystemAccent();
            c = ApplicationAccentColorManager.SystemAccent;
        }
        else
        {
            c = (Color)ColorConverter.ConvertFromString(hex);
            // Keep accent buttons close to the chosen colour (the default dark-theme shades wash it out to near-white).
            ApplicationAccentColorManager.Apply(c, c, Mix(c, Colors.White, 0.12), Mix(c, Colors.White, 0.28));
        }
        // maison's own accent brushes: sidebar selection, tab underline, labels, icons, gauges.
        var res = System.Windows.Application.Current.Resources;
        res["MaisonAccentBrush"] = Frozen(c);
        res["MaisonAccentSoftBrush"] = Frozen(Color.FromArgb(0x38, c.R, c.G, c.B));
        res["MaisonAccentTextBrush"] = Frozen(Mix(c, Colors.White, 0.35));
        RetintThemeBrushes(CurrentShades());
        RecolourWindowBorders(c);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
    private const int DWMWA_BORDER_COLOR = 34;

    /// <summary>
    /// The thin accent border Windows draws round the focused window. WPF-UI only recolours it when focus changes, so
    /// after picking a new accent the focused window would keep the old colour until clicked away and back.
    /// </summary>
    private static void RecolourWindowBorders(Color c)
    {
        uint colorref = (uint)(c.R | c.G << 8 | c.B << 16);   // COLORREF is 0x00BBGGRR
        foreach (System.Windows.Window w in System.Windows.Application.Current.Windows)
        {
            if (!w.IsActive) continue;   // inactive windows keep Windows' dimmed border
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero) try { DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorref, sizeof(uint)); } catch { }
        }
    }

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}

/// <summary>"This terminal is idle" notifications: tray balloon + taskbar flash when maison isn't in front, a snackbar when it is.</summary>
public static class Notifier
{
    private static System.Windows.Forms.NotifyIcon? _tray;
    private static TermSession? _lastIdle;

    public static void Idle(MainWindow window, TermSession s)
    {
        var cfg = AppConfig.Current;
        if (!cfg.IdleNotify) return;
        var title = $"{s.Spec.Title} is idle";
        var body = "It finished and is waiting for you.";
        if (cfg.IdleSound) System.Media.SystemSounds.Asterisk.Play();

        if (window.IsActive)
        {
            window.Toast(title, body, ControlAppearance.Info, SymbolRegular.AlertOn24);
            return;
        }
        Flash(window);
        try
        {
            EnsureTray(window);
            _lastIdle = s;
            _tray!.BalloonTipTitle = title;
            _tray.BalloonTipText = body + " Click to open it.";
            _tray.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
            _tray.ShowBalloonTip(5000);
        }
        catch { /* notifications unavailable */ }
    }

    private static void EnsureTray(MainWindow window)
    {
        if (_tray is not null) return;
        System.Drawing.Icon icon;
        try
        {
            using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/brand/maison.ico"))!.Stream;
            icon = new System.Drawing.Icon(s, 32, 32);
        }
        catch { icon = System.Drawing.SystemIcons.Application; }
        _tray = new System.Windows.Forms.NotifyIcon { Icon = icon, Text = "maison", Visible = true };
        void Open()
        {
            window.Dispatcher.BeginInvoke(() =>
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
                window.Navigate("terminals");
                if (_lastIdle is not null) window.SelectSession(_lastIdle);
            });
        }
        _tray.BalloonTipClicked += (_, _) => Open();
        _tray.DoubleClick += (_, _) => Open();
    }

    private static void Flash(Window w)
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(w).Handle,
            dwFlags = 3 | 12,   // FLASHW_ALL | FLASHW_TIMERNOFG
            uCount = 5,
        };
        FlashWindowEx(ref info);
    }

    public static void Dispose()
    {
        if (_tray is null) return;
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
