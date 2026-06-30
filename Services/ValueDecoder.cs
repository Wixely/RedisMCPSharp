using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RedisMCPSharp.Services;

/// <summary>
/// Best-effort content-type detection + decoding for opaque Redis string values. Returns a
/// structured shape the agent can read directly instead of staring at a base64 blob. All
/// decoders are hand-rolled here so we don't pull in non-MIT NuGet packages — JSON uses the
/// BCL <c>System.Text.Json</c>, BSON parses the documented binary layout, Protobuf scans the
/// wire format (field tags + lengths) without needing the .proto schema.
/// </summary>
public static class ValueDecoder
{
    public enum DetectedKind { Unknown, JsonObject, JsonArray, JsonScalar, Bson, Protobuf, Utf8Text, Binary }

    public sealed record Decoded(DetectedKind Kind, object? Value, string? Note);

    /// <summary>
    /// Sniff the bytes and return a parsed representation when we recognise the format.
    /// <paramref name="forceFormat"/> overrides auto-detect ("json" | "bson" | "protobuf" | "text" | "binary").
    /// </summary>
    public static Decoded Decode(byte[] data, string? forceFormat = null)
    {
        if (data.Length == 0) return new Decoded(DetectedKind.Unknown, null, "empty value");

        var format = forceFormat?.Trim().ToLowerInvariant();

        if (format is null or "" or "auto")
        {
            // JSON: usually starts with '{' / '[' / '"' / digit / 'true' / 'false' / 'null'.
            if (LooksLikeJson(data) && TryDecodeJson(data, out var json)) return json!;
            // BSON: 4-byte LE length, ends with 0x00. Length must match the actual byte count.
            if (LooksLikeBson(data) && TryDecodeBson(data, out var bson)) return bson!;
            // Protobuf: harder to detect; fall back to it only when first byte's wire-type bits are valid.
            if (LooksLikeProtobuf(data) && TryDecodeProtobuf(data, out var pb)) return pb!;
            // Plain UTF-8 text.
            if (TryDecodeUtf8(data, out var text)) return new Decoded(DetectedKind.Utf8Text, text, null);
            return new Decoded(DetectedKind.Binary, BinarySummary(data), $"{data.Length} bytes — no known format detected");
        }

        return format switch
        {
            "json" => TryDecodeJson(data, out var j) ? j! : new Decoded(DetectedKind.Unknown, null, "JSON parse failed"),
            "bson" => TryDecodeBson(data, out var b) ? b! : new Decoded(DetectedKind.Unknown, null, "BSON parse failed"),
            "protobuf" or "proto" => TryDecodeProtobuf(data, out var p) ? p! : new Decoded(DetectedKind.Unknown, null, "Protobuf decode failed"),
            "text" or "utf8" => TryDecodeUtf8(data, out var t)
                ? new Decoded(DetectedKind.Utf8Text, t, null)
                : new Decoded(DetectedKind.Binary, BinarySummary(data), "not valid UTF-8"),
            "binary" or "bytes" => new Decoded(DetectedKind.Binary, BinarySummary(data), null),
            _ => new Decoded(DetectedKind.Unknown, null, $"unknown format '{forceFormat}'"),
        };
    }

    // ───────────────────────── JSON ─────────────────────────

    private static bool LooksLikeJson(byte[] data)
    {
        int i = 0; while (i < data.Length && (data[i] == ' ' || data[i] == '\t' || data[i] == '\n' || data[i] == '\r')) i++;
        if (i >= data.Length) return false;
        var c = data[i];
        return c == '{' || c == '[' || c == '"' || c == '-' || (c >= '0' && c <= '9') || c == 't' || c == 'f' || c == 'n';
    }

    private static bool TryDecodeJson(byte[] data, out Decoded? result)
    {
        result = null;
        try
        {
            var node = JsonNode.Parse(data);
            if (node is null) return false;
            var kind = node switch
            {
                JsonObject => DetectedKind.JsonObject,
                JsonArray => DetectedKind.JsonArray,
                _ => DetectedKind.JsonScalar,
            };
            result = new Decoded(kind, node, null);
            return true;
        }
        catch (JsonException) { return false; }
    }

    // ───────────────────────── BSON ─────────────────────────

    /// <summary>
    /// BSON spec: 4-byte little-endian total length, then element list, then a single 0x00 terminator.
    /// Element = type byte + cstring key + value.
    /// </summary>
    private static bool LooksLikeBson(byte[] data)
    {
        if (data.Length < 5) return false;
        var declared = BinaryPrimitives.ReadInt32LittleEndian(data);
        return declared == data.Length && data[^1] == 0x00;
    }

    private static bool TryDecodeBson(byte[] data, out Decoded? result)
    {
        result = null;
        try
        {
            int offset = 4; // skip length prefix
            var doc = ReadBsonDocument(data, ref offset);
            // Trailing 0x00 already consumed inside ReadBsonDocument.
            result = new Decoded(DetectedKind.Bson, doc, $"BSON document, {data.Length} bytes");
            return true;
        }
        catch { return false; }
    }

    private static JsonObject ReadBsonDocument(byte[] d, ref int o)
    {
        var obj = new JsonObject();
        while (o < d.Length)
        {
            var type = d[o++];
            if (type == 0x00) return obj; // doc terminator
            var key = ReadCString(d, ref o);
            obj[key] = ReadBsonValue(d, ref o, type);
        }
        return obj;
    }

    private static JsonArray ReadBsonArray(byte[] d, ref int o)
    {
        var arr = new JsonArray();
        while (o < d.Length)
        {
            var type = d[o++];
            if (type == 0x00) return arr;
            _ = ReadCString(d, ref o); // array keys are stringified indices we don't need
            arr.Add(ReadBsonValue(d, ref o, type));
        }
        return arr;
    }

    private static JsonNode? ReadBsonValue(byte[] d, ref int o, byte type)
    {
        switch (type)
        {
            case 0x01: // 64-bit float
                var dbl = BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(o, 8)); o += 8; return JsonValue.Create(dbl);
            case 0x02: // UTF-8 string
                var slen = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o, 4)); o += 4;
                var s = Encoding.UTF8.GetString(d, o, slen - 1); o += slen; return JsonValue.Create(s);
            case 0x03: // embedded document
                var elen = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o, 4)); o += 4;
                return ReadBsonDocument(d, ref o);
            case 0x04: // array
                _ = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o, 4)); o += 4;
                return ReadBsonArray(d, ref o);
            case 0x05: // binary
                var blen = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o, 4)); o += 4;
                var subtype = d[o++];
                var hex = Convert.ToHexString(d, o, blen); o += blen;
                return new JsonObject { ["_binary_subtype"] = subtype, ["_binary_hex"] = hex };
            case 0x07: // ObjectId
                var oidHex = Convert.ToHexString(d, o, 12); o += 12;
                return JsonValue.Create("ObjectId(" + oidHex + ")");
            case 0x08: var b = d[o++]; return JsonValue.Create(b != 0); // bool
            case 0x09: // UTC datetime
                var ms = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(o, 8)); o += 8;
                return JsonValue.Create(DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o"));
            case 0x0A: return null; // null
            case 0x10: // int32
                var i32 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o, 4)); o += 4; return JsonValue.Create(i32);
            case 0x11: // timestamp
                var t = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o, 8)); o += 8; return JsonValue.Create(t);
            case 0x12: // int64
                var i64 = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(o, 8)); o += 8; return JsonValue.Create(i64);
            default:
                // Unknown / rare types — skip rest of doc by bailing. Caller will catch.
                throw new InvalidOperationException($"BSON: unsupported type byte 0x{type:X2} at offset {o}");
        }
    }

    private static string ReadCString(byte[] d, ref int o)
    {
        var start = o;
        while (o < d.Length && d[o] != 0x00) o++;
        var s = Encoding.UTF8.GetString(d, start, o - start);
        o++; // consume null
        return s;
    }

    // ───────────────────────── Protobuf (schema-less) ─────────────────────────

    /// <summary>
    /// Protobuf wire format: each field = (tag varint) + value-by-wire-type.
    /// Without a .proto we can't name fields, but we can walk the structure and dump tags +
    /// wire types + (for length-delimited) recurse if the inner bytes look like another message.
    /// </summary>
    private static bool LooksLikeProtobuf(byte[] data)
    {
        if (data.Length < 2) return false;
        // First byte: bottom 3 bits = wire type ∈ {0,1,2,5}; top bits = field number (≥1).
        var wireType = data[0] & 0x07;
        var fieldNumber = (data[0] >> 3);
        if (wireType is not (0 or 1 or 2 or 5)) return false;
        // The varint encoding sets the high bit when continuation needed; field number itself
        // can stretch across bytes. Skip a strict check — the per-field decoder will refuse if
        // the data doesn't actually walk.
        return fieldNumber >= 1 || (data[0] & 0x80) != 0;
    }

    private static bool TryDecodeProtobuf(byte[] data, out Decoded? result)
    {
        result = null;
        try
        {
            int o = 0;
            var fields = new JsonArray();
            while (o < data.Length)
            {
                var tagVal = ReadVarint(data, ref o);
                var fieldNumber = (int)(tagVal >> 3);
                var wireType = (int)(tagVal & 0x07);
                var f = new JsonObject { ["field"] = fieldNumber, ["wireType"] = WireTypeName(wireType) };

                switch (wireType)
                {
                    case 0: // varint
                        f["value"] = (long)ReadVarint(data, ref o);
                        break;
                    case 1: // 64-bit
                        f["value_hex"] = Convert.ToHexString(data, o, 8); o += 8;
                        break;
                    case 2: // length-delimited
                        var len = (int)ReadVarint(data, ref o);
                        if (o + len > data.Length) throw new InvalidOperationException("protobuf: length overflow");
                        var inner = new byte[len];
                        Array.Copy(data, o, inner, 0, len); o += len;
                        // Try to interpret as UTF-8 string first (most common case for length-delimited);
                        // if that fails or looks binary, leave hex; if it itself looks like a nested message, recurse.
                        if (TryDecodeUtf8(inner, out var s) && !s.Contains('\0'))
                            f["value_string"] = s;
                        else if (LooksLikeProtobuf(inner) && TryDecodeProtobuf(inner, out var nested))
                            f["value_message"] = (nested!.Value as JsonNode);
                        else
                            f["value_hex"] = Convert.ToHexString(inner);
                        f["length"] = len;
                        break;
                    case 5: // 32-bit
                        f["value_hex"] = Convert.ToHexString(data, o, 4); o += 4;
                        break;
                    default:
                        throw new InvalidOperationException("protobuf: deprecated wire type " + wireType);
                }
                fields.Add(f);
                if (fields.Count > 256) break; // sanity cap
            }
            result = new Decoded(DetectedKind.Protobuf, fields, $"protobuf wire-format (schema-less), {data.Length} bytes");
            return true;
        }
        catch { return false; }
    }

    private static ulong ReadVarint(byte[] d, ref int o)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (o >= d.Length) throw new InvalidOperationException("varint: unexpected end");
            var b = d[o++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new InvalidOperationException("varint: too long");
        }
    }

    private static string WireTypeName(int wt) => wt switch
    {
        0 => "varint",
        1 => "fixed64",
        2 => "length-delimited",
        5 => "fixed32",
        _ => $"unknown({wt})",
    };

    // ───────────────────────── UTF-8 + binary ─────────────────────────

    private static bool TryDecodeUtf8(byte[] data, out string text)
    {
        try
        {
            var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = enc.GetString(data);
            return true;
        }
        catch { text = ""; return false; }
    }

    private static object BinarySummary(byte[] data)
    {
        const int preview = 64;
        var hex = Convert.ToHexString(data, 0, Math.Min(preview, data.Length));
        return new
        {
            length = data.Length,
            base64 = Convert.ToBase64String(data, 0, Math.Min(2048, data.Length)),
            hexPreview = hex + (data.Length > preview ? "…" : ""),
        };
    }
}
