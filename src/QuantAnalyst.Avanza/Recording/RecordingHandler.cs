using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Logging;

namespace QuantAnalyst.Avanza.Recording;

/// <summary>
/// Writes one JSON file per exchange to <c>&lt;dir&gt;/NNN-&lt;route&gt;.json</c> (format <c>qa-recording/1</c>).
/// Even in the git-ignored live folder, the following are <b>never</b> written:
/// <list type="bullet">
/// <item>header and cookie values (names only)</item>
/// <item>request bodies and real paths of authentication routes (password, TOTP code, BankID customer id)</item>
/// <item>string and number values of authentication responses (session ids, security token): only their structure is kept</item>
/// </list>
/// Everything else is raw and must go through <c>qa recordings sanitize</c> before it is shared or committed.
/// </summary>
internal sealed class Recorder
{
    public const string Format = "qa-recording/1";
    public const string Redacted = "<redacted>";
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private int _sequence;

    public Recorder(string directory, TimeProvider time, ILogger logger)
    {
        Directory = directory;
        _time = time;
        _logger = logger;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    /// <summary>Reserves the next <c>NNN-&lt;name&gt;.json</c> file name (shared numbering for REST and stream recordings).</summary>
    public string NextFile(string name, out string fileName)
    {
        int seq = Interlocked.Increment(ref _sequence);
        fileName = string.Create(CultureInfo.InvariantCulture, $"{seq:000}-{name}.json");
        return Path.Combine(Directory, fileName);
    }

    /// <summary>Starts recording one stream connection; see <see cref="StreamRecording"/>.</summary>
    public StreamRecording BeginStream(AvanzaRoute route, HttpRequestMessage request, HttpResponseMessage response) =>
        new(this, route, request, response, _time, _logger);

    public async Task RecordAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken ct)
    {
        AvanzaRoute? route = request.Options.TryGetValue(AvanzaRequest.Route, out AvanzaRoute? r) ? r : null;
        string name = route?.Name ?? "unknown";
        bool auth = route?.IsAuthentication ?? true; // unknown routes are treated as sensitive

        byte[]? requestBody = request.Content is null || auth ? null : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(ct).ConfigureAwait(false);
        byte[] responseBody = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        string file = NextFile(name, out string fileName);

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteString("routesVersion", AvanzaRoutes.RoutesVersion);
            w.WriteString("route", name);
            w.WriteString("tier", route?.Tier?.ToString() ?? "none");
            w.WriteString("recordedAtUtc", _time.GetUtcNow());

            w.WriteStartObject("request");
            w.WriteString("method", request.Method.Method);
            // Authentication paths can carry identifiers (BankID loginPath has the customer id): template only.
            w.WriteString("path", auth ? route?.PathTemplate ?? "(authentication)" : request.RequestUri?.AbsolutePath);
            w.WriteString("query", auth ? null : request.RequestUri?.Query);
            WriteNames(w, "headerNames", request.Headers.Select(h => h.Key));
            w.WritePropertyName("body");
            if (auth && request.Content is not null)
            {
                w.WriteStringValue("<omitted: authentication request>");
            }
            else
            {
                WriteBody(w, requestBody, redactValues: false);
            }

            w.WriteEndObject();

            w.WriteStartObject("response");
            w.WriteNumber("status", (int)response.StatusCode);
            w.WriteString("contentType", response.Content.Headers.ContentType?.MediaType);
            WriteNames(w, "headerNames", response.Headers.Select(h => h.Key));
            WriteNames(w, "setCookieNames", CookieNames(response));
            w.WritePropertyName("body");
            WriteBody(w, responseBody, redactValues: auth);
            w.WriteEndObject();

            w.WriteEndObject();
        }

        await File.WriteAllBytesAsync(file, stream.ToArray(), ct).ConfigureAwait(false);
        Log.Recorded(_logger, name, fileName);
    }

    internal static IEnumerable<string> CookieNames(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.Select(v => v.Split('=', 2)[0].Trim())
            : [];

    internal static void WriteNames(Utf8JsonWriter w, string property, IEnumerable<string> names)
    {
        w.WriteStartArray(property);
        foreach (string n in names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            w.WriteStringValue(n);
        }

        w.WriteEndArray();
    }

    private static void WriteBody(Utf8JsonWriter w, byte[]? body, bool redactValues)
    {
        if (body is null || body.Length == 0)
        {
            w.WriteNullValue();
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (redactValues)
            {
                WriteStructureOnly(w, doc.RootElement);
            }
            else
            {
                doc.RootElement.WriteTo(w);
            }
        }
        catch (JsonException)
        {
            // Non-JSON (e.g. an HTML login page). Auth responses: length only.
            w.WriteStringValue(redactValues
                ? $"<non-JSON body, {body.Length} bytes, omitted>"
                : Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 64 * 1024)));
        }
    }

    /// <summary>Keeps keys, arrays, booleans and nulls; replaces every string and number.</summary>
    internal static void WriteStructureOnly(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    w.WritePropertyName(p.Name);
                    WriteStructureOnly(w, p.Value);
                }

                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (JsonElement item in e.EnumerateArray())
                {
                    WriteStructureOnly(w, item);
                }

                w.WriteEndArray();
                break;
            case JsonValueKind.String:
                w.WriteStringValue(Redacted);
                break;
            case JsonValueKind.Number:
                w.WriteNumberValue(0);
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }
}

/// <summary>Outermost handler: records the final exchange of each logical call (after retries).</summary>
internal sealed class RecordingHandler(Recorder recorder) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await recorder.RecordAsync(request, response, cancellationToken).ConfigureAwait(false);
        return response;
    }
}
