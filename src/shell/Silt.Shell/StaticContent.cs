using System.IO;

namespace Silt.Shell;

/// <summary>
/// Serves the built SPA out of the application directory.
/// </summary>
/// <remarks>
/// <para>
/// Silt originally used <c>SetVirtualHostNameToFolderMapping</c> for this, which is less
/// code. It was removed because that mapping is handled below
/// <c>WebResourceRequested</c>: once a host name is mapped, the interception event stops
/// firing for it entirely, so the API could never be served from the same origin. Verified
/// empirically — with a <c>"*"</c> filter registered, not a single request arrived.
/// </para>
/// <para>
/// Serving files here instead means one origin, one request path, and full control of
/// response headers.
/// </para>
/// </remarks>
internal static class StaticContent
{
    internal static string RootDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "wwwroot");

    /// <summary>
    /// Resolves a request path to a file inside the web root, or null if it escapes.
    /// </summary>
    /// <remarks>
    /// The containment check compares canonical paths and requires a directory separator
    /// after the root prefix. A bare <c>StartsWith</c> would let a sibling directory named
    /// <c>wwwroot-evil</c> pass, and comparing before canonicalisation would let
    /// <c>..</c> segments through. This is the same class of check the cleanup engine will
    /// later depend on, so it is written strictly here rather than casually.
    /// </remarks>
    internal static string? ResolveFile(string requestPath)
    {
        string relative = requestPath.TrimStart('/');
        if (relative.Length == 0)
        {
            relative = "index.html";
        }

        string root = Path.GetFullPath(RootDirectory);
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException
                                      or NotSupportedException)
        {
            return null;
        }

        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(candidate) ? candidate : null;
    }

    internal static string ContentTypeFor(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".map" => "application/json; charset=utf-8",
            _ => "application/octet-stream",
        };
}
