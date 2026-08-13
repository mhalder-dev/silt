using System.Text;
using Microsoft.Win32;
using Silt.Core.Scanning;

namespace Silt.Core.Attribution;

/// <summary>Groups scanned directories into per-application footprints.</summary>
public interface IAppAttributor
{
    IReadOnlyList<AppFootprint> Attribute(ScanResult scan, long minimumBytes = 0);
}

/// <summary>
/// Attributes disk usage to applications by grouping the well-known per-app directories.
/// </summary>
/// <remarks>
/// <para>
/// Windows scatters one application across up to five unrelated trees: Program Files,
/// <c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c>, <c>%LOCALAPPDATA%\Packages</c>, and
/// <c>%PROGRAMDATA%</c>. Folder-size tools show each separately, so an application that
/// costs 19 GB appears as several unremarkable folders and never gets investigated.
/// </para>
/// <para>
/// Grouping is a heuristic, and heuristics are wrong sometimes. The mitigation is
/// transparency, not cleverness: every footprint carries the exact list of directories that
/// were merged into it, so a wrong grouping is visible and dismissible rather than a silently
/// wrong number.
/// </para>
/// </remarks>
public sealed class AppAttributor : IAppAttributor
{
    /// <summary>
    /// Directories that are containers or scratch space, not applications. Excluded at the
    /// top level so they do not masquerade as enormous apps.
    /// </summary>
    private static readonly HashSet<string> NotAnApplication = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp", "Packages", "Programs", "CrashDumps", "Common Files", "WindowsApps",
        "ModifiableWindowsApps", "Windows", "WinStore", "Application Data", "History",
        "INetCache", "INetCookies", "IconCache.db", "Desktop", "Documents", "Downloads",
        "Microsoft Shared", "SystemAppData", "VirtualStore", "ConnectedDevicesPlatform",
        "PlaceholderTileLogoFolder", "Publishers", "D3DSCache", "GPUCache", "usoshared",
        "USOShared", "USOPrivate", "Start Menu", "Templates", "Package Cache",
    };

    /// <summary>
    /// An MSIX package family name is <c>Name_PublisherId</c>, where the publisher id is a
    /// 13-character lowercase base32 hash — for example <c>Claude_pzs8sxrjxfjjc</c> or
    /// <c>Microsoft.WindowsStore_8wekyb3d8bbwe</c>.
    /// </summary>
    private const int PublisherHashLength = 13;

    /// <summary>
    /// Shortest normalized key eligible to absorb a longer one by prefix. Five characters
    /// keeps "claude" + "claude3p" together without letting a three-letter fragment swallow
    /// unrelated applications.
    /// </summary>
    private const int MinimumPrefixMergeLength = 5;

    /// <summary>
    /// Longest trailing remainder that still counts as a decoration of the same product
    /// rather than a different product from the same vendor.
    /// </summary>
    /// <remarks>
    /// Without this bound, prefix merging collapses an entire vendor. Measured on the
    /// development machine: "microsoft" absorbed 85 directories, folding Office, VS Code,
    /// OneDrive, Azure Storage Explorer and every Store stub into a single meaningless
    /// 10.9 GiB row. Those are distinct products a user thinks about separately.
    ///
    /// Three characters is the line between a suffix and a name:
    ///   claude + "3p"      -> same product      (Claude-3p is Claude's sandbox data)
    ///   office + "15"      -> same product      (a version marker)
    ///   microsoft + "office" -> different product
    ///   google + "updater"   -> different product
    /// </remarks>
    private const int MaximumMergeRemainderLength = 3;

    public IReadOnlyList<AppFootprint> Attribute(ScanResult scan, long minimumBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(scan);

        List<Candidate> candidates = CollectCandidates(scan.Root);
        Dictionary<string, string> mergeMap = BuildPrefixMergeMap(
            candidates.Select(c => c.Key).Distinct(StringComparer.Ordinal).ToList());

        Dictionary<string, RegistryEntry> registry = ReadInstalledPrograms();

        var groups = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        foreach (Candidate candidate in candidates)
        {
            string key = mergeMap.TryGetValue(candidate.Key, out string? merged)
                ? merged
                : candidate.Key;

            if (!groups.TryGetValue(key, out List<Candidate>? bucket))
            {
                bucket = [];
                groups[key] = bucket;
            }
            bucket.Add(candidate);
        }

        var footprints = new List<AppFootprint>(groups.Count);
        foreach ((string key, List<Candidate> members) in groups)
        {
            long bytes = members.Sum(m => m.Node.TotalAllocatedBytes);
            if (bytes < minimumBytes)
            {
                continue;
            }

            registry.TryGetValue(key, out RegistryEntry entry);

            footprints.Add(new AppFootprint(
                key,
                entry.DisplayName ?? ChooseDisplayName(members),
                entry.Publisher,
                bytes,
                members.Sum(m => m.Node.TotalFileCount),
                [.. members
                    .Select(m => new AppLocation(
                        m.Node.BuildPath(),
                        m.Node.TotalAllocatedBytes,
                        m.Node.TotalFileCount,
                        m.Kind))
                    .OrderByDescending(l => l.AllocatedBytes)]));
        }

        return [.. footprints.OrderByDescending(f => f.TotalAllocatedBytes)];
    }

    /// <summary>
    /// Picks the most human-readable of the merged directory names — normally the shortest,
    /// since suffixed variants ("Claude-3p", "Claude_pzs8sxrjxfjjc") are decorations of a
    /// clean base name.
    /// </summary>
    private static string ChooseDisplayName(List<Candidate> members) =>
        members
            .Select(m => StripPackageSuffix(m.OriginalName))
            .OrderBy(n => n.Length)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .First();

    private static List<Candidate> CollectCandidates(ScanNode root)
    {
        var results = new List<Candidate>(256);

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        Add(local, AppLocationKind.LocalData);
        Add(roaming, AppLocationKind.RoamingData);
        Add(programData, AppLocationKind.MachineData);
        Add(programFiles, AppLocationKind.Install);
        Add(programFilesX86, AppLocationKind.Install);

        // These two are containers, so the applications sit one level deeper.
        Add(Path.Combine(local, "Packages"), AppLocationKind.PackageData);
        Add(Path.Combine(local, "Programs"), AppLocationKind.Install);

        return results;

        void Add(string directory, AppLocationKind kind)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            foreach (ScanNode child in ScanTree.ChildrenOf(root, directory))
            {
                if (NotAnApplication.Contains(child.Name) || child.TotalAllocatedBytes <= 0)
                {
                    continue;
                }

                string key = Normalize(child.Name);
                if (key.Length == 0)
                {
                    continue;
                }

                results.Add(new Candidate(key, child.Name, child, kind));
            }
        }
    }

    /// <summary>
    /// Reduces a directory name to a comparison key: package suffix removed, case folded,
    /// separators and punctuation dropped.
    /// </summary>
    internal static string Normalize(string name)
    {
        string stripped = StripPackageSuffix(name);

        var builder = new StringBuilder(stripped.Length);
        foreach (char c in stripped)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>Turns <c>Claude_pzs8sxrjxfjjc</c> into <c>Claude</c>.</summary>
    internal static string StripPackageSuffix(string name)
    {
        int underscore = name.LastIndexOf('_');
        if (underscore <= 0 || name.Length - underscore - 1 != PublisherHashLength)
        {
            return name;
        }

        for (int i = underscore + 1; i < name.Length; i++)
        {
            char c = name[i];
            bool isLowerAlphanumeric = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (!isLowerAlphanumeric)
            {
                return name;
            }
        }

        return name[..underscore];
    }

    /// <summary>
    /// Maps longer keys onto a shorter key that prefixes them, so <c>claude3p</c> joins
    /// <c>claude</c>.
    /// </summary>
    /// <remarks>
    /// Only a key of at least <see cref="MinimumPrefixMergeLength"/> characters may absorb
    /// another, and absorption is always onto the shortest matching prefix so chains cannot
    /// form. This is the part most likely to mis-group; every footprint therefore lists the
    /// directories it merged.
    /// </remarks>
    private static Dictionary<string, string> BuildPrefixMergeMap(List<string> keys)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> sorted = [.. keys.OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal)];

        foreach (string key in sorted)
        {
            foreach (string shorter in sorted)
            {
                if (shorter.Length >= key.Length)
                {
                    break; // sorted by length; nothing shorter remains
                }

                if (shorter.Length >= MinimumPrefixMergeLength &&
                    key.Length - shorter.Length <= MaximumMergeRemainderLength &&
                    key.StartsWith(shorter, StringComparison.Ordinal))
                {
                    map[key] = shorter;
                    break;
                }
            }
        }

        return map;
    }

    private readonly record struct Candidate(
        string Key, string OriginalName, ScanNode Node, AppLocationKind Kind);

    private readonly record struct RegistryEntry(string? DisplayName, string? Publisher);

    /// <summary>
    /// Reads installed-program metadata so footprints can show a proper product name and
    /// publisher instead of a directory name.
    /// </summary>
    /// <remarks>
    /// Best effort by design. Registry reads are wrapped because a malformed or
    /// access-restricted uninstall key must degrade the display name, never fail the scan.
    /// </remarks>
    private static Dictionary<string, RegistryEntry> ReadInstalledPrograms()
    {
        var result = new Dictionary<string, RegistryEntry>(StringComparer.Ordinal);

        (RegistryKey Hive, string Path)[] sources =
        [
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        ];

        foreach ((RegistryKey hive, string path) in sources)
        {
            try
            {
                using RegistryKey? uninstall = hive.OpenSubKey(path);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (string subKeyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? entry = uninstall.OpenSubKey(subKeyName);
                        if (entry?.GetValue("DisplayName") is not string displayName ||
                            string.IsNullOrWhiteSpace(displayName))
                        {
                            continue;
                        }

                        // Updates and hotfixes are not applications the user installed.
                        if (entry.GetValue("SystemComponent") is int component && component == 1)
                        {
                            continue;
                        }

                        string key = Normalize(displayName);
                        if (key.Length == 0 || result.ContainsKey(key))
                        {
                            continue;
                        }

                        result[key] = new RegistryEntry(
                            displayName.Trim(),
                            entry.GetValue("Publisher") as string);
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException
                                                  or UnauthorizedAccessException or IOException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException
                                          or UnauthorizedAccessException or IOException)
            {
            }
        }

        return result;
    }
}
