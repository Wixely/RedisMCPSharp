using DnaX.MCPFab;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RedisMCPSharp.Services;
using StackExchange.Redis;

namespace RedisMCPSharp.Tools;

[McpServerToolType]
public sealed class DiscoveryTools
{
    [McpServerTool(Name = "redis_list_servers"),
     Description("**CALL FIRST** when you don't already know the aliases. Returns every configured Redis connection's alias + description + the default alias picked when a tool omits `alias`. Each alias is the handle every other tool accepts.")]
    public static string ListServers(RedisRegistry reg) =>
        McpJson.Object().Set("defaultAlias", reg.DefaultAlias).Set("servers", McpJson.Array(reg.Servers.Values, s => McpJson.Object().Set("alias", s.Alias).Set("description", s.Description).Set("database", s.Database).Set("versionOverride", string.IsNullOrEmpty(s.VersionOverride) ? null : s.VersionOverride))).ToJsonString();

    [McpServerTool(Name = "redis_test_connection"),
     Description("PING the alias and return latency in ms. Fast first move when debugging credentials, network, or auth.")]
    public static async Task<string> TestConnection(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var inst = await reg.GetAsync(alias).ConfigureAwait(false);
            var latency = await inst.Db().PingAsync().ConfigureAwait(false);
            sw.Stop();
            return McpJson.Object().Set("alias", alias).Set("ok", true).Set("pingMs", latency.TotalMilliseconds).Set("totalMs", sw.ElapsedMilliseconds).Set("version", inst.VersionRaw).Set("cluster", inst.IsCluster).ToJsonString();
        }
        catch (Exception ex)
        {
            sw.Stop();
            return McpJson.Object().Set("alias", alias).Set("ok", false).Set("totalMs", sw.ElapsedMilliseconds).Set("error", ex.Message).ToJsonString();
        }
    }

    [McpServerTool(Name = "redis_server_info"),
     Description("Run INFO and return a structured view. Optional section name (server, clients, memory, persistence, stats, replication, cpu, commandstats, latencystats, cluster, keyspace, errorstats). Omit `section` for everything.")]
    public static async Task<string> ServerInfo(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null,
        [Description("INFO section: server | clients | memory | persistence | stats | replication | cpu | commandstats | latencystats | cluster | keyspace | errorstats. Empty = all.")] string? section = null,
        CancellationToken ct = default)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        var server = inst.FirstServer();
        var groups = string.IsNullOrWhiteSpace(section)
            ? await server.InfoAsync().ConfigureAwait(false)
            : await server.InfoAsync(section).ConfigureAwait(false);
        return McpJson.Object().Set("alias", alias).Set("section", section).Set("sections", McpJson.Array(groups, g => McpJson.Object().Set("name", g.Key).Set("values", McpJson.Map(g.ToDictionary(kv => kv.Key, kv => kv.Value), entry => McpJson.Scalar(entry))))).ToJsonString();
    }

    [McpServerTool(Name = "redis_version_info"),
     Description("Return the detected Redis version + cluster shape + per-feature capability flags (e.g. hash-field-ttl on 7.4+). Useful before reaching for a version-sensitive command.")]
    public static async Task<string> VersionInfo(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        return McpJson.Object().Set("alias", inst.Entry.Alias).Set("version", inst.VersionRaw).Set("versionMajorMinor", $"{inst.Version.Major}.{inst.Version.Minor}").Set("cluster", inst.IsCluster).Set("features", McpJson.Object().Set("hashFieldTtl", inst.HasFeature("hash-field-ttl")).Set("functionList", inst.HasFeature("function-list")).Set("objectFreq", inst.HasFeature("object-freq"))).ToJsonString();
    }

    [McpServerTool(Name = "redis_dbsize"),
     Description("DBSIZE — number of keys in the currently selected database (or every node, summed, on cluster).")]
    public static async Task<string> Dbsize(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        if (inst.IsCluster)
        {
            long total = 0;
            var perNode = new List<object>();
            foreach (var ep in inst.Multiplexer.GetEndPoints())
            {
                try
                {
                    var s = inst.Multiplexer.GetServer(ep);
                    if (s.IsConnected && !s.IsReplica)
                    {
                        var c = await s.DatabaseSizeAsync().ConfigureAwait(false);
                        total += c; perNode.Add(new { endpoint = ep.ToString(), keys = c });
                    }
                }
                catch (Exception ex) { perNode.Add(new { endpoint = ep.ToString(), error = ex.Message }); }
            }
            return JsonSerializer.Serialize(new { alias, cluster = true, total, perNode }, JsonOpts.Default);
        }
        else
        {
            var count = await inst.FirstServer().DatabaseSizeAsync(inst.Entry.Database).ConfigureAwait(false);
            return McpJson.Object().Set("alias", alias).Set("database", inst.Entry.Database).Set("keys", count).ToJsonString();
        }
    }
}
