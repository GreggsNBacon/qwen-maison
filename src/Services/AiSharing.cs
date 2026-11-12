using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.IO;
using System.Net.NetworkInformation;
using Maison.Models;

namespace Maison.Services;

/// <summary>
/// "Share this PC's AI": lets maison (or Claude Code / Qwen Code) on other PCs at home use this PC's Ollama, the way
/// Claude Code uses a model in the cloud. The agent and its files stay on the other PC; only the model runs here.
///
/// Ollama has no password, so the port is opened in Windows Firewall only for the home network (the local subnet)
/// and Tailscale's range, on Private/Domain networks. Never the internet.
/// </summary>
public static class AiSharing
{
    public const string RuleName = "maison: share this PC's AI (Ollama)";
    private const string ShareHost = "0.0.0.0:11434";

    /// <summary>What's actually in place right now (not just what the setting says).</summary>
    public sealed record State(bool HostSet, bool FirewallOpen, bool Listening)
    {
        public bool Working => HostSet && FirewallOpen && Listening;
    }

    public static State Check() => new(
        HostSet: (Environment.GetEnvironmentVariable("OLLAMA_HOST", EnvironmentVariableTarget.User) ?? "").StartsWith("0.0.0.0"),
        FirewallOpen: RuleExists(),
        Listening: IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Any(e => e.Port == OllamaClient.DefaultPort && (e.Address.Equals(IPAddress.Any) || e.Address.Equals(IPAddress.IPv6Any))));

    /// <summary>Addresses other PCs can type in "Use the AI from another PC".</summary>
    public static List<string> Addresses()
    {
        var list = new List<string> { Environment.MachineName.ToLowerInvariant() };
        list.AddRange(PortalNet.LanAddresses().Select(a => a.ToString()));
        return list;
    }

    /// <summary>What the firewall and network look like before sharing (read without admin).</summary>
    public sealed record Survey(IReadOnlyList<string> BroadOllamaRules, IReadOnlyList<(string Name, int Index)> PublicNetworks);

    public static async Task<Survey> SurveyAsync()
    {
        var output = await RunPowerShellAsync("""
            Get-NetFirewallApplicationFilter | Where-Object Program -match 'ollama' | Get-NetFirewallRule |
              Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' } |
              Where-Object { ($_ | Get-NetFirewallAddressFilter).RemoteAddress -contains 'Any' } |
              ForEach-Object { 'RULE ' + $_.Name }
            Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public' |
              ForEach-Object { 'NET ' + $_.InterfaceIndex + ' ' + $_.Name }
            """);
        var rules = new List<string>();
        var nets = new List<(string, int)>();
        foreach (var line in output.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("RULE ")) rules.Add(line[5..]);
            else if (line.StartsWith("NET ") && line[4..].Split(' ', 2) is [var idx, var name] && int.TryParse(idx, out var i)) nets.Add((name, i));
        }
        return new Survey(rules, nets);
    }

    /// <summary>
    /// Turns sharing on. One admin prompt: adds maison's firewall rule (port 11434, home subnet + Tailscale only),
    /// switches off Windows' broad "allow ollama.exe from anywhere" rules (made when someone clicked Allow on its
    /// prompt; they'd make the restriction pointless), and optionally marks Public networks as Private.
    /// </summary>
    public static async Task<(bool Ok, string Message)> EnableAsync(Survey survey, bool makePrivate, Action<string> log)
    {
        // Firewall first: if Ollama starts listening before a rule allows it, Windows shows its own
        // "allow on all networks?" prompt, which is broader than we want.
        var script = new System.Text.StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"Get-NetFirewallRule -DisplayName '{RuleName}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
        script.AppendLine($"New-NetFirewallRule -DisplayName '{RuleName}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {OllamaClient.DefaultPort} " +
                          "-RemoteAddress LocalSubnet,100.64.0.0/10 -Profile Private,Domain " +
                          "-Description 'Lets PCs on your home network use this PC''s Ollama. Added by maison; turn off in maison Settings.' | Out-Null");
        foreach (var r in survey.BroadOllamaRules)
            script.AppendLine($"Disable-NetFirewallRule -Name '{r.Replace("'", "''")}'");
        if (makePrivate)
            foreach (var (_, index) in survey.PublicNetworks)
                script.AppendLine($"Set-NetConnectionProfile -InterfaceIndex {index} -NetworkCategory Private");

        log("Setting up Windows Firewall for your home network only (Windows will ask for permission)…");
        if (await RunElevatedPowerShellAsync(script.ToString()) is { } err) return (false, err);
        if (!RuleExists()) return (false, "The firewall rule wasn't created, so other PCs would be blocked. Nothing else was changed.");
        if (survey.BroadOllamaRules.Count > 0) log("Switched off Windows' \"allow Ollama from anywhere\" rules.");

        log("Telling Ollama to listen on the network (OLLAMA_HOST=0.0.0.0:11434)…");
        Environment.SetEnvironmentVariable("OLLAMA_HOST", ShareHost, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("OLLAMA_HOST", ShareHost, EnvironmentVariableTarget.Process);   // Ollama started by maison inherits it
        await RestartOllamaAsync(log);

        AppConfig.Current.ShareAi = true;
        AppConfig.Current.Save();
        var stillPublic = !makePrivate && survey.PublicNetworks.Count > 0;
        return Check().Listening
            ? (true, stillPublic
                ? $"Shared, but your network ({survey.PublicNetworks[0].Name}) is set to Public in Windows, so other PCs are still blocked. Set it to Private in Windows Settings → Network."
                : "This PC's AI is shared with your home network.")
            : (true, "Sharing is set up, but Ollama isn't listening on the network yet. Restart Ollama (or this PC) and check again.");
    }

    /// <summary>Turns sharing off: Ollama back to this PC only, firewall rule removed.</summary>
    public static async Task<(bool Ok, string Message)> DisableAsync(Action<string> log)
    {
        log("Ollama back to this PC only…");
        Environment.SetEnvironmentVariable("OLLAMA_HOST", null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("OLLAMA_HOST", null, EnvironmentVariableTarget.Process);
        await RestartOllamaAsync(log);
        AppConfig.Current.ShareAi = false;
        AppConfig.Current.Save();

        string? fwError = null;
        if (RuleExists())
        {
            log("Closing the firewall port (Windows will ask for permission)…");
            fwError = await RunElevatedPowerShellAsync($"Get-NetFirewallRule -DisplayName '{RuleName}' | Remove-NetFirewallRule");
        }
        var listening = Check().Listening;
        return (true, (listening
                ? "maison stopped sharing, but Ollama still listens on the network by its own setting (\"Expose Ollama to the network\" in Ollama's settings). Turn that off too to keep it private."
                : "This PC's AI is private again.")
            + (fwError is null ? "" : " The firewall rule is still there: " + fwError));
    }
    private static async Task RestartOllamaAsync(Action<string> log)
    {
        if (!SystemProbe.OllamaInstalled) return;
        log("Restarting Ollama…");
        await SystemProbe.StopOllamaAsync();
        log(await SystemProbe.StartOllamaAsync() ? "Ollama is running." : "Ollama didn't come back by itself: click Start on the Dashboard.");
    }

    private static bool RuleExists()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<string> RunPowerShellAsync(string script)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output;
        }
        catch { return ""; }
    }

    /// <summary>Runs a PowerShell script as admin (one UAC prompt). Returns null on success, else why it failed.</summary>
    private static async Task<string?> RunElevatedPowerShellAsync(string script)
    {
        var errFile = Path.Combine(Path.GetTempPath(), $"maison-share-{Guid.NewGuid():N}.txt");
        var wrapped = $"try {{\n{script}\n}} catch {{ $_.Exception.Message | Out-File -Encoding utf8 '{errFile}'; exit 1 }}";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(wrapped)))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            await p.WaitForExitAsync();
            if (p.ExitCode == 0) return null;
            var why = File.Exists(errFile) ? File.ReadAllText(errFile).Trim() : $"exit code {p.ExitCode}";
            return $"the firewall change failed ({why}).";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Windows didn't get permission (the prompt was declined), so nothing was changed.";
        }
        catch (Exception ex) { return ex.Message; }
        finally { try { File.Delete(errFile); } catch { } }
    }
}