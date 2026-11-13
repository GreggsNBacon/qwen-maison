using Microsoft.Win32;

namespace Maison.Services;

/// <summary>
/// "Start with Windows": an entry in the user's own Run key (no admin). It points at the exe that turned it on. If that
/// file is gone (e.g. the old version was deleted after updating), the next maison that starts points it at itself.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string MinimizedArg = "--minimized";

    /// <summary>The Run value's name ("maison"; tests use their own via MAISON_RUN_NAME).</summary>
    private static string ValueName => Environment.GetEnvironmentVariable("MAISON_RUN_NAME") is { Length: > 0 } n ? n : "maison";

    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>What's registered now, or null.</summary>
    public static string? Command
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) as string;
        }
    }

    public static bool IsOn => Command is not null;
    public static bool StartsMinimized => Command?.Contains(MinimizedArg) == true;

    public static void Set(bool on, bool minimized)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(ValueName, $"\"{ExePath}\"" + (minimized ? " " + MinimizedArg : ""));
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>At start-up: if it's on but its exe no longer exists (deleted after updating, or moved), point it here.</summary>
    public static void Refresh()
    {
        try
        {
            if (Command is { } c && c.StartsWith('"') && c.IndexOf('"', 1) is var end and > 1 && !System.IO.File.Exists(c[1..end]))
                Set(true, StartsMinimized);
        }
        catch { /* registry unavailable: leave it */ }
    }
}
