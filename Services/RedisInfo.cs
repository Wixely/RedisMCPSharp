namespace RedisMCPSharp.Services;

/// <summary>
/// Parses the text reply of INFO into named sections.
/// </summary>
/// <remarks>
/// <c>IServer.InfoAsync</c> does this for us but is flagged admin-only by StackExchange.Redis, so
/// it throws unless the multiplexer has <c>AllowAdmin=true</c> - and redis_server_info failed on
/// every alias (issue #4). AllowAdmin would also unlock FLUSHALL, SHUTDOWN and friends through the
/// typed API, so instead INFO goes through raw Execute like every other diagnostic, and the reply
/// is parsed here. The layout is "# Section" headers followed by "key:value" lines.
/// </remarks>
internal static class RedisInfo
{
    public static IReadOnlyList<KeyValuePair<string, Dictionary<string, string>>> Parse(string? raw)
    {
        var sections = new List<KeyValuePair<string, Dictionary<string, string>>>();
        if (string.IsNullOrEmpty(raw)) return sections;

        Dictionary<string, string>? current = null;
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                sections.Add(new(line.TrimStart('#').Trim(), current));
                continue;
            }

            var idx = line.IndexOf(':');
            if (idx <= 0) continue;

            // A reply with no header (seen from some proxies) still gets a home.
            if (current is null)
            {
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                sections.Add(new("", current));
            }
            current[line[..idx]] = line[(idx + 1)..];
        }
        return sections;
    }
}
