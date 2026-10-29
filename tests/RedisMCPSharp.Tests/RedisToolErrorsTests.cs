using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RedisMCPSharp.Tools;
using StackExchange.Redis;
using Xunit;

namespace RedisMCPSharp.Tests;

/// <summary>
/// Cover for issue #4: Redis' own error text never reached the caller.
/// </summary>
public sealed class RedisToolErrorsTests
{
    private static JsonElement Parse(string? json)
    {
        Assert.NotNull(json);
        return JsonDocument.Parse(json!).RootElement.Clone();
    }

    [Fact]
    public void AServerErrorKeepsTheServersOwnText()
    {
        var error = Parse(RedisToolErrors.Describe(
            "redis_execute", new RedisServerException("ERR wrong number of arguments for 'get' command")));

        Assert.False(error.GetProperty("ok").GetBoolean());
        Assert.Equal("redis_error", error.GetProperty("code").GetString());
        Assert.Contains("ERR wrong number of arguments for 'get' command", error.GetProperty("error").GetString());
        Assert.Equal("redis_execute", error.GetProperty("tool").GetString());
    }

    [Fact]
    public void WrongTypeNamesTheActualTypeAndTheToolThatReadsIt()
    {
        var error = Parse(RedisToolErrors.Describe(
            "redis_hgetall",
            new RedisServerException("WRONGTYPE Operation against a key holding the wrong kind of value"),
            key: "scoreboardeventlist",
            actualType: "string"));

        Assert.Equal("wrong_type", error.GetProperty("code").GetString());
        var message = error.GetProperty("error").GetString();
        Assert.Contains("WRONGTYPE", message);
        Assert.Contains("holds a string", message);
        Assert.Contains("redis_get", message);
        Assert.Equal("string", error.GetProperty("detail").GetProperty("actualType").GetString());
    }

    [Fact]
    public void WrongTypeWithoutALookupStillSaysWrongType()
    {
        // The TYPE lookup is best effort; when it fails the server's text alone must survive.
        var error = Parse(RedisToolErrors.Describe(
            "redis_hgetall", new RedisServerException("WRONGTYPE Operation against a key holding the wrong kind of value")));

        Assert.Equal("wrong_type", error.GetProperty("code").GetString());
        Assert.StartsWith("WRONGTYPE", error.GetProperty("error").GetString());
    }

    [Fact]
    public void ClientSideRefusalsAreSurfacedToo()
    {
        var error = Parse(RedisToolErrors.Describe(
            "redis_server_info", new RedisCommandException("This operation is not available unless admin mode is enabled: INFO")));

        Assert.Equal("redis_command_refused", error.GetProperty("code").GetString());
        Assert.Contains("admin mode", error.GetProperty("error").GetString());
    }

    [Fact]
    public void ConnectionAndTimeoutFailuresHaveTheirOwnCodes()
    {
        Assert.Equal("redis_unavailable", Parse(RedisToolErrors.Describe(
            "redis_get", new RedisConnectionException(ConnectionFailureType.UnableToConnect, "No connection"))).GetProperty("code").GetString());
        Assert.Equal("redis_timeout", Parse(RedisToolErrors.Describe(
            "redis_get", new RedisTimeoutException("Timeout", CommandStatus.Sent))).GetProperty("code").GetString());
    }

    [Fact]
    public void NonRedisExceptionsAreLeftToMcpFab()
    {
        // MCPFab owns the policy for everything else, including not leaking arbitrary messages.
        Assert.Null(RedisToolErrors.Describe("redis_get", new InvalidOperationException("read-only")));
        Assert.Null(RedisToolErrors.Describe("redis_get", new IOException("C:\\secret\\path")));
    }

#pragma warning disable MCPEXP002
    [Fact]
    public async Task TheFilterTurnsARedisExceptionIntoAnErrorResultEndToEnd()
    {
        // Through a real McpServer, so the wiring is covered and not just the formatting: without the
        // filter the SDK replies "An error occurred invoking 'redis_hgetall'." and nothing else.
        var c2s = new Pipe();
        var s2c = new Pipe();
        var options = new McpServerOptions
        {
            ToolCollection =
            [
                McpServerTool.Create(
                    (string key) => { throw new RedisServerException("ERR wrong number of arguments for 'get' command"); },
                    new() { Name = "redis_execute" }),
                McpServerTool.Create(
                    () => { throw new FormatException("not ours"); },
                    new() { Name = "other" }),
            ],
        };
        options.Filters.Request.CallToolFilters.Add(RedisToolErrors.Create(NullLoggerFactory.Instance));

        await using var server = McpServer.Create(
            new StreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream()), options);
        var running = server.RunAsync();
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()));

        var result = await client.CallToolAsync("redis_execute", new Dictionary<string, object?> { ["key"] = "k" });
        Assert.True(result.IsError);
        var error = Parse(((TextContentBlock)result.Content[0]).Text);
        Assert.Equal("redis_error", error.GetProperty("code").GetString());
        Assert.Contains("wrong number of arguments", error.GetProperty("error").GetString());

        // Anything else carries on outward untouched.
        var other = await client.CallToolAsync("other");
        Assert.True(other.IsError);
        Assert.DoesNotContain("\"code\"", ((TextContentBlock)other.Content[0]).Text);
    }
#pragma warning restore MCPEXP002
}
