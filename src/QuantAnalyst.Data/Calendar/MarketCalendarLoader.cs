using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Data.Calendar;

/// <summary>A calendar file is missing, malformed or inconsistent.</summary>
public sealed class CalendarConfigException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Loads <c>config/market-calendar.&lt;MIC&gt;.&lt;year&gt;.json</c> (format <c>qa-market-calendar/1</c>) strictly: unknown or
/// missing fields, a file name that disagrees with its content, a time zone other than Europe/Stockholm, or any entry
/// <see cref="MarketCalendar"/> rejects (weekend, duplicate, wrong year) fail the load.
/// </summary>
public static class MarketCalendarLoader
{
    public const string Format = "qa-market-calendar/1";
    public const string TimeZone = "Europe/Stockholm";

    public static string FileName(string mic, int year) => string.Create(CultureInfo.InvariantCulture, $"market-calendar.{mic}.{year}.json");

    /// <summary>Loads every <c>market-calendar.&lt;mic&gt;.*.json</c> in <paramref name="directory"/>.</summary>
    public static MarketCalendar LoadDirectory(string directory, string mic = "XSTO")
    {
        if (!Directory.Exists(directory))
        {
            throw new CalendarConfigException($"Calendar folder '{directory}' does not exist.");
        }

        string[] files = [.. Directory.EnumerateFiles(directory, $"market-calendar.{mic}.*.json").Order(StringComparer.Ordinal)];
        if (files.Length == 0)
        {
            throw new CalendarConfigException($"No market-calendar.{mic}.<year>.json files in '{directory}'.");
        }

        var years = files.Select(LoadYear).ToList();
        try
        {
            return new MarketCalendar(years);
        }
        catch (ArgumentException ex)
        {
            throw new CalendarConfigException(ex.Message, ex);
        }
    }

    public static CalendarYear LoadYear(string path)
    {
        CalendarFileDto dto;
        try
        {
            dto = JsonSerializer.Deserialize(File.ReadAllBytes(path), CalendarJsonContext.Default.CalendarFileDto)
                  ?? throw new CalendarConfigException($"{Path.GetFileName(path)} is empty.");
        }
        catch (JsonException ex) when (ex.Path == "$.verified_on")
        {
            throw new CalendarConfigException(
                $"{Path.GetFileName(path)}: verified_on must be null (not checked yet) or the date you checked the file, in quotes, e.g. \"2026-09-26\".", ex);
        }
        catch (JsonException ex)
        {
            throw new CalendarConfigException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }

        string name = Path.GetFileName(path);
        if (dto.Format != Format)
        {
            throw new CalendarConfigException($"{name}: format must be '{Format}'.");
        }

        if (!string.Equals(name, FileName(dto.Mic, dto.Year), StringComparison.Ordinal))
        {
            throw new CalendarConfigException($"{name}: the content says {dto.Mic} {dto.Year}; the file must be named {FileName(dto.Mic, dto.Year)}.");
        }

        if (dto.TimeZone != TimeZone)
        {
            throw new CalendarConfigException($"{name}: timeZone must be {TimeZone} (session times are Stockholm local time).");
        }

        if (string.IsNullOrWhiteSpace(dto.SourceUrl) || !Uri.TryCreate(dto.SourceUrl, UriKind.Absolute, out _))
        {
            throw new CalendarConfigException($"{name}: source_url must be an absolute URL.");
        }

        var year = new CalendarYear(
            dto.Mic,
            dto.Year,
            Time(dto.RegularSession.Open, name),
            Time(dto.RegularSession.Close, name),
            Time(dto.HalfDaySession.Open, name),
            Time(dto.HalfDaySession.Close, name),
            [.. dto.Closed.Select(e => Entry(e, name))],
            [.. dto.HalfDays.Select(e => Entry(e, name))],
            dto.SourceUrl,
            dto.VerifiedOn is null ? null : Date(dto.VerifiedOn, name));
        try
        {
            _ = new MarketCalendar([year]); // per-file validation with a file-specific message
        }
        catch (ArgumentException ex)
        {
            throw new CalendarConfigException($"{name}: {ex.Message}", ex);
        }

        return year;
    }

    private static CalendarEntry Entry(CalendarEntryDto e, string file) =>
        new(Date(e.Date, file), e.Name, e.Close is null ? null : Time(e.Close, file));

    private static DateOnly Date(string text, string file) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d)
            ? d
            : throw new CalendarConfigException($"{file}: '{text}' is not a yyyy-MM-dd date.");

    private static TimeOnly Time(string text, string file) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly t)
            ? t
            : throw new CalendarConfigException($"{file}: '{text}' is not an HH:mm time.");
}

internal sealed class CalendarFileDto
{
    [JsonPropertyName("format")]
    public required string Format { get; init; }

    [JsonPropertyName("mic")]
    public required string Mic { get; init; }

    [JsonPropertyName("year")]
    public required int Year { get; init; }

    [JsonPropertyName("timeZone")]
    public required string TimeZone { get; init; }

    [JsonPropertyName("regularSession")]
    public required SessionDto RegularSession { get; init; }

    [JsonPropertyName("halfDaySession")]
    public required SessionDto HalfDaySession { get; init; }

    [JsonPropertyName("source_url")]
    public required string SourceUrl { get; init; }

    /// <summary>yyyy-MM-dd when the owner checked the file against the exchange's calendar; null until then.</summary>
    [JsonPropertyName("verified_on")]
    public required string? VerifiedOn { get; init; }

    [JsonPropertyName("draft_basis")]
    public required string DraftBasis { get; init; }

    [JsonPropertyName("closed")]
    public required List<CalendarEntryDto> Closed { get; init; }

    [JsonPropertyName("halfDays")]
    public required List<CalendarEntryDto> HalfDays { get; init; }
}

internal sealed class SessionDto
{
    [JsonPropertyName("open")]
    public required string Open { get; init; }

    [JsonPropertyName("close")]
    public required string Close { get; init; }
}

internal sealed class CalendarEntryDto
{
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Early-close time for a half day when it differs from the file's half-day session.</summary>
    [JsonPropertyName("close")]
    public string? Close { get; init; }
}

[JsonSourceGenerationOptions(
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    AllowTrailingCommas = false)]
[JsonSerializable(typeof(CalendarFileDto))]
internal sealed partial class CalendarJsonContext : JsonSerializerContext;
