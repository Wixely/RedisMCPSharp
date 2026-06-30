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
- **Safe by default.** `Redis:ReadOnly` is `true` out of the box. Write tools (`set`, `del`,
  `hset`, …) refuse until you flip it. Destructive admin ops (`FLUSHDB`, `CONFIG SET`, `DEBUG`,
  `FAILOVER`, raw `execute` against an unknown verb) are double-gated by `Redis:AllowDangerous`.

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
| `Redis:AllowDangerous` | `false` | `FLUSHDB`/`FLUSHALL`/`SHUTDOWN`/`DEBUG`/`FAILOVER`/`CONFIG SET`/`SCRIPT FLUSH`/`FUNCTION FLUSH` and raw `execute` against unwrapped admin verbs. |
| `Redis:CommandTimeoutMs` | `5000` | Per-command sync/async timeout passed to StackExchange.Redis. |
| `Redis:MaxItems` | `1000` | Cap on keys / fields / members returned in one tool call. |
| `Redis:MaxValueChars` | `8000` | Cap on string-value chars before truncation. |
| `Redis:DefaultScanPageSize` | `100` | Hint for `SCAN COUNT`. Bigger = fewer round-trips, larger payloads. |

## Tool surface

Every tool accepts an optional `alias` parameter — omit it to use the default alias.

### Discovery
- `list_servers` — **call first.** Every configured alias + description + default. The handle
  every other tool expects.
- `test_connection` — `PING` the alias and report latency.
- `server_info` — `INFO`, optionally narrowed to a section (`memory`, `replication`, `stats`, …).
- `version_info` — detected version + cluster shape + per-feature capability flags.
- `dbsize` — `DBSIZE` (summed across primaries on cluster).

### Keys
- `exists`, `type`, `ttl`, `expire`, `persist`, `rename`, `del`
- `object_encoding`, `object_idletime` — internal representation + idle time
- `scan` — non-blocking key scan, optionally filtered by type. Cluster-aware.
- `scan_with_values` — scan + value previews (string head, list head, hash slice, set slice, zset slice)
- `search_keys` — alias for `scan` with a glob

### Strings
- `get`, `mget`, `set`, `mset`, `append`, `incrby`
- `get_deserialised` — fetch + auto-detect JSON / BSON / Protobuf / UTF-8 / binary
- `detect_format` — sniff without decoding (cheap survey)

### Collections
- HASH: `hget`, `hgetall`, `hkeys`, `hset`, `hdel`
- LIST: `lrange`, `llen`, `lpush`, `rpush`
- SET: `smembers`, `sismember`, `sadd`, `srem`
- ZSET (sorted set): `zrange`, `zadd`, `zrem`
- STREAM: `xrange`, `xlen`, `xadd`

### Cluster
- `cluster_info`, `cluster_nodes`, `cluster_slots`, `cluster_keyslot`, `cluster_countkeysinslot`

### Diagnostics
- `slowlog_get`, `slowlog_len`, `slowlog_reset`
- `memory_usage`, `memory_stats`, `memory_doctor`
- `client_list`, `client_kill`
- `config_get`, `config_set`, `config_resetstat`
- `latency_history`, `latency_latest`, `latency_reset`
- `lastsave`, `command_count`, `module_list`, `function_list`
- `debug_object` *(dangerous — gated)*

### Raw / extensions
- `execute` — run any Redis verb with arguments. Safe-listed read commands always run; writes
  need `ReadOnly=false`, destructive admin verbs need `AllowDangerous=true`.
- `ft_list`, `ft_info`, `ft_search` — convenience wrappers around RediSearch `FT.*`.

Use `execute` directly for RedisJSON (`JSON.GET`, `JSON.SET`), RedisTimeSeries (`TS.*`),
RedisBloom (`BF.*`, `CF.*`, `CMS.*`, `TOPK.*`, `TDIGEST.*`) and any other module command.

## Multi-version notes

| Feature | Min Redis | Tool |
|---|---|---|
| `CLUSTER SHARDS` | 7.0 | `cluster_slots` (falls back to `CLUSTER SLOTS` < 7.0) |
| Functions (`FUNCTION LIST` etc.) | 7.0 | `function_list` (returns `supported: false` on older) |
| Hash-field TTL (`HEXPIRE`, `HPERSIST`, …) | 7.4 | Use `execute` — version flag exposed via `version_info.features.hashFieldTtl` |
| RediSearch / RedisJSON / RedisTimeSeries / RedisBloom | Module | `module_list` to confirm load, then `execute` (or `ft_*` helpers) |

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
