using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QuantAnalyst.Avanza.Recording;

public sealed class SanitizeOptions
{
    /// <summary>Keep numbers on personal routes (balances, volumes, prices paid). Default: replace them.</summary>
    public bool KeepAmounts { get; init; }

    /// <summary>Values that must not appear in the output (e.g. the stored credentials), compared in memory only.</summary>
    public IReadOnlyList<string> ForbiddenValues { get; init; } = [];
}

/// <summary>Result of a sanitize run. <see cref="Problems"/> name files and JSON paths, never values.</summary>
public sealed record SanitizeReport(int Files, IReadOnlyDictionary<string, int> Replacements, IReadOnlyList<string> Problems)
{
    public bool Succeeded => Problems.Count == 0;
}

/// <summary>
/// Turns raw recordings (<c>recordings/live/&lt;stamp&gt;</c>) into shareable fixtures (docs/plans/03 "Recording and
/// sanitizing"). Deterministic: the same input gives the same output. It fails closed: when the leak scan finds
/// anything, nothing is written.
/// </summary>
public static partial class RecordingSanitizer
{
    public const string RedactedValue = "<redacted>";

    private static readonly HashSet<string> RedactKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "customerId", "pushSubscriptionId", "authenticationSession", "securityToken", "greetingName", "noteId",
        "verificationNumber", "username", "password", "totpCode", "clearingAccountNumber", "transactionId",
        "invalidSessionId", "pushBaseUrl", "creditAccountClearingAccountNumber", "identificationNumber",
    };

    // Any key containing one of these (case-insensitive) is treated as a banking or personal identifier, so new
    // fields like "creditAccountClearingAccountNumber" (seen live 2026-09-25) are redacted without a code change.
    private static readonly string[] RedactKeyFragments =
    [
        "accountnumber", "clearing", "iban", "bankaccount", "identificationnumber", "personalnumber", "personnummer",
        "ssn", "email", "phone", "mobile", "address", "zipcode", "postalcode",
    ];

    private static bool IsRedactKey(string? key) =>
        key is not null
        && (RedactKeys.Contains(key) || RedactKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase)));

    private static readonly HashSet<string> AccountIdKeys = new(StringComparer.Ordinal) { "accountId", "cAccountId" };

    // Routes whose numbers describe the owner's money and holdings.
    private static readonly HashSet<string> PersonalRoutes = new(StringComparer.Ordinal)
    {
        "accounts-overview", "trading-accounts", "positions", "orders", "deals", "transactions",
    };

    // Identifiers of the owner's own records (transactions, positions, orders, deals) on personal routes. Instrument
    // and orderbook ids are public and stay; account ids have their own mapping.
    private static readonly HashSet<string> RecordIdKeys = new(StringComparer.Ordinal) { "id", "orderId", "dealId" };

    // Structural numbers kept even on personal routes.
    private static readonly HashSet<string> KeepNumberKeys = new(StringComparer.Ordinal)
    {
        "decimalPrecision", "volumeFactor", "tradingUnit", "todayChangeDirection", "threeMonthsAgoChangeDirection",
    };

    public static SanitizeReport Sanitize(string inputDirectory, string outputDirectory, SanitizeOptions? options = null)
    {
        options ??= new SanitizeOptions();
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new IOException($"Output folder '{outputDirectory}' is not empty; choose a new folder.");
        }

        string[] files = [.. Directory.GetFiles(inputDirectory, "*.json").Order(StringComparer.Ordinal)];
        if (files.Length == 0)
        {
            throw new IOException($"No recordings (*.json) in '{inputDirectory}'.");
        }

        var documents = new List<(string Name, JsonNode Node)>();
        foreach (string f in files)
        {
            JsonNode node = JsonNode.Parse(File.ReadAllText(f))
                            ?? throw new InvalidDataException($"{Path.GetFileName(f)}: empty recording.");
            if (node["format"]?.GetValue<string>() != Recorder.Format)
            {
                throw new InvalidDataException($"{Path.GetFileName(f)}: not a {Recorder.Format} file.");
            }

            documents.Add((Path.GetFileName(f), node));
        }

        // Pass 1: collect identifiers that must be replaced consistently across all files.
        var accountIds = new SortedSet<string>(StringComparer.Ordinal);
        var urlKeys = new SortedSet<string>(StringComparer.Ordinal);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var recordIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((_, JsonNode node) in documents)
        {
            bool personal = PersonalRoutes.Contains(node["route"]?.GetValue<string>() ?? string.Empty);
            var sets = new IdSets(accountIds, urlKeys, names, personal ? recordIds : null);
            Collect(node["response"]?["body"], null, sets);
            Collect(node["request"]?["body"], null, sets);
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int n = 0;
        foreach (string id in accountIds)
        {
            n++;
            map[id] = string.Create(CultureInfo.InvariantCulture, $"9000{n:00}{(id.Length >= 3 ? id[^3..] : id)}");
        }

        n = 0;
        foreach (string key in urlKeys)
        {
            map.TryAdd(key, string.Create(CultureInfo.InvariantCulture, $"urlparam-{++n}"));
        }

        n = 0;
        foreach (string name in names)
        {
            map.TryAdd(name, string.Create(CultureInfo.InvariantCulture, $"Account {++n}"));
        }

        n = 0;
        foreach (string id in recordIds)
        {
            map.TryAdd(id, string.Create(CultureInfo.InvariantCulture, $"rec-{++n}"));
        }

        // Pass 2: rewrite.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var output = new List<(string Name, string Text)>();
        foreach ((string name, JsonNode node) in documents)
        {
            string route = node["route"]?.GetValue<string>() ?? "unknown";
            bool scramble = !options.KeepAmounts && PersonalRoutes.Contains(route);
            var ctx = new Context(map, counts, scramble, route);
            foreach (string part in new[] { "request", "response" })
            {
                if (node[part] is JsonObject section)
                {
                    JsonNode? body = section["body"];
                    JsonNode? rewritten = Rewrite(body, null, "$", ctx);
                    if (!ReferenceEquals(body, rewritten))
                    {
                        section["body"] = rewritten;
                    }

                    if (section["query"] is JsonValue q && q.TryGetValue(out string? query))
                    {
                        section["query"] = ReplaceAll(query, ctx);
                    }
                }
            }

            node["sanitized"] = new JsonObject
            {
                ["tool"] = "qa recordings sanitize",
                ["amountsReplaced"] = scramble,
            };
            output.Add((name, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        }

        // Leak scan (fail closed).
        var problems = new List<string>();
        foreach ((string name, string text) in output)
        {
            foreach (string forbidden in options.ForbiddenValues.Where(v => v.Length >= 4))
            {
                if (text.Contains(forbidden, StringComparison.Ordinal))
                {
                    problems.Add($"{name}: contains a value from the secret store");
                }
            }

            // An id that maps to itself is already a sanitized fake (re-sanitizing a fixture folder is idempotent).
            foreach (string id in accountIds.Where(id => map[id] != id))
            {
                if (id.Length >= 4 && text.Contains(id, StringComparison.Ordinal))
                {
                    problems.Add($"{name}: an original account id survived");
                }
            }

            if (JwtLike().IsMatch(text))
            {
                problems.Add($"{name}: contains a JWT-like token");
            }

            if (HexToken().IsMatch(text) || Base64Token().Matches(text).Any(m => LooksRandom(m.Value)))
            {
                problems.Add($"{name}: contains a long token-like string");
            }
        }

        if (problems.Count == 0)
        {
            Directory.CreateDirectory(outputDirectory);
            foreach ((string name, string text) in output)
            {
                File.WriteAllText(Path.Combine(outputDirectory, name), text + "\n");
            }
        }

        return new SanitizeReport(output.Count, counts, problems);
    }

    private sealed record Context(Dictionary<string, string> Map, Dictionary<string, int> Counts, bool Scramble, string Route)
    {
        public void Count(string rule) => Counts[rule] = Counts.GetValueOrDefault(rule) + 1;
    }

    private sealed record IdSets(SortedSet<string> AccountIds, SortedSet<string> UrlKeys, SortedSet<string> Names, SortedSet<string>? RecordIds);

    private static void Collect(JsonNode? node, string? key, IdSets sets)
    {
        SortedSet<string> accountIds = sets.AccountIds;
        SortedSet<string> urlKeys = sets.UrlKeys;
        SortedSet<string> names = sets.Names;
        switch (node)
        {
            case JsonObject obj:
                bool isAccount = key is "account" || obj.ContainsKey("accountId") || (key is "accounts" && obj.ContainsKey("id"));
                foreach ((string k, JsonNode? v) in obj)
                {
                    if (v is JsonValue value && value.TryGetValue(out string? s) && !string.IsNullOrEmpty(s))
                    {
                        if (AccountIdKeys.Contains(k) || (isAccount && k == "id"))
                        {
                            accountIds.Add(s);
                        }
                        else if (k == "urlParameterId")
                        {
                            urlKeys.Add(s);
                        }
                        else if (k == "userDefinedName" || (isAccount && k is "name" or "value"))
                        {
                            names.Add(s);
                        }
                        else if (sets.RecordIds is not null && RecordIdKeys.Contains(k) && key is not ("instrument" or "orderbook"))
                        {
                            sets.RecordIds.Add(s);
                        }
                    }
                    else if (k == "accountIds" && v is JsonArray ids)
                    {
                        foreach (JsonNode? item in ids)
                        {
                            if (item is JsonValue iv && iv.TryGetValue(out string? idText) && !string.IsNullOrEmpty(idText))
                            {
                                accountIds.Add(idText);
                            }
                        }
                    }
                    else if (isAccount && k == "name" && v is JsonObject nameObj && nameObj["value"] is JsonValue nv && nv.TryGetValue(out string? accountName))
                    {
                        names.Add(accountName); // orders: account.name = { value }
                    }

                    Collect(v, k, sets);
                }

                break;
            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    // Root arrays of accounts (trading-accounts) carry accountId directly.
                    Collect(item, key ?? "accounts", sets);
                }

                break;
            default:
                break;
        }
    }

    private static JsonNode? Rewrite(JsonNode? node, string? key, string path, Context ctx)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string k in obj.Select(p => p.Key).ToList())
                {
                    JsonNode? child = obj[k];
                    JsonNode? rewritten = Rewrite(child, k, $"{path}.{k}", ctx);
                    if (!ReferenceEquals(child, rewritten))
                    {
                        obj[k] = rewritten;
                    }
                }

                return obj;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    JsonNode? child = arr[i];
                    JsonNode? rewritten = Rewrite(child, key, $"{path}[{i}]", ctx);
                    if (!ReferenceEquals(child, rewritten))
                    {
                        arr[i] = rewritten;
                    }
                }

                return arr;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                string s = value.GetValue<string>();
                if (IsRedactKey(key))
                {
                    ctx.Count("redacted-key");
                    return JsonValue.Create(RedactedValue);
                }

                if (ctx.Map.TryGetValue(s, out string? mapped))
                {
                    ctx.Count("mapped-identifier");
                    return JsonValue.Create(mapped);
                }

                string replaced = ReplaceAll(s, ctx);
                return replaced == s ? value : JsonValue.Create(replaced);
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                if (IsRedactKey(key))
                {
                    ctx.Count("redacted-key");
                    return JsonValue.Create(0);
                }

                if (ctx.Scramble && (key is null || !KeepNumberKeys.Contains(key)))
                {
                    ctx.Count("amount");
                    return JsonNode.Parse(ScrambleNumber(value.ToJsonString(), $"{ctx.Route}{path}"));
                }

                return value;
            default:
                return node;
        }
    }

    private static string ReplaceAll(string text, Context ctx)
    {
        foreach (KeyValuePair<string, string> m in ctx.Map.Where(m => m.Key.Length >= 4).OrderByDescending(m => m.Key.Length))
        {
            if (text.Contains(m.Key, StringComparison.Ordinal))
            {
                ctx.Count("mapped-substring");
                text = text.Replace(m.Key, m.Value, StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>Deterministic replacement with the same shape: sign, digit counts, and all-zero fractions kept.</summary>
    internal static string ScrambleNumber(string numberText, string salt)
    {
        string mantissa = numberText;
        string exponent = string.Empty;
        int e = numberText.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            mantissa = numberText[..e];
            exponent = numberText[e..];
        }

        bool negative = mantissa.StartsWith('-');
        string digits = negative ? mantissa[1..] : mantissa;
        int dot = digits.IndexOf('.', StringComparison.Ordinal);
        string intPart = dot < 0 ? digits : digits[..dot];
        string fracPart = dot < 0 ? string.Empty : digits[(dot + 1)..];

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(salt + "|" + numberText));
        int h = 0;
        char Next(bool nonZero)
        {
            int b = hash[h++ % hash.Length];
            return nonZero ? (char)('1' + (b % 9)) : (char)('0' + (b % 10));
        }

        var sb = new StringBuilder(numberText.Length);
        if (negative)
        {
            sb.Append('-');
        }

        for (int i = 0; i < intPart.Length; i++)
        {
            sb.Append(intPart.Length > 1 && i == 0 ? Next(true) : Next(false));
        }

        if (dot >= 0)
        {
            sb.Append('.');
            bool allZero = fracPart.All(c => c == '0');
            foreach (char _ in fracPart)
            {
                sb.Append(allZero ? '0' : Next(false));
            }
        }

        return sb.Append(exponent).ToString();
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex JwtLike();

    [GeneratedRegex(@"\b[A-Fa-f0-9]{32,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HexToken();

    [GeneratedRegex(@"[A-Za-z0-9+/_-]{40,}={0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex Base64Token();

    // Random tokens mix digits, upper and lower case; URL paths and slugs do not.
    private static bool LooksRandom(string s) => s.Any(char.IsAsciiDigit) && s.Any(char.IsAsciiLetterUpper) && s.Any(char.IsAsciiLetterLower);
}
