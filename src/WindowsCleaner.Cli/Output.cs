using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Utilities;

namespace WindowsCleaner.Cli;

/// <summary>Renders scan and clean results as either a text report or JSON.</summary>
internal static class Output
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void WriteScanReport(ScanResult result, CliOptions options)
    {
        if (options.Json)
        {
            Write(ScanDto(result));
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  Space to reclaim : " + ByteSizeFormatter.Format(result.TotalSizeBytes));
        Console.WriteLine("  Files found      : " + result.Files.Count.ToString("N0"));
        Console.WriteLine($"  Locations        : {result.Targets.Count(t => t.Accessible)} of {result.Targets.Count} reachable");
        Console.WriteLine("  Scan time        : " + string.Create(null, $"{result.Duration.TotalSeconds:0.0}s"));

        // User-added folders are called out explicitly: they widen what a clean can touch, so
        // they should never be invisible in a report.
        var custom = result.Targets.Where(t => t.IsUserAdded).ToArray();
        if (custom.Length > 0)
        {
            Console.WriteLine("  Custom locations : " + string.Join(", ", custom.Select(t => t.Path)));
        }

        foreach (var target in result.Targets.Where(t => !t.Accessible))
        {
            Console.WriteLine($"      - {target.DisplayName}: {target.Error}");
        }

        var skipped = DescribeSkipped(result.Skipped);
        if (skipped.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  {result.SkippedCount:N0} entries skipped: {skipped}");
        }

        WriteFileList(result.Files, options.Top);
    }

    public static void WriteCleanReport(CleanResult result, CliOptions options)
    {
        if (options.Json)
        {
            Write(CleanDto(result));
            return;
        }

        Console.WriteLine();

        if (result.Mode == DeletionMode.RecycleBin)
        {
            // Deliberately not called "freed": the bytes are still on the volume until the bin
            // is emptied, and conflating the two would make the tool lie about disk space.
            Console.WriteLine("  Moved to Recycle Bin : " + ByteSizeFormatter.Format(result.RecycledBytes));
            Console.WriteLine("  Files moved          : " + result.RecycledCount.ToString("N0"));
            Console.WriteLine("  Empty the Recycle Bin to actually reclaim the space.");
        }
        else
        {
            Console.WriteLine("  Space freed : " + ByteSizeFormatter.Format(result.FreedBytes));
            Console.WriteLine("  Files removed : " + result.DeletedCount.ToString("N0"));
        }

        if (result.FailedCount > 0)
        {
            Console.WriteLine($"  Skipped     : {result.FailedCount:N0}");

            foreach (var group in result.Failures.GroupBy(f => f.Reason).OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"      - {group.Count():N0} {group.Key.ToLowerInvariant()}");
            }
        }

        Console.WriteLine("  Elapsed     : " + string.Create(null, $"{result.Duration.TotalSeconds:0.0}s"));
        Console.WriteLine();
    }

    public static void WriteFailureSummary(CleanResult result)
    {
        // Only the first few are worth showing; a temp folder can produce hundreds and the
        // grouped counts above already convey the shape of the problem.
        const int limit = 5;

        foreach (var failure in result.Failures.Take(limit))
        {
            Console.WriteLine($"      {failure.Path}  ({failure.Reason})");
        }

        if (result.Failures.Count > limit)
        {
            Console.WriteLine($"      ... and {result.Failures.Count - limit:N0} more");
        }
    }

    private static void WriteFileList(IReadOnlyList<JunkFile> files, int top)
    {
        if (top <= 0 || files.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  Largest files");

        foreach (var file in files.Take(top))
        {
            Console.WriteLine($"    {ByteSizeFormatter.Format(file.SizeBytes),10}  {file.FullPath}");
        }

        if (files.Count > top)
        {
            Console.WriteLine($"    ... and {files.Count - top:N0} more");
        }
    }

    private static string DescribeSkipped(IReadOnlyDictionary<SkipReason, int> skipped) =>
        string.Join(", ", skipped
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Value:N0} {SkipReasonFormatter.Describe(kv.Key)}"));

    private static void Write(object dto) => Console.WriteLine(JsonSerializer.Serialize(dto, JsonOptions));

    private static ScanReportDto ScanDto(ScanResult result) => new(
        "scan",
        result.TotalSizeBytes,
        result.Files.Count,
        result.Duration.TotalSeconds,
        result.Targets.Select(t => new LocationDto(t.DisplayName, t.Path, t.Accessible, t.Error, t.FileCount, t.TotalBytes)).ToArray(),
        result.Skipped.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        result.Files.Select(f => new FileDto(f.FullPath, f.SizeBytes, f.LastWriteTimeUtc, f.TargetName)).ToArray());

    private static CleanReportDto CleanDto(CleanResult result) => new(
        "clean",
        result.Mode.ToString(),
        result.FreedBytes,
        result.RecycledBytes,
        result.DeletedCount,
        result.RecycledCount,
        result.FailedCount,
        result.Duration.TotalSeconds,
        result.Failures.Select(f => new FailureDto(f.Path, f.Reason)).ToArray());

    private sealed record ScanReportDto(
        string Command,
        long TotalBytes,
        int TotalFiles,
        double DurationSeconds,
        LocationDto[] Locations,
        Dictionary<string, int> Skipped,
        FileDto[] Files);

    private sealed record LocationDto(string Name, string Path, bool Accessible, string? Error, int Files, long Bytes);

    private sealed record FileDto(string Path, long Bytes, DateTime LastWriteUtc, string Target);

    private sealed record CleanReportDto(
        string Command,
        string Mode,
        long FreedBytes,
        long RecycledBytes,
        int DeletedCount,
        int RecycledCount,
        int FailedCount,
        double DurationSeconds,
        FailureDto[] Failures);

    private sealed record FailureDto(string Path, string Reason);
}
