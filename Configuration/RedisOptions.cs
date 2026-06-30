namespace RedisMCPSharp.Configuration;

/// <summary>
/// Top-level Redis MCP config. Multiple endpoints can be configured with aliases; the agent
/// passes <c>alias=</c> to pick which one each tool acts against. Single connections,
/// Sentinel deployments, and clusters (just list every node in the connection string) are all
/// supported by StackExchange.Redis transparently.
/// </summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>
    /// Master safety switch. When true, every mutating tool (<c>set</c>, <c>del</c>, <c>expire</c>,
    /// <c>hset</c>, <c>execute</c> with a write command, etc.) is refused with a clear error.
    /// Read / inspect / scan / diagnostic tools stay available. Default true.
    /// </summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>
    /// Even with ReadOnly=false, destructive ops (<c>FLUSHDB</c>, <c>FLUSHALL</c>, raw
    /// <c>execute</c> against unknown commands, key-pattern delete) are doubly gated.
    /// Default false.
    /// </summary>
    public bool AllowDangerous { get; set; } = false;

    /// <summary>Per-command timeout (ms). Forwarded to StackExchange.Redis as SyncTimeout/AsyncTimeout.</summary>
    public int CommandTimeoutMs { get; set; } = 5000;

    /// <summary>Cap on the number of keys / fields / list-entries returned in one tool call.</summary>
    public int MaxItems { get; set; } = 1000;

    /// <summary>Cap on a single string-value's chars before tools truncate and flag. Keeps a 10 MB BLOB from blowing the agent's context.</summary>
    public int MaxValueChars { get; set; } = 8000;

    /// <summary>Default SCAN count hint. Larger = fewer round-trips but bigger per-batch payload.</summary>
    public int DefaultScanPageSize { get; set; } = 100;

    /// <summary>Configured Redis endpoints.</summary>
    public List<RedisServerEntry> Servers { get; set; } = new();

    /// <summary>
    /// Alias picked when a tool omits <c>alias</c>. Falls back to the first entry in
    /// <see cref="Servers"/> when blank.
    /// </summary>
    public string? DefaultAlias { get; set; }
}

public sealed class RedisServerEntry
{
    /// <summary>
    /// Stable handle the agent uses to pick this server. When omitted the registry generates
    /// one at load time (<c>redis-1</c>, <c>redis-2</c>, …).
    /// </summary>
    public string Alias { get; set; } = "";

    /// <summary>
    /// Connection string in StackExchange.Redis format. Examples:
    /// <list type="bullet">
    ///   <item><c>localhost:6379</c></item>
    ///   <item><c>node1:7000,node2:7000,node3:7000</c> (cluster)</item>
    ///   <item><c>redis-prod:6379,password=secret,ssl=true,abortConnect=false</c></item>
    /// </list>
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Default DB index for non-cluster deployments (0–15). Ignored on cluster.</summary>
    public int Database { get; set; } = 0;

    /// <summary>
    /// Optional Redis version pin. When set (e.g. "7.2", "7.4"), tools use the syntax
    /// appropriate for that version even if INFO reports something different. Useful for
    /// proxies (Envoy, Twemproxy) that mask the upstream version. Leave empty for auto-detect.
    /// </summary>
    public string VersionOverride { get; set; } = "";

    /// <summary>Free-text description shown by <c>list_servers</c>.</summary>
    public string Description { get; set; } = "";
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5713;
    public string Path { get; set; } = "/mcp";

    public string WindowsServiceName { get; set; } = "RedisMCPSharp";
    public string Password { get; set; } = string.Empty;
}
