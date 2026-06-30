using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RedisMCPSharp.Services;
using StackExchange.Redis;

namespace RedisMCPSharp.Tools;

/// <summary>
/// Raw-command escape hatch. For commands we haven't wrapped explicitly (FT.*, JSON.*, TS.*,
/// PFCOUNT, COPY, CLIENT NO-EVICT, etc.) the agent uses this. Doubly-gated:
///   - read mode: only commands on the safe-list run; everything else needs Redis:ReadOnly=false.
///   - dangerous mode: FLUSHDB / FLUSHALL / DEBUG / SHUTDOWN / SCRIPT FLUSH / FUNCTION FLUSH /
///     CONFIG REWRITE / CLUSTER RESET / FAILOVER need Redis:AllowDangerous=true.
/// </summary>
[McpServerToolType]
public sealed class ExecuteTool
{
    /// <summary>
    /// Commands that don't mutate state. When ReadOnly=true, anything outside this set is refused.
    /// Includes RediSearch / RedisJSON / RedisTimeSeries / RedisBloom read-side commands so an
    /// agent can poke at module data even on a locked-down server.
    /// </summary>
    private static readonly HashSet<string> SafeReadCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // Core read
        "GET", "MGET", "EXISTS", "TYPE", "TTL", "PTTL", "EXPIRETIME", "PEXPIRETIME",
        "STRLEN", "GETRANGE", "SUBSTR", "OBJECT", "DUMP", "RANDOMKEY", "KEYS",
        "SCAN", "DBSIZE", "INFO", "PING", "ECHO", "TIME", "ROLE", "LASTSAVE",
        "CLIENT", "COMMAND", "CONFIG", "DEBUG", "LATENCY", "SLOWLOG", "MEMORY",
        "WAIT", "WAITAOF", "RESET",
        // Hash read
        "HGET", "HMGET", "HGETALL", "HKEYS", "HVALS", "HLEN", "HEXISTS", "HSTRLEN", "HSCAN",
        "HRANDFIELD", "HEXPIRETIME", "HPEXPIRETIME", "HTTL", "HPTTL",
        // List read
        "LLEN", "LRANGE", "LINDEX", "LPOS",
        // Set read
        "SCARD", "SMEMBERS", "SISMEMBER", "SMISMEMBER", "SRANDMEMBER", "SSCAN", "SINTER", "SUNION", "SDIFF",
        // Sorted set read
        "ZCARD", "ZCOUNT", "ZLEXCOUNT", "ZRANGE", "ZRANGEBYSCORE", "ZRANGEBYLEX",
        "ZREVRANGE", "ZREVRANGEBYSCORE", "ZREVRANGEBYLEX", "ZRANK", "ZREVRANK",
        "ZSCORE", "ZMSCORE", "ZSCAN", "ZRANDMEMBER",
        // Stream read
        "XLEN", "XRANGE", "XREVRANGE", "XREAD", "XPENDING", "XINFO",
        // HyperLogLog read
        "PFCOUNT",
        // Bitmap read
        "BITCOUNT", "BITPOS", "GETBIT", "BITOP",
        // Cluster read
        "CLUSTER",
        // Pub/Sub introspection
        "PUBSUB",
        // RediSearch (FT.*) — read-side
        "FT.SEARCH", "FT.AGGREGATE", "FT.INFO", "FT.LIST", "FT._LIST", "FT.EXPLAIN", "FT.EXPLAINCLI",
        "FT.PROFILE", "FT.SPELLCHECK", "FT.SYNDUMP", "FT.SUGGET", "FT.SUGLEN", "FT.TAGVALS",
        "FT.CONFIG", "FT.CURSOR",
        // RedisJSON — read-side
        "JSON.GET", "JSON.MGET", "JSON.TYPE", "JSON.STRLEN", "JSON.ARRLEN", "JSON.OBJLEN",
        "JSON.OBJKEYS", "JSON.ARRINDEX", "JSON.DEBUG", "JSON.RESP",
        // RedisTimeSeries — read-side
        "TS.GET", "TS.MGET", "TS.RANGE", "TS.REVRANGE", "TS.MRANGE", "TS.MREVRANGE", "TS.INFO",
        "TS.QUERYINDEX",
        // RedisBloom — read-side
        "BF.EXISTS", "BF.MEXISTS", "BF.INFO", "BF.SCANDUMP",
        "CF.EXISTS", "CF.MEXISTS", "CF.COUNT", "CF.INFO", "CF.SCANDUMP",
        "CMS.QUERY", "CMS.INFO",
        "TOPK.QUERY", "TOPK.LIST", "TOPK.INFO", "TOPK.COUNT",
        "TDIGEST.QUANTILE", "TDIGEST.CDF", "TDIGEST.MIN", "TDIGEST.MAX", "TDIGEST.INFO",
        "TDIGEST.RANK", "TDIGEST.REVRANK", "TDIGEST.BYRANK", "TDIGEST.BYREVRANK",
        "TDIGEST.TRIMMED_MEAN",
        // Functions / scripting introspection
        "FUNCTION", "SCRIPT",
    };

    /// <summary>
    /// Commands that, even in write mode, need explicit Redis:AllowDangerous=true. These either
    /// wipe data or change global server state in ways that are hard to undo.
    /// </summary>
    private static readonly HashSet<string> DangerousCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "FLUSHDB", "FLUSHALL", "SHUTDOWN", "BGREWRITEAOF", "BGSAVE", "SAVE",
        "REPLICAOF", "SLAVEOF", "FAILOVER", "DEBUG", "RESET",
    };

    /// <summary>
    /// Sub-commands of CONFIG / FUNCTION / SCRIPT / CLUSTER / CLIENT that escalate to dangerous
    /// even though the parent command can be read-only.
    /// </summary>
    private static readonly HashSet<string> DangerousSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONFIG REWRITE", "CONFIG RESETSTAT",
        "SCRIPT FLUSH", "FUNCTION FLUSH", "FUNCTION DELETE",
        "CLUSTER RESET", "CLUSTER FAILOVER", "CLUSTER FORGET", "CLUSTER MEET",
        "CLIENT KILL", "CLIENT PAUSE", "CLIENT UNPAUSE", "CLIENT NO-EVICT",
    };

    [McpServerTool(Name = "redis_execute"),
     Description(
        "Run a raw Redis command. Pass the verb in `command` and its arguments in `args` (one element per arg — don't pre-quote). " +
        "Read-side commands are always permitted. Write commands require Redis:ReadOnly=false. " +
        "Destructive admin commands (FLUSHDB, FLUSHALL, SHUTDOWN, DEBUG, FAILOVER, CONFIG REWRITE, …) additionally require Redis:AllowDangerous=true. " +
        "Use this for RediSearch (FT.*), RedisJSON (JSON.*), RedisTimeSeries (TS.*), RedisBloom (BF.*/CF.*/CMS.*/TOPK.*/TDIGEST.*) and any other command not wrapped explicitly.")]
    public static async Task<string> Execute(
        RedisRegistry reg,
        [Description("Command verb, e.g. \"FT.SEARCH\", \"JSON.GET\", \"PFCOUNT\".")] string command,
        [Description("Command arguments in order. Pass strings; integers/floats are accepted as strings too.")] string[]? args = null,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("command is required.");

        var verb = command.Trim().ToUpperInvariant();
        var firstArg = args is { Length: > 0 } ? args[0]?.Trim().ToUpperInvariant() : null;
        var composite = firstArg is null ? verb : $"{verb} {firstArg}";

        var isDangerous = DangerousCommands.Contains(verb) || (firstArg is not null && DangerousSubcommands.Contains(composite));
        var isWrite = !SafeReadCommands.Contains(verb) || isDangerous;

        if (isWrite) reg.RequireWritable($"execute({verb})");
        if (isDangerous) reg.RequireDangerous($"execute({composite})");

        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var fullArgs = (args ?? Array.Empty<string>()).Cast<object>().ToArray();
        var raw = await inst.Db().ExecuteAsync(verb, fullArgs).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            alias,
            command = verb,
            args = args ?? Array.Empty<string>(),
            classification = new { write = isWrite, dangerous = isDangerous },
            type = raw.Resp2Type.ToString(),
            result = FlattenResult(raw),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_ft_list"),
     Description("Convenience: FT._LIST — every RediSearch index name on the server. Returns empty array if RediSearch isn't loaded.")]
    public static async Task<string> FtList(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        try
        {
            var raw = (RedisResult[]?)await inst.Db().ExecuteAsync("FT._LIST").ConfigureAwait(false) ?? Array.Empty<RedisResult>();
            return JsonSerializer.Serialize(new { alias, indexes = raw.Select(r => r.ToString()).ToArray() }, JsonOpts.Default);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(new { alias, available = false, note = "RediSearch module not loaded." }, JsonOpts.Default);
        }
    }

    [McpServerTool(Name = "redis_ft_info"),
     Description("Convenience: FT.INFO <index> — fields, docs, stats, attributes for a RediSearch index.")]
    public static async Task<string> FtInfo(
        RedisRegistry reg,
        [Description("Index name.")] string index,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (RedisResult[]?)await inst.Db().ExecuteAsync("FT.INFO", index).ConfigureAwait(false) ?? Array.Empty<RedisResult>();
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < raw.Length; i += 2) dict[raw[i].ToString()] = FlattenResult(raw[i + 1]);
        return JsonSerializer.Serialize(new { alias, index, info = dict }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_ft_search"),
     Description("Convenience: FT.SEARCH <index> <query> [LIMIT offset count] — full-text query against a RediSearch index. Returns total + matched docs.")]
    public static async Task<string> FtSearch(
        RedisRegistry reg,
        [Description("Index name.")] string index,
        [Description("Query string (RediSearch syntax — \"@field:value\", \"*\" for match-all, etc.).")] string query,
        [Description("Skip first N. Default 0.")] int offset = 0,
        [Description("Return at most N. Default 10.")] int limit = 10,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (RedisResult[]?)await inst.Db().ExecuteAsync("FT.SEARCH", index, query, "LIMIT", offset, limit).ConfigureAwait(false)
                  ?? Array.Empty<RedisResult>();
        // First element = total. Then alternating (docId, [field, value, ...]) pairs.
        var total = raw.Length > 0 ? (long?)raw[0] : null;
        var docs = new List<object>();
        for (int i = 1; i + 1 < raw.Length; i += 2)
        {
            var docId = raw[i].ToString();
            var fields = (RedisResult[]?)raw[i + 1] ?? Array.Empty<RedisResult>();
            var fdict = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int j = 0; j + 1 < fields.Length; j += 2) fdict[fields[j].ToString()] = fields[j + 1].ToString();
            docs.Add(new { id = docId, fields = fdict });
        }
        return JsonSerializer.Serialize(new { alias, index, query, total, returned = docs.Count, docs }, JsonOpts.Default);
    }

    private static object? FlattenResult(RedisResult r)
    {
        if (r.IsNull) return null;
        switch (r.Resp2Type)
        {
            case ResultType.SimpleString:
            case ResultType.BulkString:
                return r.ToString();
            case ResultType.Integer:
                return (long)r;
            case ResultType.Array:
                var arr = (RedisResult[])r!;
                return arr.Select(FlattenResult).ToArray();
            default:
                return r.ToString();
        }
    }
}
