using System.Text.Json.Serialization;

namespace RedisMCPSharp.Tools;

/// <summary>
/// Schema metadata for tool parameters whose types the MCP SDK cannot discover once trimmed.
/// </summary>
/// <remarks>
/// <para>
/// The SDK builds each tool's JSON input schema from its parameter types, resolving them through
/// a fixed chain of two source-generated contexts. A parameter typed
/// <see cref="Dictionary{TKey, TValue}"/> is in neither, so under PublishTrimmed the server throws
/// NotSupportedException while the DI container is built - before Kestrel starts. Not one broken
/// tool: no server at all.
/// </para>
/// <para>
/// This is the half of the JSON story that source generation is right for. Tool inputs have named,
/// fixed types declared in the signature, so a context costs four attributes and is exact. Tool
/// outputs are ad hoc and boxed, which is why those use McpJson instead.
/// </para>
/// </remarks>
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
internal sealed partial class RedisJsonContext : JsonSerializerContext;
