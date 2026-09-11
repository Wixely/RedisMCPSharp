using DnaX.MCPFab;

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
    /// <remarks>Canonical MCPFab name; supersedes <c>AllowDangerous</c>.</remarks>
    public bool AllowDestructive { get; set; }

    /// <summary>Per-command timeout (ms). Forwarded to StackExchange.Redis as SyncTimeout/AsyncTimeout.</summary>
    public int CommandTimeoutMs { get; set; } = 5000;

    /// <summary>Cap on the number of keys / fields / list-entries returned in one tool call.</summary>
    public int MaxItems { get; set; } = 1000;

    /// <summary>
    /// Cap on a single string-value's chars before tools truncate and flag. Keeps a 10 MB BLOB
    /// from blowing the agent's context.
    /// </summary>
    /// <remarks>Canonical MCPFab name; supersedes <c>MaxValueChars</c>.</remarks>
    public int MaxChars { get; set; } = 8000;

    /// <summary>Default SCAN count hint. Larger = fewer round-trips but bigger per-batch payload.</summary>
    public int DefaultScanPageSize { get; set; } = 100;

    /// <summary>Configured Redis endpoints.</summary>
    public List<RedisServerEntry> Servers { get; set; } = new();

    /// <summary>
    /// Alias picked when a tool omits <c>alias</c>. Falls back to the first entry in
    /// <see cref="Servers"/> when blank.
    /// </summary>
    public string? DefaultAlias { get; set; }

    // --- Superseded keys -----------------------------------------------------------------
    // Still bound, still shipped in RedisMCPSharp.json, and still honoured. They cannot simply
    // be deleted: MCPHub's config merge takes object keys from the NEW shipped default, so a key
    // absent from it is dropped from the user's file on update. Removing these before a release
    // that ships both would silently discard an operator's explicit safety setting.

    /// <summary>Superseded by <see cref="AllowDestructive"/>. Honoured when set to true.</summary>
    public bool? AllowDangerous { get; set; }

    /// <summary>Superseded by <see cref="MaxChars"/>. Honoured when set.</summary>
    public int? MaxValueChars { get; set; }

    /// <summary>
    /// Folds superseded keys into their canonical properties, returning a message for each so the
    /// operator is told at startup rather than discovering it when a gate behaves unexpectedly.
    /// </summary>
    public IReadOnlyList<string> ApplySupersededKeys()
    {
        List<string> notices = [];

        // Stricter wins: a legacy true must not be silently downgraded by a canonical default.
        if (AllowDangerous is true && !AllowDestructive)
        {
            AllowDestructive = true;
            notices.Add("Redis:AllowDangerous is superseded by Redis:AllowDestructive. The old key was honoured; move your setting across.");
        }

        if (MaxValueChars is { } legacyMaxChars && legacyMaxChars > 0)
        {
            MaxChars = legacyMaxChars;
            notices.Add("Redis:MaxValueChars is superseded by Redis:MaxChars. The old key was honoured; move your setting across.");
        }

        return notices;
    }
}

/// <summary>One configured Redis endpoint.</summary>
/// <remarks>
/// <c>Alias</c>, <c>Description</c> and <c>Enabled</c> come from <see cref="McpFabTargetEntry"/>,
/// which is the registry shape five servers had each written separately.
/// </remarks>
public sealed class RedisServerEntry : McpFabTargetEntry
{
    /// <summary>
    /// Connection string in StackExchange.Redis format. Examples:
    /// <list type="bullet">
    ///   <item><c>localhost:6379</c></item>
    ///   <item><c>node1:7000,node2:7000,node3:7000</c> (cluster)</item>
    ///   <item><c>redis-prod:6379,password=secret,ssl=true,abortConnect=false</c></item>
    /// </list>
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Default DB index for non-cluster deployments (0-15). Ignored on cluster.</summary>
    public int Database { get; set; }

    /// <summary>
    /// Optional Redis version pin. When set (e.g. "7.2", "7.4"), tools use the syntax
    /// appropriate for that version even if INFO reports something different. Useful for
    /// proxies (Envoy, Twemproxy) that mask the upstream version. Leave empty for auto-detect.
    /// </summary>
    public string VersionOverride { get; set; } = "";
}
