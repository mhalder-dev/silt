using Silt.Safety;

namespace Silt.Core.Safety;

/// <summary>A protected path the denylist failed to protect.</summary>
public sealed record CanaryFailure(string Path, string Expectation);

/// <summary>
/// Asserts at startup that the denylist actually protects what it claims to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The paths here are derived from environment variables, not from
/// <see cref="Environment.GetFolderPath"/>.</b> That asymmetry is the entire point.
/// </para>
/// <para>
/// The denylist resolves locations through <c>SHGetKnownFolderPath</c>. A canary using the
/// same resolver would agree with the denylist even when both were wrong — and they can be
/// wrong together: if the process token ever resolves a different profile (a scheduled task,
/// "run as different user", a future service context), <c>SHGetKnownFolderPath</c> returns
/// <c>C:\Windows\System32\config\systemprofile\Documents</c>. The denylist would then
/// silently protect that instead of the user's Documents, and a shared-resolver canary would
/// pass. Review flagged exactly this shape of bug in the original design.
/// </para>
/// <para>
/// Deriving from <c>%USERPROFILE%</c> and friends gives an independent answer, so a
/// divergence between the two sources surfaces as a failure instead of as silence.
/// </para>
/// </remarks>
public static class StartupCanary
{
    /// <summary>
    /// Returns every protected location the denylist failed to refuse. An empty result means
    /// the denylist is behaving; a non-empty one means the host must not start.
    /// </summary>
    public static IReadOnlyList<CanaryFailure> Verify(Denylist denylist)
    {
        ArgumentNullException.ThrowIfNull(denylist);

        var failures = new List<CanaryFailure>();

        foreach (string path in BuildIndependentPaths())
        {
            DenyVerdict verdict = denylist.Check(path);
            if (!verdict.IsDenied)
            {
                failures.Add(new CanaryFailure(
                    path, "Expected the denylist to refuse this path, but it allowed it."));
            }
        }

        // The inverse check matters too. A denylist that refuses everything would pass the
        // assertions above while making the product useless, and the failure would only
        // appear as "no rule ever finds anything".
        foreach (string path in BuildExpectedAllowedPaths())
        {
            DenyVerdict verdict = denylist.Check(path);
            if (verdict.IsDenied)
            {
                failures.Add(new CanaryFailure(
                    path,
                    $"Expected the denylist to allow this cleanable path, but it refused: {verdict.Reason}"));
            }
        }

        return failures;
    }

    /// <summary>
    /// Paths built from environment variables rather than the known-folder API.
    /// </summary>
    private static List<string> BuildIndependentPaths()
    {
        var paths = new List<string>(64);

        string userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
        string appData = Environment.GetEnvironmentVariable("APPDATA") ?? string.Empty;
        string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;
        string windir = Environment.GetEnvironmentVariable("SystemRoot")
                        ?? Environment.GetEnvironmentVariable("windir")
                        ?? string.Empty;
        string programFiles = Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty;
        string programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty;

        // Operating system.
        AddUnder(windir, "System32", "System32", "drivers", "explorer.exe", "notepad.exe");
        AddUnder(windir, "SysWOW64");
        AddUnder(programFiles, "Common Files", "Windows Defender");
        AddUnder(programFilesX86, "Common Files");

        // Personal data, including the exact shapes that motivated the protection: scanned
        // identity documents living in Documents.
        AddUnder(userProfile, "Documents", @"Documents\passport.pdf", @"Documents\nid.pdf",
            @"Documents\taxes\2025.xlsx", "Desktop", @"Desktop\notes.txt",
            "Pictures", "Videos", "Music", "Favorites");

        // Credentials, vaults, certificates, DPAPI.
        AddUnder(appData, @"Microsoft\Credentials", @"Microsoft\Credentials\blob",
            @"Microsoft\Vault", @"Microsoft\Protect", @"Microsoft\Protect\S-1-5-21\key",
            @"Microsoft\Crypto", @"Microsoft\SystemCertificates",
            @"Microsoft\SystemCertificates\My\Keys");
        AddUnder(localAppData, @"Microsoft\Credentials", @"Microsoft\Vault",
            @"Microsoft\Crypto", @"Microsoft\SystemCertificates");

        // Silt's own state.
        AddUnder(localAppData, "Silt", @"Silt\snapshots", @"Silt\audit.jsonl");

        // Source control and project files, wherever they live.
        AddUnder(userProfile, @"source\repos\app\.git", @"source\repos\app\.git\objects\ab\cdef",
            @"source\repos\app\App.csproj", @"source\repos\app\App.sln",
            @"source\repos\app\appsettings.json", @"source\repos\app\appsettings.Production.json",
            @"source\repos\app\.env", @"source\repos\app\secrets.json",
            @"source\.ssh\id_rsa", @"certs\wildcard.pfx", @"vault\passwords.kdbx");

        // Volume roots are never a target.
        string? systemDriveRoot = Path.GetPathRoot(windir);
        if (!string.IsNullOrEmpty(systemDriveRoot))
        {
            paths.Add(systemDriveRoot);
        }

        return paths;

        void AddUnder(string root, params string[] relatives)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }
            foreach (string relative in relatives)
            {
                paths.Add(Path.Combine(root, relative));
            }
        }
    }

    /// <summary>
    /// Paths that must remain deletable, or the product does nothing.
    /// </summary>
    private static List<string> BuildExpectedAllowedPaths()
    {
        var paths = new List<string>(8);
        string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            paths.Add(Path.Combine(localAppData, @"Temp\some-stale-folder\file.tmp"));
            paths.Add(Path.Combine(localAppData, @"npm-cache\_cacache\content-v2\sha512\ab\cd\ef"));
            paths.Add(Path.Combine(localAppData, @"Google\Chrome\User Data\Default\Cache\data_1"));
            paths.Add(Path.Combine(localAppData, @"CrashDumps\app.exe.1234.dmp"));
        }

        return paths;
    }
}
