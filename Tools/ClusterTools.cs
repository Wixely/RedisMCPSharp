using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RedisMCPSharp.Services;
using StackExchange.Redis;

namespace RedisMCPSharp.Tools;

/// <summary>
/// Redis Cluster introspection. Works against standalone too (most commands degrade to a
/// reasonable error / empty result), but designed for multi-node deployments.
/// </summary>
[McpServerToolType]
public sealed class ClusterTools
{
    [McpServerTool(Name = "cluster_info"),
     Description("CLUSTER INFO — cluster state, known nodes, slot coverage, epoch, etc.")]
    public static async Task<string> ClusterInfo(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var server = inst.FirstServer();
        var raw = (string?)await inst.Db().ExecuteAsync("CLUSTER", "INFO").ConfigureAwait(false) ?? "";
        var dict = ParseKvLines(raw);
        return JsonSerializer.Serialize(new { alias, isCluster = inst.IsCluster, info = dict }, JsonOpts.Default);
    }

    [McpServerTool(Name = "cluster_nodes"),
     Description("CLUSTER NODES — parsed list of every node (id, endpoint, flags, master/replica, slot ranges, ping/pong, link state).")]
    public static async Task<string> ClusterNodes(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var raw = (string?)await inst.Db().ExecuteAsync("CLUSTER", "NODES").ConfigureAwait(false) ?? "";
        var nodes = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            // <id> <ip:port@cport[,hostname]> <flags> <master> <ping> <pong> <epoch> <linkstate> [slot range ...]
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new
            {
                id = parts.ElementAtOrDefault(0),
                endpoint = parts.ElementAtOrDefault(1),
                flags = parts.ElementAtOrDefault(2),
                master = parts.ElementAtOrDefault(3),
                pingSent = parts.ElementAtOrDefault(4),
                pongRecv = parts.ElementAtOrDefault(5),
                epoch = parts.ElementAtOrDefault(6),
                linkState = parts.ElementAtOrDefault(7),
                slots = parts.Length > 8 ? parts.Skip(8).ToArray() : Array.Empty<string>(),
            };
        });
        return JsonSerializer.Serialize(new { alias, nodes }, JsonOpts.Default);
    }

    [McpServerTool(Name = "cluster_slots"),
     Description("CLUSTER SHARDS / SLOTS — slot ranges and the nodes serving them. Useful for spotting unbalanced shards.")]
    public static async Task<string> ClusterSlots(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        // CLUSTER SHARDS is the Redis 7.0+ replacement for CLUSTER SLOTS (which still works).
        // Both are nested arrays; we just stringify the response — the structure is engine-specific.
        var cmd = inst.Version >= new Version(7, 0) ? "SHARDS" : "SLOTS";
        var raw = await inst.Db().ExecuteAsync("CLUSTER", cmd).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, command = $"CLUSTER {cmd}", raw = raw.ToString() }, JsonOpts.Default);
    }

    [McpServerTool(Name = "cluster_keyslot"),
     Description("CLUSTER KEYSLOT — which slot (0–16383) a given key would hash to.")]
    public static async Task<string> ClusterKeyslot(
        RedisRegistry reg,
        [Description("Key to hash.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var slot = (long?)await inst.Db().ExecuteAsync("CLUSTER", "KEYSLOT", key).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, key, slot }, JsonOpts.Default);
    }

    [McpServerTool(Name = "cluster_countkeysinslot"),
     Description("CLUSTER COUNTKEYSINSLOT — count of keys mapped to a given slot. Use cluster_keyslot first to find the slot for a representative key.")]
    public static async Task<string> ClusterCountKeysInSlot(
        RedisRegistry reg,
        [Description("Slot index 0–16383.")] int slot,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var n = (long?)await inst.Db().ExecuteAsync("CLUSTER", "COUNTKEYSINSLOT", slot).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, slot, keys = n }, JsonOpts.Default);
    }

    private static Dictionary<string, string> ParseKvLines(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            dict[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return dict;
    }
}
