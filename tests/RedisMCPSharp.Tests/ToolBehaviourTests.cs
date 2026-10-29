using System.Text.Json;
using RedisMCPSharp.Services;
using RedisMCPSharp.Tools;
using StackExchange.Redis;
using Xunit;

namespace RedisMCPSharp.Tests;

/// <summary>
/// Cover for issue #4's silent failures: TTL, the scan type filter, unknown verbs and INFO.
/// </summary>
public sealed class ToolBehaviourTests
{
    // --- redis_ttl ------------------------------------------------------------------------------

    [Fact]
    public void AMissingKeyIsReportedAsMissing()
    {
        // Before: {"key":"does-not-exist"} - the TTL fields vanished, same as for a key with no TTL.
        var ttl = JsonDocument.Parse(KeyTools.TtlPayload(null, "does-not-exist", -2).ToJsonString()).RootElement;

        Assert.False(ttl.GetProperty("exists").GetBoolean());
        Assert.Equal(-2, ttl.GetProperty("ttlSeconds").GetDouble());
        Assert.Equal(-2, ttl.GetProperty("ttlMs").GetInt64());
    }

    [Fact]
    public void AKeyWithNoExpiryIsDistinctFromAMissingOne()
    {
        var ttl = JsonDocument.Parse(KeyTools.TtlPayload(null, "k", -1).ToJsonString()).RootElement;

        Assert.True(ttl.GetProperty("exists").GetBoolean());
        Assert.Equal(-1, ttl.GetProperty("ttlSeconds").GetDouble());
        Assert.Equal(-1, ttl.GetProperty("ttlMs").GetInt64());
    }

    [Fact]
    public void ARealTtlIsReportedInBothUnits()
    {
        var ttl = JsonDocument.Parse(KeyTools.TtlPayload(null, "k", 1500).ToJsonString()).RootElement;

        Assert.True(ttl.GetProperty("exists").GetBoolean());
        Assert.Equal(1.5, ttl.GetProperty("ttlSeconds").GetDouble());
        Assert.Equal(1500, ttl.GetProperty("ttlMs").GetInt64());
    }

    // --- scan type filter -----------------------------------------------------------------------

    [Theory]
    [InlineData("zset", RedisType.SortedSet)]
    [InlineData("ZSET", RedisType.SortedSet)]
    [InlineData("sortedset", RedisType.SortedSet)]
    [InlineData("string", RedisType.String)]
    [InlineData("hash", RedisType.Hash)]
    [InlineData(" list ", RedisType.List)]
    [InlineData("set", RedisType.Set)]
    [InlineData("stream", RedisType.Stream)]
    public void TheDocumentedTypeNamesParse(string input, RedisType expected)
    {
        Assert.Equal(expected, RedisTypeNames.ParseFilter(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoFilterMeansEveryKey(string? input)
    {
        Assert.Null(RedisTypeNames.ParseFilter(input));
    }

    [Fact]
    public void AnUnknownTypeIsAnErrorListingTheValidValues()
    {
        // Before: an empty, success-shaped scan that read as "no keys of that type".
        var ex = Assert.Throws<ArgumentException>(() => RedisTypeNames.ParseFilter("notatype"));

        Assert.Contains("notatype", ex.Message);
        Assert.Contains("zset", ex.Message);
        Assert.Contains("stream", ex.Message);
    }

    [Fact]
    public void ASortedSetIsReportedAsZsetLikeRedisDoes()
    {
        Assert.Equal("zset", RedisTypeNames.Name(RedisType.SortedSet));
        Assert.Equal("string", RedisTypeNames.Name(RedisType.String));
        Assert.Equal("none", RedisTypeNames.Name(RedisType.None));
    }

    // --- redis_execute unknown verb -------------------------------------------------------------

    [Fact]
    public void CommandInfoWithANilEntryMeansUnknown()
    {
        var unknown = RedisResult.Create(new[] { RedisResult.Create(RedisValue.Null) });
        var known = RedisResult.Create(new[] { RedisResult.Create(new[] { RedisResult.Create((RedisValue)"get") }) });

        Assert.True(ExecuteTool.IsUnknownCommand(unknown));
        Assert.False(ExecuteTool.IsUnknownCommand(known));
    }

    [Fact]
    public void AnUnexpectedCommandInfoReplyIsNotTakenAsUnknown()
    {
        // Falls back to the read-only refusal rather than guessing.
        Assert.False(ExecuteTool.IsUnknownCommand(RedisResult.Create((RedisValue)"OK")));
        Assert.False(ExecuteTool.IsUnknownCommand(RedisResult.Create(Array.Empty<RedisResult>())));
    }

    // --- redis_server_info ----------------------------------------------------------------------

    [Fact]
    public void InfoTextParsesIntoSections()
    {
        const string raw = "# Server\r\nredis_version:7.2.5\r\nredis_mode:cluster\r\n\r\n# Keyspace\r\ndb0:keys=3,expires=0,avg_ttl=0\r\n";

        var sections = RedisInfo.Parse(raw);

        Assert.Equal(new[] { "Server", "Keyspace" }, sections.Select(s => s.Key));
        Assert.Equal("7.2.5", sections[0].Value["redis_version"]);
        Assert.Equal("cluster", sections[0].Value["redis_mode"]);
        // Only the first colon splits: values may contain their own.
        Assert.Equal("keys=3,expires=0,avg_ttl=0", sections[1].Value["db0"]);
    }

    [Fact]
    public void AValueContainingAColonIsKeptWhole()
    {
        var sections = RedisInfo.Parse("# Server\nexecutable:C:\\redis\\redis-server.exe\n");

        Assert.Equal("C:\\redis\\redis-server.exe", sections[0].Value["executable"]);
    }

    [Fact]
    public void AnEmptyReplyHasNoSections()
    {
        Assert.Empty(RedisInfo.Parse(""));
        Assert.Empty(RedisInfo.Parse(null));
    }
}
