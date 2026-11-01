using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Utilities;

namespace WindowsCleaner.App.ViewModels;

/// <summary>One run in the cleaning history, pre-formatted for display.</summary>
/// <remarks>
/// Formatted here rather than through converters in the view: the wording of "moved" versus
/// "freed" is the same distinction the rest of the app is careful about, and it belongs next to
/// the model that draws it rather than spread across a template.
/// </remarks>
public sealed record HistoryRow(string When, string Origin, string Result)
{
    public static HistoryRow From(CleanReport report, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(report);

        var result = report.Mode == DeletionMode.RecycleBin
            ? $"Moved {ByteSizeFormatter.Format(report.RecycledBytes)} \u00b7 {report.RecycledCount:N0} files"
            : $"Freed {ByteSizeFormatter.Format(report.FreedBytes)} \u00b7 {report.DeletedCount:N0} files";

        if (report.FailedCount > 0)
        {
            result += $" \u00b7 {report.FailedCount:N0} skipped";
        }

        return new HistoryRow(
            Humanize.Age(now - report.TimestampUtc),
            CleanReportSources.Describe(report.Source),
            result);
    }
}
