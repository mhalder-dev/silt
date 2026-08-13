using System.Diagnostics;
using System.IO;

namespace Silt.Shell;

/// <summary>
/// Minimal file log for diagnosing startup and request-routing problems.
/// </summary>
/// <remarks>
/// A WebView2 host has two failure modes that leave no visible trace: the environment fails
/// to create, or a resource request is never routed. Neither surfaces in a debugger attached
/// after the fact, and the second is invisible without devtools. This writes enough to tell
/// which happened.
///
/// Deliberately not telemetry. It is a local file, it never leaves the machine, and it
/// records request paths only - never file contents or scan results.
/// </remarks>
internal static class Diagnostics
{
    private static readonly Lock Gate = new();

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Silt",
        "silt.log");

    internal static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Diagnostics must never be the reason the app fails.
        }
        catch (UnauthorizedAccessException)
        {
        }

        Debug.WriteLine(message);
    }

    internal static string Path_ => LogPath;
}
