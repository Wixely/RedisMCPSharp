using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RedisMCPSharp.Services;
using StackExchange.Redis;

namespace RedisMCPSharp.Tools;

[McpServerToolType]
public sealed class DiscoveryTools
{
    [McpServerTool(Name = "list_servers"),
     Description("**CALL FIRST** when you don't already know the aliases. Returns every configured Redis connection's alias + description + the default alias picked when a tool omits `alias`. Each alias is the handle every other tool accepts.")]
    public static string ListServers(RedisRegistry reg) =>
        JsonSerializer.Serialize(new
        {
            defaultAlias = reg.DefaultAlias,
            servers = reg.Servers.Values.Select(s => new
            {
                alias = s.Alias,
                description = s.Description,
                database = s.Database,
                versionOverride = string.IsNullOrEmpty(s.VersionOverride) ? null : s.VersionOverride,
            }),
        }, JsonOpts.Default);

    [McpServerTool(Name = "test_connection"),
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
            return JsonSerializer.Serialize(new { alias, ok = true, pingMs = latency.TotalMilliseconds, totalMs = sw.ElapsedMilliseconds, version = inst.VersionRaw, cluster = inst.IsCluster }, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return JsonSerializer.Serialize(new { alias, ok = false, totalMs = sw.ElapsedMilliseconds, error = ex.Message }, JsonOpts.Default);
        }
    }

    [McpServerTool(Name = "server_info"),
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
        return JsonSerializer.Serialize(new
        {
            alias,
            section,
            sections = groups.Select(g => new
            {
                name = g.Key,
                values = g.ToDictionary(kv => kv.Key, kv => kv.Value),
            }),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "version_info"),
     Description("Return the detected Redis version + cluster shape + per-feature capability flags (e.g. hash-field-ttl on 7.4+). Useful before reaching for a version-sensitive command.")]
    public static async Task<string> VersionInfo(
        RedisRegistry reg,
        [Description("Alias. Omit for default.")] string? alias = null)
    {
        var inst = await reg.GetAsync(alias).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            alias = inst.Entry.Alias,
            version = inst.VersionRaw,
            versionMajorMinor = $"{inst.Version.Major}.{inst.Version.Minor}",
            cluster = inst.IsCluster,
            features = new
            {
                hashFieldTtl = inst.HasFeature("hash-field-ttl"),
                functionList = inst.HasFeature("function-list"),
                objectFreq = inst.HasFeature("object-freq"),
            },
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "dbsize"),
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
            return JsonSerializer.Serialize(new { alias, database = inst.Entry.Database, keys = count }, JsonOpts.Default);
        }
    }
}
