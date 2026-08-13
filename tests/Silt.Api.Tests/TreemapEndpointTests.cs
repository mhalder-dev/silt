using System.Text.Json;
using Silt.Api;
using Silt.Core.Scanning;

namespace Silt.Api.Tests;

/// <summary>
/// Verifies the treemap endpoint through the router the shell actually calls, including the
/// serialized shape the renderer parses.
/// </summary>
/// <remarks>
/// Asserting on the DTO objects alone would miss the part most likely to break the UI: the
/// JSON property names. They are deliberately one letter each to keep a 20,000-node response
/// small, which also means a rename is invisible to the C# compiler and fatal to the
/// renderer.
/// </remarks>
public sealed class TreemapEndpointTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private sealed class FakeScanner(Func<ScanResult> factory) : IVolumeScanner
    {
        public ScanResult Scan(ScanOptions options, CancellationToken cancellationToken = default)
            => factory();
    }

    private static ScanResult BuildTree(string root)
    {
        var rootNode = new ScanNode { Name = root, OwnAllocatedBytes = 1 * Gib };
        var users = new ScanNode { Name = "Users", Parent = rootNode, TotalAllocatedBytes = 60 * Gib };
        var profile = new ScanNode { Name = "someone", Parent = users, TotalAllocatedBytes = 60 * Gib };
        var windows = new ScanNode { Name = "Windows", Parent = rootNode, TotalAllocatedBytes = 25 * Gib };

        users.Children = [profile];
        rootNode.Children = [users, windows];
        rootNode.TotalAllocatedBytes = 86 * Gib;

        return new ScanResult
        {
            Root = rootNode,
            Duration = TimeSpan.FromSeconds(1),
            TotalAllocatedBytes = rootNode.TotalAllocatedBytes,
            TotalFiles = 10,
            TotalDirectories = 4,
        };
    }

    private static async Task<(ScanService Service, SiltApiRouter Router, string ScanId)> StartAsync()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory)!;
        var service = new ScanService(new FakeScanner(() => BuildTree(root)));
        var router = new SiltApiRouter(service);

        ScanHandleDto handle = service.Start(root);
        for (int i = 0; i < 200 && service.GetStatus(handle.ScanId)?.State == ScanState.Running; i++)
        {
            await Task.Delay(25);
        }

        return (service, router, handle.ScanId);
    }

    private static JsonElement Get(SiltApiRouter router, string path, string query = "")
    {
        ApiResponse response = router.Handle(new ApiRequest("GET", path, query, string.Empty));
        Assert.Equal(200, response.StatusCode);
        return JsonDocument.Parse(response.Body).RootElement.Clone();
    }

    [Fact]
    public async Task Treemap_ReturnsAFlatProjectionWhoseChildrenFillTheirParents()
    {
        (ScanService service, SiltApiRouter router, string scanId) = await StartAsync();
        using (service)
        {
            JsonElement body = Get(router, $"/api/scans/{scanId}/treemap");

            Assert.Equal(86L * Gib, body.GetProperty("totalAllocatedBytes").GetInt64());

            JsonElement nodes = body.GetProperty("nodes");
            Assert.True(nodes.GetArrayLength() >= 4);

            // The wire contract, exactly as the renderer reads it.
            JsonElement first = nodes[0];
            Assert.Equal(-1, first.GetProperty("p").GetInt32());
            Assert.Equal("Directory", first.GetProperty("k").GetString());
            Assert.Equal(86L * Gib, first.GetProperty("b").GetInt64());

            var sums = new Dictionary<int, long>();
            var sizes = new List<long>();
            foreach (JsonElement node in nodes.EnumerateArray())
            {
                sizes.Add(node.GetProperty("b").GetInt64());
                int parent = node.GetProperty("p").GetInt32();
                if (parent >= 0)
                {
                    sums[parent] = sums.GetValueOrDefault(parent) + node.GetProperty("b").GetInt64();
                }
            }

            foreach ((int parent, long sum) in sums)
            {
                Assert.Equal(sizes[parent], sum);
            }

            // The root's own 1 GiB of loose files must have a rectangle of its own, or the
            // two subdirectories would be drawn as if they were the entire volume.
            Assert.Contains(
                nodes.EnumerateArray(),
                n => n.GetProperty("k").GetString() == "Files" && n.GetProperty("b").GetInt64() == Gib);
        }
    }

    [Fact]
    public async Task Treemap_ScopesToARequestedSubfolder()
    {
        (ScanService service, SiltApiRouter router, string scanId) = await StartAsync();
        using (service)
        {
            string volume = Path.GetPathRoot(Environment.SystemDirectory)!;
            string usersPath = Path.Combine(volume, "Users");

            JsonElement body = Get(
                router, $"/api/scans/{scanId}/treemap", $"?path={Uri.EscapeDataString(usersPath)}");

            Assert.Equal(60L * Gib, body.GetProperty("totalAllocatedBytes").GetInt64());

            // The label is the folder, not the path: the path is already carried once, and
            // repeating it would make the root rectangle's caption the entire path.
            Assert.Equal("Users", body.GetProperty("nodes")[0].GetProperty("n").GetString());
            Assert.EndsWith("Users", body.GetProperty("path").GetString()!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Treemap_IsNotFoundForAPathOutsideTheScan()
    {
        (ScanService service, SiltApiRouter router, string scanId) = await StartAsync();
        using (service)
        {
            ApiResponse response = router.Handle(new ApiRequest(
                "GET", $"/api/scans/{scanId}/treemap", @"?path=C:\definitely\not\scanned", string.Empty));

            Assert.Equal(404, response.StatusCode);
        }
    }
}
