# Silt

**Find out where your disk space actually went — and get it back safely.**

> *Silt: fine sediment that accumulates quietly until it chokes the channel.*

[![CI](https://github.com/mhalder-dev/silt/actions/workflows/ci.yml/badge.svg)](https://github.com/mhalder-dev/silt/actions/workflows/ci.yml)

---

## Why

A developer machine loses disk space in places no tool shows you. Measured on a real
Windows 11 machine in August 2026:

- `%LOCALAPPDATA%\Temp` had grown to **44 GB** — silently, over months.
- **Claude Desktop occupied 18.9 GB** across three unrelated directories. Every existing
  tool shows three small folders. None says *"Claude Desktop: 18.9 GB."*
- npm's cache held **6.75 GB** with no UI anywhere in the system.
- A naive PowerShell scan of the user profile **timed out twice** before finishing in
  over five minutes.

Existing free tools scan fast (WizTree) or draw treemaps (WinDirStat), but none of them
attribute space **per application**, track **growth over time**, or offer **guided cleanup
that tells you how a deleted thing comes back**.

Silt does those three.

## What it does

| | |
|---|---|
| 🔍 **Per-app footprint** | Aggregates an application's real total across `AppData\Local`, `AppData\Roaming`, `Packages`, and `Program Files` |
| 📈 **Growth tracking** | Daily snapshots and diffs — *"Temp grew 12 GB this week"* |
| 🧹 **Guided cleanup** | Dry-run first, always. Every rule names how the data regenerates |
| 🗺️ **Treemap** | Fast canvas visualization of what is actually large |
| ⚖️ **Honest accounting** | A reconciliation waterfall that refuses to hide unexplained space |

## What it deliberately does not do

No registry cleaning. No "RAM boosters" (calling `EmptyWorkingSet` across all processes
just forces pages to disk and makes the machine slower). No unattended deletion. No
telemetry — a tool that indexes your entire filesystem should never phone home.

See [the refusal list](docs/PLAN.md#7-the-refusal-list).

## Safety model

Silt deletes files, so the safety machinery is the product:

- **Rule 0** — nothing is deleted unless the rule can name *how it comes back*. Enforced at
  schema load.
- **Dry-run is the only planning path.** Execution re-validates every item against file id,
  size, last-write time, attributes, and lock state before touching it.
- **Recycle Bin by default.** If a batch exceeds the volume's Recycle Bin capacity, Silt
  **refuses and offers to split it** rather than silently permanently deleting the overflow.
- **A compiled denylist that cannot be overridden** — no `force` or `skipGuard` flag exists
  in the wire format at all.
- **A startup canary** asserts ~60 known-protected paths at boot; the host refuses to start
  if any assertion fails.
- **v1 runs entirely unelevated.** No UAC prompt, no privileged helper. Every byte worth
  reclaiming on a normal machine is user-owned anyway.

## Status

🚧 **Pre-release.** Every capability above is built and verified: the scanner, per-app
attribution, snapshots and growth diffs, the cleanup engine, and the treemap. M6 —
packaging — is in progress, so there is no published release to download yet.

| | |
|---|---|
| M0 · Shell | ✅ WPF + WebView2 + React |
| M1 · Scanner | ✅ Whole `C:` in ~9 s, hardlink de-duplicated |
| M2 · Attribution | ✅ Per-app totals across scattered install locations |
| M3 · Snapshots | ✅ History and growth diffing |
| M4 · Cleanup | ✅ Denylist, dry-run, Recycle Bin, capacity refusal |
| M5 · Treemap | ✅ Squarified, one canvas, spatial-index picking |
| M6 · Packaging | 🚧 Installer and release pipeline |

See [`docs/PLAN.md`](docs/PLAN.md) for the plan of record, the milestone table, and the
architecture review that shaped it.

## Installing

Not yet — there is no published release. When there is, it will be a signed-by-nobody,
checksummed, self-contained installer; [`docs/INSTALL.md`](docs/INSTALL.md) covers what it
does, why it elevates once and never again, and where Silt keeps your data.

## Building

Requires the **.NET 10 SDK** and **Node 22+**.

```bash
git clone https://github.com/mhalder-dev/silt.git
cd silt
dotnet build
npm --prefix src/frontend install
npm --prefix src/frontend run dev
```

To produce the shippable artifact — the SPA plus a self-contained single-file exe, both
verified — and then the installer (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
pwsh scripts/publish.ps1
iscc installer\silt.iss
```

## License

MIT — see [LICENSE](LICENSE).
