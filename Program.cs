using DnaX.MCPFab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RedisMCPSharp.Configuration;
using RedisMCPSharp.Services;

namespace RedisMCPSharp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Content root, the config chain, Kestrel binding, Windows service detection, Serilog,
        // authentication, /healthz, exception handlers and the shutdown path all come from
        // MCPFab. What remains below is only what is actually specific to this server.
        McpFabBuilder builder = McpFabHost.CreateBuilder(
            args,
            new McpFabProduct("RedisMCPSharp", "REDISMCP_", DefaultPort: 5713, LogFilePrefix: "redismcp"));

        builder.Services
            .AddOptions<RedisOptions>()
            .Bind(builder.Configuration.GetSection(RedisOptions.SectionName));
        builder.Services.AddSingleton<RedisRegistry>();

        // Single-engine product, so every tool class registers unconditionally. The registry
        // itself surfaces "No Redis servers configured" if Servers is empty at first call.
        builder.Mcp
            .WithTools<Tools.DiscoveryTools>()
            .WithTools<Tools.KeyTools>()
            .WithTools<Tools.StringTools>()
            .WithTools<Tools.CollectionTools>()
            .WithTools<Tools.ClusterTools>()
            .WithTools<Tools.DiagnosticTools>()
            .WithTools<Tools.ExecuteTool>();

        builder
            .Banner((services, banner) =>
            {
                RedisRegistry registry = services.GetRequiredService<RedisRegistry>();
                banner.Line("Read-only", registry.IsReadOnly.ToString());
                banner.Line("Allow destructive", registry.AllowDestructive.ToString());
                banner.Line("Default alias", registry.DefaultAlias ?? "(none - Servers list is empty)");
                banner.Line("Configured servers", registry.Servers.Count.ToString());

                foreach (RedisServerEntry entry in registry.Servers.Values)
                {
                    string description = string.IsNullOrEmpty(entry.Description) ? "" : $" ({entry.Description})";
                    banner.Detail($"  {entry.Alias} -> {MaskConnectionString(entry.ConnectionString)}{description}");
                }

                foreach (string problem in registry.ConfigurationProblems)
                {
                    banner.Warn(problem);
                }
            })
            .Health(services =>
            {
                RedisRegistry registry = services.GetRequiredService<RedisRegistry>();
                return McpJson.Object()
                    .Set("readOnly", registry.IsReadOnly)
                    .Set("allowDestructive", registry.AllowDestructive)
                    .Set("defaultAlias", registry.DefaultAlias)
                    .Set("servers", McpJson.Array(
                        registry.Servers.Values,
                        entry => McpJson.Object()
                            .Set("alias", entry.Alias)
                            .Set("description", entry.Description)
                            .Set("versionOverride", string.IsNullOrEmpty(entry.VersionOverride) ? null : entry.VersionOverride)));
            });

        return await builder.Build().RunAsync().ConfigureAwait(false);
    }

    /// <summary>Strips <c>password=</c> from a connection string before it reaches the log.</summary>
    private static string MaskConnectionString(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return "(empty)";
        }

        string[] parts = connectionString.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].TrimStart().StartsWith("password=", StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = "password=***";
            }
        }

        return string.Join(',', parts);
    }
}
