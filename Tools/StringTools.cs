using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using RedisMCPSharp.Services;
using StackExchange.Redis;

namespace RedisMCPSharp.Tools;

[McpServerToolType]
public sealed class StringTools
{
    [McpServerTool(Name = "redis_get"),
     Description("GET — return the string value at key. Returns null if the key doesn't exist. Use `get_deserialised` if the value is JSON / BSON / Protobuf.")]
    public static async Task<string> Get(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var v = await inst.Db().StringGetAsync(key).ConfigureAwait(false);
        if (v.IsNull) return JsonSerializer.Serialize(new { alias, key, exists = false }, JsonOpts.Default);
        var text = (string?)v;
        var truncated = text is not null && text.Length > reg.Options.MaxValueChars;
        if (truncated) text = text![..reg.Options.MaxValueChars] + $"…(+{text.Length - reg.Options.MaxValueChars} chars)";
        return JsonSerializer.Serialize(new { alias, key, exists = true, value = text, truncated, lengthBytes = ((byte[]?)v)?.Length }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_mget"),
     Description("MGET — get multiple string values in one round-trip. Returns parallel array, with nulls for missing keys.")]
    public static async Task<string> MGet(
        RedisRegistry reg,
        [Description("Keys to fetch.")] string[] keys,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var values = await inst.Db().StringGetAsync(keys.Select(k => (RedisKey)k).ToArray()).ConfigureAwait(false);
        var rows = keys.Zip(values, (k, v) =>
        {
            var s = (string?)v;
            var trunc = s is not null && s.Length > reg.Options.MaxValueChars;
            if (trunc) s = s![..reg.Options.MaxValueChars] + $"…(+{s.Length - reg.Options.MaxValueChars} chars)";
            return new { key = k, value = s, exists = !v.IsNull, truncated = trunc };
        });
        return JsonSerializer.Serialize(new { alias, count = keys.Length, items = rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_get_deserialised"),
     Description("GET + auto-detect content type (JSON / BSON / Protobuf / UTF-8 / binary) and return a structured representation. Useful when the value is a serialised object and you don't want to look at base64.")]
    public static async Task<string> GetDeserialised(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("Force a specific decoder: json | bson | protobuf | text | binary. Omit for auto-detect.")] string? format = null,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (byte[]?)await inst.Db().StringGetAsync(key).ConfigureAwait(false);
        if (raw is null) return JsonSerializer.Serialize(new { alias, key, exists = false }, JsonOpts.Default);
        var decoded = ValueDecoder.Decode(raw, format);
        return JsonSerializer.Serialize(new
        {
            alias, key, exists = true,
            lengthBytes = raw.Length,
            kind = decoded.Kind.ToString(),
            note = decoded.Note,
            value = decoded.Value,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_detect_format"),
     Description("Sniff a value (read by key) and return only the detected content type (no decoded payload). Cheap diagnostic for surveying what's stored.")]
    public static async Task<string> DetectFormat(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (byte[]?)await inst.Db().StringGetAsync(key).ConfigureAwait(false);
        if (raw is null) return JsonSerializer.Serialize(new { alias, key, exists = false }, JsonOpts.Default);
        var decoded = ValueDecoder.Decode(raw);
        return JsonSerializer.Serialize(new { alias, key, exists = true, lengthBytes = raw.Length, kind = decoded.Kind.ToString(), note = decoded.Note }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_set"),
     Description("SET — write a string value. Supports TTL (seconds) and NX (only-if-missing) / XX (only-if-exists). Refused when Redis:ReadOnly=true.")]
    public static async Task<string> Set(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("Value to store.")] string value,
        [Description("Optional TTL in seconds. Empty = no TTL.")] double? ttlSeconds = null,
        [Description("Mode: '' (default), 'NX' (only-if-missing), 'XX' (only-if-exists).")] string mode = "",
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("set");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var when = mode.Trim().ToUpperInvariant() switch
        {
            "NX" => When.NotExists,
            "XX" => When.Exists,
            _ => When.Always,
        };
        var ok = await inst.Db().StringSetAsync(key, value,
            ttlSeconds.HasValue ? TimeSpan.FromSeconds(ttlSeconds.Value) : (TimeSpan?)null,
            when: when).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, written = ok, ttlSeconds, mode = string.IsNullOrEmpty(mode) ? null : mode }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_mset"),
     Description("MSET — write multiple key/value strings atomically. Pass an object: { \"k1\": \"v1\", \"k2\": \"v2\" }. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> MSet(
        RedisRegistry reg,
        [Description("Object of key→value strings.")] Dictionary<string, string> pairs,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("mset");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var pairsArr = pairs.Select(p => new KeyValuePair<RedisKey, RedisValue>(p.Key, p.Value)).ToArray();
        var ok = await inst.Db().StringSetAsync(pairsArr).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, count = pairs.Count, ok }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_append"),
     Description("APPEND — append to an existing string (creates if missing). Returns new length. Refused when Redis:ReadOnly=true.")]
    public static async Task<string> Append(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("Value to append.")] string value,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("append");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var newLen = await inst.Db().StringAppendAsync(key, value).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, newLength = newLen }, JsonOpts.Default);
    }

    [McpServerTool(Name = "redis_incrby"),
     Description("INCRBY — atomically increment an integer-valued string by N (negative for DECRBY). Refused when Redis:ReadOnly=true.")]
    public static async Task<string> IncrBy(
        RedisRegistry reg,
        [Description("Key.")] string key,
        [Description("Delta (use negative for decrement). Default 1.")] long by = 1,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        reg.RequireWritable("incrby");
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var newVal = await inst.Db().StringIncrementAsync(key, by).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, newValue = newVal }, JsonOpts.Default);
    }
}
