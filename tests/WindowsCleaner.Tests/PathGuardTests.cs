using WindowsCleaner.Core.Safety;

namespace WindowsCleaner.Tests;

/// <summary>
/// The allowlist is the only thing standing between "temp cleaner" and "delete my documents",
/// so it gets tested against the specific ways a prefix check is usually broken.
/// </summary>
public sealed class PathGuardTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "wc-guard-root");

    [Fact]
    public void Accepts_a_file_strictly_inside_the_root()
    {
        var candidate = Path.Combine(Root, "sub", "file.tmp");

        Assert.True(PathGuard.IsStrictlyWithin(candidate, Root));
    }

    [Fact]
    public void Rejects_the_root_directory_itself()
    {
        // The root must never itself be a valid target, or "clean temp" would delete %TEMP%.
        Assert.False(PathGuard.IsStrictlyWithin(Root, Root));
    }

    [Fact]
    public void Rejects_a_sibling_directory_that_shares_the_root_prefix()
    {
        // The classic prefix bug: "C:\TempEvil" starts with "C:\Temp" as a plain string.
        var sibling = Path.Combine(Path.GetTempPath(), "wc-guard-root-evil", "file.tmp");

        Assert.False(PathGuard.IsStrictlyWithin(sibling, Root));
    }

    [Fact]
    public void Rejects_a_path_that_escapes_via_parent_segments()
    {
        var escaping = Path.Combine(Root, "..", "..", "Windows", "System32", "kernel32.dll");

        Assert.False(PathGuard.IsStrictlyWithin(escaping, Root));
    }

    [Fact]
    public void Rejects_an_unrelated_absolute_path()
    {
        Assert.False(PathGuard.IsStrictlyWithin(@"C:\Windows\System32\config\SAM", Root));
    }

    [Fact]
    public void Is_case_insensitive_on_windows()
    {
        var candidate = Path.Combine(Root.ToUpperInvariant(), "File.tmp");

        Assert.True(PathGuard.IsStrictlyWithin(candidate, Root.ToLowerInvariant()));
    }

    [Fact]
    public void EnsureWithin_throws_a_descriptive_exception_for_an_outside_path()
    {
        var outside = @"C:\Windows\System32\drivers\etc\hosts";

        var ex = Assert.Throws<UnsafePathException>(() =>
            PathGuard.EnsureWithin(outside, [Root]));

        Assert.Equal(outside, ex.AttemptedPath);
        Assert.Contains("allowlisted", ex.Message);
    }

    [Fact]
    public void EnsureWithin_accepts_a_path_inside_any_of_several_roots()
    {
        var other = Path.Combine(Path.GetTempPath(), "wc-guard-other");
        var candidate = Path.Combine(other, "file.tmp");

        PathGuard.EnsureWithin(candidate, [Root, other]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_null_or_blank_paths(string? candidate)
    {
        Assert.False(PathGuard.IsStrictlyWithin(candidate!, Root));
    }

    [Fact]
    public void GetRejectionReason_returns_null_for_a_safe_path()
    {
        var candidate = Path.Combine(Root, "file.tmp");

        Assert.Null(PathGuard.GetRejectionReason(candidate, [Root]));
    }

    [Fact]
    public void GetRejectionReason_explains_why_an_outside_path_is_refused()
    {
        var reason = PathGuard.GetRejectionReason(@"C:\Windows\notepad.exe", [Root]);

        Assert.NotNull(reason);
        Assert.Contains("allowlisted", reason);
    }

    [Fact]
    public void NormalizeRoot_produces_a_canonical_form_without_a_trailing_separator()
    {
        var normalized = PathGuard.NormalizeRoot(Root + Path.DirectorySeparatorChar);

        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), normalized[^1].ToString());
        Assert.Equal(Path.GetFullPath(Root), normalized);
    }

    [Fact]
    public void Handles_a_drive_root_without_doubling_the_separator()
    {
        // "C:\" already ends in a separator; naively appending another builds "C:\\" and then
        // matches nothing, which would silently disable the guard for whole-volume roots.
        var drive = Path.GetPathRoot(Path.GetTempPath());
        Assert.NotNull(drive);

        Assert.True(PathGuard.IsStrictlyWithin(Path.GetTempPath(), drive));
        Assert.False(PathGuard.IsStrictlyWithin(drive, drive));
    }

    [Fact]
    public void Recognises_the_reparse_point_attribute()
    {
        Assert.True(PathGuard.IsReparsePoint(FileAttributes.ReparsePoint | FileAttributes.Directory));
        Assert.False(PathGuard.IsReparsePoint(FileAttributes.Normal));
        Assert.False(PathGuard.IsReparsePoint(FileAttributes.Directory | FileAttributes.Hidden));
    }
}
