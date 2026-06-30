using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RedisMCPSharp.Services;
using StackExchange.Redis;

namespace RedisMCPSharp.Tools;

[McpServerToolType]
public sealed class CollectionTools
{
    // ───────────────────────── HASH ─────────────────────────

    [McpServerTool(Name = "redis_hget"),
     Description("HGET — value of a single field in a hash. Returns null if the field doesn't exist.")]
    public static async Task<string> HGet(
        RedisRegistry reg,
        [Description("Hash key.")] string key,
        [Description("Field name.")] string field,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var v = await inst.Db().HashGetAsync(key, field).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, field, value = v.IsNull ? null : (string?)v }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_hgetall"),
     Description("HGETALL — every field/value in a hash. Cap on returned entries (Redis:MaxItems).")]
    public static async Task<string> HGetAll(
        RedisRegistry reg,
        [Description("Hash key.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var entries = await inst.Db().HashGetAllAsync(key).ConfigureAwait(false);
        var cap = reg.Options.MaxItems;
        var rows = entries.Take(cap).ToDictionary(e => e.Name.ToString(), e => (string?)e.Value);
        return JsonSerializer.Serialize(new { alias, key, count = entries.Length, returned = rows.Count, truncated = entries.Length > cap, fields = rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_hkeys"),
     Description("HKEYS — list of field names in a hash.")]
    public static async Task<string> HKeys(
        RedisRegistry reg,
        [Description("Hash key.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var fields = await inst.Db().HashKeysAsync(key).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, count = fields.Length, fields = fields.Select(f => (string?)f) }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_hset"),
     Description("HSET — write one or more hash field/values. Pass `fields` as an object. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> HSet(
        RedisRegistry reg,
        [Description("Hash key.")] string key,
        [Description("Object of field→value pairs.")] Dictionary<string, string> fields,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("hset");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var entries = fields.Select(p => new HashEntry(p.Key, p.Value)).ToArray();
        await inst.Db().HashSetAsync(key, entries).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, written = fields.Count }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_hdel"),
     Description("HDEL — remove one or more fields from a hash. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> HDel(
        RedisRegistry reg,
        [Description("Hash key.")] string key,
        [Description("Field name(s).")] string[] fields,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("hdel");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var removed = await inst.Db().HashDeleteAsync(key, fields.Select(f => (RedisValue)f).ToArray()).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, requested = fields.Length, removed }, JsonOpts.Default);
    }

    // ───────────────────────── LIST ─────────────────────────

    [McpServerTool(Name = "redis_lrange"),
     Description("LRANGE — slice a list by index. Negative indices count from the end (-1 = last). Default returns the whole list, capped at Redis:MaxItems.")]
    public static async Task<string> LRange(
        RedisRegistry reg,
        [Description("List key.")] string key,
        [Description("Start index (0-based, may be negative). Default 0.")] long start = 0,
        [Description("Stop index (inclusive, may be negative). Default -1 (last).")] long stop = -1,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var items = await inst.Db().ListRangeAsync(key, start, stop).ConfigureAwait(false);
        var cap = reg.Options.MaxItems;
        var trimmed = items.Take(cap).Select(v => (string?)v);
        return JsonSerializer.Serialize(new { alias, key, count = items.Length, returned = Math.Min(items.Length, cap), truncated = items.Length > cap, items = trimmed }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_llen"),
     Description("LLEN — number of elements in a list.")]
    public static async Task<string> LLen(
        RedisRegistry reg,
        [Description("List key.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var n = await inst.Db().ListLengthAsync(key).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, length = n }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_lpush"),
     Description("LPUSH — prepend one or more values to a list. Returns new list length. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> LPush(
        RedisRegistry reg,
        [Description("List key.")] string key,
        [Description("Value(s) to push (leftmost first).")] string[] values,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("lpush");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var n = await inst.Db().ListLeftPushAsync(key, values.Select(v => (RedisValue)v).ToArray()).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, newLength = n }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_rpush"),
     Description("RPUSH — append one or more values to a list. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> RPush(
        RedisRegistry reg,
        [Description("List key.")] string key,
        [Description("Value(s) to push.")] string[] values,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("rpush");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var n = await inst.Db().ListRightPushAsync(key, values.Select(v => (RedisValue)v).ToArray()).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, newLength = n }, JsonOpts.Default);
    }

    // ───────────────────────── SET ─────────────────────────

    [McpServerTool(Name = "redis_smembers"),
     Description("SMEMBERS — all members of a set. Capped at Redis:MaxItems.")]
    public static async Task<string> SMembers(
        RedisRegistry reg,
        [Description("Set key.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var members = await inst.Db().SetMembersAsync(key).ConfigureAwait(false);
        var cap = reg.Options.MaxItems;
        var trimmed = members.Take(cap).Select(v => (string?)v);
        return JsonSerializer.Serialize(new { alias, key, count = members.Length, returned = Math.Min(members.Length, cap), truncated = members.Length > cap, members = trimmed }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_sismember"),
     Description("SISMEMBER — test whether a value is in a set. Returns boolean.")]
    public static async Task<string> SIsMember(
        RedisRegistry reg,
        [Description("Set key.")] string key,
        [Description("Value to test for membership.")] string member,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var ok = await inst.Db().SetContainsAsync(key, member).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, member, isMember = ok }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_sadd"),
     Description("SADD — add one or more members to a set. Returns the count that were newly added. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> SAdd(
        RedisRegistry reg,
        [Description("Set key.")] string key,
        [Description("Value(s) to add.")] string[] members,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("sadd");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var added = await inst.Db().SetAddAsync(key, members.Select(m => (RedisValue)m).ToArray()).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, requested = members.Length, added }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_srem"),
     Description("SREM — remove members from a set. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> SRem(
        RedisRegistry reg,
        [Description("Set key.")] string key,
        [Description("Value(s) to remove.")] string[] members,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("srem");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var removed = await inst.Db().SetRemoveAsync(key, members.Select(m => (RedisValue)m).ToArray()).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, requested = members.Length, removed }, JsonOpts.Default);
    }

    // ───────────────────────── SORTED SET ─────────────────────────

    [McpServerTool(Name = "redis_zrange"),
     Description("ZRANGE — slice a sorted set by index. Returns members and (optionally) their scores. Capped at Redis:MaxItems.")]
    public static async Task<string> ZRange(
        RedisRegistry reg,
        [Description("Sorted-set key.")] string key,
        [Description("Start index (0-based). Default 0.")] long start = 0,
        [Description("Stop index (-1 = last). Default -1.")] long stop = -1,
        [Description("Reverse order (high score first). Default false.")] bool descending = false,
        [Description("Include scores. Default true.")] bool withScores = true,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var order = descending ? Order.Descending : Order.Ascending;
        if (withScores)
        {
            var entries = await inst.Db().SortedSetRangeByRankWithScoresAsync(key, start, stop, order).ConfigureAwait(false);
            var cap = reg.Options.MaxItems;
            var trimmed = entries.Take(cap).Select(e => new { value = (string?)e.Element, score = e.Score });
            return JsonSerializer.Serialize(new { alias, key, count = entries.Length, returned = Math.Min(entries.Length, cap), truncated = entries.Length > cap, items = trimmed }, JsonOpts.Default);
        }
        else
        {
            var members = await inst.Db().SortedSetRangeByRankAsync(key, start, stop, order).ConfigureAwait(false);
            var cap = reg.Options.MaxItems;
            var trimmed = members.Take(cap).Select(v => (string?)v);
            return JsonSerializer.Serialize(new { alias, key, count = members.Length, returned = Math.Min(members.Length, cap), truncated = members.Length > cap, items = trimmed }, JsonOpts.Default);
        }
    }

    [McpServerTool(Name = "redis_zadd"),
     Description("ZADD — add member(s) to a sorted set with scores. Pass an object: { \"member\": score }. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> ZAdd(
        RedisRegistry reg,
        [Description("Sorted-set key.")] string key,
        [Description("Object mapping member → numeric score.")] Dictionary<string, double> members,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("zadd");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var entries = members.Select(p => new SortedSetEntry(p.Key, p.Value)).ToArray();
        var added = await inst.Db().SortedSetAddAsync(key, entries).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, requested = members.Count, added }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_zrem"),
     Description("ZREM — remove member(s) from a sorted set. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> ZRem(
        RedisRegistry reg,
        [Description("Sorted-set key.")] string key,
        [Description("Member name(s) to remove.")] string[] members,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("zrem");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var removed = await inst.Db().SortedSetRemoveAsync(key, members.Select(m => (RedisValue)m).ToArray()).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, requested = members.Length, removed }, JsonOpts.Default);
    }

    // ───────────────────────── STREAM ─────────────────────────

    [McpServerTool(Name = "redis_xrange"),
     Description("XRANGE — read entries from a Redis Stream by ID range. Defaults to whole stream up to count.")]
    public static async Task<string> XRange(
        RedisRegistry reg,
        [Description("Stream key.")] string key,
        [Description("Start ID. '-' = earliest. Default '-'.")] string start = "-",
        [Description("End ID. '+' = latest. Default '+'.")] string end = "+",
        [Description("Max entries. Default 100.")] int count = 100,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var entries = await inst.Db().StreamRangeAsync(key, start, end, count).ConfigureAwait(false);
        var items = entries.Select(e => new
        {
            id = e.Id.ToString(),
            fields = e.Values.ToDictionary(v => v.Name.ToString(), v => (string?)v.Value),
        });
        return JsonSerializer.Serialize(new { alias, key, returned = entries.Length, items }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_xlen"),
     Description("XLEN — number of entries in a stream.")]
    public static async Task<string> XLen(
        RedisRegistry reg,
        [Description("Stream key.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var n = await inst.Db().StreamLengthAsync(key).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, length = n }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_xadd"),
     Description("XADD — append an entry to a stream. Returns the assigned entry ID. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> XAdd(
        RedisRegistry reg,
        [Description("Stream key.")] string key,
        [Description("Object of field→value pairs to store as one entry.")] Dictionary<string, string> fields,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("xadd");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var nv = fields.Select(p => new NameValueEntry(p.Key, p.Value)).ToArray();
        var id = await inst.Db().StreamAddAsync(key, nv).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, id = id.ToString() }, JsonOpts.Default);
    }
}
