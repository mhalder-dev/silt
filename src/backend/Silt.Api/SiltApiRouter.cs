using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Silt.Api;

/// <summary>An intercepted request from the renderer.</summary>
public readonly record struct ApiRequest(string Method, string Path, string Query, string Body);

/// <summary>The response to write back into the WebView2 resource stream.</summary>
public sealed record ApiResponse(int StatusCode, string ContentType, byte[] Body)
{
    public static ApiResponse Json<T>(T value, int status = 200) =>
        new(status, "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(value, SiltApiRouter.JsonOptions));

    public static ApiResponse Error(int status, string message) =>
        Json(new ErrorDto(message), status);
}

/// <summary>
/// Routes API calls from the renderer to the scan service.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not ASP.NET Core. Silt's API is reached through WebView2's
/// <c>WebResourceRequested</c> interception, never over a socket, so a full HTTP server
/// would contribute a pipeline, a listener, and tens of megabytes of working set for a
/// dispatch table this small. On a product whose measured footprint is already over its
/// own budget, that is not a neutral choice.
/// </para>
/// <para>
/// The CI gate in <c>ci.yml</c> enforces the absence of any TCP listener. A loopback port
/// would be reachable by every other process on the machine and by any page in any browser.
/// </para>
/// </remarks>
public sealed class SiltApiRouter(ScanService scans, CleanupService? cleanup = null)
{
    private readonly CleanupService _cleanup = cleanup ?? new CleanupService();

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public ApiResponse Handle(ApiRequest request)
    {
        try
        {
            return Dispatch(request);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            return ApiResponse.Error(500, ex.Message);
        }
    }

    private ApiResponse Dispatch(ApiRequest request)
    {
        ReadOnlySpan<char> path = request.Path.AsSpan().TrimEnd('/');

        if (path.Equals("/api/volumes", StringComparison.OrdinalIgnoreCase))
        {
            return request.Method is "GET"
                ? ApiResponse.Json(ListVolumes())
                : MethodNotAllowed();
        }

        if (path.Equals("/api/cleanup/safety", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<Silt.Core.Safety.CanaryFailure> failures = _cleanup.VerifySafety();
            return ApiResponse.Json(new
            {
                healthy = failures.Count == 0,
                failures = failures.Select(f => new { f.Path, f.Expectation }),
            });
        }

        // Planning is a GET-shaped idea but creates a server-side plan, so it is a POST.
        if (path.Equals("/api/cleanup/plans", StringComparison.OrdinalIgnoreCase))
        {
            return request.Method is "POST"
                ? ApiResponse.Json(_cleanup.CreatePlan(DateTimeOffset.UtcNow), 201)
                : MethodNotAllowed();
        }

        const string planPrefix = "/api/cleanup/plans/";
        if (path.StartsWith(planPrefix, StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<char> rest = path[planPrefix.Length..];
            int slash = rest.IndexOf('/');
            string planId = (slash < 0 ? rest : rest[..slash]).ToString();
            string action = slash < 0 ? string.Empty : rest[(slash + 1)..].ToString();

            if (action.Length == 0)
            {
                return _cleanup.GetPlan(planId) is { } plan
                    ? ApiResponse.Json(plan)
                    : ApiResponse.Error(404, "No such plan.");
            }

            // Execution names a rule from an already-issued plan. There is deliberately no
            // endpoint that accepts paths, so nothing can be deleted that a dry run has not
            // already enumerated for the user.
            if (action.Equals("execute", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Method is not "POST")
                {
                    return MethodNotAllowed();
                }

                string? ruleId = ReadStringFromBody(request.Body, "ruleId");
                if (string.IsNullOrWhiteSpace(ruleId))
                {
                    return ApiResponse.Error(400, "A 'ruleId' from the plan is required.");
                }

                return _cleanup.Execute(planId, ruleId, DateTimeOffset.UtcNow) is { } result
                    ? ApiResponse.Json(result)
                    : ApiResponse.Error(404, "No such plan or rule.");
            }

            return ApiResponse.Error(404, "Unknown endpoint.");
        }

        if (path.Equals("/api/cleanup/journal", StringComparison.OrdinalIgnoreCase))
        {
            return ApiResponse.Json(_cleanup.GetJournal(200));
        }

        if (path.Equals("/api/scans", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Method is not "POST")
            {
                return MethodNotAllowed();
            }

            string? root = ReadRootFromBody(request.Body);
            if (string.IsNullOrWhiteSpace(root))
            {
                return ApiResponse.Error(400, "A 'root' path is required.");
            }

            if (!Directory.Exists(root))
            {
                return ApiResponse.Error(404, $"'{root}' does not exist or is not readable.");
            }

            return ApiResponse.Json(scans.Start(root), 202);
        }

        // /api/scans/{id}[/summary|/tree|/cancel]
        const string prefix = "/api/scans/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<char> rest = path[prefix.Length..];
            int slash = rest.IndexOf('/');
            string id = (slash < 0 ? rest : rest[..slash]).ToString();
            string action = slash < 0 ? string.Empty : rest[(slash + 1)..].ToString();

            return action.ToLowerInvariant() switch
            {
                "" => scans.GetStatus(id) is { } status
                    ? ApiResponse.Json(status)
                    : ApiResponse.Error(404, "No such scan."),

                "summary" => scans.GetSummary(id) is { } summary
                    ? ApiResponse.Json(summary)
                    : ApiResponse.Error(404, "Scan not finished, or no such scan."),

                "tree" => scans.GetTree(id, GetQueryValue(request.Query, "path")) is { } tree
                    ? ApiResponse.Json(tree)
                    : ApiResponse.Error(404, "No such scan or path."),

                "treemap" => scans.GetTreemap(id, GetQueryValue(request.Query, "path")) is { } map
                    ? ApiResponse.Json(map)
                    : ApiResponse.Error(404, "No such scan or path."),

                "apps" => scans.GetApps(id, ParseMinimumBytes(request.Query)) is { } apps
                    ? ApiResponse.Json(apps)
                    : ApiResponse.Error(404, "Scan not finished, or no such scan."),

                "growth" => scans.GetGrowth(id, ParseDays(request.Query)) is { } growth
                    ? ApiResponse.Json(growth)
                    : ApiResponse.Error(404, "Scan not finished, or no such scan."),

                "cancel" => scans.Cancel(id)
                    ? ApiResponse.Json(new { cancelled = true })
                    : ApiResponse.Error(404, "No such scan."),

                _ => ApiResponse.Error(404, "Unknown endpoint."),
            };
        }

        return ApiResponse.Error(404, "Unknown endpoint.");
    }

    private static ApiResponse MethodNotAllowed() =>
        ApiResponse.Error(405, "Method not allowed.");

    /// <summary>
    /// How far back to look for a comparison snapshot. Defaults to a week; clamped so a
    /// nonsense value cannot produce a nonsense comparison window.
    /// </summary>
    private static double ParseDays(string query)
    {
        const double defaultDays = 7;

        string? raw = GetQueryValue(query, "days");
        return double.TryParse(raw, CultureInfo.InvariantCulture, out double parsed)
            ? Math.Clamp(parsed, 0.5, 365)
            : defaultDays;
    }

    /// <summary>
    /// Size floor for the application list.
    /// </summary>
    /// <remarks>
    /// Defaults to 50 MiB. Without a floor the list runs to hundreds of rows of runtime
    /// stubs and extension packages, and stops being a way to find what is actually large.
    /// </remarks>
    private static long ParseMinimumBytes(string query)
    {
        const long defaultFloor = 50L * 1024 * 1024;

        string? raw = GetQueryValue(query, "min");
        return long.TryParse(raw, out long parsed) && parsed >= 0 ? parsed : defaultFloor;
    }

    private static string? ReadRootFromBody(string body) => ReadStringFromBody(body, "root");

    private static string? ReadStringFromBody(string body, string property)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(property, out JsonElement value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one value out of a raw query string.
    /// </summary>
    /// <remarks>
    /// Values are percent-decoded, which matters because paths contain spaces and
    /// backslashes. <c>Uri.UnescapeDataString</c> is used rather than a query parser so
    /// there is no dependency on ASP.NET Core for a single lookup.
    /// </remarks>
    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (Range segment in query.AsSpan().TrimStart('?').Split('&'))
        {
            ReadOnlySpan<char> pair = query.AsSpan().TrimStart('?')[segment];
            int eq = pair.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            if (pair[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..].ToString());
            }
        }

        return null;
    }

    private static List<VolumeDto> ListVolumes()
    {
        var result = new List<VolumeDto>();

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                bool ready = drive.IsReady;
                result.Add(new VolumeDto(
                    drive.RootDirectory.FullName,
                    ready && !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? drive.VolumeLabel
                        : drive.Name.TrimEnd('\\'),
                    ready ? drive.DriveFormat : "unknown",
                    ready ? drive.TotalSize : 0,
                    ready ? drive.TotalFreeSpace : 0,
                    ready));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that disappears mid-enumeration is not an error worth failing on.
            }
        }

        return result;
    }
}
