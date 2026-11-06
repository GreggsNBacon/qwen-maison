using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace Maison.Services;

/// <summary>
/// Installs the pieces a fresh PC needs: Ollama, Claude Code, Node.js and Qwen Code.
/// Everything is per-user except Node.js (winget asks for admin once).
/// </summary>
public static class Installer
{
    public const string OllamaSetupUrl = "https://ollama.com/download/OllamaSetup.exe";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static bool HasWinget => SystemProbe.FindOnPath("winget") is not null;
    public static string? ClaudePath => SystemProbe.FindOnPath("claude");
    public static string? QwenPath => SystemProbe.FindOnPath("qwen");
    public static string? NpmPath => SystemProbe.FindOnPath("npm");

    /// <summary>Downloads OllamaSetup.exe (with progress) and runs it silently, then starts the server.</summary>
    public static async Task InstallOllamaAsync(Action<string> log, IProgress<double> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Maison");
        Directory.CreateDirectory(dir);
        var setup = Path.Combine(dir, "OllamaSetup.exe");

        log($"Downloading {OllamaSetupUrl}");
        using (var resp = await Http.GetAsync(OllamaSetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(setup);
            var buf = new byte[1 << 20];
            long done = 0; int n; var last = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0 && (DateTime.Now - last).TotalMilliseconds > 250)
                {
                    progress.Report(100.0 * done / total);
                    last = DateTime.Now;
                }
            }
            progress.Report(100);
            log($"Downloaded {done / 1048576.0:N0} MB");
        }

        log("Running the Ollama installer silently (per-user, no admin needed)…");
        int code = await RunAsync(setup, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-"], log, ct);
        if (code != 0) throw new InvalidOperationException($"Ollama installer exited with code {code}");
        if (!SystemProbe.OllamaInstalled) throw new InvalidOperationException("Installer finished but ollama.exe was not found.");
        try { File.Delete(setup); } catch { /* keep going */ }

        log("Starting Ollama…");
        if (!await SystemProbe.StartOllamaAsync()) throw new InvalidOperationException("Ollama installed but the server did not start.");
        log("Ollama is running.");
    }

    /// <summary>Anthropic's native installer for Claude Code (per-user, adds itself to PATH).</summary>
    public static async Task InstallClaudeAsync(Action<string> log, CancellationToken ct)
    {
        log("Installing Claude Code (irm https://claude.ai/install.ps1 | iex)…");
        int code = await RunAsync("powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "irm https://claude.ai/install.ps1 | iex"], log, ct);
        if (code != 0 || ClaudePath is null) throw new InvalidOperationException($"Claude Code install failed (exit {code}).");
        log("Claude Code installed: " + ClaudePath);
    }

    /// <summary>Node.js LTS via winget (needed for Qwen Code). Windows will ask for admin once.</summary>
    public static async Task InstallNodeAsync(Action<string> log, CancellationToken ct)
    {
        if (!HasWinget) throw new InvalidOperationException("winget is not available. Install Node.js LTS from https://nodejs.org and try again.");
        log("Installing Node.js LTS with winget (approve the Windows admin prompt if it appears)…");
        int code = await RunAsync("winget",
            ["install", "--id", "OpenJS.NodeJS.LTS", "-e", "--silent", "--accept-source-agreements", "--accept-package-agreements"], log, ct);
        if (NpmPath is null) throw new InvalidOperationException($"Node.js install did not complete (winget exit {code}).");
        log("Node.js installed: " + NpmPath);
    }

    /// <summary>Qwen Code CLI from npm.</summary>
    public static async Task InstallQwenAsync(Action<string> log, CancellationToken ct)
    {
        var npm = NpmPath ?? throw new InvalidOperationException("npm not found. Install Node.js first.");
        log("Installing Qwen Code (npm install -g @qwen-code/qwen-code)…");
        int code = await RunAsync("cmd.exe", ["/d", "/c", npm, "install", "-g", "@qwen-code/qwen-code"], log, ct);
        if (code != 0 || QwenPath is null) throw new InvalidOperationException($"Qwen Code install failed (exit {code}).");
        log("Qwen Code installed: " + QwenPath);
    }

    // ------------------------------------------------------------------ uninstall / reinstall

    /// <summary>Runs Ollama's own uninstaller silently. Downloaded models (in the model folder) are kept.</summary>
    public static async Task UninstallOllamaAsync(Action<string> log, CancellationToken ct)
    {
        var unins = Path.Combine(SystemProbe.OllamaDir, "unins000.exe");
        if (!File.Exists(unins)) throw new InvalidOperationException("Ollama's uninstaller wasn't found — it may already be removed.");
        log("Stopping Ollama…");
        await SystemProbe.StopOllamaAsync();
        log("Running the Ollama uninstaller…");
        int code = await RunAsync(unins, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], log, ct);
        for (int i = 0; i < 20 && SystemProbe.OllamaInstalled; i++) await Task.Delay(500, ct);   // the uninstaller finishes in a child process
        if (SystemProbe.OllamaInstalled) throw new InvalidOperationException($"Ollama is still installed (uninstaller exit {code}).");
        log("Ollama removed. Your models are still in " + OllamaClient.ModelsDir);
    }

    /// <summary>Removes the native Claude Code install (program files only; ~/.claude settings and history are kept).</summary>
    public static Task UninstallClaudeAsync(Action<string> log, CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var exe = Path.Combine(home, ".local", "bin", "claude.exe");
        var data = Path.Combine(home, ".local", "share", "claude");
        if (!File.Exists(exe) && !Directory.Exists(data))
            throw new InvalidOperationException("No native Claude Code install found in ~/.local (it may have been installed another way, e.g. npm).");
        try
        {
            if (File.Exists(exe)) { File.Delete(exe); log("Removed " + exe); }
            if (Directory.Exists(data)) { Directory.Delete(data, recursive: true); log("Removed " + data); }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException("Claude Code is running somewhere (a terminal, an editor or a background session). Close every Claude Code window and try again.");
        }
        log("Claude Code removed. Your settings in ~/.claude were kept.");
        return Task.CompletedTask;
    }

    public static async Task UninstallNodeAsync(Action<string> log, CancellationToken ct)
    {
        if (!HasWinget) throw new InvalidOperationException("winget is not available — remove Node.js from Settings → Apps.");
        log("Uninstalling Node.js LTS with winget (approve the Windows admin prompt if it appears)…");
        int code = await RunAsync("winget", ["uninstall", "--id", "OpenJS.NodeJS.LTS", "-e", "--silent", "--accept-source-agreements"], log, ct);
        if (NpmPath is not null) throw new InvalidOperationException($"Node.js is still installed (winget exit {code}). It may have been installed another way — remove it from Settings → Apps.");
        log("Node.js removed.");
    }

    public static async Task UninstallQwenAsync(Action<string> log, CancellationToken ct)
    {
        var npm = NpmPath ?? throw new InvalidOperationException("npm not found, so Qwen Code can't be uninstalled through it.");
        log("Uninstalling Qwen Code (npm uninstall -g @qwen-code/qwen-code)…");
        int code = await RunAsync("cmd.exe", ["/d", "/c", npm, "uninstall", "-g", "@qwen-code/qwen-code"], log, ct);
        if (QwenPath is not null) throw new InvalidOperationException($"Qwen Code is still installed (npm exit {code}). Close any running Qwen Code and try again.");
        log("Qwen Code removed. Its settings in ~/.qwen were kept.");
    }

    /// <summary>Reinstalls Node.js over the top (winget --force).</summary>
    public static async Task ReinstallNodeAsync(Action<string> log, CancellationToken ct)
    {
        if (!HasWinget) throw new InvalidOperationException("winget is not available.");
        log("Reinstalling Node.js LTS with winget…");
        await RunAsync("winget", ["install", "--id", "OpenJS.NodeJS.LTS", "-e", "--silent", "--force", "--accept-source-agreements", "--accept-package-agreements"], log, ct);
        if (NpmPath is null) throw new InvalidOperationException("Node.js reinstall did not complete.");
        log("Node.js reinstalled.");
    }

    /// <summary>Runs a process hidden, streaming its output lines to <paramref name="log"/>. Uses the fresh PATH.</summary>
    public static async Task<int> RunAsync(string file, IEnumerable<string> args, Action<string> log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = SystemProbe.FreshPath;

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void OnLine(object _, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            // winget/npm draw progress bars with \r and block characters; keep only readable text.
            var text = e.Data.Split('\r').Last().Trim();
            if (text.Length > 0 && text.Any(char.IsLetterOrDigit)) log(text);
        }
        p.OutputDataReceived += OnLine;
        p.ErrorDataReceived += OnLine;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }
}
