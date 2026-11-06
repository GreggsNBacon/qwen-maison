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

    public static void ApplyAccent(string name)
    {
        var hex = Accents.FirstOrDefault(a => a.Name == name).Hex;
        if (string.IsNullOrEmpty(hex))
        {
            ApplicationAccentColorManager.ApplySystemAccent();
            return;
        }
        var c = (Color)ColorConverter.ConvertFromString(hex);
        ApplicationAccentColorManager.Apply(c, ApplicationTheme.Dark, systemGlassColor: false);
    }
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
