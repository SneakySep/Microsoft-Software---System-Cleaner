using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Scheduling;

namespace WindowsCleaner.Tests;

/// <summary>
/// Reading the scheduled job back out of Task Scheduler.
/// </summary>
/// <remarks>
/// The fixtures below are real <c>schtasks /Query /XML</c> output, captured from a job this app
/// registered, not hand-written XML. That matters: the panel that shows the user what is
/// scheduled is only as trustworthy as this parser, and a hand-written fixture would encode the
/// author's assumptions about the format rather than the format.
/// </remarks>
public sealed class ScheduledTaskSnapshotTests
{
    private const string DailyDefinition = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Date>2026-10-28T00:12:19</Date>
            <Author>ROBLOXGOD\User</Author>
            <URI>\WindowsCleaner\ScheduledClean</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-21-1537349290-242735569-1628657486-1000</UserId>
              <LogonType>InteractiveToken</LogonType>
            </Principal>
          </Principals>
          <Settings>
            <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <IdleSettings>
              <Duration>PT10M</Duration>
              <WaitTimeout>PT1H</WaitTimeout>
              <StopOnIdleEnd>true</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
          </Settings>
          <Triggers>
            <CalendarTrigger>
              <StartBoundary>2026-10-28T03:00:00</StartBoundary>
              <ScheduleByDay>
                <DaysInterval>1</DaysInterval>
              </ScheduleByDay>
            </CalendarTrigger>
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>"E:\Xampp\Files\htdocs\WindowsCleaner\src\WindowsCleaner.Cli\bin\Debug\net10.0\wclean.exe"</Command>
              <Arguments>clean --yes --mode permanent --min-age 1440</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    private const string WeeklyDefinition = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Date>2026-10-28T00:13:02</Date>
            <URI>\WindowsCleaner\ScheduledClean</URI>
          </RegistrationInfo>
          <Triggers>
            <CalendarTrigger>
              <StartBoundary>2026-10-28T21:30:00</StartBoundary>
              <ScheduleByWeek>
                <WeeksInterval>1</WeeksInterval>
                <DaysOfWeek>
                  <Thursday />
                </DaysOfWeek>
              </ScheduleByWeek>
            </CalendarTrigger>
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>"E:\Xampp\Files\htdocs\WindowsCleaner\src\WindowsCleaner.Cli\bin\Debug\net10.0\wclean.exe"</Command>
              <Arguments>clean --yes --mode recycle --min-age 720</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    [Fact]
    public void Reads_a_daily_job_as_the_scheduler_stores_it()
    {
        var parsed = Parse(DailyDefinition);

        Assert.Equal(ScheduleFrequency.Daily, parsed.Frequency);
        Assert.Equal(new TimeSpan(3, 0, 0), parsed.TimeOfDay);
        Assert.Null(parsed.Day);

        // The fixture's command spells out --mode permanent, so that is what it reports.
        Assert.Equal(DeletionMode.Permanent, parsed.Mode);
        Assert.Equal(TimeSpan.FromHours(24), parsed.MinimumAge);
        Assert.Equal("every day at 03:00", parsed.DescribeSchedule());
    }

    [Fact]
    public void Reads_a_weekly_job_as_the_scheduler_stores_it()
    {
        var parsed = Parse(WeeklyDefinition);

        Assert.Equal(ScheduleFrequency.Weekly, parsed.Frequency);
        Assert.Equal(DayOfWeek.Thursday, parsed.Day);
        Assert.Equal(new TimeSpan(21, 30, 0), parsed.TimeOfDay);
        Assert.Equal(DeletionMode.RecycleBin, parsed.Mode);
        Assert.Equal(TimeSpan.FromHours(12), parsed.MinimumAge);
        Assert.Equal("every Thursday at 21:30", parsed.DescribeSchedule());
    }

    [Fact]
    public void Strips_the_quotes_the_scheduler_puts_around_the_executable_path()
    {
        var parsed = Parse(DailyDefinition);

        // The stored value is quoted because it is a command line; the path itself is not.
        Assert.DoesNotContain('"', parsed.ExecutablePath);
        Assert.EndsWith("wclean.exe", parsed.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wclean.exe", parsed.DescribeCommand(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Does_not_depend_on_the_schema_namespace_being_present()
    {
        const string minimal = """
            <Task>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>2026-01-01T07:15:00</StartBoundary>
                  <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
              <Actions>
                <Exec>
                  <Command>wclean.exe</Command>
                  <Arguments>clean --yes</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var parsed = Parse(minimal);

        Assert.Equal(new TimeSpan(7, 15, 0), parsed.TimeOfDay);
        Assert.Equal("wclean.exe", parsed.ExecutablePath);
    }

    [Theory]
    [InlineData("clean --yes --mode=recycle --min-age=90", DeletionMode.RecycleBin, 90)]
    [InlineData("clean --yes --mode permanent", DeletionMode.Permanent, 0)]

    // No --mode at all: the command recycles, because that is the CLI's default for clean. The
    // app repeats this back in a confirmation, so it has to describe what will really happen.
    [InlineData("clean --yes", DeletionMode.RecycleBin, 0)]
    public void Recovers_the_disposal_mode_and_guard_from_the_command_line(
        string arguments,
        DeletionMode expectedMode,
        int expectedMinutes)
    {
        var parsed = Parse(DailyDefinition.Replace(
            "clean --yes --mode permanent --min-age 1440",
            arguments,
            StringComparison.Ordinal));

        Assert.Equal(expectedMode, parsed.Mode);
        Assert.Equal(expectedMinutes == 0 ? null : TimeSpan.FromMinutes(expectedMinutes), parsed.MinimumAge);
    }

    [Fact]
    public void Reports_an_unreadable_definition_instead_of_a_wrong_one()
    {
        Assert.False(ScheduledTaskSnapshot.TryParse("{ not xml at all", out var snapshot, out var error));
        Assert.Null(snapshot);
        Assert.NotNull(error);
    }

    [Fact]
    public void Fails_when_the_job_has_nothing_to_run()
    {
        const string noAction = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>2026-01-01T03:00:00</StartBoundary>
                  <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
            </Task>
            """;

        Assert.False(ScheduledTaskSnapshot.TryParse(noAction, out _, out var error));
        Assert.Contains("action", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fails_on_a_trigger_this_app_never_creates()
    {
        const string logonTrigger = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger>
                  <StartBoundary>2026-01-01T03:00:00</StartBoundary>
                </LogonTrigger>
              </Triggers>
              <Actions>
                <Exec>
                  <Command>wclean.exe</Command>
                  <Arguments>clean --yes</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        // Better to admit the trigger is not understood than to describe it as daily: a panel
        // claiming "every day at 03:00" for a job that actually runs at logon would be worse
        // than saying nothing.
        Assert.False(ScheduledTaskSnapshot.TryParse(logonTrigger, out var snapshot, out var error));
        Assert.Null(snapshot);
        Assert.Contains("trigger", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Works_out_the_next_daily_run_as_today_or_tomorrow()
    {
        var parsed = Parse(DailyDefinition);

        // Built at the machine's own offset, because the schedule is stored as local wall-clock
        // time and that is what "tomorrow at 03:00" has to mean here.
        var before = LocalTime(2026, 10, 28, 1, 0);
        var after = LocalTime(2026, 10, 28, 4, 0);

        Assert.Equal(LocalTime(2026, 10, 28, 3, 0), parsed.GetNextRun(before));
        Assert.Equal(LocalTime(2026, 10, 29, 3, 0), parsed.GetNextRun(after));
    }

    [Fact]
    public void Works_out_the_next_weekly_run()
    {
        var parsed = Parse(WeeklyDefinition);

        // The weekly fixture runs Thursdays at 21:30. 28 October 2026 is a Wednesday.
        Assert.Equal(LocalTime(2026, 10, 29, 21, 30), parsed.GetNextRun(LocalTime(2026, 10, 28, 23, 0)));

        // Once that Thursday's run has been and gone, it is a week later.
        Assert.Equal(LocalTime(2026, 11, 5, 21, 30), parsed.GetNextRun(LocalTime(2026, 10, 29, 22, 0)));
    }

    [Fact]
    public void Declines_to_guess_the_next_run_for_a_longer_repeat_interval()
    {
        // A job edited elsewhere to run every three days. Those runs are counted from the original
        // start date, so the honest answer is that this app does not know when the next one is.
        var parsed = Parse(DailyDefinition.Replace(
            "<DaysInterval>1</DaysInterval>",
            "<DaysInterval>3</DaysInterval>",
            StringComparison.Ordinal));

        Assert.Equal(3, parsed.Interval);
        Assert.Null(parsed.GetNextRun(LocalTime(2026, 10, 28, 1, 0)));
        Assert.Contains("every 3 days", parsed.DescribeSchedule());
    }

    [Fact]
    public void Keeps_a_quoted_argument_with_spaces_together()
    {
        // The definition stores its arguments as one string. Handing that over whole would turn a
        // quoted path containing a space into several arguments.
        var parts = ScheduledTaskSnapshot.SplitArguments(
            """clean --yes --target "C:\Program Files\cache" --min-age 60""");

        Assert.Equal(["clean", "--yes", "--target", @"C:\Program Files\cache", "--min-age", "60"], parts);
    }

    [Fact]
    public void Splits_nothing_out_of_nothing()
    {
        Assert.Empty(ScheduledTaskSnapshot.SplitArguments(null));
        Assert.Empty(ScheduledTaskSnapshot.SplitArguments("   "));
    }

    [Fact]
    public void Keeps_an_empty_quoted_argument()
    {
        // Dropping it would shift every argument after it.
        Assert.Equal(["a", string.Empty, "b"], ScheduledTaskSnapshot.SplitArguments("""a "" b"""));
    }

    private static DateTimeOffset LocalTime(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static ScheduledTaskSnapshot Parse(string xml)
    {
        Assert.True(ScheduledTaskSnapshot.TryParse(xml, out var snapshot, out var error), error);
        return snapshot!;
    }
}
