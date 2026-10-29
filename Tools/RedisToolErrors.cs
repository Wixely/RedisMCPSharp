using System.Text.Json;
using DnaX.MCPFab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RedisMCPSharp.Services;
using StackExchange.Redis;

namespace RedisMCPSharp.Tools;

/// <summary>
/// Turns StackExchange.Redis exceptions escaping a tool into the shared error envelope, with the
/// server's own text.
/// </summary>
/// <remarks>
/// <para>
/// MCPFab already surfaces deliberate refusals (InvalidOperationException, ArgumentException, ...)
/// but reports anything else by type alone, because an arbitrary message may leak secrets. For this
/// server that left the most useful failures opaque: "WRONGTYPE ...", "ERR wrong number of
/// arguments for 'get' command" and "ERR unknown command" are written by Redis for the caller, and
/// an agent that cannot see them cannot correct itself (issue #4).
/// </para>
/// <para>
/// One filter rather than a try/catch per tool, so every current and future tool is covered. It is
/// registered after MCPFab's, which makes it the inner of the two: Redis exceptions stop here,
/// everything else carries on outward to MCPFab's handling unchanged.
/// </para>
/// </remarks>
internal static class RedisToolErrors
{
    /// <summary>The cheapest command that reads a key of each type, for the WRONGTYPE hint.</summary>
    private static readonly Dictionary<string, string> ReaderFor = new(StringComparer.Ordinal)
    {
        ["string"] = "redis_get",
        ["list"] = "redis_lrange",
        ["hash"] = "redis_hgetall",
        ["set"] = "redis_smembers",
        ["zset"] = "redis_zrange",
        ["stream"] = "redis_xrange",
    };

    /// <summary>
    /// Renders a Redis failure as the error envelope, or returns null for an exception this server
    /// has nothing to add to.
    /// </summary>
    /// <param name="tool">Tool name, echoed so a batch of calls can be told apart.</param>
    /// <param name="exception">What the tool threw.</param>
    /// <param name="key">The call's <c>key</c> argument, when it had one.</param>
    /// <param name="actualType">The key's real type, when it could be looked up.</param>
    internal static string? Describe(string tool, Exception exception, string? key = null, string? actualType = null)
    {
        var envelope = exception switch
        {
            RedisServerException ex when ex.Message.StartsWith("WRONGTYPE", StringComparison.Ordinal) =>
                McpJson.Error("wrong_type", WrongTypeMessage(ex.Message, key, actualType),
                    McpJson.Object().Set("key", key).Set("actualType", actualType)),
            RedisServerException ex => McpJson.Error("redis_error", $"Redis rejected the command: {ex.Message}"),
            RedisCommandException ex => McpJson.Error("redis_command_refused", ex.Message),
            RedisTimeoutException ex => McpJson.Error("redis_timeout", ex.Message),
            RedisConnectionException ex => McpJson.Error("redis_unavailable", ex.Message),
            _ => null,
        };
        return envelope?.Set("tool", tool).ToJsonString();
    }

    private static string WrongTypeMessage(string serverText, string? key, string? actualType)
    {
        if (key is null || actualType is null) return serverText;
        if (actualType == "none") return $"{serverText}. Key '{key}' no longer exists.";
        var hint = ReaderFor.TryGetValue(actualType, out var reader) ? $" Read it with {reader}." : "";
        return $"{serverText}. Key '{key}' holds a {actualType}.{hint}";
    }

    /// <summary>
    /// Builds the call-tool filter. MCPEXP002 is suppressed here for the same reason MCPFab gives:
    /// the filter pipeline is the only hook that reaches every tool, and keeping the suppression in
    /// this file keeps the dependency on an experimental API greppable.
    /// </summary>
#pragma warning disable MCPEXP002
    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger(typeof(RedisToolErrors).FullName!);

        return next => async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is RedisException or RedisCommandException or RedisTimeoutException)
            {
                string tool = context.Params?.Name ?? "unknown";
                logger.LogError(exception, "Tool {Tool} failed.", tool);

                string? key = StringArgument(context.Params, "key");
                string? actualType = exception is RedisServerException && key is not null
                    ? await TryGetTypeAsync(context.Services, key, StringArgument(context.Params, "alias")).ConfigureAwait(false)
                    : null;

                string? text = Describe(tool, exception, key, actualType);
                if (text is null) throw;

                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = text }],
                };
            }
        };
    }
#pragma warning restore MCPEXP002

    private static string? StringArgument(CallToolRequestParams? request, string name) =>
        request?.Arguments is { } args && args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Best effort: one TYPE round-trip to name what the key really is.</summary>
    private static async Task<string?> TryGetTypeAsync(IServiceProvider? services, string key, string? alias)
    {
        try
        {
            var reg = services?.GetService<RedisRegistry>();
            if (reg is null) return null;
            var inst = await reg.GetAsync(alias).ConfigureAwait(false);
            return RedisTypeNames.Name(await inst.Db().KeyTypeAsync(key).ConfigureAwait(false));
        }
        catch
        {
            return null;
        }
    }
}
