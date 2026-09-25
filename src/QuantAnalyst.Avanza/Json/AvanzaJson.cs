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
/// <item>Tier A: any unknown or missing required path ⇒ <see cref="SchemaDriftException"/> listing all of them.</item>
/// <item>Tier B: missing required paths ⇒ <see cref="SchemaDriftException"/>; unknown paths are logged once per
/// route (drift.warning).</item>
/// <item>Both tiers: invalid JSON, nulls in non-nullable members and type mismatches ⇒ <see cref="SchemaDriftException"/>
/// with the JSON path.</item>
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
        SchemaScan scan = UnknownFieldScanner.Scan(root, typeInfo);
        IReadOnlyList<string> unknown = scan.Unknown;
        if (scan.Missing.Count > 0 || (tier == DtoTier.A && unknown.Count > 0))
        {
            IReadOnlyList<string> unknownReported = tier == DtoTier.A ? unknown : [];
            string detail = string.Join(
                "; ",
                new[]
                {
                    unknownReported.Count > 0 ? $"{unknownReported.Count} unknown field(s)" : null,
                    scan.Missing.Count > 0 ? $"{scan.Missing.Count} missing required field(s): {string.Join(", ", scan.Missing.Take(20))}" : null,
                }.Where(s => s is not null));
            throw new SchemaDriftException(route, dtoVersion, tier, [.. unknownReported, .. scan.Missing], detail);
        }

        if (unknown.Count > 0)
        {
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
