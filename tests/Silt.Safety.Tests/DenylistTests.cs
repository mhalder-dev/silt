using Silt.Safety;

namespace Silt.Safety.Tests;

/// <summary>
/// The denylist is the layer nothing can override, so its behaviour is pinned in both
/// directions: what it must refuse, and what it must continue to allow.
/// </summary>
/// <remarks>
/// A denylist that refuses too much fails silently — every rule finds nothing and the
/// product merely looks useless rather than broken. Both failure modes are tested.
/// </remarks>
public sealed class DenylistTests
{
    private static Denylist Build() => new(
    [
        new ProtectedPath(@"C:\Windows", "OS files."),
        new ProtectedPath(@"C:\Program Files", "Installed programs."),
        new ProtectedPath(@"C:\Users\bob\Documents", "Personal documents."),
        new ProtectedPath(@"C:\Users\bob\AppData\Roaming\Microsoft\Credentials", "Credentials."),
        new ProtectedPath(@"C:\Users\bob\AppData\Local\Silt", "Silt's own audit trail."),
    ]);

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Users\bob\Documents\passport.pdf")]
    [InlineData(@"C:\Users\bob\AppData\Roaming\Microsoft\Credentials\blob")]
    [InlineData(@"C:\Users\bob\AppData\Local\Silt\audit.jsonl")]
    public void Check_RefusesProtectedSubtrees(string path)
    {
        Assert.True(Build().Check(path).IsDenied);
    }

    [Theory]
    // The classic prefix bug: a sibling directory whose name starts with a protected one.
    [InlineData(@"C:\Windows-old\junk.tmp")]
    [InlineData(@"C:\Program Files Backup\old.zip")]
    [InlineData(@"C:\Users\bob\Documents2\file.txt")]
    public void Check_DoesNotOverreachIntoSimilarlyNamedSiblings(string path)
    {
        Assert.False(Build().Check(path).IsDenied);
    }

    [Theory]
    [InlineData(@"C:\Users\bob\AppData\Local\Temp\stale\file.tmp")]
    [InlineData(@"C:\Users\bob\AppData\Local\npm-cache\_cacache\content\ab\cd")]
    [InlineData(@"C:\Users\bob\AppData\Local\CrashDumps\app.dmp")]
    public void Check_AllowsTheThingsCleanupExistsFor(string path)
    {
        DenyVerdict verdict = Build().Check(path);
        Assert.False(verdict.IsDenied, verdict.Reason);
    }

    [Theory]
    [InlineData(@"C:\code\project\.git")]
    [InlineData(@"C:\code\project\.git\objects\ab\cdef")]
    [InlineData(@"C:\anywhere\.svn\entries")]
    [InlineData(@"C:\Windows-old\WinSxS\thing")]
    public void Check_RefusesForbiddenDirectoryNamesAnywhere(string path)
    {
        Assert.True(Build().Check(path).IsDenied);
    }

    [Theory]
    [InlineData(@"C:\temp\cert.pfx")]
    [InlineData(@"C:\temp\key.pem")]
    [InlineData(@"C:\temp\vault.kdbx")]
    [InlineData(@"C:\scratch\App.csproj")]
    [InlineData(@"C:\scratch\App.sln")]
    public void Check_RefusesSecretsAndProjectFilesByExtension(string path)
    {
        Assert.True(Build().Check(path).IsDenied);
    }

    [Theory]
    [InlineData(@"C:\temp\appsettings.json")]
    [InlineData(@"C:\temp\appsettings.Production.json")]
    [InlineData(@"C:\temp\.env")]
    [InlineData(@"C:\temp\.env.local")]
    [InlineData(@"C:\temp\id_rsa")]
    [InlineData(@"C:\temp\secrets.json")]
    public void Check_RefusesConfigurationAndKeyFilesByName(string path)
    {
        Assert.True(Build().Check(path).IsDenied);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:")]
    public void Check_RefusesVolumeRoots(string path)
    {
        DenyVerdict verdict = Build().Check(path);
        Assert.True(verdict.IsDenied);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0invalid")]
    public void Check_FailsClosedOnPathsItCannotInterpret(string path)
    {
        // Anything unparseable is refused rather than waved through as harmless.
        Assert.True(Build().Check(path).IsDenied);
    }

    [Fact]
    public void Check_RefusesTraversalThatLandsInsideAProtectedSubtree()
    {
        Assert.True(Build().Check(@"C:\Users\bob\AppData\..\Documents\tax.pdf").IsDenied);
    }

    [Fact]
    public void Check_ReportsAReasonAUserCanActuallyRead()
    {
        DenyVerdict verdict = Build().Check(@"C:\Users\bob\Documents\passport.pdf");

        Assert.True(verdict.IsDenied);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));

        // The reason is shown verbatim, so it must explain the consequence rather than
        // restate the rule id.
        Assert.DoesNotContain("denied", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactOnlyProtectionCoversThePathButNotItsContents()
    {
        var denylist = new Denylist([
            new ProtectedPath(@"C:\data\keep", "Keep this folder.", ProtectionKind.ExactOnly),
        ]);

        Assert.True(denylist.Check(@"C:\data\keep").IsDenied);
        Assert.False(denylist.Check(@"C:\data\keep\inner.tmp").IsDenied);
    }
}
