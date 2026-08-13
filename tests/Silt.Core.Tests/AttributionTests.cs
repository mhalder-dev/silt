using Silt.Core.Attribution;
using Silt.Core.Scanning;

namespace Silt.Core.Tests;

/// <summary>
/// Rules for grouping directories into applications.
/// </summary>
/// <remarks>
/// Grouping is a heuristic, so these tests pin the exact boundary between "same product,
/// decorated name" and "different product, same vendor". Both failure directions are real:
/// merging too little leaves Claude reported as five unremarkable folders, and merging too
/// much collapses an entire vendor into one meaningless row.
/// </remarks>
public sealed class AttributionTests
{
    [Theory]
    [InlineData("Claude_pzs8sxrjxfjjc", "Claude")]
    [InlineData("Microsoft.WindowsStore_8wekyb3d8bbwe", "Microsoft.WindowsStore")]
    [InlineData("MicrosoftWindows.Client.CBS_cw5n1h2txyewy", "MicrosoftWindows.Client.CBS")]
    public void StripPackageSuffix_RemovesPublisherHash(string input, string expected)
    {
        Assert.Equal(expected, AppAttributor.StripPackageSuffix(input));
    }

    [Theory]
    // Too short to be a publisher hash.
    [InlineData("My_App")]
    // Right length but contains an uppercase character, so it is not a base32 publisher id.
    [InlineData("Thing_ABCDEFGHIJKLM")]
    // No underscore at all.
    [InlineData("JetBrains")]
    public void StripPackageSuffix_LeavesOrdinaryNamesAlone(string input)
    {
        Assert.Equal(input, AppAttributor.StripPackageSuffix(input));
    }

    [Theory]
    [InlineData("Claude", "claude")]
    [InlineData("Claude-3p", "claude3p")]
    [InlineData("Telegram Desktop", "telegramdesktop")]
    [InlineData("Claude_pzs8sxrjxfjjc", "claude")]
    public void Normalize_FoldsCaseAndPunctuation(string input, string expected)
    {
        Assert.Equal(expected, AppAttributor.Normalize(input));
    }

    [Fact]
    public void Attribute_GroupsOneAppAcrossSeveralRoots()
    {
        // The motivating case: on the development machine Claude occupied AppData\Roaming,
        // AppData\Local (as Claude-3p) and AppData\Local\Packages. Three unremarkable
        // folders in every other tool; one 18.87 GiB application here.
        var tree = new FakeTree();
        tree.AddApp(Environment.SpecialFolder.ApplicationData, "Claude", gib: 11);
        tree.AddApp(Environment.SpecialFolder.LocalApplicationData, "Claude-3p", gib: 7);

        AppFootprint claude = Attribute(tree).Single(a => a.DisplayName == "Claude");

        Assert.Equal(2, claude.Locations.Count);
        Assert.True(claude.IsSplitAcrossLocations);
        Assert.Equal(18L * Gib, claude.TotalAllocatedBytes);
    }

    [Fact]
    public void Attribute_KeepsDistinctProductsFromTheSameVendorApart()
    {
        // Regression: unbounded prefix merging folded Office, VS Code and OneDrive into a
        // single "Microsoft" row spanning 85 directories on the development machine.
        var tree = new FakeTree();
        tree.AddApp(Environment.SpecialFolder.LocalApplicationData, "Microsoft", gib: 2);
        tree.AddApp(Environment.SpecialFolder.ProgramFiles, "Microsoft Office", gib: 4);
        tree.AddApp(Environment.SpecialFolder.ProgramFiles, "Microsoft OneDrive", gib: 1);

        IReadOnlyList<AppFootprint> apps = Attribute(tree);

        Assert.Contains(apps, a => a.DisplayName == "Microsoft" && a.TotalAllocatedBytes == 2 * Gib);
        Assert.Contains(apps, a => a.DisplayName == "Microsoft Office");
        Assert.Contains(apps, a => a.DisplayName == "Microsoft OneDrive");
    }

    [Fact]
    public void Attribute_MergesVersionSuffixesIntoTheSameProduct()
    {
        // A short trailing remainder is a version marker, not a different product.
        var tree = new FakeTree();
        tree.AddApp(Environment.SpecialFolder.ProgramFiles, "Microsoft Office", gib: 4);
        tree.AddApp(Environment.SpecialFolder.ProgramFiles, "Microsoft Office 15", gib: 1);

        AppFootprint office = Attribute(tree).Single(a => a.DisplayName == "Microsoft Office");

        Assert.Equal(2, office.Locations.Count);
        Assert.Equal(5L * Gib, office.TotalAllocatedBytes);
    }

    [Fact]
    public void Attribute_ExcludesContainerDirectories()
    {
        var tree = new FakeTree();
        tree.AddApp(Environment.SpecialFolder.LocalApplicationData, "Temp", gib: 40);
        tree.AddApp(Environment.SpecialFolder.LocalApplicationData, "Realapp", gib: 1);

        IReadOnlyList<AppFootprint> apps = Attribute(tree);

        // Temp is scratch space, not an application, however large it grows.
        Assert.DoesNotContain(apps, a => a.DisplayName == "Temp");
        Assert.Contains(apps, a => a.DisplayName == "Realapp");
    }

    [Fact]
    public void Attribute_HonoursMinimumSizeFloor()
    {
        var tree = new FakeTree();
        tree.AddApp(Environment.SpecialFolder.LocalApplicationData, "Bigapp", gib: 3);
        tree.AddAppBytes(Environment.SpecialFolder.LocalApplicationData, "Tinyapp", bytes: 1024);

        IReadOnlyList<AppFootprint> apps =
            new AppAttributor().Attribute(tree.Build(), minimumBytes: Gib);

        Assert.Contains(apps, a => a.DisplayName == "Bigapp");
        Assert.DoesNotContain(apps, a => a.DisplayName == "Tinyapp");
    }

    private const long Gib = 1024L * 1024 * 1024;

    private static IReadOnlyList<AppFootprint> Attribute(FakeTree tree) =>
        new AppAttributor().Attribute(tree.Build());

    /// <summary>
    /// Builds a synthetic scan tree over the machine's real well-known folder paths, so
    /// attribution is exercised against the same paths it resolves in production without
    /// touching the filesystem.
    /// </summary>
    private sealed class FakeTree
    {
        private readonly Dictionary<string, List<(string Name, long Bytes)>> _byRoot = new(StringComparer.OrdinalIgnoreCase);

        public void AddApp(Environment.SpecialFolder folder, string name, int gib) =>
            AddAppBytes(folder, name, gib * Gib);

        public void AddAppBytes(Environment.SpecialFolder folder, string name, long bytes)
        {
            string root = Environment.GetFolderPath(folder);
            if (!_byRoot.TryGetValue(root, out List<(string, long)>? list))
            {
                list = [];
                _byRoot[root] = list;
            }
            list.Add((name, bytes));
        }

        public ScanResult Build()
        {
            string volume = Path.GetPathRoot(Environment.SystemDirectory)!;
            var root = new ScanNode { Name = volume, Parent = null };

            foreach ((string rootPath, List<(string Name, long Bytes)> apps) in _byRoot)
            {
                ScanNode parent = EnsurePath(root, volume, rootPath);
                var children = new List<ScanNode>();
                foreach ((string name, long bytes) in apps)
                {
                    children.Add(new ScanNode
                    {
                        Name = name,
                        Parent = parent,
                        TotalAllocatedBytes = bytes,
                        TotalFileCount = 1,
                    });
                }
                parent.Children = [.. (parent.Children ?? []), .. children];
            }

            return new ScanResult { Root = root, Duration = TimeSpan.Zero };
        }

        private static ScanNode EnsurePath(ScanNode root, string volume, string fullPath)
        {
            ScanNode current = root;
            foreach (string segment in fullPath[volume.Length..].Split(
                         Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                ScanNode? next = (current.Children ?? [])
                    .FirstOrDefault(c => string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));

                if (next is null)
                {
                    next = new ScanNode { Name = segment, Parent = current };
                    current.Children = [.. (current.Children ?? []), next];
                }
                current = next;
            }
            return current;
        }
    }
}
