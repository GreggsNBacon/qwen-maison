using System.Diagnostics;
using System.IO;
using System.Text;

namespace Maison.Services;

/// <summary>
/// Builds the per-window environment/commands for Claude Code, Qwen Code and plain chat,
/// and opens them in Windows Terminal (or a PowerShell window).
/// Nothing global is changed: settings live only in the launched window.
/// </summary>
public static class Launcher
{
    public static readonly Dictionary<string, int> ContextSizes = new()
    {
        ["Ollama default"] = 0, ["32K"] = 32768, ["64K"] = 65536, ["128K"] = 131072,
    };

    /// <summary>Name of the settings-only variant used for a non-default context, e.g. qwen3.8:27b-ctx64k.</summary>
    public static string EffectiveModel(string baseModel, int ctx)
    {
        if (ctx == 0 || string.IsNullOrWhiteSpace(baseModel)) return baseModel;
        var suffix = $"-ctx{ctx / 1024}k";
        return baseModel.Contains(':') ? baseModel + suffix : $"{baseModel}:latest{suffix}";
    }

    public static bool IsVariant(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, @"-ctx\d+k$");

    public static Dictionary<string, string> ClaudeEnv(string main, string fast) => new()
    {
        ["ANTHROPIC_BASE_URL"] = OllamaClient.BaseUrl,
        ["ANTHROPIC_AUTH_TOKEN"] = "ollama",
        ["ANTHROPIC_MODEL"] = main,
        ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = main,
        ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = main,
        ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = fast,
        ["CLAUDE_CODE_SUBAGENT_MODEL"] = main,
        ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
        ["DISABLE_PROMPT_CACHING"] = "1",
    };

    public static string ClaudeCommand(bool skipPermissions) => skipPermissions ? "claude --dangerously-skip-permissions" : "claude";

    public static Dictionary<string, string> QwenEnv(string model) => new()
    {
        ["OPENAI_BASE_URL"] = OllamaClient.BaseUrl + "/v1",
        ["OPENAI_API_KEY"] = "ollama",
        ["OPENAI_MODEL"] = model,
    };

    public static string QwenCommand(string model, bool yolo) =>
        $"qwen --auth-type openai --openai-base-url {Q(OllamaClient.BaseUrl + "/v1")} --openai-api-key ollama -m {Q(model)}" + (yolo ? " --yolo" : "");

    public static string ChatCommand(string model) => $"& {Q(SystemProbe.OllamaExe)} run {Q(model)}";

    /// <summary>PowerShell single-quoted literal.</summary>
    public static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>The copy-pasteable commands panel.</summary>
    public static string CommandsText(string baseMain, string main, string fast, int ctx, bool skip)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Claude Code -> local Qwen via Ollama (only affects this PowerShell window)");
        foreach (var (k, v) in ClaudeEnv(main, fast)) sb.AppendLine($"$env:{k} = {Q(v)}");
        sb.AppendLine("Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue");
        sb.AppendLine(ClaudeCommand(skip));
        sb.AppendLine();
        sb.AppendLine("# Qwen Code -> Ollama (OpenAI-compatible endpoint)");
        sb.AppendLine(QwenCommand(main, skip));
        sb.AppendLine();
        sb.AppendLine("# Plain chat");
        sb.AppendLine($"ollama run {main}");
        if (ctx > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# One-time: create the larger-context variant used above (settings only, no download)");
            sb.AppendLine($"@{{model={Q(main)}; from={Q(baseMain)}; parameters=@{{num_ctx={ctx}}}; stream=$false}} | ConvertTo-Json | Invoke-RestMethod {OllamaClient.BaseUrl}/api/create -Method Post -ContentType application/json");
        }
        return sb.ToString().TrimEnd();
    }

    public const string TargetApp = "This app", TargetTerminal = "Windows Terminal", TargetPowerShell = "PowerShell window";
    public static readonly string[] Targets = [TargetApp, TargetTerminal, TargetPowerShell];

    /// <summary>A launch request: what to run, where, with which per-session environment.</summary>
    public sealed record Session(string Title, string WorkDir, IDictionary<string, string>? Env, IEnumerable<string>? Preamble, string Command)
    {
        /// <summary>powershell.exe arguments that set everything up and run the command (keeps the shell open afterwards).</summary>
        public string[] PowerShellArgs => ["-NoExit", "-NoLogo", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Encoded(Script(this))];

        /// <summary>Full command line for ConPTY.</summary>
        public string CommandLine => "powershell.exe " + string.Join(' ', PowerShellArgs);
    }

    private static string Encoded(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>Opens a new external terminal window (Windows Terminal or classic PowerShell).</summary>
    public static void OpenExternal(Session session, bool useWindowsTerminal)
    {
        if (!Directory.Exists(session.WorkDir)) throw new DirectoryNotFoundException($"Working folder not found: {session.WorkDir}");
        ProcessStartInfo psi;
        if (useWindowsTerminal && SystemProbe.HasWindowsTerminal)
        {
            psi = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
            foreach (var a in new[] { "-w", "new", "powershell.exe" }.Concat(session.PowerShellArgs)) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = session.WorkDir };
            foreach (var a in session.PowerShellArgs) psi.ArgumentList.Add(a);
        }
        Process.Start(psi);
    }

    private static string Script(Session x)
    {
        var (title, workDir, env, preamble, command) = (x.Title, x.WorkDir, x.Env, x.Preamble, x.Command);
        var s = new StringBuilder();
        s.AppendLine($"$host.UI.RawUI.WindowTitle = {Q(title)}");
        // Pick up tools installed since this app started (Setup page): the launcher's own PATH is stale.
        s.AppendLine("$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')");
        if (env is not null) foreach (var (k, v) in env) s.AppendLine($"$env:{k} = {Q(v)}");
        if (preamble is not null) foreach (var line in preamble) s.AppendLine(line);
        s.AppendLine($"Set-Location -LiteralPath {Q(workDir)}");
        s.AppendLine($"Write-Host {Q("== " + title + " ==")} -ForegroundColor Cyan");
        s.AppendLine(command);
        // Passed as -EncodedCommand: avoids every quoting problem (wt treats ';' as a separator, paths with spaces, etc.)
        return s.ToString();
    }
}
