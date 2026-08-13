# Silt — Plan of Record

> Silt: fine sediment that accumulates quietly until it chokes the channel.

**Status:** v0.1 scaffold · **Owner:** mhalder-dev · **Last revised:** 2026-08-13

---

## 1. What Silt is

A Windows desktop app that answers three questions no free tool answers together:

1. **Where did my disk space actually go?** — including space attributed *per application*, not per folder.
2. **What changed since last week?** — longitudinal growth tracking with alerting.
3. **What can I safely delete, and how does it come back?** — guided, dry-run-first cleanup.

**What Silt is NOT:** a registry cleaner, a "RAM booster", a startup-optimizer suite, or a
one-click "speed up my PC" product. Those are refused by design (see §7).

### 1.1 The gap, stated honestly

| Capability | WizTree | WinDirStat | TreeSize Free | Storage Sense | Silt |
|---|---|---|---|---|---|
| Fast whole-volume scan | ✅ | ❌ slow | ⚠️ | n/a | ✅ |
| Treemap | ✅ | ✅ | ⚠️ | ❌ | ✅ |
| **Per-app aggregate footprint** | ❌ | ❌ | ❌ | ❌ | ✅ |
| **Growth-over-time / alerts** | ❌ | ❌ | Pro only | ❌ | ✅ |
| Guided safe cleanup w/ dry-run | ❌ | ❌ | ❌ | ⚠️ opaque | ✅ |

**Discovery alone is a solved problem.** WizTree already reads the MFT and treemaps a volume
in seconds. Silt's defensible value is the bottom three rows — attribution, growth, and
safe guided reclamation. The plan is sequenced accordingly.

### 1.2 The motivating incident (measured, 2026-08-12)

On the author's machine, `C:` had 173 GB free and no obvious cause. Manual investigation found:

| Finding | Size | Why no tool surfaced it |
|---|---|---|
| `%LOCALAPPDATA%\Temp` | **44 GB** | Grew silently over months; nothing alerts on growth |
| Claude Desktop | **18.9 GB** | Split across 3 unrelated paths; shown as 3 small folders |
| `Documents\MEGA downloads` | 19.45 GB | Two game installs, forgotten |
| `Desktop\Android` firmware | 11.6 GB | Extracted images from finished work |
| npm cache | 6.75 GB | Invisible; no UI anywhere |
| Chrome (3 profiles) | 7.4 GB | Per-profile caches |
| JetBrains | 12 GB | Config vs cache indistinguishable to the user |

A naive `Get-ChildItem -Recurse | Measure-Object Length` **timed out twice** (180 s, 300 s)
before completing in >5 min. Explorer separately reported free space ~18 GB stale.

**Claude Desktop is the thesis in one number:** 18.9 GB living in
`%APPDATA%\Claude` + `%LOCALAPPDATA%\Packages\Claude_*` + `%LOCALAPPDATA%\Claude-3p`.
Every existing tool shows three unrelated folders. None says *"Claude Desktop: 18.9 GB."*

---

## 2. Architecture

### 2.1 Process model — v1 runs **entirely unelevated**

```
┌─────────────────────────────────────────────────────┐
│  Silt.Shell.exe   (WPF, net10.0-windows, asInvoker) │
│  ┌───────────────────────────────────────────────┐  │
│  │  WebView2  →  React 19 SPA (canvas treemap)   │  │
│  └───────────────────────────────────────────────┘  │
│  ┌───────────────────────────────────────────────┐  │
│  │  Silt.Api  (ASP.NET Core, in-process)         │  │
│  │  Silt.Core (scan · attribute · snapshot)      │  │
│  │  Silt.Safety (path jail — pure, net10.0)      │  │
│  └───────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────┘
                  user token · no UAC · no broker
```

**Why no elevated broker in v1 — this is the single biggest scope decision.**

Every reclaimable byte measured in §1.2 — all ~100 GB of it — is user-owned and deletable
with no elevation whatsoever. Adversarial review costed the elevated-broker design
(separate `Silt.Broker.exe`, named pipes with DACLs + mandatory integrity labels, 256-bit
capability tokens, client PID/start-time verification, Native AOT, a second manifest,
a Program-Files-only installer, a portable-mode veto marker) at **60–100 hours — all of it
before a single byte is scanned**, to unlock only: `C:\Windows\Temp`, the itemized half of
the free-space reconciliation, and a faster scan mode.

It also introduced **three of the seven critical security findings** in review: an
arbitrary-file-write elevation path via restore, a pipe integrity-label trap, and a
CDP-injection route into an elevated renderer.

Elevation returns in v2 as an **optional, explicitly-invoked** capability, not a
launch prerequisite. v1 never shows a UAC prompt.

### 2.2 Assembly layout and target frameworks

The dependency direction is load-bearing. A `net10.0` project **cannot** reference a
`net10.0-windows` project (NuGet `NU1201`). Review caught the draft plan violating this in
three places, which would have been a hard restore failure on day one.

| Project | TFM | Depends on | Purpose |
|---|---|---|---|
| `Silt.Safety` | `net10.0` | *(nothing)* | Pure path-jail predicate + denylist. No I/O, no P/Invoke. Runs on the Linux CI runner. |
| `Silt.Core` | `net10.0-windows` | Safety | Scanning, attribution, snapshots, rules, planning |
| `Silt.Api` | `net10.0-windows` | Core, Safety | ASP.NET Core endpoints + progress streaming |
| `Silt.Shell` | `net10.0-windows` | Api, Core, Safety | WPF host + WebView2 |
| `Silt.Safety.Tests` | `net10.0` | Safety | Property tests (CsCheck) — **Linux-runnable** |
| `Silt.Core.Tests` | `net10.0-windows` | Core | Scan/attribution/rule tests |

> ⚠️ **Path-jail tests must also run on Windows.** On Linux, `\` is a legal filename
> character and there is no drive concept, so `Path.GetFullPath(@"C:\a\..\b")` returns a
> single filename and every Windows-specific bypass case degrades to noise. The Ubuntu run
> is for speed; the **Windows run is the one that counts**. 8.3 short names (`PROGRA~1`)
> cannot be resolved by a pure predicate at all — the jail *rejects* them rather than
> pretending to resolve them.

### 2.3 WebView2 hardening — mandatory, not optional

Review found two defects that are fatal as-shipped. Both are fixed at the single
environment-creation call site:

```csharp
// 1. Default user data folder is "<exe>.exe.WebView2" NEXT TO THE EXECUTABLE.
//    Verified on this machine: C:\Program Files grants BUILTIN\Users only
//    ReadAndExecute+Synchronize. The shell runs asInvoker (never elevated), so
//    CreateAsync fails with access-denied and the app shows a dead window for
//    EVERY standard user. Not a degradation — a total first-launch failure.
var udf = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Silt", "WebView2");
Directory.CreateDirectory(udf);

// 2. WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS is an ENVIRONMENT VARIABLE. Any same-user
//    process can write HKCU\Environment with no elevation; the value is inherited by
//    every process launched afterwards, including Silt. An attacker sets
//    --remote-debugging-port and gets an unauthenticated CDP endpoint on loopback TCP,
//    i.e. full script execution in our renderer. Clearing it in OUR process block
//    before environment creation closes the inheritance route.
Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", null);

var env = await CoreWebView2Environment.CreateAsync(userDataFolder: udf);
```

Plus, on the `CoreWebView2` instance:

- `Settings.AreDevToolsEnabled = false` in Release
- `Settings.IsWebMessageEnabled = true`, everything else off
- `AddWebResourceRequestedFilter` scoped to the virtual host only
- **Navigation lockdown**: cancel any `NavigationStarting` whose URI is outside the virtual
  host; route external links to the default browser
- **Strict CSP**, `default-src 'self'` with no `unsafe-inline`

> **Filenames are attacker-controlled input.** A scanned directory can legitimately contain
> a file named `<img src=x onerror=...>`. React escapes text nodes but not
> `dangerouslySetInnerHTML`, not URL-typed props, and not the router. A renderer compromise
> here is not defacement — the renderer talks to the delete engine. Treat every path string
> from the scanner as hostile.

### 2.4 Memory budget — the tool must not become the problem

You chose the WebView2 + React desktop app knowing the cost. Review measured the realistic
steady state at **450–800 MB** (browser + GPU + renderer + utility processes ≈ 300–500 MB;
three full-viewport canvases at DPR 2 on 4K ≈ 100 MB of backing store; WPF shell 60–80 MB).
On a 16 GB machine currently showing ~2.66 GB available, that is real.

> ### ⚠️ MEASURED AT M0 — 470.5 MB, over budget, doing nothing
>
> Measured on 2026-08-13 against the actual M0 build, displaying a **static page**: no scan
> index, no treemap, no canvases, no data.
>
> | Process | Working set |
> |---|---|
> | `msedgewebview2.exe` (browser) | 132.2 MB |
> | `Silt.exe` (WPF shell) | 110.6 MB |
> | `msedgewebview2.exe` (gpu) | 95.5 MB |
> | `msedgewebview2.exe` (renderer) | 61.1 MB |
> | `msedgewebview2.exe` ×3 (utility/crashpad) | 71.1 MB |
> | **Total — 7 processes** | **470.5 MB** |
>
> The 400 MB budget is **already violated before any feature exists.** Review predicted
> 450–800 MB and was right. This is the cost of the WebView2 + React shell, accepted
> knowingly, and it is now a measured fact rather than an estimate.
>
> ### Re-measured at M1 — 627.5 MB with a full C: scan resident
>
> | Process | M0 (static page) | M1 (C: scan held) |
> |---|---|---|
> | `Silt.exe` (shell + index) | 110.6 MB | **235.7 MB** |
> | WebView2 (6 processes) | 359.9 MB | 391.8 MB |
> | **Total** | **470.5 MB** | **627.5 MB** |
>
> #### Retained-heap measurement and fix (resolved)
>
> Working set overstates cost — it includes uncollected garbage and allocator slack. Measured
> properly instead (forced compacting collection, `GC.KeepAlive` on the rooted tree, managed
> heap delta), a whole-C: scan tree retained:
>
> | | Retained heap | Per directory |
> |---|---|---|
> | Before | 73.8 MiB | 498 B |
> | **After** | **30.7 MiB** | **208 B** |
>
> **−58%.** Two causes, both pure redundancy:
>
> 1. `ScanNode.FullPath` stored a complete path string per node — ~200 bytes each,
>    duplicating what the parent chain already encodes. Removed; the path now travels with
>    the work item during the scan (short-lived garbage) and is rebuilt on demand by
>    `BuildPath()` for the few hundred rows actually displayed.
> 2. `Children` was a `List<ScanNode>`. The child count is final the moment a directory
>    finishes enumerating, so the list wrapper was ~32 bytes of dead weight per node.
>    Now an exact-sized array.
>
> Guarded by `BuildPath_ReconstructsFullPathAtEveryDepth` and
> `BuildPath_DoesNotDoubleSeparatorAtAVolumeRoot` — dropping a stored path is only safe
> while reconstruction is exact.

> **Consequences — these stop being optimizations and become requirements:**
> - The single-canvas rule (§ below) is mandatory. Three full-viewport canvases at DPR 2 on
>   4K would add ~100 MB of backing store on top of this.
> - Releasing the index on minimize is mandatory, not a nice-to-have.
> - **Re-measure at M1, M2, and M5.** If the trend line puts a real workload past ~700 MB,
>   the shell choice must be revisited — a disk tool that costs 5 % of a 16 GB machine's RAM
>   to report on disk usage has undermined its own premise.
> - The budget below is retained as a *target to drive back down to*, not a claim of
>   compliance. Do not quietly raise it to match whatever gets measured.

Mitigations that **actually work** (`--js-flags="--max-old-space-size"` is a placebo — it
reserves nothing, frees nothing, and merely converts a large heap into a hard OOM crash):

- **Budget: ≤400 MB steady state.** Currently **exceeded** — see the measurement above.
- **One canvas, not three.** Picking via a spatial index (interval tree over the squarified
  layout) computed in JS — not a second readback canvas. Avoids the `getImageData`
  premultiplication/antialiasing id-collision bug review identified, *and* the backing store.
- **Cull to `minArea ≥ 9 px²`** before upload — 3k–15k rects ever reach the canvas, not 100k.
  (Consequently: do not write a milestone promising "interactive at 100k rects" — it measures nothing.)
- **The backend owns the tree.** The renderer receives a capped (≤8 MB) packed binary buffer
  for the current view, never the full index.
- **Release the index** when the window is minimized for >5 min; rehydrate from snapshot.

### 2.5 Scan engine — BFS in v1, MFT deferred to v2

The draft plan led with a raw `$MFT` parser. Two facts killed that ordering:

1. **The benchmark that justified it was invalid.** The 12.05 s warm / 39.16 s cold figures
   were taken with `FileSystemEnumerator<T>`, whose `FileSystemEntry` exposes **no file
   identity whatsoever** (verified by reflection: no `Id`, `Index`, or `Serial` member; the
   native fill is `FILE_FULL_DIR_INFORMATION`, which carries no file ID). But hardlink
   dedup — required, or WinSxS over-reports ~2× — needs `(VolumeSerialNumber, FileIndex)`.
   Getting IDs means abandoning that enumerator for
   `GetFileInformationByHandleEx(FileIdBothDirectoryInfo)`, at which point the measurement
   no longer describes the shipped code.
2. **12 s warm / 39 s cold is fine.** The 5-minute timeout in §1.2 was PowerShell's
   `PSObject` pipeline overhead, not NTFS.

So v1 ships a **parallel BFS** on `GetFileInformationByHandleEx(FileIdBothDirectoryInfo)`
with a bounded work-stealing queue. Correctness requirements:

- `AttributesToSkip = 0`, `IgnoreInaccessible = false`, with a **counted** continue-on-error —
  silently skipped subtrees are how a space tool lies to you.
- **Reparse traversal predicate is `IsReparseTagNameSurrogate` — `(tag & 0x20000000) != 0` —
  not `attributes.HasFlag(ReparsePoint)`.** The blunt check skips OneDrive cloud-backed and
  WOF-compressed directories entirely, silently erasing whole subtrees from the report.
  (This profile has 17 directory reparse points in its top two levels alone.)
- **Never hydrate** `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` placeholders.
- Report **allocated** size by default, logical on toggle. Surface both for compressed/sparse.
- A **reconciliation waterfall** that refuses to silently absorb the residual: 
  `volume used = Σ(scanned) + Σ(inaccessible) + $MFT/metadata + VSS + pagefile/hiberfil + Unaccounted`.

MFT parsing lands in v2 with a **1 % BFS cross-validation gate**. Note for whoever writes it:
the USA fixup stride is `BytesPerSector`, **not a hardcoded 512** (silently corrupts every
record on 4Kn volumes), and the attribute table must handle **resident `$DATA`** — files
under ~700 bytes live inside the MFT record, and on a dev machine (`node_modules`,
`.git/objects`, `_cacache`) that is hundreds of thousands of files that would otherwise
report zero bytes.

---

## 3. Cleanup & safety

### 3.1 Six rules, not fifty-four

The draft carried a 54-row rule table. Review showed ~20 rows were not rules at all
(denylist predicates and advisory UI copy), and of the genuinely deletable remainder,
**six rules cover ~75 GB of the measured ~100 GB**. The other ~28 rules cover single-digit
GB in total, and several find nothing on this machine — while each still costs schema +
detection + estimator + a destructive test + a docs entry.

| # | Rule | Target | Measured | Regeneration |
|---|---|---|---|---|
| 1 | `temp.user.aged` | `%LOCALAPPDATA%\Temp`, mtime ≥ 7 d | 44 GB | Recreated on demand |
| 2 | `npm.cache` | `%LOCALAPPDATA%\npm-cache\_cacache` | 6.75 GB | `npm cache clean --force` |
| 3 | `chrome.cache` | `User Data\*\{Cache,Code Cache,GPUCache}` | ~3 GB | Refetched |
| 4 | `jetbrains.caches` | `%LOCALAPPDATA%\JetBrains\*\caches` | ~2.8 GB | Re-indexed on open |
| 5 | `crashdumps.thumbcache` | `CrashDumps`, `thumbcache_*.db` | ~1 GB | Regenerated |
| 6 | `pkgmgr.caches` | NuGet http-cache, pip, uv | ~1.6 GB | Re-downloaded |

Everything else is **advisory only** in v1: Silt names it, sizes it, explains it, and lets
you act in Explorer. No rule, no estimator, no destructive test — a fraction of the cost for
most of the value.

> **Rule 0 — the governing invariant.** Nothing is deleted unless the rule can name *how it
> comes back*. A rule with a null `undoStrategy.regeneration` cannot be authored. This is
> enforced at schema load, not by convention.

### 3.2 Disposition — Recycle Bin only in v1

The draft's quarantine ring buffer was **arithmetically impossible**: cap =
`min(10 GB, 2 % of volume)` = **8.5 GB** on a 425 GB C:, against the **44 GB** flagship Temp
payload. It would have evicted ~35 GB of its own undo data mid-operation — and quarantine was
the specified undo mode for rules whose contents *exist nowhere else*. The Recycle Bin is
likewise capped (verified: **23,826 MB** on C:), so a 44 GB recycle silently permanently
deletes the overflow.

**v1 resolution — no clever storage, just honesty:**

- Default disposition is **Recycle Bin**, via `IFileOperation` with `FOF_ALLOWUNDO`.
- **Before executing, compare the plan's byte total against the volume's actual Recycle Bin
  `MaxCapacity`.** If it exceeds it, Silt **refuses the batch** and offers to split it —
  it does not proceed and report `restore_possible = 0` afterwards. Honest reporting of data
  loss is not prevention.
- Use `FOF_WANTNUKEWARNING` alongside `FOF_NOCONFIRMATION` — this keeps the permanent-delete
  warning while suppressing per-item shell dialogs. (The draft's "never pass
  `FOF_NOCONFIRMATION`" rule would hand modality to the shell and raise dialogs mid-run
  against 1.2 M Temp files — the developer would strip it under pressure and lose the reason.)
- **No permanent delete and no quarantine in v1.** Deferred until there is a real design.

### 3.3 The veto engine must not veto the flagship

The draft's veto rules — *"any file modified in the last 30 days"*, *"any open handle"* —
applied bottom-up at **directory** level would permanently suppress `%LOCALAPPDATA%\Temp`,
which always has recent writes and open handles. The 44 GB rule would never fire. Same
collision hits every cache rule whenever its app is running.

**Resolution: vetoes apply per-item, not per-directory.** A directory is never demoted
wholesale. Each candidate file is independently tested against the rule predicate
(age ≥ 7 d) *and* the vetoes (no open handle, not memory-mapped). Directory-level signals
(contains `.sln`/`.csproj`/`.git`, contains documents/IDs) demote only *suggestions* in the
discovery UI — they never gate an explicit, dry-run-reviewed rule execution.

### 3.4 Non-negotiable safety machinery

Carried forward from review essentially intact — this part of the draft was strong:

- **Compiled, non-overridable denylist.** No `force`/`override`/`skipGuard` exists in the
  wire format at all. Additions review flagged as missing and present on this machine:
  `%APPDATA%\Microsoft\Credentials`, `%LOCALAPPDATA%\Microsoft\Credentials`,
  `%APPDATA%\Microsoft\Vault`, `%LOCALAPPDATA%\Microsoft\Vault`,
  `%APPDATA%\Microsoft\SystemCertificates` — the Credential Manager store and per-user
  certificate store. Protecting DPAPI master keys while leaving the ciphertext they protect
  deletable is useless.
- **Startup canary.** ~60 known paths asserted protected at boot; the host **refuses to
  start** if any assertion fails. ⚠️ Review caught that canary and denylist sharing
  `SHGetKnownFolderPath` makes the canary blind to the exact failure it exists to catch
  (wrong profile resolution). **The canary must assert against independently-derived paths.**
- **`SandboxedFileSystem` funnel** — the only code permitted to mutate the filesystem.
  It lives in `Silt.Core` (`net10.0-windows`), **not** `Silt.Safety` — it needs COM/STA and
  P/Invoke, which would contaminate the pure, Linux-testable jail.
- **Dry-run is the only planning path.** Execute re-validates every item against file id,
  size, last-write, attributes and lock state before touching it.
- **The restore path is a guarded write primitive, not an afterthought.** Review found it was
  an unguarded arbitrary-file-write: destinations read from a SQLite journal any same-user
  process can rewrite. v1 fix: restore destinations are re-validated against the path jail on
  the way *out*, the journal is integrity-checked, and — because v1 is unelevated — the blast
  radius is bounded to what the user could do by hand.
- **Hash-chained append-only audit log.**

> **CI guard correctness.** The draft's two safety gates used
> `Select-String -Path 'src/backend/**/*.cs'`. **PowerShell provider wildcards do not
> implement recursive `**`.** Reproduced during review: with matching files at three depths,
> the expression matched **1 of 3** — every subdirectory was exempt from the gate calling
> itself "Layer 1". Use `Get-ChildItem -Recurse -Filter *.cs | Select-String`. A safety gate
> that silently passes is worse than none.

---

## 4. Tech stack

| Layer | Choice | Version | Note |
|---|---|---|---|
| Runtime | .NET | **10 LTS** | .NET 9 left support **12 May 2026** — the installed 9.0.312 is already unpatched |
| Shell | WPF + WebView2 | `net10.0-windows` | `asInvoker`, never elevated |
| API | ASP.NET Core | in-process | No TCP listener; `WebResourceRequested` interception |
| UI | React + TypeScript | 19.x / 5.x | |
| Build | Vite | 7.x | |
| Lint | Biome | 2.x | |
| Test (C#) | xUnit v3 + Shouldly | | **`Verify.XunitV3`**, not `Verify.Xunit` (v2-only — will not attach) |
| Property test | CsCheck | | Path-jail fuzzing |
| Test (TS) | Vitest | | |
| Installer | Inno Setup | | **Self-contained publish** — see below |

> **Publish self-contained.** Verified on this machine: `Microsoft.WindowsDesktop.App` is
> **9.0.14 only**, with no 10.x entry, despite `Microsoft.NETCore.App 10.0.7` being present.
> A framework-dependent WPF build would fail to launch on a clean machine with the OS
> runtime-missing dialog. The draft's "3 MB installer" figure was true only for users who
> already had the runtime — i.e. almost nobody.

**Deliberately deferred:** Native AOT (collides with `Microsoft.AspNetCore.OpenApi`, which is
reflection-based and emits IL2026/IL3050 — fatal under `TreatWarningsAsErrors`), Ed25519
rule-pack signing (the install ACL already restricts modification to administrators), and
the 7-job CI matrix (built for a team that does not exist).

---

## 5. Milestones — recomputed honestly

Review found the draft's 456 h / 38-week budget **~2.2× optimistic**, with the error
front-loaded where morale is most fragile. At 12 h/week it put v0.5 at month 10–13, and
"month three is ~15 % done" — the plan's own stated abandonment condition.

**Planning at 8 h/week**, not 12 — holidays, work crunches, and motivation dips are not
optional, and 2–4 h sessions on a multi-project codebase lose 20–30 % to re-orientation.

| Phase | Deliverable | Est. | Cumulative |
|---|---|---|---|
| **M0** | Scaffold, solution builds, minimal CI green, WPF+WebView2 window renders React | 20 h | wk 3 |
| **M1** | BFS scanner + reconciliation waterfall + folder tree UI | 40 h | wk 8 |
| **M2** | **Per-app attribution** — the differentiator | 30 h | wk 12 |
| **M3** | Snapshots + growth diff + "what grew this week" | 30 h | wk 16 |
| **M4** | Safety core + dry-run + the 6 rules + Recycle Bin execute | 60 h | wk 24 |
| **M5** | Treemap (single canvas, spatial-index picking) | 30 h | wk 28 |
| **M6** | Installer, self-contained publish, GitHub Releases, docs | 25 h | wk 31 |

**v1.0 ≈ 235 h ≈ 31 weeks at 8 h/week.**

Note the sequencing inversion versus the draft: **attribution and growth (M2, M3) ship before
the delete engine (M4)**. Rationale — growth tracking is the only capability with no free
competitor, and Explorer deletes folders perfectly well once Silt has told you *which* ones.
If M4 slips or is abandoned, M0–M3 is still a product nobody else ships.

**v2 backlog:** MFT scanner · optional elevation for `C:\Windows\Temp` + VSS reporting ·
duplicate finder · RAM subsystem · scheduled background scans.

---

## 6. Testing destructive operations safely

Never against the real filesystem. Four independent interlocks:

1. **Scratch VHDX.** Destructive tests create, mount, format, and later dismount a VHDX via
   `diskpart`. Test volumes only — a test that cannot find its VHDX **fails**, it does not
   fall back.
2. **Env-var gate.** `SILT_DESTRUCTIVE_TESTS=1` plus an explicit volume GUID.
3. **Trait filter.** `[Trait("Category","Destructive")]`, excluded by default.
   ⚠️ Review flagged that xUnit v3 on Microsoft.Testing.Platform uses `--filter-*` options
   rather than VSTest `--filter` syntax — **verify the filter is actually honoured**, because
   a silently-ignored filter is the worst possible outcome here.
4. **Path-jail assertion** inside `SandboxedFileSystem`, active in tests too.

`WebApplicationFactory` substitutes `TestServer` and never exercises the real transport —
so it tests endpoints, not the hosting model. Don't confuse the two.

---

## 7. The refusal list

Silt will not implement these, regardless of later pressure:

- **"RAM boosters"** that call `EmptyWorkingSet` across all processes — this forces pages to
  disk and makes the machine *slower* on re-fault. Pure placebo.
- **Registry cleaning.** No measurable benefit; real breakage risk.
- **Automatic pagefile disabling.** On a 16 GB machine this trades disk for instability.
- **Unattended automatic deletion.** Every destructive action requires a reviewed dry-run.
- **Telemetry.** A tool that indexes your entire filesystem does not phone home. Ever.

---

## 8. Hosting — the honest answer

**The application needs no hosting.** It runs on your machine and reads your disks. Sending a
full file index to a cloud service would be a privacy hole and pure added latency.

| Need | Solution | Cost |
|---|---|---|
| Release binaries | GitHub Releases | Free, unlimited (public repo) |
| Docs / landing page | GitHub Pages | Free |
| Update feed | Static JSON on Pages / Releases API | Free |
| CI minutes | GitHub Actions | Free, unlimited (public repo) |
| Telemetry | *(refused — see §7)* | — |

**Total recurring cost: $0**, with no free-tier expiry to worry about. The repo is public
specifically to get unlimited Actions minutes and Release bandwidth.

---

## 9. Definition of Done

A change is done when:

- [ ] Builds clean with `TreatWarningsAsErrors` and nullable enabled
- [ ] Unit tests pass; path-jail property tests pass **on Windows** (not only Ubuntu)
- [ ] Any filesystem mutation goes through `SandboxedFileSystem` — CI grep confirms
      (with a **recursive** file listing)
- [ ] Any new rule has a non-null regeneration command (Rule 0)
- [ ] Any new denylist entry has a startup-canary assertion from an independent path source
- [ ] Dry-run output verified by hand against a real directory
- [ ] Memory budget still ≤400 MB steady state
- [ ] Conventional Commit message

---

## Appendix A — Review provenance

This plan is the corrected output of an 11-agent design workflow: six parallel specialists
(scan engine, safety model, tech stack, product spec, RAM subsystem, DevOps), a synthesizing
architect, and three adversarial critics (data-loss, scope/feasibility, technology).

The critics returned **59 findings, 7 critical**, several verified empirically against this
machine. The findings that changed the architecture:

| # | Finding | Change |
|---|---|---|
| 1 | Elevated broker: 60–100 h before first scan, ~90 % of bytes need no elevation | Removed from v1 |
| 2 | Quarantine cap 8.5 GB vs 44 GB payload — self-evicting undo | Removed; bin-capacity pre-check |
| 3 | Restore = unguarded elevated arbitrary-file-write (local EoP) | Guarded + unelevated v1 |
| 4 | `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` env injection → unauthenticated CDP | Cleared pre-creation |
| 5 | `net10.0` cannot reference `net10.0-windows` (NU1201) | TFM layout corrected |
| 6 | Safety CI gate matched **1 of 3** files — PowerShell `**` is not recursive | `Get-ChildItem -Recurse` |
| 7 | WebView2 UDF defaults next to exe; Program Files is read-only for Users | UDF pinned to LocalAppData |
| 8 | Veto engine suppresses the flagship 44 GB rule | Vetoes made per-item |
| 9 | Growth tracking (no free competitor) scheduled last | Moved to M3 |
| 10 | MFT benchmark taken with an enumerator lacking file identity | BFS in v1, MFT v2 |
| 11 | .NET 9 EOL was 12 May 2026, not Nov 2026 | .NET 10 confirmed, urgency real |
| 12 | Credential Manager / Vault / SystemCertificates missing from denylist | Added |
| 13 | Schedule ~2.2× optimistic at 12 h/wk | Replanned at 8 h/wk, 235 h |

Full agent transcripts: workflow run `wf_4d677f78-555`.
