using System.Text.Json;

namespace Dam.Domain.Integrations;

/// <summary>JSON Pointer (RFC 6901): <c>/access_token</c>, <c>/data/0/token</c>, <c>/a~1b</c> for a key containing a slash.</summary>
public static class JsonPointer
{
    public const int MaxLength = 200;

    /// <summary>Null when the text is a usable pointer for a value inside a document (so not the empty pointer, which is the whole document).</summary>
    public static string? Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "A JSON pointer is required, such as /access_token.";
        if (path.Length > MaxLength) return $"A JSON pointer is at most {MaxLength} characters.";
        if (path[0] != '/') return "A JSON pointer starts with a slash, such as /access_token or /data/token.";
        for (var i = 0; i < path.Length; i++)
        {
            if (char.IsControl(path[i])) return "A JSON pointer cannot contain control characters.";
            if (path[i] == '~' && (i + 1 >= path.Length || path[i + 1] is not ('0' or '1')))
                return "In a JSON pointer, ~ must be written ~0 and / inside a name ~1.";
        }
        return null;
    }

    /// <summary>Finds the value the pointer names. False when the pointer is invalid or nothing is there.</summary>
    public static bool TryResolve(JsonElement root, string path, out JsonElement value)
    {
        value = default;
        if (Check(path) is not null) return false;
        var current = root;
        foreach (var raw in path[1..].Split('/'))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            switch (current.ValueKind)
            {
                case JsonValueKind.Object:
                    if (!current.TryGetProperty(token, out current)) return false;
                    break;
                case JsonValueKind.Array:
                    if (!IsArrayIndex(token) || !int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                        || index >= current.GetArrayLength()) return false;
                    current = current[index];
                    break;
                default:
                    return false;
            }
        }
        value = current;
        return true;
    }

    private static bool IsArrayIndex(string token) =>
        token.Length > 0 && token.All(char.IsAsciiDigit) && (token.Length == 1 || token[0] != '0');
}
