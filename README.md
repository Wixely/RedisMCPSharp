# RedisMCPSharp

An MCP (Model Context Protocol) server that gives an agent first-class access to one or many
Redis deployments — read, write, search, deserialise, diagnose. Built on the MCPSharp product
line conventions.

- **MIT-licensed end to end.** Every runtime dependency is MIT-permissive (StackExchange.Redis,
  Serilog, Microsoft.Extensions.*). JSON / BSON / Protobuf deserialisation is done with
  hand-rolled decoders so there are zero copyleft transitive deps.
- **Multi-version aware.** Supports Redis 7.0 and up; auto-detects the server version and routes
  to the right syntax (e.g. `CLUSTER SHARDS` on 7.0+, falls back to `CLUSTER SLOTS` on older
  builds; hash-field TTL surfaced on 7.4+).
- **Cluster aware.** Connection strings with multiple endpoints are parsed as a cluster;
  `scan_*` tools fan out across every primary node.
- **Multi-endpoint.** Configure as many Redis servers as you like under different aliases —
  `prod-cache`, `staging-cluster`, `local`, etc. The agent picks one per call with `alias`.
- **Safe by default.** `Redis:ReadOnly` is `true` out of the box. Write tools (`redis_set`,
  `redis_del`, `redis_hset`, …) refuse until you flip it. Destructive admin ops (`FLUSHDB`,
  `CONFIG SET`, `DEBUG`, `FAILOVER`, raw `redis_execute` against an unknown verb) are
  double-gated by `Redis:AllowDangerous`.

Default port: **5713**. MCP endpoint: `http://localhost:5713/mcp`.

## Build & run

```pwsh
dotnet build
dotnet run --project RedisMCPSharp
```

You should see a startup block like:

```
RedisMCPSharp startup
  Endpoint: http://localhost:5713/mcp
  Transport: HTTP
  Mode: Console
  Read-only: True
  Allow dangerous: False
  Default alias: local
  Configured servers: 1
    local → localhost:6379 (Local Redis (replace with your own endpoints).)
```

Hit `http://localhost:5713/healthz` to confirm the server is up and see which aliases are
registered.

## Running in Docker

```sh
docker run --rm -p 5713:5713 \
  -e REDISMCP_Redis__Servers__0__Alias=local \
  -e REDISMCP_Redis__Servers__0__ConnectionString=redis-host:6379 \
  -e REDISMCP_Redis__ReadOnly=true \
  -e REDISMCP_Server__Password=change-me \
  ghcr.io/wixely/redismcpsharp:latest
```

The image supports `linux/amd64` and `linux/arm64`. Read-only mode is on by default; set
`REDISMCP_Redis__ReadOnly=false` (and `REDISMCP_Redis__AllowDangerous=true` for FLUSH /
CONFIG SET / DEBUG / FAILOVER) only when you want write or admin tools. Use the
`Redis__Servers__N__*` indexing pattern (where `N` is `0`, `1`, …) to declare multiple
endpoints without a JSON file.

## Running as a Windows Service

Publish a single-file build, drop it under `C:\Services\RedisMCPSharp\`, then register with
SCM:

```powershell
sc.exe create RedisMCPSharp `
    binPath= "C:\Services\RedisMCPSharp\RedisMCPSharp.exe" `
    start= auto `
    DisplayName= "Redis MCP (C#)"
sc.exe description RedisMCPSharp "MCP server for Redis."
sc.exe start RedisMCPSharp
```

Put connection strings in `C:\Services\RedisMCPSharp\RedisMCPSharp.Local.json` (or set
`REDISMCP_Redis__Servers__0__ConnectionString` as a machine-level env var) — never in
`RedisMCPSharp.json`, which is checked in.

To remove:

```powershell
sc.exe stop RedisMCPSharp
sc.exe delete RedisMCPSharp
```

The service hosts the same HTTP endpoint as the console runner — `http://localhost:5713/mcp`
by default. Set `REDISMCP_Server__Host=0.0.0.0` if you want to bind to all interfaces.

## Configuration

`RedisMCPSharp.json` (next to the binary) is the canonical config. You can also override every
field via environment variables (`REDISMCP_Redis__Servers__0__ConnectionString=...`) or a
`RedisMCPSharp.Local.json` for machine-specific overrides.

### Minimal — single Redis

```json
{
  "Redis": {
    "ReadOnly": true,
    "Servers": [
      {
        "Alias": "local",
        "ConnectionString": "localhost:6379",
        "Database": 0,
        "Description": "Dev box."
      }
    ]
  }
}
```

### Multiple environments

```json
{
  "Redis": {
    "ReadOnly": false,
    "DefaultAlias": "staging",
    "Servers": [
      {
        "Alias": "local",
        "ConnectionString": "localhost:6379",
        "Description": "Dev box."
      },
      {
        "Alias": "staging",
        "ConnectionString": "redis-staging.internal:6379,password=secret",
        "Database": 0,
        "Description": "Shared staging cache."
      },
      {
        "Alias": "prod-cache",
        "ConnectionString": "redis-prod.internal:6380,password=secret,ssl=true,abortConnect=false",
        "Description": "Read-replica fronted production cache.",
        "VersionOverride": "7.2"
      }
    ]
  }
}
```

### Cluster

Just list every node in the connection string — StackExchange.Redis handles cluster discovery
transparently.

```json
{
  "Redis": {
    "Servers": [
      {
        "Alias": "prod-cluster",
        "ConnectionString": "node1:7000,node2:7000,node3:7000,abortConnect=false",
        "Description": "3-shard production cluster."
      }
    ]
  }
}
```

Cluster-only commands (`cluster_info`, `cluster_nodes`, `cluster_slots`, `cluster_keyslot`,
`cluster_countkeysinslot`) work against any node. `scan` and `scan_with_values` iterate every
primary.

### Auth header

Set `Server.Password` to require an `X-MCP-Password` (or `Authorization: Bearer`) header on
`/mcp` requests. Empty string disables the gate.

### Safety knobs

| Key | Default | What it gates |
|---|---|---|
| `Redis:ReadOnly` | `true` | Every mutating tool. Flip to `false` to allow writes. |
| `Redis:AllowDangerous` | `false` | `FLUSHDB`/`FLUSHALL`/`SHUTDOWN`/`DEBUG`/`FAILOVER`/`CONFIG SET`/`SCRIPT FLUSH`/`FUNCTION FLUSH` and raw `redis_execute` against unwrapped admin verbs. |
| `Redis:CommandTimeoutMs` | `5000` | Per-command sync/async timeout passed to StackExchange.Redis. |
| `Redis:MaxItems` | `1000` | Cap on keys / fields / members returned in one tool call. |
| `Redis:MaxValueChars` | `8000` | Cap on string-value chars before truncation. |
| `Redis:DefaultScanPageSize` | `100` | Hint for `SCAN COUNT`. Bigger = fewer round-trips, larger payloads. |

## Tool surface

Every tool name is prefixed with `redis_` to avoid collisions when an agent has multiple MCP
servers attached (verbs like `get` / `set` / `execute` are too generic on their own). Every
tool accepts an optional `alias` parameter — omit it to use the default alias.

### Discovery
- `redis_list_servers` — **call first.** Every configured alias + description + default. The
  handle every other tool expects.
- `redis_test_connection` — `PING` the alias and report latency.
- `redis_server_info` — `INFO`, optionally narrowed to a section (`memory`, `replication`, `stats`, …).
- `redis_version_info` — detected version + cluster shape + per-feature capability flags.
- `redis_dbsize` — `DBSIZE` (summed across primaries on cluster).

### Keys
- `redis_exists`, `redis_type`, `redis_ttl`, `redis_expire`, `redis_persist`, `redis_rename`, `redis_del`
- `redis_object_encoding`, `redis_object_idletime` — internal representation + idle time
- `redis_scan` — non-blocking key scan, optionally filtered by type. Cluster-aware.
- `redis_scan_with_values` — scan + value previews (string head, list head, hash slice, set slice, zset slice)
- `redis_search_keys` — alias for `redis_scan` with a glob

### Strings
- `redis_get`, `redis_mget`, `redis_set`, `redis_mset`, `redis_append`, `redis_incrby`
- `redis_get_deserialised` — fetch + auto-detect JSON / BSON / Protobuf / UTF-8 / binary
- `redis_detect_format` — sniff without decoding (cheap survey)

### Collections
- HASH: `redis_hget`, `redis_hgetall`, `redis_hkeys`, `redis_hset`, `redis_hdel`
- LIST: `redis_lrange`, `redis_llen`, `redis_lpush`, `redis_rpush`
- SET: `redis_smembers`, `redis_sismember`, `redis_sadd`, `redis_srem`
- ZSET (sorted set): `redis_zrange`, `redis_zadd`, `redis_zrem`
- STREAM: `redis_xrange`, `redis_xlen`, `redis_xadd`

### Cluster
- `redis_cluster_info`, `redis_cluster_nodes`, `redis_cluster_slots`, `redis_cluster_keyslot`, `redis_cluster_countkeysinslot`

### Diagnostics
- `redis_slowlog_get`, `redis_slowlog_len`, `redis_slowlog_reset`
- `redis_memory_usage`, `redis_memory_stats`, `redis_memory_doctor`
- `redis_client_list`, `redis_client_kill`
- `redis_config_get`, `redis_config_set`, `redis_config_resetstat`
- `redis_latency_history`, `redis_latency_latest`, `redis_latency_reset`
- `redis_lastsave`, `redis_command_count`, `redis_module_list`, `redis_function_list`
- `redis_debug_object` *(dangerous — gated)*

### Raw / extensions
- `redis_execute` — run any Redis verb with arguments. Safe-listed read commands always run; writes
  need `ReadOnly=false`, destructive admin verbs need `AllowDangerous=true`.
- `redis_ft_list`, `redis_ft_info`, `redis_ft_search` — convenience wrappers around RediSearch `FT.*`.

Use `redis_execute` directly for RedisJSON (`JSON.GET`, `JSON.SET`), RedisTimeSeries (`TS.*`),
RedisBloom (`BF.*`, `CF.*`, `CMS.*`, `TOPK.*`, `TDIGEST.*`) and any other module command.

## Multi-version notes

| Feature | Min Redis | Tool |
|---|---|---|
| `CLUSTER SHARDS` | 7.0 | `redis_cluster_slots` (falls back to `CLUSTER SLOTS` < 7.0) |
| Functions (`FUNCTION LIST` etc.) | 7.0 | `redis_function_list` (returns `supported: false` on older) |
| Hash-field TTL (`HEXPIRE`, `HPERSIST`, …) | 7.4 | Use `redis_execute` — version flag exposed via `redis_version_info.features.hashFieldTtl` |
| RediSearch / RedisJSON / RedisTimeSeries / RedisBloom | Module | `redis_module_list` to confirm load, then `redis_execute` (or `redis_ft_*` helpers) |

If you front Redis with a proxy (Envoy, Twemproxy, Cluster Proxy) that masks the upstream
version, set `VersionOverride` on the server entry to force a specific version's behaviour.

## Deserialisation

`get_deserialised` and `detect_format` sniff the byte payload:

- **JSON** — leading `{`/`[`/`"` after whitespace, parsed with `System.Text.Json.JsonNode`.
- **BSON** — first 4 bytes match the BSON document length; type-byte + cstring-key + value loop.
- **Protobuf** — sequence of valid varint tags with known wire types; nested messages decoded recursively.
- **UTF-8 text** — valid UTF-8 with no NULs.
- **Binary** — everything else; returned as base64 with a length annotation.

Pass `format=json|bson|protobuf|text|binary` to force a decoder.

## Licensing

MIT. See [LICENSE](LICENSE).

Runtime dependencies (all MIT or MIT-compatible permissive):
- [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis) — MIT
- [Serilog](https://serilog.net/) — Apache 2.0 (compatible — note that MIT-only redistributions
  remain MIT-clean because Apache 2.0 is a permissive licence; if you need pure MIT
  redistribution you can drop Serilog and route logging elsewhere)
- Microsoft.Extensions.* — MIT
- [ModelContextProtocol.AspNetCore](https://github.com/modelcontextprotocol/csharp-sdk) — MIT

BSON / Protobuf decoders are hand-written specifically to avoid Apache-licensed
`MongoDB.Bson` and `Google.Protobuf`. They are intentionally not fully-featured — they cover
the inspection / display use case that an agent needs, not round-trip-fidelity serialisation.
