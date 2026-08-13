using Silt.Core.Safety;
using Silt.Safety;
using Xunit.Abstractions;

namespace Silt.Core.Tests;

/// <summary>
/// Runs the startup canary against this machine's real protected locations.
/// </summary>
/// <remarks>
/// This is the test that would have caught the profile-resolution bug review identified:
/// the canary derives its paths from environment variables while the denylist resolves
/// through the known-folder API, so the two disagreeing shows up here instead of silently.
/// </remarks>
public sealed class StartupCanaryTests(ITestOutputHelper output)
{
    [Fact]
    public void Canary_PassesAgainstTheRealMachine()
    {
        Denylist denylist = WindowsProtectedPaths.BuildDenylist();

        IReadOnlyList<CanaryFailure> failures = StartupCanary.Verify(denylist);

        foreach (CanaryFailure failure in failures)
        {
            output.WriteLine($"{failure.Path}\n    {failure.Expectation}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Canary_DetectsADenylistThatProtectsNothing()
    {
        // The canary is only worth having if it fails when the denylist is broken.
        IReadOnlyList<CanaryFailure> failures = StartupCanary.Verify(new Denylist([]));

        Assert.NotEmpty(failures);
        output.WriteLine($"empty denylist produced {failures.Count} failures, as it should");
    }

    [Fact]
    public void Canary_DetectsADenylistThatProtectsEverything()
    {
        // A denylist that refuses the whole volume would pass every "must refuse" assertion
        // while making the product inert, so the canary asserts in both directions.
        var overreaching = new Denylist([
            new ProtectedPath(
                Path.GetPathRoot(Environment.SystemDirectory)!, "Everything, apparently."),
        ]);

        IReadOnlyList<CanaryFailure> failures = StartupCanary.Verify(overreaching);

        Assert.NotEmpty(failures);
        Assert.Contains(failures, f =>
            f.Expectation.Contains("allow this cleanable path", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolvedProtectedPaths_CoverCredentialsAndPersonalData()
    {
        IReadOnlyList<ProtectedPath> paths = WindowsProtectedPaths.Resolve();

        output.WriteLine($"{paths.Count} protected locations resolved:");
        foreach (ProtectedPath path in paths)
        {
            output.WriteLine($"  {path.Path}");
        }

        Assert.Contains(paths, p => p.Path.EndsWith("Documents", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Path.EndsWith("Credentials", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Path.EndsWith("Protect", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Path.EndsWith("SystemCertificates", StringComparison.OrdinalIgnoreCase));
    }
}
