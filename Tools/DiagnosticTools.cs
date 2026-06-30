using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RedisMCPSharp.Services;
using StackExchange.Redis;

namespace RedisMCPSharp.Tools;

/// <summary>
/// Live-ops diagnostics. Slow log, memory accounting, latency events, connected clients, config
/// inspection. None of these mutate data, so they're available regardless of ReadOnly.
/// </summary>
[McpServerToolType]
public sealed class DiagnosticTools
{
    [McpServerTool(Name = "slowlog_get"),
     Description("SLOWLOG GET — N most recent slow commands (id, timestamp, duration µs, command argv, client name/addr if available). Use to find queries eating CPU.")]
    public static async Task<string> SlowlogGet(
        RedisRegistry reg,
        [Description("How many entries to fetch. Default 10.")] int count = 10,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (RedisResult[]?)await inst.Db().ExecuteAsync("SLOWLOG", "GET", count).ConfigureAwait(false) ?? Array.Empty<RedisResult>();
        var rows = raw.Select(entry =>
        {
            var fields = (RedisResult[]?)entry ?? Array.Empty<RedisResult>();
            return new
            {
                id = fields.ElementAtOrDefault(0)?.ToString(),
                unixSeconds = AsLong(fields, 1),
                durationMicros = AsLong(fields, 2),
                command = ((RedisResult[]?)fields.ElementAtOrDefault(3) ?? Array.Empty<RedisResult>())
                    .Select(p => p.ToString()).ToArray(),
                clientAddr = fields.ElementAtOrDefault(4)?.ToString(),
                clientName = fields.ElementAtOrDefault(5)?.ToString(),
            };
        });
        return JsonSerializer.Serialize(new { alias, count = raw.Length, entries = rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "slowlog_len"),
     Description("SLOWLOG LEN — current number of entries in the slow log.")]
    public static async Task<string> SlowlogLen(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var len = (long?)await inst.Db().ExecuteAsync("SLOWLOG", "LEN").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, length = len }, JsonOpts.Default);
    }

    [McpServerTool(Name = "slowlog_reset"),
     Description("SLOWLOG RESET — wipe the slow log. Refused when Redis:ReadOnly=true (it's not destructive to data but is an admin op).")]
    public static async Task<string> SlowlogReset(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("slowlog_reset");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        await inst.Db().ExecuteAsync("SLOWLOG", "RESET").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, reset = true }, JsonOpts.Default);
    }

    [McpServerTool(Name = "memory_usage"),
     Description("MEMORY USAGE — bytes one specific key (and its overhead) is taking on the heap. Sampling depth optional.")]
    public static async Task<string> MemoryUsage(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("SAMPLES depth for nested types. 0 = all elements (most accurate, slowest). Default 0.")] int samples = 0,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var bytes = (long?)await inst.Db().ExecuteAsync("MEMORY", "USAGE", key, "SAMPLES", samples).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, bytes }, JsonOpts.Default);
    }

    [McpServerTool(Name = "memory_stats"),
     Description("MEMORY STATS — global memory accounting (peak, fragmentation, allocator chunks, dataset bytes, replication backlog, …). Wide output.")]
    public static async Task<string> MemoryStats(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (RedisResult[]?)await inst.Db().ExecuteAsync("MEMORY", "STATS").ConfigureAwait(false) ?? Array.Empty<RedisResult>();
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < raw.Length; i += 2)
        {
            var k = raw[i].ToString();
            var v = raw[i + 1];
            dict[k] = FlattenResult(v);
        }
        return JsonSerializer.Serialize(new { alias, stats = dict }, JsonOpts.Default);
    }

    [McpServerTool(Name = "memory_doctor"),
     Description("MEMORY DOCTOR — Redis' own narrative diagnosis of memory hotspots. Plain-English heuristic output.")]
    public static async Task<string> MemoryDoctor(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (string?)await inst.Db().ExecuteAsync("MEMORY", "DOCTOR").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, report = raw }, JsonOpts.Default);
    }

    [McpServerTool(Name = "client_list"),
     Description("CLIENT LIST — every connected client (id, addr, fd, name, age, idle, last command, db). Heavy on big servers — filter with `type` when possible.")]
    public static async Task<string> ClientList(
        RedisRegistry reg,
        [Description("Optional TYPE filter: normal | master | replica | pubsub. Empty = all.")] string? type = null,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = string.IsNullOrWhiteSpace(type)
            ? (string?)await inst.Db().ExecuteAsync("CLIENT", "LIST").ConfigureAwait(false)
            : (string?)await inst.Db().ExecuteAsync("CLIENT", "LIST", "TYPE", type).ConfigureAwait(false);
        raw ??= "";
        var rows = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = token.IndexOf('=');
                if (idx > 0) dict[token[..idx]] = token[(idx + 1)..];
            }
            return dict;
        });
        return JsonSerializer.Serialize(new { alias, type, clients = rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "client_kill"),
     Description("CLIENT KILL — close a specific client by id or addr. Refused when Redis:ReadOnly=true. Use to evict misbehaving consumers.")]
    public static async Task<string> ClientKill(
        RedisRegistry reg,
        [Description("Client id (preferred) — from client_list. Empty = use `addr`.")] string? id = null,
        [Description("Client addr ip:port — from client_list. Empty = use `id`.")] string? addr = null,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("client_kill");
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(addr))
            throw new ArgumentException("Provide either `id` or `addr`.");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var args = !string.IsNullOrWhiteSpace(id)
            ? new object[] { "ID", id! }
            : new object[] { "ADDR", addr! };
        var result = await inst.Db().ExecuteAsync("CLIENT", new object[] { "KILL" }.Concat(args).ToArray()).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, id, addr, result = result.ToString() }, JsonOpts.Default);
    }

    [McpServerTool(Name = "config_get"),
     Description("CONFIG GET — fetch one or more runtime config parameters by glob. Examples: \"maxmemory\", \"max*\", \"*\".")]
    public static async Task<string> ConfigGet(
        RedisRegistry reg,
        [Description("Glob pattern. \"*\" = all (large output).")] string pattern = "*",
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (RedisResult[]?)await inst.Db().ExecuteAsync("CONFIG", "GET", pattern).ConfigureAwait(false) ?? Array.Empty<RedisResult>();
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < raw.Length; i += 2)
            dict[raw[i].ToString()] = raw[i + 1].ToString();
        return JsonSerializer.Serialize(new { alias, pattern, parameters = dict }, JsonOpts.Default);
    }

    [McpServerTool(Name = "config_set"),
     Description("CONFIG SET — change a runtime parameter (e.g. maxmemory, timeout). Refused when Redis:ReadOnly=true and double-gated by Redis:AllowDangerous (changing config is administrative).")]
    public static async Task<string> ConfigSet(
        RedisRegistry reg,
        [Description("Parameter name.")] string parameter,
        [Description("New value (as a string — Redis parses on its end).")] string value,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("config_set");
        reg.RequireDangerous("config_set");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var result = await inst.Db().ExecuteAsync("CONFIG", "SET", parameter, value).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, parameter, value, result = result.ToString() }, JsonOpts.Default);
    }

    [McpServerTool(Name = "config_resetstat"),
     Description("CONFIG RESETSTAT — reset stats reported by INFO (commandstats, keyspace hits/misses, evictions). Refused when Redis:ReadOnly=true.")]
    public static async Task<string> ConfigResetStat(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("config_resetstat");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        await inst.Db().ExecuteAsync("CONFIG", "RESETSTAT").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, reset = true }, JsonOpts.Default);
    }

    [McpServerTool(Name = "latency_history"),
     Description("LATENCY HISTORY <event> — recent latency spikes for a given event (e.g. \"fork\", \"event-loop\", \"command\"). Returns [timestamp, ms] pairs.")]
    public static async Task<string> LatencyHistory(
        RedisRegistry reg,
        [Description("Event name (see `latency_latest` for what's tracked on this server).")] string @event,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (RedisResult[]?)await inst.Db().ExecuteAsync("LATENCY", "HISTORY", @event).ConfigureAwait(false) ?? Array.Empty<RedisResult>();
        var rows = raw.Select(r =>
        {
            var pair = (RedisResult[]?)r ?? Array.Empty<RedisResult>();
            return new
            {
                unixSeconds = AsLong(pair, 0),
                latencyMs = AsLong(pair, 1),
            };
        });
        return JsonSerializer.Serialize(new { alias, @event, samples = rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "latency_latest"),
     Description("LATENCY LATEST — most recent spike for every tracked event. Quickest \"is anything bad happening right now?\" check.")]
    public static async Task<string> LatencyLatest(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (RedisResult[]?)await inst.Db().ExecuteAsync("LATENCY", "LATEST").ConfigureAwait(false) ?? Array.Empty<RedisResult>();
        var rows = raw.Select(entry =>
        {
            var f = (RedisResult[]?)entry ?? Array.Empty<RedisResult>();
            return new
            {
                @event = f.ElementAtOrDefault(0)?.ToString(),
                unixSeconds = AsLong(f, 1),
                latestMs = AsLong(f, 2),
                maxMs = AsLong(f, 3),
            };
        });
        return JsonSerializer.Serialize(new { alias, events = rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "latency_reset"),
     Description("LATENCY RESET — clear the latency history. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> LatencyReset(
        RedisRegistry reg,
        [Description("Specific event(s) to clear. Empty = all.")] string[]? events = null,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("latency_reset");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        object[] args = events is { Length: > 0 }
            ? new object[] { "RESET" }.Concat(events.Cast<object>()).ToArray()
            : new object[] { "RESET" };
        var cleared = (long?)await inst.Db().ExecuteAsync("LATENCY", args).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, cleared }, JsonOpts.Default);
    }

    [McpServerTool(Name = "lastsave"),
     Description("LASTSAVE — UNIX timestamp of the last successful RDB save. Compare against now() to see how stale a snapshot is.")]
    public static async Task<string> LastSave(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var ts = await inst.FirstServer().LastSaveAsync().ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, lastSaveUtc = ts, lastSaveUnix = new DateTimeOffset(ts, TimeSpan.Zero).ToUnixTimeSeconds() }, JsonOpts.Default);
    }

    [McpServerTool(Name = "command_count"),
     Description("COMMAND COUNT — number of commands the server knows about (built-in + modules + functions).")]
    public static async Task<string> CommandCount(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var n = (long?)await inst.Db().ExecuteAsync("COMMAND", "COUNT").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, commands = n }, JsonOpts.Default);
    }

    [McpServerTool(Name = "module_list"),
     Description("MODULE LIST — loaded Redis modules (RediSearch, RedisJSON, RedisBloom, RedisTimeSeries, …) with names and versions. Tells you which capabilities are available.")]
    public static async Task<string> ModuleList(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (RedisResult[]?)await inst.Db().ExecuteAsync("MODULE", "LIST").ConfigureAwait(false) ?? Array.Empty<RedisResult>();
        var rows = raw.Select(entry =>
        {
            // Each entry is a list of name/value pairs: name <n> ver <v> path <p> args <a>
            var f = (RedisResult[]?)entry ?? Array.Empty<RedisResult>();
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < f.Length; i += 2) dict[f[i].ToString()] = f[i + 1].ToString();
            return dict;
        });
        return JsonSerializer.Serialize(new { alias, modules = rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "function_list"),
     Description("FUNCTION LIST — registered Redis Functions (7.0+) with engine and per-library function names. Returns empty array on older versions.")]
    public static async Task<string> FunctionList(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        if (!inst.HasFeature("function-list"))
            return JsonSerializer.Serialize(new { alias, supported = false, note = "FUNCTION LIST requires Redis 7.0+." }, JsonOpts.Default);

        var raw = (RedisResult[]?)await inst.Db().ExecuteAsync("FUNCTION", "LIST").ConfigureAwait(false) ?? Array.Empty<RedisResult>();
        var libs = raw.Select(lib =>
        {
            var f = (RedisResult[]?)lib ?? Array.Empty<RedisResult>();
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < f.Length; i += 2) dict[f[i].ToString()] = FlattenResult(f[i + 1]);
            return dict;
        });
        return JsonSerializer.Serialize(new { alias, supported = true, libraries = libs }, JsonOpts.Default);
    }

    [McpServerTool(Name = "debug_object"),
     Description("DEBUG OBJECT — low-level encoding/refcount/serialised-length for one key. Doubly-gated by Redis:AllowDangerous (DEBUG group is admin-only).")]
    public static async Task<string> DebugObject(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireDangerous("debug_object");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (string?)await inst.Db().ExecuteAsync("DEBUG", "OBJECT", key).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, info = raw }, JsonOpts.Default);
    }

    private static long? AsLong(RedisResult[] arr, int idx)
    {
        if ((uint)idx >= (uint)arr.Length) return null;
        var r = arr[idx];
        if (r.IsNull) return null;
        try { return (long)r; }
        catch { return long.TryParse(r.ToString(), out var v) ? v : (long?)null; }
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
                // Heuristic: even-length arrays where every-other entry is a bulk-string key look like a map.
                if (arr.Length > 0 && arr.Length % 2 == 0 &&
                    Enumerable.Range(0, arr.Length / 2).All(i => arr[i * 2].Resp2Type is ResultType.SimpleString or ResultType.BulkString))
                {
                    var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (int i = 0; i + 1 < arr.Length; i += 2) dict[arr[i].ToString()] = FlattenResult(arr[i + 1]);
                    return dict;
                }
                return arr.Select(FlattenResult).ToArray();
            default:
                return r.ToString();
        }
    }
}
