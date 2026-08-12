using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

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
public partial class MainWindow : Window
{
    /// <summary>
    /// Virtual host the SPA is served from. A .invalid TLD is reserved by RFC 2606 and can
    /// never resolve publicly, so a mapping bug fails closed instead of silently reaching
    /// out to a real site.
    /// </summary>
    private const string VirtualHost = "silt.invalid";

    private const string AppOrigin = "https://" + VirtualHost + "/";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeBrowserAsync().ConfigureAwait(true);
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

            var environment = await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: userDataFolder)
                .ConfigureAwait(true);

            await Browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

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

            // Serve the built SPA from a virtual host. DenyCors: the SPA's own origin is the
            // only thing permitted to read these files.
            var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!Directory.Exists(webRoot))
            {
                ShowFailure(
                    "UI assets are missing.",
                    $"Expected the built frontend at:\n{webRoot}\n\n" +
                    "Run:  npm --prefix src/frontend run build");
                return;
            }

            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, webRoot, CoreWebView2HostResourceAccessKind.DenyCors);

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
