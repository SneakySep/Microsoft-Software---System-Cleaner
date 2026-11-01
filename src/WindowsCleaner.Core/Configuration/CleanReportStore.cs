using System.Text.Json;
using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Core.Configuration;

/// <summary>
/// Persists recent <see cref="CleanReport"/>s under <c>%APPDATA%</c>.
/// </summary>
/// <remarks>
/// <para>
/// A short history rather than only the most recent run, because a single clean in isolation says
/// very little: what a temp folder is doing is only visible across a few runs. The list is capped,
/// so the file cannot grow without bound and does not become a long-term record of what was on
/// the user's disk.
/// </para>
/// <para>
/// The file is rewritten on every append (read, prepend, trim, write). That is a handful of
/// kilobytes at worst, so an append-only scheme would add machinery for no benefit.
/// </para>
/// <para>
/// Like <see cref="SettingsStore"/>, every operation is defensive: this is a convenience, and a
/// missing, corrupt or unwritable file must never stop a clean from being reported on screen.
/// </para>
/// </remarks>
public sealed class CleanReportStore
{
    /// <summary>Bumped if the stored shape changes incompatibly. 1 held a single report.</summary>
    public const int CurrentVersion = 2;

    /// <summary>How many runs are kept. Older entries fall off the end.</summary>
    public const int DefaultMaxEntries = 50;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CleanReportStore(string? filePath = null, int maxEntries = DefaultMaxEntries)
    {
        FilePath = filePath ?? DefaultFilePath;
        LegacyFilePath = Path.Combine(
            Path.GetDirectoryName(FilePath) ?? string.Empty,
            "clean-report.json");
        MaxEntries = Math.Max(1, maxEntries);
    }

    /// <summary>The default location: <c>%APPDATA%\WindowsCleaner\last-clean.json</c>.</summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowsCleaner",
        "last-clean.json");

    public string FilePath { get; }

    /// <summary>
    /// Where the record lived while there was only one of them.
    /// </summary>
    /// <remarks>
    /// The file was renamed when a single report became a history. An existing one is still read
    /// whenever the new file is not there yet, so upgrading does not look like the record of the
    /// last clean was silently thrown away; the first append after that carries the old run into
    /// the new file, which is the last it is ever read.
    /// </remarks>
    public string LegacyFilePath { get; }

    public int MaxEntries { get; }

    /// <summary>Recorded runs, newest first. Empty when there is nothing readable.</summary>
    public IReadOnlyList<CleanReport> LoadHistory() =>
        File.Exists(FilePath) ? Read(FilePath)
        : File.Exists(LegacyFilePath) ? Read(LegacyFilePath)
        : [];

    private static IReadOnlyList<CleanReport> Read(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            if (root.TryGetProperty("version", out var versionElement)
                && versionElement.TryGetInt32(out var version)
                && version > CurrentVersion)
            {
                // A newer build wrote this. Reading it as the current shape would invent numbers
                // that were never recorded, which is worse than saying nothing.
                return [];
            }

            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                var history = new List<CleanReport>();

                foreach (var element in entries.EnumerateArray())
                {
                    if (element.Deserialize<ReportDto>(SerializerOptions) is { } dto)
                    {
                        history.Add(ToReport(dto));
                    }
                }

                return history;
            }

            // Version 1 wrote one report at the root of the file. Those are still readable, so an
            // upgrade does not throw away the run somebody was looking at.
            return root.Deserialize<ReportDto>(SerializerOptions) is { } legacy
                ? [ToReport(legacy)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>The most recent run, or null when nothing has been recorded.</summary>
    public CleanReport? LoadLast() => LoadHistory().FirstOrDefault();

    /// <summary>
    /// Adds a run to the history, newest first.
    /// </summary>
    /// <returns>False rather than throwing when the file cannot be written.</returns>
    public bool Append(CleanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var history = new List<CleanReport> { report };
        history.AddRange(LoadHistory());

        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var dto = new HistoryDto(
                CurrentVersion,
                [.. history.Take(MaxEntries).Select(ToDto)]);

            File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, SerializerOptions));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static ReportDto ToDto(CleanReport report) => new(
        report.TimestampUtc,
        report.Mode.ToString(),
        report.AttemptedCount,
        report.AttemptedBytes,
        report.RemovedCount,
        report.DeletedCount,
        report.RecycledCount,
        report.FreedBytes,
        report.RecycledBytes,
        report.FailedCount,

        // Stored as seconds rather than a TimeSpan so the file does not depend on how the
        // serializer happens to render one.
        report.Duration.TotalSeconds,

        report.RecycleBinBefore.SizeBytes,
        report.RecycleBinBefore.ItemCount,
        report.RecycleBinAfter.SizeBytes,
        report.RecycleBinAfter.ItemCount,
        report.Source);

    private static CleanReport ToReport(ReportDto dto) => new()
    {
        TimestampUtc = dto.TimestampUtc,
        Mode = Enum.TryParse<DeletionMode>(dto.Mode, ignoreCase: true, out var mode)
            ? mode
            : DeletionMode.RecycleBin,
        AttemptedCount = dto.AttemptedCount,
        AttemptedBytes = dto.AttemptedBytes,
        RemovedCount = dto.RemovedCount,
        DeletedCount = dto.DeletedCount,
        RecycledCount = dto.RecycledCount,
        FreedBytes = dto.FreedBytes,
        RecycledBytes = dto.RecycledBytes,
        FailedCount = dto.FailedCount,
        Duration = TimeSpan.FromSeconds(dto.DurationSeconds),
        RecycleBinBefore = new RecycleBinStats(dto.RecycleBinBeforeBytes, dto.RecycleBinBeforeItems),
        RecycleBinAfter = new RecycleBinStats(dto.RecycleBinAfterBytes, dto.RecycleBinAfterItems),
        Source = string.IsNullOrWhiteSpace(dto.Source) ? CleanReportSources.Cli : dto.Source,
    };

    private sealed record HistoryDto(int Version, ReportDto[] Entries);

    /// <summary>
    /// One entry. The version number is not part of it any more: it lives on the file, not on each
    /// run, so a version 1 file (which had it on the report) still deserialises into this shape
    /// with the extra property simply ignored.
    /// </summary>
    private sealed record ReportDto(
        DateTimeOffset TimestampUtc,
        string Mode,
        int AttemptedCount,
        long AttemptedBytes,
        int RemovedCount,
        int DeletedCount,
        int RecycledCount,
        long FreedBytes,
        long RecycledBytes,
        int FailedCount,
        double DurationSeconds,
        long RecycleBinBeforeBytes,
        long RecycleBinBeforeItems,
        long RecycleBinAfterBytes,
        long RecycleBinAfterItems,
        string? Source);
}
