using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using QuantAnalyst.Avanza.Logging;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Json;

/// <summary>
/// Tiered strict deserialization (ADR 0002 §2).
/// <list type="bullet">
/// <item>Tier A: any unknown path ⇒ <see cref="SchemaDriftException"/> listing every path.</item>
/// <item>Tier B: unknown paths are logged once per route (drift.warning).</item>
/// <item>Both tiers: invalid JSON, missing required members, nulls in non-nullable members and type
/// mismatches ⇒ <see cref="SchemaDriftException"/> with the JSON path.</item>
/// </list>
/// </summary>
internal sealed class AvanzaJson(ILogger logger)
{
    private readonly ConcurrentDictionary<string, bool> _warnedRoutes = new(StringComparer.Ordinal);

    public T Deserialize<T>(ReadOnlySpan<byte> utf8, JsonTypeInfo<T> typeInfo, string route, string dtoVersion, DtoTier tier)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8.ToArray());
        }
        catch (JsonException ex)
        {
            throw new SchemaDriftException(route, dtoVersion, tier, [ex.Path ?? "$"], "response is not valid JSON", ex);
        }

        using (document)
        {
            return Deserialize(document.RootElement, typeInfo, route, dtoVersion, tier);
        }
    }

    public T Deserialize<T>(JsonElement root, JsonTypeInfo<T> typeInfo, string route, string dtoVersion, DtoTier tier)
    {
        IReadOnlyList<string> unknown = UnknownFieldScanner.Scan(root, typeInfo);
        if (unknown.Count > 0)
        {
            if (tier == DtoTier.A)
            {
                throw new SchemaDriftException(route, dtoVersion, tier, unknown, $"{unknown.Count} unknown field(s)");
            }

            if (_warnedRoutes.TryAdd(route, true))
            {
                Log.DriftWarning(logger, route, dtoVersion, unknown.Count, string.Join(", ", unknown.Take(20)));
            }
        }

        try
        {
            return root.Deserialize(typeInfo)
                   ?? throw new SchemaDriftException(route, dtoVersion, tier, ["$"], "response is null");
        }
        catch (JsonException ex)
        {
            // STJ messages name types and paths, never values.
            throw new SchemaDriftException(route, dtoVersion, tier, [ex.Path ?? "$"], ex.Message, ex);
        }
    }
}
