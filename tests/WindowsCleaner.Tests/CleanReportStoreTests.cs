using WindowsCleaner.Core.Configuration;
using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Tests;

/// <summary>
/// The record of previous cleans.
/// </summary>
/// <remarks>
/// A history rather than only the latest run, because that is what lets an unattended clean be
/// found afterwards, and what shows whether the cleaning is keeping up.
/// </remarks>
public sealed class CleanReportStoreTests
{
    [Fact]
    public void Round_trips_every_value_the_dashboard_reports()
    {
        using var sandbox = new Sandbox();
        var store = new CleanReportStore(Path.Combine(sandbox.Root, "last-clean.json"));

        var report = new CleanReport
        {
            TimestampUtc = new DateTimeOffset(2026, 10, 28, 3, 0, 4, TimeSpan.Zero),
            Mode = DeletionMode.RecycleBin,
            AttemptedCount = 12,
            AttemptedBytes = 4096,
            RemovedCount = 11,
            DeletedCount = 0,
            RecycledCount = 11,
            FreedBytes = 0,
            RecycledBytes = 3072,
            FailedCount = 1,
            Duration = TimeSpan.FromSeconds(1.25),
            RecycleBinBefore = new RecycleBinStats(1000, 1),
            RecycleBinAfter = new RecycleBinStats(4072, 12),
            Source = CleanReportSources.Scheduled,
        };

        Assert.True(store.Append(report));

        var loaded = store.LoadLast();

        Assert.Equal(report, loaded);

        // Spelled out as well, because these are the values the UI reads and a silent mismatch in
        // any of them would show the user a wrong account of their own disk.
        Assert.NotNull(loaded);
        Assert.Equal(DeletionMode.RecycleBin, loaded.Mode);
        Assert.Equal(TimeSpan.FromSeconds(1.25), loaded.Duration);
        Assert.Equal(0, loaded.ReclaimedBytes);
        Assert.Equal(new RecycleBinStats(1000, 1), loaded.RecycleBinBefore);
        Assert.Equal(new RecycleBinStats(4072, 12), loaded.RecycleBinAfter);
        Assert.Equal(CleanReportSources.Scheduled, loaded.Source);
    }

    [Fact]
    public void Keeps_the_most_recent_runs_newest_first()
    {
        using var sandbox = new Sandbox();
        var store = new CleanReportStore(Path.Combine(sandbox.Root, "last-clean.json"));

        for (var index = 0; index < 3; index++)
        {
            Assert.True(store.Append(Report(freedBytes: (index + 1) * 100)));
        }

        var history = store.LoadHistory();

        Assert.Equal([300L, 200L, 100L], history.Select(report => report.FreedBytes));
        Assert.Equal(300, store.LoadLast()!.FreedBytes);
    }

    [Fact]
    public void Drops_the_oldest_runs_beyond_the_cap()
    {
        using var sandbox = new Sandbox();
        var store = new CleanReportStore(Path.Combine(sandbox.Root, "last-clean.json"), maxEntries: 3);

        for (var index = 0; index < 6; index++)
        {
            store.Append(Report(freedBytes: (index + 1) * 100));
        }

        var history = store.LoadHistory();

        // Capped, and the newest are the ones kept.
        Assert.Equal(3, history.Count);
        Assert.Equal([600L, 500L, 400L], history.Select(report => report.FreedBytes));
    }

    [Fact]
    public void Reads_a_file_written_before_the_history_existed()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "last-clean.json");

        // The version 1 shape: one report at the root of the file, with the version on the report.
        File.WriteAllText(path, """
            {
              "version": 1,
              "timestampUtc": "2026-10-28T03:00:00+00:00",
              "mode": "Permanent",
              "attemptedCount": 3,
              "attemptedBytes": 4096,
              "removedCount": 3,
              "deletedCount": 3,
              "recycledCount": 0,
              "freedBytes": 4096,
              "recycledBytes": 0,
              "failedCount": 0,
              "durationSeconds": 1.5,
              "recycleBinBeforeBytes": 100,
              "recycleBinBeforeItems": 1,
              "recycleBinAfterBytes": 100,
              "recycleBinAfterItems": 1,
              "source": "cli"
            }
            """);

        // An upgrade must not throw away the run somebody was looking at.
        var entry = Assert.Single(new CleanReportStore(path).LoadHistory());

        Assert.Equal(4096, entry.FreedBytes);
        Assert.Equal(DeletionMode.Permanent, entry.Mode);
        Assert.Equal(TimeSpan.FromSeconds(1.5), entry.Duration);
        Assert.Equal(CleanReportSources.Cli, entry.Source);
    }

    [Fact]
    public void Reads_the_file_from_before_the_history_was_renamed()
    {
        using var sandbox = new Sandbox();
        var store = new CleanReportStore(Path.Combine(sandbox.Root, "last-clean.json"));

        // The old name held exactly the version 1 shape too: one report at the root.
        File.WriteAllText(store.LegacyFilePath, """
            { "version": 1, "mode": "RecycleBin", "freedBytes": 0, "recycledBytes": 2048, "source": "cli" }
            """);

        var entry = Assert.Single(store.LoadHistory());

        Assert.Equal(2048, entry.RecycledBytes);
    }

    [Fact]
    public void The_old_file_is_folded_in_once_and_read_no_more()
    {
        using var sandbox = new Sandbox();
        var store = new CleanReportStore(Path.Combine(sandbox.Root, "last-clean.json"));
        File.WriteAllText(store.LegacyFilePath, """{ "version": 1, "freedBytes": 999 }""");

        store.Append(Report(freedBytes: 100));

        // The old run is carried over rather than dropped, and from here on the new file is the
        // only source: otherwise the left-behind one would be reported for ever.
        Assert.Equal([100L, 999L], store.LoadHistory().Select(report => report.FreedBytes));

        store.Append(Report(freedBytes: 200));
        Assert.Equal([200L, 100L, 999L], store.LoadHistory().Select(report => report.FreedBytes));
    }

    [Fact]
    public void Keeps_reading_after_the_upgrade_has_rewritten_the_file()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "last-clean.json");
        var store = new CleanReportStore(path);

        store.Append(Report(freedBytes: 100));
        store.Append(Report(freedBytes: 200));

        Assert.Equal([200L, 100L], store.LoadHistory().Select(report => report.FreedBytes));
    }

    [Fact]
    public void Returns_nothing_when_no_clean_has_been_recorded()
    {
        using var sandbox = new Sandbox();
        var store = new CleanReportStore(Path.Combine(sandbox.Root, "last-clean.json"));

        Assert.Empty(store.LoadHistory());
        Assert.Null(store.LoadLast());
    }

    [Fact]
    public void Returns_nothing_when_the_file_is_corrupt()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "last-clean.json");
        File.WriteAllText(path, "{ truncated");

        Assert.Empty(new CleanReportStore(path).LoadHistory());
    }

    [Fact]
    public void Ignores_a_file_written_by_a_newer_version()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "last-clean.json");
        File.WriteAllText(path, """{ "version": 99, "entries": [ { "mode": "RecycleBin" } ] }""");

        // Reading it as if it were the current shape would invent numbers that were never
        // recorded, which is worse than saying nothing.
        Assert.Empty(new CleanReportStore(path).LoadHistory());
    }

    [Fact]
    public void A_corrupt_file_does_not_stop_the_next_run_being_recorded()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "last-clean.json");
        File.WriteAllText(path, "{ truncated");
        var store = new CleanReportStore(path);

        Assert.True(store.Append(Report(freedBytes: 42)));
        Assert.Equal(42, store.LoadLast()!.FreedBytes);
    }

    [Fact]
    public void Creates_the_containing_directory_on_first_save()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "nested", "deeper", "last-clean.json");

        var saved = new CleanReportStore(path).Append(Report(freedBytes: 10));

        Assert.True(saved);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Reports_failure_instead_of_throwing_when_it_cannot_write()
    {
        using var sandbox = new Sandbox();

        // A file where the directory needs to be makes the write impossible.
        var blocker = Path.Combine(sandbox.Root, "blocker");
        File.WriteAllText(blocker, "not a directory");

        var saved = new CleanReportStore(Path.Combine(blocker, "last-clean.json")).Append(Report(freedBytes: 10));

        // Recording the run is a convenience; failing to record it must never fail a clean.
        Assert.False(saved);
    }

    [Fact]
    public void Defaults_to_roaming_app_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowsCleaner",
            "last-clean.json");

        Assert.Equal(expected, CleanReportStore.DefaultFilePath);
    }

    private static CleanReport Report(long freedBytes) => new()
    {
        TimestampUtc = DateTimeOffset.UtcNow,
        Mode = DeletionMode.Permanent,
        AttemptedCount = 1,
        AttemptedBytes = freedBytes,
        RemovedCount = 1,
        DeletedCount = 1,
        RecycledCount = 0,
        FreedBytes = freedBytes,
        RecycledBytes = 0,
        FailedCount = 0,
        Duration = TimeSpan.FromMilliseconds(5),
    };
}
