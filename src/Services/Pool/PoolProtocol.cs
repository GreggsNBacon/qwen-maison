namespace Maison.Services.Pool;

/// <summary>
/// What AI PCs and the PCs using them say to each other. Ports: the pool service (HTTPS) on 11435, discovery (UDP)
/// on 11436, and on the PC using the pool a local relay (HTTP, this PC only) on 11437.
/// </summary>
public static class PoolPorts
{
    public static int Service => int.TryParse(Environment.GetEnvironmentVariable("MAISON_POOL_PORT"), out var p) ? p : 11435;
    public const int Discovery = 11436;
    public static int Relay => int.TryParse(Environment.GetEnvironmentVariable("MAISON_RELAY_PORT"), out var p) ? p : 11437;
    public const string DiscoverMessage = "MAISON-DISCOVER 1";
}

/// <summary>Answer to discovery and GET /maison/hello (no key needed).</summary>
public sealed record PoolHello(string Id, string Name, string Version, int Port);

public sealed record PairRequest(string Code, string Client, string ClientId);
public sealed record PairReply(string Token, string Id, string Name);

public sealed record PoolGpu(string Name, double TotalGb, double UsedGb, int LoadPercent);
public sealed record PoolModel(string Name, double SizeGb);
public sealed record PoolLoaded(string Name, double SizeGb, int Context);

/// <summary>An agent using this AI PC: from a paired PC through the pool, or a session in maison on the AI PC itself.</summary>
public sealed record PoolAgent(string Client, string Kind, string Model, DateTime LastSeen, bool Working);

/// <summary>A job the AI PC is doing for the pool (installing Ollama, downloading a model).</summary>
public sealed record PoolTask(string Title, string Status, double Percent, bool Running, string? Error);

/// <summary>GET /maison/info: everything the Pool page shows about an AI PC.</summary>
public sealed record PoolInfo(
    string Id, string Name, string Version,
    PoolGpu? Gpu, double RamGb,
    bool OllamaInstalled, bool OllamaRunning, string? OllamaVersion,
    List<PoolModel> Models, List<PoolLoaded> Loaded,
    int Active, bool Busy,
    List<PoolAgent> Agents,
    PoolTask? Task,
    string RecommendedModel, double RecommendedSizeGb);

public sealed record ModelRequest(string Model);
