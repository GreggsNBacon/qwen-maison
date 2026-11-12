using System.Diagnostics;
using System.IO;
using System.Text;
using Maison.Models;

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

    /// <summary>Everything one launch needs: models, the real context window, permissions and the Start-command options.</summary>
    public sealed record Plan(string Main, string Fast, int Ctx, string Perm, LaunchOptions O, IReadOnlyList<string> Models)
    {
        /// <summary>Max tokens per reply: the user's number, or a quarter of the window (4K–32K) so prompt + reply fit.</summary>
        public int OutputTokens => O.MaxOutputTokens > 0 ? O.MaxOutputTokens : Math.Clamp(Ctx / 4, 4096, 32768);
    }

    public static Dictionary<string, string> ClaudeEnv(Plan p)
    {
        var o = p.O;
        var env = new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = OllamaClient.BaseUrl,
            ["ANTHROPIC_AUTH_TOKEN"] = "ollama",
            ["ANTHROPIC_MODEL"] = p.Main,
            ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = p.Main,
            ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = p.Main,
            ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = p.Fast,
            ["CLAUDE_CODE_SUBAGENT_MODEL"] = p.Main,
            // Claude Code assumes a 200K window for models it doesn't know, so without this it never compacts
            // before Ollama's (much smaller) window is full and the session fails.
            ["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = p.OutputTokens.ToString(),
        };
        if (o.TellContextWindow && p.Ctx > 0) env["CLAUDE_CODE_MAX_CONTEXT_TOKENS"] = p.Ctx.ToString();
        if (o.ClaudeCompactPercent is > 0 and <= 100) env["CLAUDE_AUTOCOMPACT_PCT_OVERRIDE"] = o.ClaudeCompactPercent.ToString();
        if (o.ClaudeDisableThinking) env["CLAUDE_CODE_DISABLE_THINKING"] = "1";
        if (o.ClaudeNoNonessentialTraffic) env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        if (o.ClaudeNoPromptCaching) env["DISABLE_PROMPT_CACHING"] = "1";
        foreach (var (k, v) in ParseEnv(o.ClaudeExtraEnv)) env[k] = v;
        return env;
    }

    /// <summary>maison permission choices → (label, Claude Code --permission-mode, Qwen Code --approval-mode, tooltip).</summary>
    public static readonly (string Key, string Label, string Claude, string Qwen, string Tip)[] PermissionModes =
    [
        ("ask", "Ask each time", "manual", "default", "The agent asks before editing files or running commands."),
        ("edits", "Accept edits", "acceptEdits", "auto-edit", "File edits are applied without asking; commands still ask."),
        ("plan", "Plan only", "plan", "plan", "Read and plan only: no edits, no commands."),
        ("skip", "Skip all prompts", "bypassPermissions", "yolo", "Everything runs without asking. Only use in a folder you don't mind changing."),
    ];

    public static (string Key, string Label, string Claude, string Qwen, string Tip) Mode(string key) =>
        PermissionModes.FirstOrDefault(m => m.Key == key) is { Key: not null } m ? m : PermissionModes[0];

    /// <summary>
    /// Always passes an explicit mode: Claude Code's "auto" mode relies on a safety classifier on Anthropic's servers,
    /// which can't be reached when requests go to Ollama ("Auto mode can't check actions in this session").
    /// </summary>
    public static string ClaudeCommand(Plan p) =>
        Join("claude", $"--permission-mode {Mode(p.Perm).Claude}", p.O.ClaudeContinue ? "--continue" : "", p.O.ClaudeExtraArgs);

    public static Dictionary<string, string> QwenEnv(Plan p, string? settingsPath)
    {
        var env = new Dictionary<string, string>
        {
            ["OPENAI_BASE_URL"] = OllamaClient.BaseUrl + "/v1",
            ["OPENAI_API_KEY"] = "ollama",
            ["OPENAI_MODEL"] = p.Main,
            // Unset, Qwen Code asks for the model's full output limit (64K+), which alone overflows a 32K/64K window.
            ["QWEN_CODE_MAX_OUTPUT_TOKENS"] = p.OutputTokens.ToString(),
            ["QWEN_CODE_API_TIMEOUT_MS"] = (Math.Max(30, p.O.QwenTimeoutSeconds) * 1000).ToString(),
        };
        if (settingsPath is not null) env["QWEN_CODE_SYSTEM_SETTINGS_PATH"] = settingsPath;
        foreach (var (k, v) in ParseEnv(p.O.QwenExtraEnv)) env[k] = v;
        return env;
    }

    /// <summary>
    /// Qwen Code only learns the window size from settings, not flags. maison writes its own settings file and points
    /// QWEN_CODE_SYSTEM_SETTINGS_PATH at it for the session, so ~/.qwen/settings.json is never touched. Every installed
    /// model is listed (a provider list replaces the user's for the session, and /model can still switch).
    /// </summary>
    public static string? WriteQwenSettings(Plan p)
    {
        if (!p.O.TellContextWindow || p.Ctx <= 0) return null;
        var timeoutMs = Math.Max(30, p.O.QwenTimeoutSeconds) * 1000;
        object Gen() => new Dictionary<string, object>
        {
            ["contextWindowSize"] = p.Ctx,
            ["timeout"] = timeoutMs,
            ["streamIdleTimeoutMs"] = Math.Max(timeoutMs, 240000),
            ["samplingParams"] = new Dictionary<string, object> { ["max_tokens"] = p.OutputTokens },
        };
        var ids = new List<string> { p.Main };
        ids.AddRange(p.Models.Where(m => m != p.Main));
        var settings = new Dictionary<string, object>
        {
            ["$version"] = 4,
            ["model"] = new Dictionary<string, object> { ["generationConfig"] = Gen() },
            ["context"] = new Dictionary<string, object> { ["autoCompactThreshold"] = Math.Clamp(p.O.QwenCompactThreshold, 0.3, 1.0) },
            ["modelProviders"] = new Dictionary<string, object>
            {
                ["openai"] = ids.Select(id => new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = $"{id} (Ollama via maison)",
                    ["baseUrl"] = OllamaClient.BaseUrl + "/v1",
                    ["envKey"] = "OPENAI_API_KEY",
                    ["generationConfig"] = Gen(),
                }).ToList(),
            },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var dir = Path.Combine(AppConfig.Folder, "qwen");
        Directory.CreateDirectory(dir);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..12];
        var file = Path.Combine(dir, $"settings-{hash}.json");
        if (!File.Exists(file)) File.WriteAllText(file, json);
        return file;
    }

    public static string QwenCommand(Plan p) =>
        Join($"qwen --auth-type openai --openai-base-url {Q(OllamaClient.BaseUrl + "/v1")} --openai-api-key ollama -m {Q(p.Main)}",
             $"--approval-mode {Mode(p.Perm).Qwen}", p.O.QwenContinue ? "--continue" : "", p.O.QwenExtraArgs);

    public static string ChatCommand(Plan p) =>
        Join($"& {Q(SystemProbe.OllamaExe)} run {Q(p.Main)}", p.O.ChatVerbose ? "--verbose" : "",
             p.O.ChatThink switch { "on" => "--think=true", "off" => "--think=false", _ => "" }, p.O.ChatExtraArgs);

    private static string Join(params string[] parts) => string.Join(' ', parts.Select(x => x.Trim()).Where(x => x.Length > 0));

    /// <summary>KEY=VALUE lines (blank lines and # comments ignored; invalid names skipped).</summary>
    public static IEnumerable<(string Key, string Value)> ParseEnv(string text)
    {
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$")) continue;
            yield return (key, line[(eq + 1)..].Trim());
        }
    }

    /// <summary>PowerShell single-quoted literal.</summary>
    public static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>The copy-pasteable commands panel (what maison runs, with the current options).</summary>
    public static string CommandsText(Plan p, string baseMain)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Context window: {p.Ctx:N0} tokens · replies up to {p.OutputTokens:N0} tokens");
        sb.AppendLine();
        sb.AppendLine("# Claude Code -> local model via Ollama (only affects this PowerShell window)");
        foreach (var (k, v) in ClaudeEnv(p)) sb.AppendLine($"$env:{k} = {Q(v)}");
        sb.AppendLine("Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue");
        sb.AppendLine(ClaudeCommand(p));
        sb.AppendLine();
        sb.AppendLine("# Qwen Code -> Ollama (OpenAI-compatible endpoint)");
        foreach (var (k, v) in QwenEnv(p, p.O.TellContextWindow ? "<maison writes this settings file at launch>" : null)) sb.AppendLine($"$env:{k} = {Q(v)}");
        sb.AppendLine(QwenCommand(p));
        sb.AppendLine();
        sb.AppendLine("# Plain chat");
        sb.AppendLine(ChatCommand(p));
        if (p.Main != baseMain)
        {
            sb.AppendLine();
            sb.AppendLine("# One-time: create the context variant used above (settings only, no download)");
            sb.AppendLine($"@{{model={Q(p.Main)}; from={Q(baseMain)}; parameters=@{{num_ctx={p.Ctx}}}; stream=$false}} | ConvertTo-Json | Invoke-RestMethod {OllamaClient.BaseUrl}/api/create -Method Post -ContentType application/json");
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
