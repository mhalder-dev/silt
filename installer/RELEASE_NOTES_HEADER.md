## Install

Download `Silt-<version>-win-x64-setup.exe` below and run it. Requires 64-bit Windows 10
1809 or newer.

**Windows will warn you.** Silt is not code-signed — there is no certificate behind this
project, and pretending otherwise would be worse than saying so. SmartScreen shows
*"Windows protected your PC"*; choose **More info → Run anyway** if you trust the source.
Verify the download against `SHA256SUMS.txt` first:

```powershell
Get-FileHash .\Silt-<version>-win-x64-setup.exe -Algorithm SHA256
```

The installer needs administrator rights to write to `Program Files`. **Silt itself never
runs elevated** — it has no privileged helper and shows no UAC prompt after installation.
That is deliberate: see [the architecture notes](https://github.com/mhalder-dev/silt/blob/main/docs/PLAN.md#21-process-model--v1-runs-entirely-unelevated).

If the app opens an empty window, the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
is missing. Installing it fixes Silt without reinstalling.

## Before you delete anything

Silt plans every cleanup as a dry run first, deletes to the Recycle Bin, and **refuses**
any batch that would exceed the bin's capacity rather than silently destroying the
overflow. Read the plan before pointing it at anything you care about.

---
