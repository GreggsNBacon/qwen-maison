namespace Maison.Services;

/// <summary>Turns raw stats into advice the user can act on.</summary>
public static class HealthAdvice
{
    /// <summary>
    /// Detects other apps holding GPU memory while a model is loaded. When VRAM is full, Windows quietly moves part of
    /// the model into system RAM and generation drops from ~35 tok/s to single digits (or near zero).
    /// </summary>
    public static string? VramWarning(StatsSnapshot s) =>
        VramHog(s) is { } h
            ? $"GPU memory is full and {h.Name} is using {h.Gb:0.0} GB — the model may spill into system RAM and run very slowly. Close it or pick a smaller context."
            : null;

    /// <summary>The app crowding a loaded model out of GPU memory, if any.</summary>
    public static (string Name, double Gb)? VramHog(StatsSnapshot s)
    {
        if (s.Gpu is not { TotalGb: > 0 } g) return null;
        if (s.OllamaVramGb < 1) return null;                          // no model loaded
        var others = s.VramUsers.Where(p => !StatsService.IsOllama(p.Name) && p.VramGb >= 0.75).ToList();
        if (g.UsedGb < g.TotalGb * 0.92 || others.Count == 0) return null;
        var top = others.OrderByDescending(p => p.VramGb).First();
        return (top.Name, top.VramGb);
    }
}
