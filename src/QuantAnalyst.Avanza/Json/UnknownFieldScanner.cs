using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace QuantAnalyst.Avanza.Json;

/// <summary>Result of a schema scan: JSON properties the DTO does not model, and required DTO members the JSON lacks.</summary>
internal sealed record SchemaScan(IReadOnlyList<string> Unknown, IReadOnlyList<string> Missing)
{
    public bool Clean => Unknown.Count == 0 && Missing.Count == 0;
}

/// <summary>
/// Walks a JSON document alongside the (source-generated) serialization metadata of the target type and returns
/// in one pass:
/// <list type="bullet">
/// <item>the path of every JSON property the type does not model, e.g. <c>$.accounts[0].newField</c></item>
/// <item>the path of every <c>required</c> member the JSON lacks, e.g. <c>$.orderbookStatus</c></item>
/// </list>
/// A single live probe therefore reports the whole fix list. <see cref="JsonElement"/>-typed members accept any
/// shape and are not descended into.
/// </summary>
internal static class UnknownFieldScanner
{
    public const int MaxPaths = 200;

    public static SchemaScan Scan(JsonElement root, JsonTypeInfo typeInfo)
    {
        var unknown = new List<string>();
        var missing = new List<string>();
        Walk(root, typeInfo, "$", unknown, missing);
        return new SchemaScan(unknown, missing);
    }

    private static void Walk(JsonElement element, JsonTypeInfo typeInfo, string path, List<string> found, List<string> missing)
    {
        if (found.Count >= MaxPaths)
        {
            return;
        }

        switch (typeInfo.Kind)
        {
            case JsonTypeInfoKind.Object when element.ValueKind == JsonValueKind.Object:
                WalkObject(element, typeInfo, path, found, missing);
                break;

            case JsonTypeInfoKind.Enumerable when element.ValueKind == JsonValueKind.Array && typeInfo.ElementType is not null:
                JsonTypeInfo elementInfo = typeInfo.Options.GetTypeInfo(typeInfo.ElementType);
                int i = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, elementInfo, $"{path}[{i++}]", found, missing);
                }

                break;

            case JsonTypeInfoKind.Dictionary when element.ValueKind == JsonValueKind.Object && typeInfo.ElementType is not null:
                JsonTypeInfo valueInfo = typeInfo.Options.GetTypeInfo(typeInfo.ElementType);
                foreach (JsonProperty p in element.EnumerateObject())
                {
                    Walk(p.Value, valueInfo, Append(path, p.Name), found, missing);
                }

                break;

            default:
                // Primitives, JsonElement, or a kind mismatch (reported by the deserializer, not here).
                break;
        }
    }

    private static void WalkObject(JsonElement element, JsonTypeInfo typeInfo, string path, List<string> found, List<string> missing)
    {
        var members = new Dictionary<string, JsonPropertyInfo>(typeInfo.Properties.Count, StringComparer.Ordinal);
        foreach (JsonPropertyInfo p in typeInfo.Properties)
        {
            members[p.Name] = p;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string childPath = Append(path, property.Name);
            if (!members.TryGetValue(property.Name, out JsonPropertyInfo? member))
            {
                found.Add(childPath);
                if (found.Count >= MaxPaths)
                {
                    return;
                }

                continue;
            }

            Type memberType = Nullable.GetUnderlyingType(member.PropertyType) ?? member.PropertyType;
            if (memberType == typeof(JsonElement) || property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            Walk(property.Value, typeInfo.Options.GetTypeInfo(memberType), childPath, found, missing);
        }

        foreach (JsonPropertyInfo member in typeInfo.Properties)
        {
            if (member.IsRequired && !element.TryGetProperty(member.Name, out _) && missing.Count < MaxPaths)
            {
                missing.Add(Append(path, member.Name));
            }
        }
    }

    private static string Append(string path, string name)
    {
        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return $"{path}['{name.Replace("'", "\\'", StringComparison.Ordinal)}']";
            }
        }

        return $"{path}.{name}";
    }
}
