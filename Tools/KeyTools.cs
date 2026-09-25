using DnaX.MCPFab;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RedisMCPSharp.Services;
using StackExchange.Redis;

namespace RedisMCPSharp.Tools;

[McpServerToolType]
public sealed class KeyTools
{
    [McpServerTool(Name = "redis_exists"),
     Description("Return the count of keys that exist out of the given list. EXISTS supports multiple keys atomically.")]
    public static async Task<string> Exists(
        RedisRegistry reg,
        [Description("Key(s) to test.")] string[] keys,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var count = await inst.Db().KeyExistsAsync(keys.Select(k => (RedisKey)k).ToArray()).ConfigureAwait(false);
        return McpJson.Object().Set("alias", alias).Set("queried", keys.Length).Set("exists", count).ToJsonString();
    }

    [McpServerTool(Name = "redis_type"),
     Description("Return the Redis type of a key: string, list, hash, set, zset, stream, or none if the key doesn't exist.")]
    public static async Task<string> Type(
        RedisRegistry reg,
        [Description("Key to inspect.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var type = await inst.Db().KeyTypeAsync(key).ConfigureAwait(false);
        return McpJson.Object().Set("alias", alias).Set("key", key).Set("type", type.ToString().ToLowerInvariant()).ToJsonString();
    }

    [McpServerTool(Name = "redis_ttl"),
     Description("Return seconds until expiry (-1 = no TTL set, -2 = key doesn't exist). Also returns ms precision via pttl.")]
    public static async Task<string> Ttl(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var t = await inst.Db().KeyTimeToLiveAsync(key).ConfigureAwait(false);
        return McpJson.Object().Set("alias", alias).Set("key", key).Set("ttlSeconds", t?.TotalSeconds).Set("ttlMs", t?.TotalMilliseconds).ToJsonString();
    }

    [McpServerTool(Name = "redis_expire"),
     Description("Set or update a key's TTL. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> Expire(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("TTL in seconds.")] double seconds,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("expire");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var ok = await inst.Db().KeyExpireAsync(key, TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        return McpJson.Object().Set("alias", alias).Set("key", key).Set("set", ok).ToJsonString();
    }

    [McpServerTool(Name = "redis_persist"),
     Description("Remove a key's TTL so it lives forever. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> Persist(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("persist");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var ok = await inst.Db().KeyPersistAsync(key).ConfigureAwait(false);
        return McpJson.Object().Set("alias", alias).Set("key", key).Set("persisted", ok).ToJsonString();
    }

    [McpServerTool(Name = "redis_rename"),
     Description("Rename a key. Fails if the destination already exists unless overwrite=true. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> Rename(
        RedisRegistry reg,
        [Description("Current key name.")] string from,
        [Description("New key name.")] string to,
        [Description("Overwrite if destination exists. Default false (= RENAMENX).")] bool overwrite = false,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("rename");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var ok = await inst.Db().KeyRenameAsync(from, to, when: overwrite ? When.Always : When.NotExists).ConfigureAwait(false);
        return McpJson.Object().Set("alias", alias).Set("from", from).Set("to", to).Set("ok", ok).ToJsonString();
    }

    [McpServerTool(Name = "redis_del"),
     Description("Delete one or more keys. Returns count deleted. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> Del(
        RedisRegistry reg,
        [Description("Key(s) to delete.")] string[] keys,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("del");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var deleted = await inst.Db().KeyDeleteAsync(keys.Select(k => (RedisKey)k).ToArray()).ConfigureAwait(false);
        return McpJson.Object().Set("alias", alias).Set("requested", keys.Length).Set("deleted", deleted).ToJsonString();
    }

    [McpServerTool(Name = "redis_object_encoding"),
     Description("OBJECT ENCODING — internal representation Redis uses for the key (e.g. embstr, raw, listpack, hashtable, skiplist). Diagnostic for memory-tuning.")]
    public static async Task<string> ObjectEncoding(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var enc = (string?)await inst.Db().ExecuteAsync("OBJECT", "ENCODING", key).ConfigureAwait(false);
        return McpJson.Object().Set("alias", alias).Set("key", key).Set("encoding", enc).ToJsonString();
    }

    [McpServerTool(Name = "redis_object_idletime"),
     Description("OBJECT IDLETIME — seconds since the key was last accessed. Heuristic for finding candidates to evict.")]
    public static async Task<string> ObjectIdletime(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var v = (long?)await inst.Db().ExecuteAsync("OBJECT", "IDLETIME", key).ConfigureAwait(false);
        return McpJson.Object().Set("alias", alias).Set("key", key).Set("idleSeconds", v).ToJsonString();
    }

    [McpServerTool(Name = "redis_scan"),
     Description("Non-blocking key scan. Uses Redis SCAN under the hood — safe on production. Capped at Redis:MaxItems keys per call.")]
    public static async Task<string> Scan(
        RedisRegistry reg,
        [Description("Glob-style key pattern. Examples: \"user:*\", \"session:??:active\", \"*\".")] string pattern = "*",
        [Description("Filter by type: string | list | hash | set | zset | stream. Empty = all.")] string? type = null,
        [Description("Max keys to return. Default = Redis:MaxItems.")] int? max = null,
        [Description("Alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var cap = Math.Max(1, Math.Min(max ?? reg.Options.MaxItems, reg.Options.MaxItems));
        var pageSize = reg.Options.DefaultScanPageSize;
        var keys = new List<string>(cap);

        // On cluster, scan every primary node; on standalone, scan the single endpoint.
        var endpoints = inst.IsCluster
            ? inst.Multiplexer.GetEndPoints().Select(ep => inst.Multiplexer.GetServer(ep)).Where(s => s.IsConnected && !s.IsReplica)
            : new[] { inst.FirstServer() };

        foreach (var server in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var k in server.KeysAsync(database: inst.IsCluster ? -1 : inst.Entry.Database, pattern: pattern, pageSize: pageSize).ConfigureAwait(false))
            {
                if (type is not null)
                {
                    var t = await inst.Db().KeyTypeAsync(k).ConfigureAwait(false);
                    if (!string.Equals(t.ToString(), type, StringComparison.OrdinalIgnoreCase)) continue;
                }
                keys.Add(k.ToString());
                if (keys.Count >= cap) break;
            }
            if (keys.Count >= cap) break;
        }

        return McpJson.Object().Set("alias", alias).Set("pattern", pattern).Set("type", type).Set("returned", keys.Count).Set("truncated", keys.Count == cap).Set("keys", McpJson.Array(keys, item => McpJson.Scalar(item))).ToJsonString();
    }

    [McpServerTool(Name = "redis_scan_with_values"),
     Description("Combines SCAN + GET-style fetch. For each matched key, returns key, type, ttl, and (for strings/hashes/sets/lists with small contents) a value preview. Heavier than scan() — cap with `max` carefully.")]
    public static async Task<string> ScanWithValues(
        RedisRegistry reg,
        [Description("Glob-style key pattern.")] string pattern = "*",
        [Description("Filter by type. Empty = all.")] string? type = null,
        [Description("Max keys to return. Default 100.")] int max = 100,
        [Description("Alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var cap = Math.Max(1, Math.Min(max, reg.Options.MaxItems));
        var db = inst.Db();
        var results = new List<object>();

        var endpoints = inst.IsCluster
            ? inst.Multiplexer.GetEndPoints().Select(ep => inst.Multiplexer.GetServer(ep)).Where(s => s.IsConnected && !s.IsReplica)
            : new[] { inst.FirstServer() };

        foreach (var server in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var k in server.KeysAsync(database: inst.IsCluster ? -1 : inst.Entry.Database, pattern: pattern, pageSize: reg.Options.DefaultScanPageSize).ConfigureAwait(false))
            {
                var t = await db.KeyTypeAsync(k).ConfigureAwait(false);
                if (type is not null && !string.Equals(t.ToString(), type, StringComparison.OrdinalIgnoreCase)) continue;
                var ttl = await db.KeyTimeToLiveAsync(k).ConfigureAwait(false);
                object? preview = t switch
                {
                    RedisType.String => Trunc((string?)await db.StringGetAsync(k).ConfigureAwait(false), reg.Options.MaxChars),
                    RedisType.List => (await db.ListRangeAsync(k, 0, 4).ConfigureAwait(false)).Select(v => Trunc((string?)v, 200)),
                    RedisType.Hash => (await db.HashGetAllAsync(k).ConfigureAwait(false)).Take(5).ToDictionary(e => e.Name.ToString(), e => Trunc((string?)e.Value, 200)),
                    RedisType.Set => (await db.SetMembersAsync(k).ConfigureAwait(false)).Take(5).Select(v => Trunc((string?)v, 200)),
                    RedisType.SortedSet => (await db.SortedSetRangeByScoreWithScoresAsync(k, take: 5).ConfigureAwait(false)).Select(e => new { value = Trunc((string?)e.Element, 200), score = e.Score }),
                    _ => null,
                };
                results.Add(new { key = k.ToString(), type = t.ToString().ToLowerInvariant(), ttlSeconds = ttl?.TotalSeconds, preview });
                if (results.Count >= cap) break;
            }
            if (results.Count >= cap) break;
        }

        return JsonSerializer.Serialize(new { alias, pattern, type, returned = results.Count, truncated = results.Count == cap, items = results }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_search_keys"),
     Description("Glob pattern search across the keyspace (wraps SCAN). For full-text indexed search (RediSearch FT.SEARCH) use `execute` with the FT.* commands.")]
    public static Task<string> SearchKeys(
        RedisRegistry reg,
        [Description("Glob pattern, e.g. \"user:*:profile\".")] string pattern,
        [Description("Alias. Omit for default.")] string? alias = null,
        [Description("Max keys to return. Default 200.")] int max = 200,
        CancellationToken ct = default)
        => Scan(reg, pattern, type: null, max, alias, ct);

    private static string? Trunc(string? s, int max) =>
        s is null ? null : s.Length > max ? s[..max] + $"…(+{s.Length - max} chars)" : s;
}
