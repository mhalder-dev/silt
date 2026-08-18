using System.Runtime.InteropServices;
using Silt.Core.Duplicates;
using Silt.Safety;

namespace Silt.Core.Tests;

/// <summary>
/// Behavioural tests for the duplicate search, run against a real scratch tree.
/// </summary>
/// <remarks>
/// <para>
/// Real files, not a mocked filesystem. The whole engine is a set of decisions about what
/// the filesystem reports - logical size versus allocation, file ids, reparse attributes -
/// and a fake that answers those questions the way the code expects proves only that the
/// code agrees with itself.
/// </para>
/// <para>
/// Each test that gates a stage of the funnel was checked red before it was checked green:
/// the ones marked below were confirmed to fail against a deliberately broken version of the
/// stage they cover. A geometry test that has only ever been observed green gates nothing -
/// the same lesson §5e recorded for the treemap.
/// </para>
/// </remarks>
public sealed class DuplicateFinderTests : IDisposable
{
    private readonly string _root;

    public DuplicateFinderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "silt-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A test scratch directory that will not delete is not a test failure.
        }
    }

    private string Write(string relativePath, byte[] contents)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, contents);
        return full;
    }

    /// <summary>Deterministic filler; a constant byte would hide an offset bug in the hash.</summary>
    private static byte[] Pattern(int length, int seed)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) + seed);
        }

        return bytes;
    }

    private DuplicateResult Find(long minimumSize = 1, bool verify = true, Denylist? denylist = null) =>
        new DuplicateFinder().Find(new DuplicateOptions
        {
            RootPath = _root,
            MinimumFileSize = minimumSize,
            VerifyByteForByte = verify,
            Denylist = denylist,
        });

    [Fact]
    public void Find_GroupsIdenticalFilesAndReportsWhatOneCopyWouldFree()
    {
        byte[] content = Pattern(50_000, seed: 7);
        Write("a.bin", content);
        Write(Path.Combine("sub", "b.bin"), content);
        Write(Path.Combine("sub", "deep", "c.bin"), content);

        DuplicateResult result = Find();

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.Equal(3, group.Paths.Count);
        Assert.Equal(50_000, group.SizeBytes);

        // Three copies free two copies' worth, not three. Reporting the group's total size
        // would overstate every finding by the one copy that has to stay.
        Assert.Equal(100_000, group.ReclaimableBytes);
        Assert.Equal(100_000, result.TotalReclaimableBytes);
    }

    [Fact]
    public void Find_IgnoresFilesWithAUniqueSize()
    {
        Write("a.bin", Pattern(10_000, seed: 1));
        Write("b.bin", Pattern(10_001, seed: 1));

        DuplicateResult result = Find();

        Assert.Empty(result.Groups);

        // Nothing shared a size, so the funnel must not have opened a single file.
        Assert.Equal(0, result.CandidateFiles);
        Assert.Equal(0, result.BytesRead);
    }

    /// <summary>
    /// Gates the head-hash stage. Verified red by making the head hash a constant: both
    /// files then reached the full-hash stage and the assertion on BytesRead failed.
    /// </summary>
    [Fact]
    public void Find_SeparatesSameSizedFilesThatDifferAtTheStart()
    {
        Write("a.bin", Pattern(80_000, seed: 1));
        Write("b.bin", Pattern(80_000, seed: 2));

        DuplicateResult result = Find();

        Assert.Empty(result.Groups);
        Assert.Equal(2, result.CandidateFiles);

        // Both were culled by the head sample, so only two clusters were read - not the
        // 160 KB a naive implementation would have hashed in full.
        Assert.Equal(2 * DuplicateFinder.HeadSampleBytes, result.BytesRead);
    }

    /// <summary>
    /// Gates the full-hash stage, with byte verification deliberately OFF.
    /// </summary>
    /// <remarks>
    /// The first version of this test left verification on and passed against a build with
    /// the full-hash pass deleted entirely - the byte comparison quietly caught what the
    /// missing stage let through, so the test gated the backstop rather than the stage it
    /// named. With verification off the hash funnel has to produce the answer alone, and
    /// deleting the second Refine call now fails it.
    /// </remarks>
    [Fact]
    public void Find_SeparatesFilesThatDifferOnlyAfterTheHeadSample()
    {
        byte[] left = Pattern(80_000, seed: 3);
        byte[] right = (byte[])left.Clone();
        right[^1] ^= 0xFF;

        Write("a.bin", left);
        Write("b.bin", right);

        DuplicateResult result = Find(verify: false);

        Assert.Empty(result.Groups);
        Assert.Equal(2, result.CandidateFiles);

        // Head sample each, then a full read each. Nothing more, because nothing survived.
        Assert.Equal((2 * DuplicateFinder.HeadSampleBytes) + (2 * 80_000), result.BytesRead);
    }

    // NOTE - the byte comparison's REJECTION path is deliberately not tested, because it
    // cannot be reached by any input this suite can construct. It only ever fires on a
    // SHA-256 collision or a file mutated mid-search; a pair that differs anywhere at all is
    // already separated by the full hash, which is why an earlier attempt at this test
    // passed against a build that ignored VerifyByteForByte entirely. What IS gated is that
    // verification happens - Find_ReadsEveryConfirmedByteAgainWhenVerificationIsOn measures
    // the second read - and that a confirmed group survives it. The rejection branch itself
    // is unexercised and recorded as such in docs/PLAN.md §5i.

    [Fact]
    public void Find_GroupsFilesThatAreIdenticalOnlyBeyondTheHeadSample()
    {
        byte[] content = Pattern(80_000, seed: 4);
        Write("a.bin", content);
        Write("b.bin", content);

        DuplicateGroup group = Assert.Single(Find().Groups);

        Assert.Equal(2, group.Paths.Count);
    }

    /// <summary>
    /// Files at exactly the head-sample size take the shortcut that skips the full-hash
    /// read. That shortcut must not turn into "assume the group is identical". Verified red
    /// by making the shortcut return the group without the head pass having split it.
    /// </summary>
    [Fact]
    public void Find_StillSeparatesDifferingFilesAtExactlyTheHeadSampleSize()
    {
        Write("a.bin", Pattern(DuplicateFinder.HeadSampleBytes, seed: 5));
        Write("b.bin", Pattern(DuplicateFinder.HeadSampleBytes, seed: 6));

        // Verification off for the same reason as the full-hash test above: with it on, the
        // byte comparison rejects the pair even when the shortcut hands it over unsplit, and
        // the read total coincidentally matches. The shortcut has to be right on its own.
        DuplicateResult result = Find(verify: false);

        Assert.Empty(result.Groups);

        // One read each and no more: the head hash of a 4 KiB file already is its full hash.
        Assert.Equal(2 * DuplicateFinder.HeadSampleBytes, result.BytesRead);
    }

    [Fact]
    public void Find_SkipsFilesBelowTheMinimumSize()
    {
        byte[] content = Pattern(500, seed: 8);
        Write("a.bin", content);
        Write("b.bin", content);

        Assert.Empty(Find(minimumSize: 4096).Groups);
        Assert.Single(Find(minimumSize: 1).Groups);
    }

    /// <summary>
    /// Hardlinks are the same bytes on disk. Deleting one frees nothing, so reporting the
    /// pair as a duplicate would promise space that does not exist - the exact failure the
    /// scanner's file-id de-duplication exists to avoid, arriving by another route.
    /// </summary>
    [Fact]
    public void Find_DoesNotReportHardLinksToTheSameFile()
    {
        string original = Write("a.bin", Pattern(20_000, seed: 9));
        string link = Path.Combine(_root, "b.bin");

        Assert.True(
            CreateHardLinkW(link, original, IntPtr.Zero),
            $"CreateHardLink failed with {Marshal.GetLastWin32Error()}; the test cannot prove anything without it.");

        DuplicateResult result = Find();

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.HardLinksCollapsed);

        // Collapsed before hashing, not after: the point is to avoid the read as well.
        Assert.Equal(0, result.BytesRead);
    }

    [Fact]
    public void Find_ExcludesFilesTheDenylistRefuses()
    {
        byte[] content = Pattern(20_000, seed: 10);
        Write(Path.Combine("keep", "a.bin"), content);
        Write(Path.Combine("keep", "b.bin"), content);

        byte[] secret = Pattern(20_000, seed: 11);
        Write(Path.Combine("keep", "id_rsa"), secret);
        Write(Path.Combine("keep", "id_rsa.backup"), secret);

        var denylist = new Denylist([]);
        DuplicateResult result = Find(denylist: denylist);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.All(group.Paths, path => Assert.EndsWith(".bin", path, StringComparison.Ordinal));
        Assert.Equal(2, result.DeniedFilesSkipped);
    }

    /// <summary>
    /// Byte-for-byte verification is a second full read of what survived hashing. It cannot
    /// be observed through the group list - correct code agrees with itself either way - so
    /// it is gated on the cost it incurs. Verified red by ignoring VerifyByteForByte.
    /// </summary>
    [Fact]
    public void Find_ReadsEveryConfirmedByteAgainWhenVerificationIsOn()
    {
        byte[] content = Pattern(80_000, seed: 12);
        Write("a.bin", content);
        Write("b.bin", content);

        DuplicateResult hashOnly = Find(verify: false);
        DuplicateResult verified = Find(verify: true);

        Assert.Single(hashOnly.Groups);
        Assert.Single(verified.Groups);

        // Head sample plus full hash for each file, and nothing more.
        Assert.Equal((2 * DuplicateFinder.HeadSampleBytes) + (2 * 80_000), hashOnly.BytesRead);

        // Verification reads both files once more, in full.
        Assert.Equal(hashOnly.BytesRead + (2 * 80_000), verified.BytesRead);
    }

    [Fact]
    public void Find_OrdersGroupsByWhatTheyWouldFree()
    {
        Write(Path.Combine("small", "a.bin"), Pattern(10_000, seed: 13));
        Write(Path.Combine("small", "b.bin"), Pattern(10_000, seed: 13));

        Write(Path.Combine("big", "a.bin"), Pattern(90_000, seed: 14));
        Write(Path.Combine("big", "b.bin"), Pattern(90_000, seed: 14));

        DuplicateResult result = Find();

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(90_000, result.Groups[0].SizeBytes);
        Assert.Equal(10_000, result.Groups[1].SizeBytes);
        Assert.Equal(100_000, result.TotalReclaimableBytes);
    }

    [Fact]
    public void Find_ListsTheLikelyOriginalFirst()
    {
        byte[] content = Pattern(20_000, seed: 15);
        Write("report.bin", content);
        Write(Path.Combine("Downloads", "report (2).bin"), content);

        DuplicateGroup group = Assert.Single(Find().Groups);

        Assert.EndsWith("report.bin", group.Paths[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Find_CountsEveryFileItSaw()
    {
        Write("a.bin", Pattern(1000, seed: 16));
        Write(Path.Combine("sub", "b.bin"), Pattern(1000, seed: 17));
        Write(Path.Combine("sub", "deep", "c.bin"), Pattern(1000, seed: 18));

        Assert.Equal(3, Find().FilesExamined);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);
}
