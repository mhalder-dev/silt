using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Silt.Api;

namespace Silt.Shell;

/// <summary>
/// Hosts the Silt UI in WebView2.
/// </summary>
/// <remarks>
/// Two defects in the obvious implementation of this class are fatal in production and
/// invisible during development. Both are fixed in <see cref="InitializeBrowserAsync"/>
/// and both are explained at their fix site. Do not "simplify" either one away:
/// <list type="number">
///   <item>The default user data folder is unwritable once the app is installed.</item>
///   <item>An environment variable can inject an unauthenticated debugging port.</item>
/// </list>
/// </remarks>
public partial class MainWindow : Window, IDisposable
{
    /// <summary>
    /// Virtual host the SPA is served from. A .invalid TLD is reserved by RFC 2606 and can
    /// never resolve publicly, so a mapping bug fails closed instead of silently reaching
    /// out to a real site.
    /// </summary>
    private const string VirtualHost = "silt.invalid";

    private const string AppOrigin = "https://" + VirtualHost + "/";

    private readonly ScanService _scans = new();
    private readonly SiltApiRouter _router;
    private CoreWebView2Environment? _environment;

    public MainWindow()
    {
        InitializeComponent();
        _router = new SiltApiRouter(_scans);
        Loaded += async (_, _) => await InitializeBrowserAsync().ConfigureAwait(true);
        Closed += (_, _) => Dispose();
    }

    /// <summary>
    /// Cancels any in-flight scan and releases the scan service.
    /// </summary>
    /// <remarks>
    /// A window is not usually disposable, but this one owns a service holding worker
    /// threads and cancellation sources. Without this, closing the window while a scan of a
    /// large volume is running would leave those workers alive until process exit.
    /// </remarks>
    public void Dispose()
    {
        _scans.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            // ---------------------------------------------------------------------
            // FIX 1 - explicit user data folder.
            //
            // When userDataFolder is null, WebView2 creates "<exe>.exe.WebView2\" NEXT TO
            // THE EXECUTABLE. Silt installs to %ProgramFiles%, where BUILTIN\Users holds
            // only ReadAndExecute+Synchronize, and the shell deliberately runs asInvoker
            // (never elevated). So CreateAsync fails with access-denied and the user sees a
            // dead window - for EVERY standard user, which is every user by design.
            //
            // This does not reproduce in development, because a dev build runs from a
            // writable bin\ directory where the default works fine.
            //
            // Do NOT "fix" a failure here by loosening ACLs on the install directory. That
            // makes an admin-writable binary user-writable and reopens a local privilege
            // escalation path.
            // ---------------------------------------------------------------------
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Silt",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);

            // ---------------------------------------------------------------------
            // FIX 2 - neutralise WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS.
            //
            // WebView2 reads this ENVIRONMENT VARIABLE at environment-creation time. Any
            // process running as the current user can write HKCU\Environment with no
            // elevation whatsoever; the value is then inherited by every process that user
            // launches afterwards - including Silt.
            //
            // The attack: set --remote-debugging-port, wait for the user to launch Silt,
            // then connect to an unauthenticated Chrome DevTools Protocol endpoint on
            // loopback TCP. CDP grants full script execution inside our renderer, and the
            // renderer talks to the cleanup engine. "The renderer is untrusted" is no
            // defence when the attacker IS the renderer.
            //
            // Clearing it in OUR process block before creating the environment removes the
            // inherited value without touching the user's registry.
            // ---------------------------------------------------------------------
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", null);

            SetStatus("Initializing WebView2...");

            _environment = await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: userDataFolder)
                .ConfigureAwait(true);

            await Browser.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);

            var core = Browser.CoreWebView2;
            var settings = core.Settings;

            // Minimum viable surface. Everything not required is turned off.
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsWebMessageEnabled = true; // the shell <-> renderer channel

#if DEBUG
            settings.AreDevToolsEnabled = true;
#else
            settings.AreDevToolsEnabled = false;
#endif

            if (!Directory.Exists(StaticContent.RootDirectory))
            {
                ShowFailure(
                    "UI assets are missing.",
                    $"Expected the built frontend at:\n{StaticContent.RootDirectory}\n\n" +
                    "Run:  npm --prefix src/frontend run build");
                return;
            }

            // Everything - the SPA's files and the API - is served by intercepting requests
            // to the app's own origin. Nothing listens on a socket, so no other process on
            // the machine can reach any of it.
            //
            // SetVirtualHostNameToFolderMapping is deliberately NOT used: it is handled
            // below this event, so mapping the host would stop WebResourceRequested from
            // firing and the API could not share the origin. See StaticContent.
            core.AddWebResourceRequestedFilter(
                AppOrigin + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResourceRequested;
            Diagnostics.Log($"serving from {StaticContent.RootDirectory}");

            // -----------------------------------------------------------------
            // Navigation lockdown.
            //
            // Scanned filenames are attacker-controlled input - a directory can legitimately
            // contain a file named `<img src=x onerror=...>`. Anything that escapes the app
            // origin is treated as hostile and cancelled. External links open in the user's
            // real browser, never in this control.
            // -----------------------------------------------------------------
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                }
            };

            // Never let the page open a popup window it controls.
            core.NewWindowRequested += (_, e) => e.Handled = true;

            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                {
                    StatusPanel.Visibility = Visibility.Collapsed;
                    Browser.Visibility = Visibility.Visible;
                }
                else
                {
                    ShowFailure("The UI failed to load.", $"WebErrorStatus: {e.WebErrorStatus}");
                }
            };

            SetStatus("Loading interface...");
            core.Navigate(AppOrigin + "index.html");
        }
        catch (Exception ex)
        {
            ShowFailure("Silt could not start.", ex.ToString());
        }
    }

    /// <summary>
    /// Serves the API by intercepting the renderer's own fetch calls.
    /// </summary>
    /// <remarks>
    /// Runs synchronously on the UI thread, which is safe only because every handler returns
    /// immediately: starting a scan hands it to a background task and returns a handle, and
    /// every other call reads already-computed state. If a handler ever needs to block, take
    /// a deferral rather than stalling the message loop.
    /// </remarks>
    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_environment is null)
        {
            return;
        }

        try
        {
            var uri = new Uri(e.Request.Uri);

            e.Response = uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                ? ServeApi(e, uri)
                : ServeFile(uri);
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or ArgumentException)
        {
            Diagnostics.Log($"ERR {e.Request.Uri}: {ex.Message}");
            e.Response = BuildResponse(_environment, ApiResponse.Error(500, ex.Message));
        }
    }

    private CoreWebView2WebResourceResponse ServeApi(
        CoreWebView2WebResourceRequestedEventArgs e, Uri uri)
    {
        string body = string.Empty;
        if (e.Request.Content is { } content)
        {
            using var reader = new StreamReader(content, Encoding.UTF8);
            body = reader.ReadToEnd();
        }

        ApiResponse response = _router.Handle(
            new ApiRequest(e.Request.Method, uri.AbsolutePath, uri.Query, body));

        Diagnostics.Log($"API {e.Request.Method} {uri.PathAndQuery} -> {response.StatusCode}");
        return BuildResponse(_environment!, response);
    }

    private CoreWebView2WebResourceResponse ServeFile(Uri uri)
    {
        string? file = StaticContent.ResolveFile(uri.AbsolutePath);
        if (file is null)
        {
            Diagnostics.Log($"404 {uri.AbsolutePath}");
            return BuildResponse(_environment!, ApiResponse.Error(404, "Not found."));
        }

        var stream = new MemoryStream(File.ReadAllBytes(file));
        string contentType = StaticContent.ContentTypeFor(file);

        var headerLines = new List<string>(4)
        {
            $"Content-Type: {contentType}",
            "Cache-Control: no-cache",
            "X-Content-Type-Options: nosniff",
        };

        // Only documents need a CSP, and only a header delivery makes frame-ancestors
        // effective. See StaticContent.ContentSecurityPolicy.
        if (contentType.StartsWith("text/html", StringComparison.Ordinal))
        {
            headerLines.Add($"Content-Security-Policy: {StaticContent.ContentSecurityPolicy}");
        }

        return _environment!.CreateWebResourceResponse(
            stream, 200, "OK", string.Join('\n', headerLines));
    }

    private static CoreWebView2WebResourceResponse BuildResponse(
        CoreWebView2Environment environment, ApiResponse response)
    {
        var stream = new MemoryStream(response.Body);

        // no-store: scan results change constantly, and a cached status response would make
        // the progress display freeze at whatever the first poll returned.
        string headers = string.Join(
            '\n',
            $"Content-Type: {response.ContentType}",
            "Cache-Control: no-store",
            "X-Content-Type-Options: nosniff");

        return environment.CreateWebResourceResponse(
            stream,
            response.StatusCode,
            ReasonPhrase(response.StatusCode),
            headers);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        _ => "Unknown",
    };

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void ShowFailure(string message, string detail)
    {
        Browser.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = message;
        StatusDetail.Text = detail;
        StatusDetail.Visibility = Visibility.Visible;
    }
}
