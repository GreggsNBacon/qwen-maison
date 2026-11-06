using System.IO;

namespace Maison.Services;

/// <summary>
/// Moves Ollama's model store. Ollama keeps every model in one folder (OLLAMA_MODELS), so the location is per-PC,
/// not per-model; this moves the whole store and points Ollama at it.
/// </summary>
public static class ModelStorage
{
    public static string Current => OllamaClient.ModelsDir;

    public static async Task MoveAsync(string target, bool moveExisting, Action<string> log)
    {
        target = Path.GetFullPath(target);
        var source = Path.GetFullPath(Current);
        if (string.Equals(source.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That's already the model folder.");
        if (target.StartsWith(source.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Pick a folder outside the current model folder.");
        Directory.CreateDirectory(target);

        log("Stopping Ollama…");
        await SystemProbe.StopOllamaAsync();

        if (moveExisting && Directory.Exists(source))
        {
            log($"Moving models from {source} to {target} (this can take a while for big models)…");
            // robocopy: resumable, keeps going on large files; exit codes below 8 mean success.
            int code = await Installer.RunAsync("robocopy.exe", [source, target, "/E", "/MOVE", "/R:2", "/W:2", "/NP", "/NFL", "/NDL"], log, CancellationToken.None);
            if (code >= 8) throw new InvalidOperationException($"Moving the models failed (robocopy exit {code}). Nothing was changed in Ollama's settings.");
        }

        Environment.SetEnvironmentVariable("OLLAMA_MODELS", target, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("OLLAMA_MODELS", target, EnvironmentVariableTarget.Process);   // Ollama started from maison inherits it
        log($"OLLAMA_MODELS = {target}");

        log("Starting Ollama…");
        if (!await SystemProbe.StartOllamaAsync()) log("Ollama did not come back up by itself — click Start on the Dashboard.");
        else log("Done. Ollama is using the new folder.");
    }
}
