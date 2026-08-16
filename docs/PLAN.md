# Silt — Plan of Record

> Silt: fine sediment that accumulates quietly until it chokes the channel.

**Status:** v0.1 pre-release, M0–M5 complete · **Owner:** mhalder-dev · **Last revised:** 2026-08-16

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
>
> #### Working-set trend
>
> | Milestone | Total | Shell process |
> |---|---|---|
> | M0 — static page | 470.5 MB | 110.6 MB |
> | M1 — C: scan resident | 627.5 MB | 235.7 MB |
> | **M2 — scan + attribution** | **590.0 MB** | **180.2 MB** |
>
> M2 went **down** despite adding a feature: the path fix reclaimed more than attribution
> costs. Still above the 400 MB target and still WebView2-dominated (~410 MB of the 590 is
> the six browser processes), but the trend is no longer pointing at the ~700 MB threshold.
>
> **M6 update — see §5d for the full table.** The published Release build measures
> **468.8 MB idle** across the same 7 processes, i.e. the treemap costs nothing when nothing
> is loaded and the single-canvas rule is holding. The *loaded* M5 figure this section asked
> for is still owed; it needs a manual session, not automation.

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
  **The canvas is also allocated once and reused** — see §5f for the mousemove that was
  reallocating it, and for the 8 M device-pixel area cap that now bounds it.
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
| Test (C#) | xUnit **2.9.3**, plain `Assert` | | ⚠️ Drift: this row said "xUnit v3 + Shouldly". Neither is installed — the projects reference `xunit` 2.9.3 with no assertion library. Corrected to what the repo actually builds, rather than adding packages to match a document. The `Verify.XunitV3` note stands **if** v3 is ever adopted. |
| Property test | CsCheck | | Path-jail fuzzing |
| Test (TS) | Vitest | 4.x | 48 tests over the treemap geometry, the pixel-ratio path and the byte formatter, run in CI. See §5e and §5f — the §5c browser measurements are now assertions rather than one-off observations. |
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
| ~~**M0**~~ ✅ | Scaffold, solution builds, CI green, WPF+WebView2 window renders React | 20 h | done |
| ~~**M1**~~ ✅ | BFS scanner + reconciliation waterfall + folder tree UI | 40 h | done |
| ~~**M2**~~ ✅ | **Per-app attribution** — the differentiator | 30 h | done |
| ~~**M3**~~ ✅ | Snapshots + growth diff + "what grew this week" | 30 h | done |
| ~~**M4**~~ ✅ | Safety core + dry-run + the 6 rules + Recycle Bin execute | 60 h | done |
| ~~**M5**~~ ✅ | Treemap (single canvas, spatial-index picking) | 30 h | done |
| **M6** 🚧 | Installer, self-contained publish, GitHub Releases, docs | 25 h | wk 31 — packaging done, release untagged |

**v1.0 ≈ 235 h ≈ 31 weeks at 8 h/week.**

Note the sequencing inversion versus the draft: **attribution and growth (M2, M3) ship before
the delete engine (M4)**. Rationale — growth tracking is the only capability with no free
competitor, and Explorer deletes folders perfectly well once Silt has told you *which* ones.
If M4 slips or is abandoned, M0–M3 is still a product nobody else ships.

**v2 backlog:** MFT scanner · optional elevation for `C:\Windows\Temp` + VSS reporting ·
duplicate finder · RAM subsystem · scheduled background scans.

---

## 5a. Resolved: the "cancel bug" that wasn't

During M3 testing a scan received `POST /api/scans/{id}/cancel` twice with no apparent user
action, and it was logged as a defect. **It was not one.**

`api.cancel` has exactly one call site — the Cancel button's `onClick`. No effect, cleanup,
or lifecycle path invokes it. The two calls arrived **ten seconds apart**, while the
synthetic keystrokes under test spanned 900 ms; ten-second spacing is human timing. The
window had also appeared unexpectedly on a second monitor during other work. The cancels
were real clicks.

**The lesson is about the diagnosis, not the code:** an anomaly seen through an unreliable
test harness was attributed to the application. Before filing UI behaviour as a bug, confirm
the harness itself was in a known state.

One genuine defect *was_* found while reading that code and has been fixed: `ScanProgress`
listed `onDone`/`onError` in its effect dependencies, and the parent recreates both as fresh
closures on every render — so any parent re-render tore down the poll loop and started a new
one mid-scan. The callbacks now live in a ref and the effect depends on `scanId` alone.

### Verifying the UI

Driving the app with synthetic global keyboard input proved unreliable and, on a
multi-monitor setup, **unsafe** — input intended for Silt reached whichever window actually
held focus, and a screenshot captured an unrelated application. That approach is abandoned.

The replacement is a **dev-only mock API** (`src/frontend/dev/mockApi.ts`, registered with
`apply: 'serve'` so it cannot reach a production build). In the shipped app the API is
served by the shell intercepting WebView2 requests, so `npm run dev` previously had no
backend and the UI could not be reviewed in a browser at all. With fixtures modelled on real
measurements, every panel can now be exercised in an ordinary browser with real clicks and
no synthetic input.

Two things that verification immediately caught:

- **`frame-ancestors` is ignored in a `<meta>` element.** The CSP looked like it forbade
  framing and did nothing. The shell now sends the full policy as an HTTP header on HTML
  responses, where the directive takes effect; the meta tag remains as the dev-mode fallback
  minus that directive.
- **`ScanStatusDto.State` is a real enum** and therefore serializes camelCase (`completed`),
  while the `Kind` fields are plain strings from `ToString()` and stay PascalCase. A mock
  that got this wrong stranded the UI on the progress screen forever — a good reminder that
  the mock must mirror the contract, not a guess at it.

Note for M5: `worker-src` is absent from the CSP, so it falls back to `script-src 'self'` and
blob-backed Web Workers are blocked. The production bundle creates none today (verified), but
moving treemap layout into a worker will require explicitly widening the policy.

## 5b. M4 progress — safety first, deletion last

Built, in this order, deliberately:

1. **`Denylist`** (`Silt.Safety`, pure) — locations and file kinds that are never deletable.
   No override exists: no flag, no force parameter, and no field in the wire format that
   could express one.
2. **`WindowsProtectedPaths`** — resolves 21 protected locations on this machine, including
   the Credential Manager, Vault, DPAPI and certificate stores that review found missing.
3. **`StartupCanary`** — asserts the denylist works, deriving its paths from **environment
   variables** while the denylist resolves through the **known-folder API**. That asymmetry
   is the point: a canary sharing the denylist's resolver agrees with it even when both are
   wrong. It also asserts in the *opposite* direction, since a denylist that refuses
   everything would pass every "must refuse" check while making the product inert.
4. **`CleanupRule`** — Rule 0 enforced in the constructor. A rule that cannot name how its
   data comes back cannot be *constructed*, so it can never be executed.
5. **`RuleCatalog`** — the six rules, as data.
6. **`CleanupPlanner`** — dry-run. Every candidate is checked against the denylist
   *individually, after expansion*, so a rule cannot widen its own reach.

Then, only once all of the above was in place:

7. **`SandboxedFileSystem`** — the sole mutation point. The Win32 interop lives *in that
   file* so no deletion primitive is callable from anywhere else. It re-checks the denylist
   at execution time (a path can become a junction between planning and executing) and
   re-validates each item against what the plan recorded. **There is no permanent-delete
   path in the type at all** — not a flag, not an overload; the capability does not exist.
8. **`OperationJournal`** — append-only, hash-chained. It cannot prevent tampering by a
   process running as the user, but it makes tampering *detectable*, which is the
   achievable property.
9. **`CleanupExecutor`** — whose most important behaviour is **refusal**.

### Two interop bugs, both found by tests that insisted on ground truth

Both were `Pack = 1` on a Win32 struct — correct for x86, wrong for x64.

- **`SHFILEOPSTRUCTW`**: misaligned every pointer after `wFunc`, so the shell dereferenced
  garbage and the process died with an access violation (0xC0000005) *inside the delete
  call*. The interlocks confined it to a scratch directory.
- **`SHQUERYRBINFO`**: measured 20 bytes instead of 24, so `cbSize` was wrong and
  `SHQueryRecycleBin` rejected every call. This one failed **silently and dangerously**: the
  bin always appeared empty, so the capacity guard believed the entire quota was free no
  matter what the bin actually held.

The second was caught only because the destructive test asserted the Recycle Bin's item
count *grew*, rather than trusting that the files had vanished. Vanishing is not evidence of
recoverability — the first run reported `deleted=2, failed=0, recoverable=true` while the
bin count stayed at `0 → 0`. After the fix: `6 → 8`, genuinely recoverable.

### Capacity refusal

The Recycle Bin is a quota, not a guarantee: an oversized delete does not fail, it
permanently destroys the overflow and reports success. Measured on this machine, the quota
is **23,826 MB on C:** and 4,607 MB on D:. A batch that will not fit is therefore **refused
before anything is touched**, and the user is told to split it. A refusal is not journalled,
because nothing happened.

### Measured dry-run on the development machine

3.63 GiB across 239 items in 6.68 s. Temp contributed only 0.03 GiB with **747 items
excluded** — it had been cleared earlier the same day, and the 7-day age test correctly
disqualified everything written since, which is the rule behaving exactly as intended.

### A fail-open bug this phase caught

`PathJail.IsContained` returned **false for every path inside a volume root**.
`Path.TrimEndingDirectorySeparator` deliberately leaves `C:\` intact, so appending a
separator produced the prefix `C:\\`, which nothing matches. A denylist entry protecting an
entire volume therefore protected nothing. It failed *open*, on the widest possible root,
in the primitive the whole cleanup engine depends on — and it was found only because the
canary asserts in both directions. Fixed, with a regression test.

## 5c. M5 — the treemap, and what it measured

Split deliberately at the wire: `TreemapProjector` (`Silt.Core`) decides *what is worth
drawing*, `layout.ts` (pure TypeScript, no DOM) decides *where it goes*, and `Treemap.tsx`
only paints. Each of the three is checkable without the other two, which is what made the
measurements below possible at all.

### Area conservation is the invariant everything rests on

The projector never drops a node. Anything culled — too small, over the node budget, past
the depth limit — is rolled into a synthetic `(smaller items)` sibling, and every directory's
own loose files get a `(files here)` node. So **an expanded node's children sum to exactly
its own byte count**, and the renderer can scale children against their parent's box with no
possibility of overflow. Without the loose-files node, a folder holding 300 bytes itself and
one 100-byte subfolder would draw that subfolder at four times its true size.

Enforced by `AssertAreaIsConserved` across seven cases including a random deep uneven tree.

### The payload cap had a real bug in the obvious implementation

Sizing the 8 MB budget with `Encoding.UTF8.GetByteCount` is wrong. `System.Text.Json`'s
default encoder escapes every non-ASCII character as `\uXXXX` — **six bytes, against the
three UTF-8 needs for the same CJK character** — so the estimate undershoots by ~2x and the
cap silently breaks. It would break only on machines whose directory names are not English,
i.e. nowhere it would be noticed. `EncodedNameCost` counts the escaped form; both the ASCII
and the non-ASCII fixture are serialized through the real encoder and asserted under 8 MiB.

### Measured on the dev fixture (810 x 460 CSS px, 160.9 GiB view)

| | |
|---|---|
| Nodes projected | 1,994 |
| Rectangles drawn | **1,396** |
| Culled below 9 px² | 529 |
| Folders rolled into `(smaller items)` | 149 |
| Child rectangles escaping their parent | **0** |
| Overlapping siblings | **0** |
| Rectangles below the 9 px² floor | **0** |
| Hit tests disagreeing with brute force (4,000 random points) | **0** |
| Aspect ratio, median / p95 | **1.45 / 2.60** |
| Canvas pixels painted (sampled) | 100 % — no gaps |

1,396 rectangles is the plan's "a few thousand, never 100k", reached without a special case.
The aspect figures are the evidence that this is genuinely squarified rather than merely
subdivided; slice-and-dice on the same data produces slivers in the hundreds.

Picking is a uniform grid over the layout — **not** a second id-encoded canvas. That
technique is not merely expensive, it is wrong: the 2D context premultiplies alpha and
antialiases any non-integer edge, so a `getImageData` readback near a boundary returns a
blend of two neighbouring ids which is itself a valid id. Wrong answer, no error, only near
edges. The grid agrees with brute force on every point tested.

### Two defects found by driving the UI, not by building it

Both passed build, typecheck and lint, and both were fatal in use:

1. **Almost the whole map was inert.** Targeting the rectangle under the cursor sounds
   obviously right and is not: a subdivided folder is entirely covered by its own children,
   so the only part of it reachable by a pointer is its ~15 px label band. Measured, 0 % of
   sampled points were clickable. Clicks now resolve upward to the nearest folder that has
   something to show — **83 %** of sampled points, the remainder being the root's own
   `(files here)` box, which correctly has nowhere to go.
2. **Zooming in crashed the results page.** Hover holds an index into `data.nodes`; clearing
   it in an effect leaves exactly one render where an index captured against the old view is
   read against the new one. Zooming returns a much smaller projection, so that index is
   routinely past the end — `TypeError: Cannot read properties of undefined (reading 'k')`,
   on the very first click. Now cleared during render, React's documented pattern for state
   derived from props. Verified over ten zoom-in/zoom-out cycles: **0 uncaught errors.**

Neither was reachable from a test that only asserts the layout is correct. The lesson is the
one already in §5a: build output is not behaviour.

### Left unverified, stated plainly

- **`devicePixelRatio` is exercised only at DPR 1.** The review browser reported 1, so the
  2x and 1.5x paths are correct by construction (`round(css * dpr)`) but not observed.
- **No M5 memory re-measurement.** §2.4 requires one at M5, and it has not been taken: the
  canvas was reviewed in an ordinary browser, not in the WebView2 shell where the 400 MB
  budget is measured. The single-canvas rule that the budget depends on *is* implemented and
  the production bundle creates no Web Worker (verified — `worker-src` is still absent from
  the CSP, so a blob worker would be blocked). **The measurement itself is still owed.**

## 5d. M6 — packaging, and the bug that only packaging could find

### Every published build shipped without a user interface

The shell copied `src/frontend/dist` into `$(OutDir)` from a target hooked to
`AfterTargets="Build"`. That works for `dotnet build` and is silently wrong for
`dotnet publish`: publish does not mirror `OutDir`, it collects the items MSBuild knows
about, and files dropped there by a custom `<Copy>` are not among them. So **every publish
this project has ever produced contained no `wwwroot` at all** — an installer built from it
would have reached a user and shown the "run `npm run build`" placeholder forever.

Nothing caught it. Build, test, lint, typecheck and the safety gates were green throughout,
because none of them ever produced the artifact a user receives. It was found by listing the
publish output by hand.

The fix moves the target to `BeforeTargets="AssignTargetPaths"` and contributes `Content`
items with `<Link>` metadata, which is the one hook that feeds both the build copy and the
publish collection. The durable fix is the **`package` CI job**: it publishes and compiles
the installer on every push, so the shippable artifact is exercised as often as the tests
are. This is the same lesson as §5a and §5c — build output is not behaviour — applied to
packaging.

`scripts/publish.ps1` verifies rather than trusts: exe present and large enough to genuinely
be self-contained, `wwwroot/index.html` and `wwwroot/assets` present, at least one JS bundle,
and `index.html` referencing a bundle that actually exists (a stale `dist/` passes every
other check and still yields a blank window). Verified by removing `dist/` and re-running:
three errors, exit 1.

### Measured

| | |
|---|---|
| Publish payload | **134.9 MB** (`Silt.exe` 134.6 MB self-contained single-file + 245 KB `wwwroot`) |
| Installer (LZMA2 max) | **43 MB** |
| ISCC compile | clean, no warnings |

Reference XML docs are now excluded from publish (`AllowedReferenceRelatedFileExtensions`),
dropping 0.76 MB of WebView2 IntelliSense payload nothing reads at runtime. PDBs stay — a
stack trace from a user's machine is worth far more than the ~100 KB.

### The uninstaller must not delete the WebView2 cache

The obvious `[UninstallDelete]` entry — drop `{localappdata}\Silt\WebView2`, which is pure
regenerable cache — is wrong, and Inno Setup says so at compile time
(`UsedUserAreasWarning`). With `PrivilegesRequired=admin` the uninstaller runs elevated, so
`{localappdata}` resolves to the **administrator's** profile. On a machine with a separate
admin account it deletes a directory belonging to someone who never ran Silt and misses the
real one. Resolved by deleting nothing on uninstall, which is also the right answer for the
snapshots and the operation journal regardless: they are the user's scan history and the
audit trail of what Silt deleted for them.

`runasoriginaluser` on the post-install launch is load-bearing for the same class of reason.
Without it, setup's admin token is inherited by the first ever run of Silt — the one thing
§2.1 says must never happen — and it would create `%LOCALAPPDATA%\Silt\WebView2` at high
integrity, which every later ordinary launch then contends with. The damage outlives the
install.

### Version has one source

The git tag stamps `Silt.exe` via `-p:Version=`; the installer reads its version back out of
the built exe rather than carrying its own copy. An installer labelled 0.2.0 therefore cannot
contain a 0.1.0 binary. `Directory.Build.props` supplies the development default only when
nothing overrides it — unconditional, it silently beat the workflow and every release would
have shipped stamped 0.1.0.

Releases are **drafts**. A mistyped tag should not put an installer in front of the world
with no step in between where a human looks at it.

### M5's owed memory measurement — partly paid

§2.4 required a re-measurement at M5 and §5c recorded it as still owed. Taken now against the
**published Release single-file build**, process tree only (an earlier reading of 896.7 MB
across 19 processes was wrong — `Get-Process msedgewebview2` catches every WebView2 app on
the machine, not Silt's):

| Milestone | Total | Shell process | Processes |
|---|---|---|---|
| M0 — static page | 470.5 MB | 110.6 MB | 7 |
| M1 — C: scan resident | 627.5 MB | 235.7 MB | 7 |
| M2 — scan + attribution | 590.0 MB | 180.2 MB | 7 |
| **M6 — published build, idle** | **468.8 MB** | **123.1 MB** | 7 |

The treemap code is present and the single-canvas rule holds: still 7 processes, and idle
cost is indistinguishable from M0's. **This is not the measurement §2.4 asked for.** It is
the idle figure. A scan-plus-treemap-resident reading requires driving the WPF window, and
§5a abandoned synthetic global input as unreliable and, on a multi-monitor setup, unsafe.
**The loaded M5 figure is still owed** and needs a deliberate manual session, not automation.

### Left unverified, stated plainly

- **The installer has never been run.** It compiles clean and the compile log accounts for
  all ten payload files, but install, upgrade-over-existing, and uninstall are unexercised:
  each needs an interactive UAC prompt, and these runs are unattended. Do this by hand, on a
  VM, before tagging anything.
- **The release workflow has never fired.** It is tag-triggered and no tag exists. The
  `package` CI job exercises the publish-and-compile half of it on every push; the
  `gh release create` half is unproven.
- **`devicePixelRatio` remains exercised only at DPR 1** (carried from §5c).

## 5e. The treemap's invariants became a gate

§5c measured the treemap's geometry by driving the real module from a browser console and
recorded the numbers: 0 child rectangles escaping their parent, 0 overlapping siblings, 0 hit
tests disagreeing with brute force over 4,000 points. That was evidence and it was **not a
gate** — nothing would have caught a later edit that broke any of it, because a treemap whose
rectangles overlap passes build, typecheck and lint without complaint. §4 named adding Vitest
the highest-value frontend follow-up. It is now installed, wired into the `frontend` CI job,
and 34 tests run in **~0.5 s**.

The pure, DOM-free split that §5c made for testability is what allowed this: `layout.ts` needs
no canvas, no jsdom and no renderer to be driven hard.

### One refactor came with it

`openTarget` — the click-resolution rule, and the source of the "0 % of the map was clickable"
defect — lived inside `Treemap.tsx` as a `useCallback`, where it could not be tested without a
React renderer. It is now `resolveOpenTarget` in `layout.ts`, called by an unchanged
component. The behaviour is identical; only its reachability changed.

### Tests that would not have failed are worthless, so they were mutation-checked

Each of the three load-bearing assertions was verified by breaking the code under it and
confirming a red run — not by observing that it passed:

| Mutation | Caught by | Signal |
|---|---|---|
| `horizontal = w >= h` → `true` (rows always laid one way) | containment | child at 199.87 px against a parent ending at 186.25 px |
| Row never closed (i.e. slice-and-dice) | aspect ratio | **median 1025.4** against a bound of 3 |
| Rectangle bucketed into its first column only | pick vs brute force | `null` returned where rect 134 was |

The slice-and-dice result is the useful one: that mutation satisfies **every** containment and
overlap assertion while producing slivers, so the aspect-ratio bound is the only thing
standing between "squarified" and "merely subdivided".

### Measured on the deterministic test fixture (810 × 460 CSS px)

| | depth 5 | depth 7 |
|---|---|---|
| Nodes projected | 1,180 | 15,160 |
| Rectangles drawn | 663 | **1,464** |
| Culled below 9 px² | 312 | 2,244 |
| Aspect ratio, median / p95 | 1.52 / 4.69 | **1.51 / 3.51** |
| Points resolving to something clickable | 81.3 % | 81.3 % |
| Layout time | 2.05 ms | **2.75 ms** |
| 2,000 hit tests | 1.37 ms | 1.33 ms |

Median 1.51 against the 1.45 §5c measured on the dev fixture, and 81.3 % clickable against
83 % — two independent fixtures agreeing to within noise, which is the corroboration that
matters more than either number alone. A 15,160-node tree still reaches the canvas as 1,464
rectangles, and the whole layout costs 2.75 ms.

### Re-driven in the browser, because build output is still not behaviour

The refactor was verified against the running app on the dev mock API, not merely compiled:

- **One canvas**, 810 × 460 CSS px, backing store 810 × 460 at DPR 1, **100 % of sampled
  pixels painted** — no gaps.
- Hover resolution correct on every point sampled, including the two cases with opposite
  answers: the root's own `(files here)` offers no destination, while a nested `(files here)`
  correctly offers *"click to open Windows"*.
- A click navigated from the root to `C:\ › Users › index-1 › snapshots-3 › bin-2` and
  redrew at *1.3 GiB in view · 4 rectangles drawn*, with **no uncaught errors** — the §5c
  zoom crash stays fixed.

### Left unverified, stated plainly

- **`Treemap.tsx` itself has no test.** The gate covers the pure layout, picking, click
  resolution and formatting; the React component — the canvas draw loop, the ResizeObserver,
  and the clear-hover-during-render fix — is still covered only by driving it by hand. Adding
  a renderer means jsdom plus a canvas stub, and a stubbed canvas would prove nothing about
  the draw loop, which is the part with cost in it.
- ~~**`devicePixelRatio` remains exercised only at DPR 1.**~~ **Closed — see §5f.**

## 5f. The pixel-ratio path, finally driven — and the bug hiding behind it

§5c, §5d and §5e each recorded "`devicePixelRatio` exercised only at DPR 1" and each waited
for a 150 % display to turn up. That was the mistake: **the ratio is an input, so it can be
supplied as one.** The arithmetic moved out of the `useEffect` into `canvas.ts`, where it is a
pure function of three numbers, and the browser review overrode `window.devicePixelRatio` to
drive the real component at 1.5, 2 and 3.

Both halves were needed. The unit tests alone would have missed the defect below entirely,
and the browser alone would have missed the guards.

### The defect: the backing store was reallocated on every mouse move

Assigning `canvas.width` discards and reallocates the backing store **even when the value
assigned is identical** — that is how the idiom clears a canvas. The draw effect depends on
`hover`, so it runs on every pointer move over the map, and it assigned `canvas.width`
unconditionally. Every mousemove therefore threw away a full backing store and allocated a
fresh one.

At DPR 1 that is 1.5 MB per pointer event and easy to miss. **At DPR 2 it is 5.96 MB per
pointer event** — and it is invisible at DPR 1, in an app §2.4 already measures as over its
400 MB budget, whose entire single-canvas rule exists because backing store scales with the
*square* of the pixel ratio. Measured over 200 pointer moves at DPR 2: **1,137 MiB of
allocation churn, now zero.** The fix is a comparison before the assignment; the draw loop
already called `clearRect` explicitly, so nothing depended on the assignment's side effect.

### The second defect: a pixel-ratio change was never noticed

Found only by driving it. There is no `devicePixelRatio` change event, and the ratio was read
inline inside the draw effect — whose dependencies are the data, the layout, the width and the
hover. Dragging the window from a 100 % monitor to a 150 % one changes **none of those**, so
the canvas kept its old backing store and the map stayed soft (or over-sharp) until some
unrelated re-render happened to fix it. Observed exactly that: after a simulated 2 → 1.5
change the store sat at 1620 × 920 until a stray pointer move corrected it.

Now the ratio lives in state, subscribed via `matchMedia('(resolution: Ndppx)')` — which is
one-shot by construction, since the query stops matching the instant the ratio moves, so the
listener re-establishes against the new value each time.

### Area, not ratio, is what gets capped

The obvious guard is a maximum `devicePixelRatio`, and it is wrong: browser zoom multiplies
the reported ratio while shrinking the CSS pixel count by the same factor, so a DPR ceiling
blurs the map for anyone zoomed in and saves nothing. The cap is therefore on backing-store
**area** — 8 M device pixels, a 32 MB ceiling. A full-width 4K window at DPR 2 is 3.07 M and
is not touched. Without the cap, a 3840 × 2160 viewport at DPR 4 asks for **132.7 M device
pixels — 530 MB of backing store on one canvas.**

`normalizeDpr` also rejects what `window.devicePixelRatio || 1` lets through: that idiom
handles `0` and `NaN` and passes `Infinity` and negatives straight to `canvas.width`, which
throws `IndexSizeError` and takes the whole panel down.

### Measured — unit, at the ratios real displays report

| DPR | CSS px | Backing store |
|---|---|---|
| 1 | 810 × 460 | 810 × 460 |
| 1.25 | 810 × 460 | 1013 × 575 |
| 1.5 | 810 × 460 | 1215 × 690 |
| 2 | 810 × 460 | 1620 × 920 |
| 3 | 810 × 460 | 2430 × 1380 |

Rounding, not truncation: 811 CSS px at DPR 1.5 wants 1216.5 device pixels, and flooring
loses the right-hand half pixel on every odd width — a hairline seam that appears only on
150 % displays, i.e. never on the developer's machine.

### Mutation-checked, as §9 requires

| Mutation | Caught by | Signal |
|---|---|---|
| `Math.round` → `Math.floor` | backing-store size | 1216 against 1217 at DPR 1.5 |
| Reallocation guard removed | allocation count | **101 allocations against 1** |
| Area cap removed | backing-store area | **132,710,400 px against a cap of 8,000,000** |
| `normalizeDpr` → the `\|\| 1` idiom | ratio normalization | `Infinity` reached `canvas.width` |

### Measured — in the browser, against the running app at DPR 2

| | |
|---|---|
| CSS size / backing store | 810 × 460 / **1620 × 920** |
| Device pixels sampled | 93,150 |
| Painted | **100 %** — including the last device column and the last device row |
| Backing-store reallocations over 200 pointer moves | **0** (was 200) |
| Allocation churn avoided | **1,137 MiB** |
| Reallocations for a 2 → 1.5 ratio change | **1**, and the CSS size stayed 810 px |
| Console errors / uncaught errors | **0 / 0** |

The last-column and last-row probes are the ones that matter: a drawing scale that disagrees
with the backing store by any amount leaves exactly that strip unpainted and nothing else
looks wrong. Sampling every fourth pixel would have missed it.

### Left unverified, stated plainly

- **The `matchMedia` subscription was fired synthetically, not by a real monitor move.** The
  ratio was overridden and the `change` event dispatched by hand. The listener, the
  re-subscription and the redraw are all observed; what is *not* observed is the browser
  choosing to fire it. That needs two displays at different scale factors.
- **Still no measurement inside the WebView2 shell.** All of the above was taken in an
  ordinary browser on the dev mock API. The loaded-treemap memory figure §2.4 asks for
  remains owed, unchanged from §5d.

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
- [ ] Frontend tests pass (`npm --prefix src/frontend test`), and any new geometry
      invariant was mutation-checked — a test confirmed red against broken code, not merely
      observed green against working code
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
