using DnaX.MCPFab;
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
    [McpServerTool(Name = "redis_cluster_info"),
     Description("CLUSTER INFO — cluster state, known nodes, slot coverage, epoch, etc.")]
    public static async Task<string> ClusterInfo(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var server = inst.FirstServer();
        var raw = (string?)await inst.Db().ExecuteAsync("CLUSTER", "INFO").ConfigureAwait(false) ?? "";
        var dict = ParseKvLines(raw);
        return McpJson.Object().Set("alias", alias).Set("isCluster", inst.IsCluster).Set("info", McpJson.Map(dict, entry => McpJson.Scalar(entry))).ToJsonString();
    }

    [McpServerTool(Name = "redis_cluster_nodes"),
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
        return McpJson.Object().Set("alias", alias).Set("nodes", McpJson.Array(raw.Split('\n', StringSplitOptions.RemoveEmptyEntries), line =>
        {
            // <id> <ip:port@cport[,hostname]> <flags> <master> <ping> <pong> <epoch> <linkstate> [slot range ...]
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return McpJson.Object().Set("id", parts.ElementAtOrDefault(0)).Set("endpoint", parts.ElementAtOrDefault(1)).Set("flags", parts.ElementAtOrDefault(2)).Set("master", parts.ElementAtOrDefault(3)).Set("pingSent", parts.ElementAtOrDefault(4)).Set("pongRecv", parts.ElementAtOrDefault(5)).Set("epoch", parts.ElementAtOrDefault(6)).Set("linkState", parts.ElementAtOrDefault(7)).Set("slots", McpJson.Array(parts.Length > 8 ? parts.Skip(8).ToArray() : Array.Empty<string>(), item => McpJson.Scalar(item)));
        })).ToJsonString();
    }

    [McpServerTool(Name = "redis_cluster_slots"),
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
        return McpJson.Object().Set("alias", alias).Set("command", $"CLUSTER {cmd}").Set("raw", raw.ToString()).ToJsonString();
    }

    [McpServerTool(Name = "redis_cluster_keyslot"),
     Description("CLUSTER KEYSLOT — which slot (0–16383) a given key would hash to.")]
    public static async Task<string> ClusterKeyslot(
        RedisRegistry reg,
        [Description("Key to hash.")] string key,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var slot = (long?)await inst.Db().ExecuteAsync("CLUSTER", "KEYSLOT", key).ConfigureAwait(false);
        return McpJson.Object().Set("alias", alias).Set("key", key).Set("slot", slot).ToJsonString();
    }

    [McpServerTool(Name = "redis_cluster_countkeysinslot"),
     Description("CLUSTER COUNTKEYSINSLOT — count of keys mapped to a given slot. Use cluster_keyslot first to find the slot for a representative key.")]
    public static async Task<string> ClusterCountKeysInSlot(
        RedisRegistry reg,
        [Description("Slot index 0–16383.")] int slot,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var n = (long?)await inst.Db().ExecuteAsync("CLUSTER", "COUNTKEYSINSLOT", slot).ConfigureAwait(false);
        return McpJson.Object().Set("alias", alias).Set("slot", slot).Set("keys", n).ToJsonString();
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
