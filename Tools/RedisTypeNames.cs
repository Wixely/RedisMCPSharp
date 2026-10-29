using StackExchange.Redis;

namespace RedisMCPSharp.Tools;

/// <summary>
/// Translates between <see cref="RedisType"/> and the names Redis itself uses for TYPE.
/// </summary>
/// <remarks>
/// The enum's member names are not Redis' names: a sorted set is <c>SortedSet</c> in .NET and
/// <c>zset</c> on the wire. Comparing a caller's string to <c>RedisType.ToString()</c> therefore
/// never matched the documented <c>zset</c>, and an unknown value matched nothing at all and came
/// back as an empty - apparently successful - scan (issue #4).
/// </remarks>
internal static class RedisTypeNames
{
    private static readonly Dictionary<string, RedisType> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["string"] = RedisType.String,
        ["list"] = RedisType.List,
        ["hash"] = RedisType.Hash,
        ["set"] = RedisType.Set,
        ["zset"] = RedisType.SortedSet,
        ["stream"] = RedisType.Stream,
        ["vectorset"] = RedisType.VectorSet,
        // Accepted because it used to be the only spelling that worked; not advertised.
        ["sortedset"] = RedisType.SortedSet,
    };

    /// <summary>The values advertised to callers, in the order the error message lists them.</summary>
    public const string ValidValues = "string, list, hash, set, zset, stream, vectorset";

    /// <summary>The name Redis' own TYPE command would report.</summary>
    public static string Name(RedisType type) => type switch
    {
        RedisType.SortedSet => "zset",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Parses a type filter. Null or blank means "no filter"; anything unrecognised throws, naming
    /// the valid values, rather than silently matching zero keys.
    /// </summary>
    public static RedisType? ParseFilter(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        if (ByName.TryGetValue(type.Trim(), out var parsed)) return parsed;
        throw new ArgumentException($"Unknown type filter '{type}'. Valid values: {ValidValues}. Omit `type` to match every key.");
    }
}
