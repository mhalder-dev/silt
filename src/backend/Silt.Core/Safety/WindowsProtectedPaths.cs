using Silt.Safety;

namespace Silt.Core.Safety;

/// <summary>
/// Resolves the machine's protected locations into a <see cref="Denylist"/>.
/// </summary>
/// <remarks>
/// Uses <see cref="Environment.GetFolderPath"/>, which is backed by
/// <c>SHGetKnownFolderPath</c>. The startup canary deliberately does <b>not</b> use this
/// type — see <see cref="StartupCanary"/> for why.
/// </remarks>
public static class WindowsProtectedPaths
{
    public static Denylist BuildDenylist() => new(Resolve());

    public static IReadOnlyList<ProtectedPath> Resolve()
    {
        var paths = new List<ProtectedPath>(32);

        Add(Environment.SpecialFolder.Windows,
            "The Windows directory. Deleting from it breaks the operating system.");
        Add(Environment.SpecialFolder.System,
            "System32. Deleting from it breaks the operating system.");
        Add(Environment.SpecialFolder.SystemX86,
            "SysWOW64. Deleting from it breaks 32-bit Windows components.");
        Add(Environment.SpecialFolder.ProgramFiles,
            "Installed program files. Removing these corrupts applications rather than " +
            "uninstalling them.");
        Add(Environment.SpecialFolder.ProgramFilesX86,
            "Installed 32-bit program files.");

        // Personal data. Silt reports on these and never proposes deleting them; anything
        // here is the user's own and frequently irreplaceable.
        Add(Environment.SpecialFolder.MyDocuments,
            "Your Documents folder holds personal files, and often scanned identity " +
            "documents. Silt will never delete from it.");
        Add(Environment.SpecialFolder.Desktop,
            "Your Desktop holds working files.");
        Add(Environment.SpecialFolder.MyPictures, "Your Pictures folder.");
        Add(Environment.SpecialFolder.MyVideos, "Your Videos folder.");
        Add(Environment.SpecialFolder.MyMusic, "Your Music folder.");
        Add(Environment.SpecialFolder.Favorites, "Your browser favourites.");

        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Credential and certificate stores.
        //
        // Review caught these missing while DPAPI master keys were already protected:
        // surviving master keys are worthless once the ciphertext they protect is gone, so
        // protecting one without the other achieves nothing. These hold saved RDP, network
        // share and Git credentials, and per-user certificate private keys.
        AddUnder(roaming, @"Microsoft\Credentials",
            "Windows Credential Manager. Holds saved passwords for network shares, RDP and Git.");
        AddUnder(local, @"Microsoft\Credentials",
            "Windows Credential Manager (local). Holds saved passwords.");
        AddUnder(roaming, @"Microsoft\Vault", "Windows Vault credential store.");
        AddUnder(local, @"Microsoft\Vault", "Windows Vault credential store (local).");
        AddUnder(roaming, @"Microsoft\Protect",
            "DPAPI master keys. Without them every credential on this profile becomes " +
            "permanently unreadable.");
        AddUnder(roaming, @"Microsoft\Crypto", "Cryptographic key containers.");
        AddUnder(local, @"Microsoft\Crypto", "Cryptographic key containers (local).");
        AddUnder(roaming, @"Microsoft\SystemCertificates",
            "Your certificate store, including private keys.");
        AddUnder(local, @"Microsoft\SystemCertificates",
            "Your certificate store (local), including private keys.");

        // Silt's own state. A cleanup tool that eats its own audit trail cannot be audited.
        AddUnder(local, "Silt",
            "Silt's own history and audit log. Deleting it would erase the record of what " +
            "Silt did.");

        return paths;

        void Add(Environment.SpecialFolder folder, string reason)
        {
            string resolved = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                paths.Add(new ProtectedPath(resolved, reason));
            }
        }

        void AddUnder(string root, string relative, string reason)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                paths.Add(new ProtectedPath(Path.Combine(root, relative), reason));
            }
        }
    }
}
