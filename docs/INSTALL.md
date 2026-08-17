# Installing Silt

## Requirements

| | |
|---|---|
| OS | 64-bit Windows 10 1809 (build 17763) or newer |
| Runtime | none — Silt ships self-contained |
| WebView2 | Microsoft Edge WebView2 Runtime (preinstalled on Windows 11) |

Silt carries its own copy of .NET, deliberately. Verified on the development machine before
that decision was taken: `Microsoft.WindowsDesktop.App` was present at **9.0.14 only**, with
no 10.x entry, even though `Microsoft.NETCore.App 10.0.7` was installed. A
framework-dependent WPF build would greet most users with the OS runtime-missing dialog. The
cost of avoiding that is a 43 MB installer instead of a 3 MB one.

## Install

1. Download `Silt-<version>-win-x64-setup.exe` from
   [Releases](https://github.com/mhalder-dev/silt/releases).
2. Check the hash against `SHA256SUMS.txt`:

   ```powershell
   Get-FileHash .\Silt-0.1.0-win-x64-setup.exe -Algorithm SHA256
   ```

3. Run it. Accept the UAC prompt — the installer writes to `Program Files`.

### Why SmartScreen warns

**Silt is not code-signed.** There is no certificate behind this project. Windows shows
*"Windows protected your PC"*; the way through is **More info → Run anyway**. This is stated
here rather than glossed over, because "just click through the security warning" is advice a
tool that deletes files has to earn, and the checksum above is the only integrity evidence on
offer.

### Why the installer elevates but the app does not

The installer needs administrator rights once, to write into `Program Files`. That ACL is
load-bearing: **Silt is the code that deletes your files, so the unprivileged process it
later runs as must not be able to rewrite `Silt.exe` on disk.** A per-user install into
`%LOCALAPPDATA%` would put the delete engine somewhere the delete engine's own user can
modify, which is why no per-user option is offered.

**Silt itself never runs elevated.** There is no privileged helper, no service, and no UAC
prompt after installation. The post-install "Launch Silt" checkbox drops the administrator
token before starting the app. See
[PLAN.md §2.1](PLAN.md#21-process-model--v1-runs-entirely-unelevated) for why v1 has no
elevated broker at all.

### If the window opens empty

The WebView2 Runtime is missing. Silt draws its entire interface in WebView2, so without it
you get a window with nothing in it. Install the Evergreen runtime from
[developer.microsoft.com](https://developer.microsoft.com/microsoft-edge/webview2/) — Silt
picks it up on the next launch, no reinstall needed. Setup warns about this up front if it
cannot find the runtime.

## Where Silt keeps things

| Path | What | Removed on uninstall? |
|---|---|---|
| `%ProgramFiles%\Silt` | The application | Yes |
| `%LOCALAPPDATA%\Silt\WebView2` | Browser cache — regenerable | **No** (see below) |
| `%LOCALAPPDATA%\Silt\snapshots` | Scan history used for growth diffs | **No** |
| `%LOCALAPPDATA%\Silt\operations.jsonl` | Hash-chained record of what Silt deleted | **No** |
| `%LOCALAPPDATA%\Silt\silt.log` | Diagnostic log | **No** |

Uninstalling removes the program and leaves everything under `%LOCALAPPDATA%\Silt` alone.
That is on purpose twice over:

- The snapshots and the journal are **your** data — scan history and the audit trail of what
  Silt deleted on your behalf. A tool whose governing rule is *nothing goes without naming
  how it comes back* does not get to shred its own audit log on the way out.
- Even the obviously-disposable WebView2 cache is left, because the uninstaller runs
  elevated and `%LOCALAPPDATA%` would resolve to the **administrator's** profile, not yours.
  On a machine with a separate admin account it would delete a directory belonging to someone
  who never ran Silt, and miss the real one. Inno Setup flags this at compile time; the fix
  was to delete nothing rather than to silence the warning.

To remove it yourself:

```powershell
Remove-Item "$env:LOCALAPPDATA\Silt" -Recurse -Force
```

## Building from source

Requires the **.NET 10 SDK**, **Node 22+**, and — for the installer only —
[Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
git clone https://github.com/mhalder-dev/silt.git
cd silt
pwsh scripts/publish.ps1          # SPA + self-contained single-file exe, then verifies both
iscc installer\silt.iss           # -> artifacts\installer\Silt-<version>-win-x64-setup.exe
```

`scripts/publish.ps1` verifies the payload rather than trusting it: exe present and large
enough to actually be self-contained, `wwwroot` present, and `index.html` referencing a
bundle that exists. Those checks are not ceremony — the packaging path shipped without any
`wwwroot` at all for the project's whole life before them, and every other gate stayed green.

For development, skip packaging entirely:

```powershell
dotnet build
npm --prefix src/frontend run dev   # dev-only mock API, reviewable in an ordinary browser
```

## Releasing

Releases are tag-driven. Pushing a `v*` tag runs `.github/workflows/release.yml`, which
publishes, compiles the installer, writes `SHA256SUMS.txt`, and creates a **draft** release.
Drafts, not published releases: a mistyped tag should not ship an installer to the world with
no step in between where a human looks at it.

```bash
git tag v0.1.0
git push origin v0.1.0
```

The version stamped into `Silt.exe`, the installer's filename, and the git tag all come from
that one tag — the installer reads its version from the built exe rather than carrying its
own copy. The rules for turning a ref into a version live in `scripts/release-version.ps1`,
which CI self-tests on every push; a tag that is not exactly `vX.Y.Z` **fails the build**
rather than quietly falling back to the development default and shipping an asset whose name
disagrees with its tag.

To exercise the pipeline without releasing anything, dispatch it. That builds, verifies and
uploads the installer as a workflow artifact, and cannot create a release — the release step
is gated on the ref being a `v` tag:

```bash
gh workflow run release.yml --ref main
```
