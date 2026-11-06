namespace QwenLauncher.Services;

public sealed record CatalogModel(string Tag, double SizeGb, string Title, string Description)
{
    /// <summary>Rough VRAM needed: weights plus KV cache/runtime headroom for a usable (32–64K) context.</summary>
    public double NeedGb => SizeGb + Math.Max(2.5, SizeGb * 0.15);
}

public enum Fit { Gpu, Tight, Cpu, TooBig }

/// <summary>
/// Curated Qwen models from the Ollama library (sizes as listed on ollama.com/library, Nov 2026).
/// Anything else can still be pulled by name from the Models page.
/// </summary>
public static class ModelCatalog
{
    public static readonly CatalogModel[] All =
    [
        new("qwen3.8:27b", 18, "Qwen 3.8 · 27B", "Newest Qwen. Strong at coding and agentic work; vision, tools and thinking. Best pick for 24 GB cards."),
        new("qwen3.5:9b", 6.6, "Qwen 3.5 · 9B", "Best balance for 10–16 GB cards. Fast and capable, 256K context."),
        new("qwen3.5:4b", 3.3, "Qwen 3.5 · 4B", "Small and quick. Good for 6–8 GB cards or as a fast helper model."),
        new("qwen3.6:35b", 23, "Qwen 3.6 · 35B", "Larger previous generation. Only fits a 24 GB card with a small context."),
        new("qwen3.6:27b", 18, "Qwen 3.6 · 27B", "Previous-generation 27B; vision, tools, thinking."),
        new("qwen3.5:27b", 17, "Qwen 3.5 · 27B", "Qwen 3.5 dense 27B; 256K context, vision."),
        new("qwen3.5:2b", 2.7, "Qwen 3.5 · 2B", "Tiny; runs on almost anything, including CPU-only machines."),
        new("qwen3.5:0.8b", 1.2, "Qwen 3.5 · 0.8B", "Smallest. For testing or very low-end hardware."),
        new("qwen3:32b", 20, "Qwen 3 · 32B", "Older dense 32B (40K context)."),
        new("qwen3:30b", 19, "Qwen 3 · 30B MoE", "Older mixture-of-experts; fast for its size, 256K context."),
        new("qwen3:14b", 9.3, "Qwen 3 · 14B", "Older 14B (40K context)."),
        new("qwen3:8b", 5.2, "Qwen 3 · 8B", "Older 8B (40K context)."),
    ];

    public static Fit FitFor(CatalogModel m, double vramGb, double ramGb) => FitFor(m.SizeGb, m.NeedGb, vramGb, ramGb);

    public static Fit FitFor(double sizeGb, double needGb, double vramGb, double ramGb)
    {
        if (vramGb <= 0) return sizeGb <= ramGb * 0.6 ? Fit.Cpu : Fit.TooBig;
        if (needGb <= vramGb) return Fit.Gpu;
        if (sizeGb + 1 <= vramGb) return Fit.Tight;
        if (sizeGb <= vramGb + ramGb * 0.6) return Fit.Cpu;
        return Fit.TooBig;
    }

    public static string FitText(Fit f) => f switch
    {
        Fit.Gpu => "Fits on your GPU",
        Fit.Tight => "Tight: small context only",
        Fit.Cpu => "Partly on CPU: slow",
        _ => "Too large for this PC",
    };

    /// <summary>The model the Setup page installs by default for this machine.</summary>
    public static CatalogModel Recommend(double vramGb, double ramGb)
    {
        string[] order = ["qwen3.8:27b", "qwen3.5:9b", "qwen3.5:4b", "qwen3.5:2b", "qwen3.5:0.8b"];
        foreach (var tag in order)
        {
            var m = All.First(x => x.Tag == tag);
            if (vramGb > 0 && FitFor(m, vramGb, ramGb) == Fit.Gpu) return m;
        }
        // No usable GPU: pick something that runs acceptably on CPU.
        return All.First(x => x.Tag == (ramGb >= 16 ? "qwen3.5:4b" : "qwen3.5:2b"));
    }
}
