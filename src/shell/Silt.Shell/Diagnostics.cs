using System.Diagnostics;
using System.IO;
using Silt.Safety;

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

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Silt");

    internal static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);

                // Guarded like every other exempted writer. This type is on the CI mutation
                // gate's exemption list, and that exemption is only honest if the constraint
                // is enforced rather than asserted in a comment.
                PathJail.Require(LogDirectory, LogPath, "write the diagnostics log");

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
