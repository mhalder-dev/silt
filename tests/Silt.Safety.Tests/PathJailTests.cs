using Silt.Safety;

namespace Silt.Safety.Tests;

/// <summary>
/// Containment rules for the path jail.
/// </summary>
/// <remarks>
/// ⚠️ These run on Linux in CI for speed, where <c>\</c> is an ordinary filename character
/// and there is no drive concept — so the Windows-specific cases below degrade to noise
/// there. The Windows run is the authoritative one. Do not read a green Ubuntu job as
/// evidence that the jail holds.
/// </remarks>
public sealed class PathJailTests
{
    [Theory]
    [InlineData(@"C:\data", @"C:\data")]
    [InlineData(@"C:\data", @"C:\data\file.txt")]
    [InlineData(@"C:\data", @"C:\data\sub\deep\file.txt")]
    [InlineData(@"C:\data\", @"C:\data\file.txt")]
    [InlineData(@"C:\data", @"C:\DATA\FILE.TXT")]
    public void IsContained_AcceptsPathsInsideTheRoot(string root, string candidate)
    {
        Assert.True(PathJail.IsContained(root, candidate));
    }

    [Theory]
    // The classic prefix bug: a sibling whose name merely begins with the root's name.
    [InlineData(@"C:\data", @"C:\data-evil\file.txt")]
    [InlineData(@"C:\data", @"C:\database\file.txt")]
    // Traversal, which only fails to escape because both sides are canonicalized first.
    [InlineData(@"C:\data", @"C:\data\..\windows\system32")]
    [InlineData(@"C:\data", @"C:\data\..\..\Users")]
    // Unrelated locations.
    [InlineData(@"C:\data", @"C:\Windows")]
    [InlineData(@"C:\data", @"D:\data\file.txt")]
    public void IsContained_RejectsPathsOutsideTheRoot(string root, string candidate)
    {
        Assert.False(PathJail.IsContained(root, candidate));
    }

    [Theory]
    [InlineData(@"C:\", @"C:\Windows")]
    [InlineData(@"C:\", @"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"D:\", @"D:\projects\silt")]
    public void IsContained_HandlesAVolumeRootAsTheRoot(string root, string candidate)
    {
        // Regression, and a failure that was open rather than closed.
        //
        // Path.TrimEndingDirectorySeparator deliberately leaves "C:\" intact, so naively
        // appending a separator yielded the prefix "C:\\" and nothing was ever contained in
        // a drive root. A denylist entry covering an entire volume silently protected
        // nothing at all.
        Assert.True(PathJail.IsContained(root, candidate));
    }

    [Fact]
    public void IsContained_StillSeparatesDifferentVolumes()
    {
        Assert.False(PathJail.IsContained(@"C:\", @"D:\Windows"));
    }

    [Fact]
    public void IsContained_TreatsInternalTraversalThatStaysInsideAsContained()
    {
        // Ugly but legitimate: it canonicalizes back to a path within the root.
        Assert.True(PathJail.IsContained(@"C:\data", @"C:\data\sub\..\other\file.txt"));
    }

    [Fact]
    public void Require_ThrowsWithBothPathsNamed()
    {
        UnauthorizedAccessException ex = Assert.Throws<UnauthorizedAccessException>(
            () => PathJail.Require(@"C:\data", @"C:\Windows\System32", "delete"));

        // The message has to be actionable; "access denied" alone explains nothing.
        Assert.Contains(@"C:\Windows\System32", ex.Message, StringComparison.Ordinal);
        Assert.Contains("delete", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Require_DoesNotThrowForAContainedPath()
    {
        PathJail.Require(@"C:\data", @"C:\data\sub\file.txt", "write");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsContained_RejectsMissingArguments(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => PathJail.IsContained(@"C:\data", value!));
        Assert.ThrowsAny<ArgumentException>(() => PathJail.IsContained(value!, @"C:\data"));
    }
}
