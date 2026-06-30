using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedisMCPSharp.Configuration;
using StackExchange.Redis;

namespace RedisMCPSharp.Services;

/// <summary>
/// Owns every <see cref="ConnectionMultiplexer"/> the server uses. Tools resolve a target by
/// alias, get an open multiplexer, and use it for the call. Multiplexers are long-lived and
/// expensive to create — StackExchange.Redis explicitly expects ONE per process per cluster,
/// shared across all consumers. So we cache them here.
///
/// Auto-naming: any entry in config that omits its alias gets a stable <c>redis-N</c> name
/// at construction time so the agent always has something to refer to.
///
/// Version detection: on first use, we call INFO and stash the reported <c>redis_version</c>
/// so version-sensitive tools (e.g. HEXPIRE on 7.4+) can dispatch correctly without re-querying.
/// </summary>
public sealed class RedisRegistry : IAsyncDisposable
{
    private readonly RedisOptions _options;
    private readonly ILogger<RedisRegistry> _log;
    private readonly Dictionary<string, RedisServerEntry> _byAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<RedisInstance>>> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _defaultAlias;

    public RedisRegistry(IOptions<RedisOptions> options, ILogger<RedisRegistry> log)
    {
        _options = options.Value;
        _log = log;

        int idx = 1;
        foreach (var entry in _options.Servers)
        {
            var alias = string.IsNullOrWhiteSpace(entry.Alias) ? $"redis-{idx}" : entry.Alias;
            while (_byAlias.ContainsKey(alias)) alias = $"redis-{++idx}";
            entry.Alias = alias;
            _byAlias[alias] = entry;
            idx++;
        }

        var preferred = _options.DefaultAlias;
        _defaultAlias = !string.IsNullOrWhiteSpace(preferred) && _byAlias.ContainsKey(preferred)
            ? preferred
            : _byAlias.Keys.FirstOrDefault();
    }

    public RedisOptions Options => _options;
    public IReadOnlyDictionary<string, RedisServerEntry> Servers => _byAlias;
    public string? DefaultAlias => _defaultAlias;

    public bool IsReadOnly => _options.ReadOnly;
    public bool AllowDangerous => _options.AllowDangerous;

    public void RequireWritable(string operation)
    {
        if (_options.ReadOnly)
            throw new InvalidOperationException(
                $"Operation '{operation}' is blocked: server is running in read-only mode (Redis:ReadOnly=true).");
    }

    public void RequireDangerous(string operation)
    {
        if (!_options.AllowDangerous)
            throw new InvalidOperationException(
                $"Operation '{operation}' is dangerous (flushes / pattern-deletes / arbitrary commands) and disabled. Set Redis:AllowDangerous=true to permit it.");
    }

    public RedisServerEntry ResolveAlias(string? alias)
    {
        if (_byAlias.Count == 0) throw new InvalidOperationException("No Redis servers configured.");
        if (!string.IsNullOrWhiteSpace(alias))
        {
            if (_byAlias.TryGetValue(alias, out var direct)) return direct;
            throw new InvalidOperationException(
                $"Unknown Redis alias '{alias}'. Available: {string.Join(", ", _byAlias.Keys)}.");
        }
        return _byAlias[_defaultAlias!];
    }

    /// <summary>
    /// Get (or lazily create) the cached <see cref="RedisInstance"/> for an alias. First call
    /// pays the connection-setup cost; subsequent calls reuse the same multiplexer.
    /// </summary>
    public Task<RedisInstance> GetAsync(string? alias)
    {
        var entry = ResolveAlias(alias);
        var lazy = _instances.GetOrAdd(entry.Alias, _ => new Lazy<Task<RedisInstance>>(() => CreateAsync(entry)));
        return lazy.Value;
    }

    private async Task<RedisInstance> CreateAsync(RedisServerEntry entry)
    {
        var cfg = ConfigurationOptions.Parse(entry.ConnectionString);
        if (_options.CommandTimeoutMs > 0)
        {
            cfg.SyncTimeout = _options.CommandTimeoutMs;
            cfg.AsyncTimeout = _options.CommandTimeoutMs;
        }
        // Keep the server reachable even if the first endpoint is briefly down — typical in
        // multi-node clusters and rolling restarts.
        cfg.AbortOnConnectFail = false;
        cfg.ClientName = $"RedisMCPSharp:{entry.Alias}";

        var multiplexer = await ConnectionMultiplexer.ConnectAsync(cfg).ConfigureAwait(false);

        // Pick the first reachable server endpoint to probe for INFO + cluster shape.
        var endpoint = multiplexer.GetEndPoints().FirstOrDefault();
        var server = endpoint is null ? null : multiplexer.GetServer(endpoint);

        string version;
        bool isCluster;
        try
        {
            version = !string.IsNullOrWhiteSpace(entry.VersionOverride)
                ? entry.VersionOverride
                : (server?.Version.ToString() ?? "unknown");
            isCluster = server?.ServerType == ServerType.Cluster;
        }
        catch
        {
            version = entry.VersionOverride;
            isCluster = false;
        }

        _log.LogInformation(
            "Connected to Redis alias={Alias} endpoints={Endpoints} version={Version} cluster={Cluster}",
            entry.Alias, string.Join(",", multiplexer.GetEndPoints().Select(e => e.ToString())), version, isCluster);

        return new RedisInstance(entry, multiplexer, version, isCluster);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, lazy) in _instances)
        {
            if (!lazy.IsValueCreated) continue;
            try
            {
                var inst = await lazy.Value.ConfigureAwait(false);
                await inst.Multiplexer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Error disposing Redis multiplexer"); }
        }
    }
}

/// <summary>
/// One cached connection + its detected version + whether it's clustered. Tools accept this
/// in place of the raw multiplexer so they don't all repeat the version-detection dance.
/// </summary>
public sealed class RedisInstance
{
    public RedisInstance(RedisServerEntry entry, ConnectionMultiplexer mux, string version, bool isCluster)
    {
        Entry = entry;
        Multiplexer = mux;
        VersionRaw = version;
        Version = ParseVersion(version);
        IsCluster = isCluster;
    }

    public RedisServerEntry Entry { get; }
    public ConnectionMultiplexer Multiplexer { get; }
    public string VersionRaw { get; }
    public Version Version { get; }
    public bool IsCluster { get; }

    /// <summary>Use the entry's configured DB unless cluster (cluster always uses 0).</summary>
    public IDatabase Db() => IsCluster ? Multiplexer.GetDatabase() : Multiplexer.GetDatabase(Entry.Database);

    /// <summary>Pick a server to send admin commands at. For cluster, any node will do for read-only INFO.</summary>
    public IServer FirstServer()
    {
        var ep = Multiplexer.GetEndPoints().FirstOrDefault()
            ?? throw new InvalidOperationException("No reachable Redis endpoint.");
        return Multiplexer.GetServer(ep);
    }

    public bool HasFeature(string feature) => feature switch
    {
        // 7.4 added field-level TTL (HEXPIRE, HPERSIST, etc.) and CLIENT NO-TOUCH.
        "hash-field-ttl" => Version >= new Version(7, 4),
        // OBJECT FREQ requires maxmemory-policy=allkeys-lfu — orthogonal to version, but the command itself is 4.0+.
        "object-freq" => Version >= new Version(4, 0),
        // FUNCTION LIST (Redis Functions) — 7.0+.
        "function-list" => Version >= new Version(7, 0),
        _ => true,
    };

    private static Version ParseVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new Version(0, 0);
        var parts = raw.Split('.', '-');
        return parts.Length switch
        {
            >= 2 when int.TryParse(parts[0], out var maj) && int.TryParse(parts[1], out var min) =>
                parts.Length >= 3 && int.TryParse(parts[2], out var patch) ? new Version(maj, min, patch) : new Version(maj, min),
            >= 1 when int.TryParse(parts[0], out var maj) => new Version(maj, 0),
            _ => new Version(0, 0),
        };
    }
}
