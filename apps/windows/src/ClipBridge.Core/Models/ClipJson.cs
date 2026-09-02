using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipBridge.Core.Models;

/// <summary>
/// Wire-format helpers. The relay uses snake_case JSON field names
/// (clip_id, device_id, content_hash, created_at, received_at, size_bytes, content_type).
/// </summary>
public static class ClipJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
    {
        public static readonly SnakeCaseNamingPolicy Instance = new();

        public override string ConvertName(string name)
        {
            // Convert PascalCase to snake_case: ClipId -> clip_id, ContentType -> content_type
            var chars = new System.Text.StringBuilder(name.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0 && (char.IsLower(name[i - 1]) || (char.IsDigit(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    {
                        chars.Append('_');
                    }
                    chars.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    chars.Append(c);
                }
            }
            return chars.ToString();
        }
    }
}
