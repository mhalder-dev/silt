import { useCallback, useEffect, useRef, useState } from 'react'
import {
  api,
  type AppFootprint,
  type AppsResponse,
  type Growth,
  type Reconciliation,
  type ScanStatus,
  type ScanSummary,
  type TreeNode,
  type TreeResponse,
  type Volume,
} from './api'
import { formatBytes, formatCount, formatDuration, formatPercent } from './format'
import './App.css'

type View =
  | { kind: 'picking' }
  | { kind: 'scanning'; scanId: string }
  | { kind: 'results'; scanId: string }

export default function App() {
  const [volumes, setVolumes] = useState<Volume[] | null>(null)
  const [view, setView] = useState<View>({ kind: 'picking' })
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .listVolumes()
      .then(setVolumes)
      .catch((e: Error) => setError(e.message))
  }, [])

  const startScan = useCallback(async (root: string) => {
    setError(null)
    try {
      const { scanId } = await api.startScan(root)
      setView({ kind: 'scanning', scanId })
    } catch (e) {
      setError((e as Error).message)
    }
  }, [])

  return (
    <div className="app">
      <header className="header">
        <h1 className="wordmark">Silt</h1>
        {view.kind !== 'picking' && (
          <button className="link-button" onClick={() => setView({ kind: 'picking' })}>
            New scan
          </button>
        )}
      </header>

      {error && <div className="error">{error}</div>}

      {view.kind === 'picking' && <VolumePicker volumes={volumes} onScan={startScan} />}
      {view.kind === 'scanning' && (
        <ScanProgress
          scanId={view.scanId}
          onDone={() => setView({ kind: 'results', scanId: view.scanId })}
          onError={setError}
        />
      )}
      {view.kind === 'results' && <Results scanId={view.scanId} />}
    </div>
  )
}

function VolumePicker({
  volumes,
  onScan,
}: {
  volumes: Volume[] | null
  onScan: (root: string) => void
}) {
  if (volumes === null) return <p className="muted">Looking for volumes…</p>
  if (volumes.length === 0) return <p className="muted">No fixed volumes found.</p>

  return (
    <section>
      <h2 className="section-title">Choose a volume</h2>
      <div className="volumes">
        {volumes.map((v) => {
          const used = v.capacityBytes - v.freeBytes
          const pct = v.capacityBytes > 0 ? used / v.capacityBytes : 0
          return (
            <button key={v.root} className="volume" onClick={() => onScan(v.root)} disabled={!v.isReady}>
              <div className="volume-head">
                <span className="volume-root">{v.root}</span>
                <span className="volume-fs">{v.fileSystem}</span>
              </div>
              <div className="bar">
                <div
                  className={pct > 0.9 ? 'bar-fill critical' : 'bar-fill'}
                  style={{ width: `${Math.min(100, pct * 100)}%` }}
                />
              </div>
              <div className="volume-foot">
                <span>{formatBytes(v.freeBytes)} free</span>
                <span className="muted">of {formatBytes(v.capacityBytes)}</span>
              </div>
            </button>
          )
        })}
      </div>
    </section>
  )
}

function ScanProgress({
  scanId,
  onDone,
  onError,
}: {
  scanId: string
  onDone: () => void
  onError: (message: string) => void
}) {
  const [status, setStatus] = useState<ScanStatus | null>(null)
  const doneRef = useRef(false)

  useEffect(() => {
    let cancelled = false

    // Polling rather than a push channel: a scan finishes in seconds, and a 200 ms poll of
    // an in-process handler costs less than standing up a streaming transport would.
    const tick = async () => {
      if (cancelled || doneRef.current) return
      try {
        const s = await api.getStatus(scanId)
        if (cancelled) return
        setStatus(s)
        if (s.state === 'completed') {
          doneRef.current = true
          onDone()
          return
        }
        if (s.state === 'failed' || s.state === 'cancelled') {
          doneRef.current = true
          onError(s.error ?? `Scan ${s.state}.`)
          return
        }
      } catch (e) {
        if (!cancelled) onError((e as Error).message)
        return
      }
      setTimeout(tick, 200)
    }

    void tick()
    return () => {
      cancelled = true
    }
  }, [scanId, onDone, onError])

  return (
    <section className="scanning">
      <div className="spinner" aria-hidden="true" />
      <h2>Scanning {status?.root ?? ''}</h2>
      <dl className="progress-stats">
        <div>
          <dt>Directories</dt>
          <dd>{formatCount(status?.directoriesScanned ?? 0)}</dd>
        </div>
        <div>
          <dt>Files</dt>
          <dd>{formatCount(status?.filesScanned ?? 0)}</dd>
        </div>
        <div>
          <dt>Measured</dt>
          <dd>{formatBytes(status?.bytesScanned ?? 0)}</dd>
        </div>
      </dl>
      <p className="current-path" title={status?.currentPath}>
        {status?.currentPath ?? '…'}
      </p>
      <button className="link-button" onClick={() => void api.cancel(scanId)}>
        Cancel
      </button>
    </section>
  )
}

function Results({ scanId }: { scanId: string }) {
  const [summary, setSummary] = useState<ScanSummary | null>(null)
  const [tree, setTree] = useState<TreeResponse | null>(null)
  const [path, setPath] = useState<string | undefined>(undefined)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .getSummary(scanId)
      .then(setSummary)
      .catch((e: Error) => setError(e.message))
  }, [scanId])

  useEffect(() => {
    api
      .getTree(scanId, path)
      .then(setTree)
      .catch((e: Error) => setError(e.message))
  }, [scanId, path])

  if (error) return <div className="error">{error}</div>
  if (!summary) return <p className="muted">Loading results…</p>

  return (
    <>
      <SummaryStats summary={summary} />
      <GrowthPanel scanId={scanId} />
      <AppFootprints scanId={scanId} />
      {summary.reconciliation && <Waterfall r={summary.reconciliation} />}
      <TreeBrowser
        tree={tree}
        rootPath={summary.root}
        currentPath={path ?? summary.root}
        onNavigate={setPath}
      />
    </>
  )
}

const WINDOW_OPTIONS = [
  { days: 1, label: '24 hours' },
  { days: 7, label: '7 days' },
  { days: 30, label: '30 days' },
]

function signed(bytes: number): string {
  return `${bytes >= 0 ? '+' : '−'}${formatBytes(Math.abs(bytes))}`
}

function GrowthPanel({ scanId }: { scanId: string }) {
  const [days, setDays] = useState(7)
  const [growth, setGrowth] = useState<Growth | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setGrowth(null)
    api
      .getGrowth(scanId, days)
      .then(setGrowth)
      .catch((e: Error) => setError(e.message))
  }, [scanId, days])

  if (error) return <div className="error">{error}</div>

  return (
    <section>
      <div className="growth-header">
        <h2 className="section-title">What changed</h2>
        <div className="window-picker" role="group" aria-label="Comparison window">
          {WINDOW_OPTIONS.map((option) => (
            <button
              key={option.days}
              className={option.days === days ? 'window active' : 'window'}
              onClick={() => setDays(option.days)}
            >
              {option.label}
            </button>
          ))}
        </div>
      </div>

      {!growth && <p className="muted">Comparing with earlier scans…</p>}

      {growth && !growth.available && (
        <div className="growth-empty">
          <p>{growth.unavailable}</p>
          <p className="muted">
            Silt records a snapshot automatically after every whole-volume scan, so history
            builds up on its own. {growth.snapshotCount === 1 && '1 snapshot recorded so far.'}
          </p>
        </div>
      )}

      {growth?.available && <GrowthBody growth={growth} />}
    </section>
  )
}

function GrowthBody({ growth }: { growth: Growth }) {
  const grew = growth.deltaBytes >= 0
  const maxDelta = Math.max(
    ...growth.directories.map((d) => Math.abs(d.selfDeltaBytes)),
    ...growth.apps.map((a) => Math.abs(a.deltaBytes)),
    1,
  )

  return (
    <>
      <div className={grew ? 'growth-headline up' : 'growth-headline down'}>
        <span className="growth-delta">{signed(growth.deltaBytes)}</span>
        <span className="growth-context">
          over {growth.spanDays.toFixed(1)} days
          {' · '}
          free space {signed(growth.freeDeltaBytes)}
        </span>
      </div>

      {growth.floorsDiffer && (
        <p className="note">
          These snapshots were recorded with different size thresholds, so a folder may appear
          as new when it only crossed the threshold.
        </p>
      )}

      {growth.apps.length > 0 && (
        <>
          <h3 className="subsection-title">By application</h3>
          <ul className="changes">
            {growth.apps.map((app) => (
              <li key={app.key} className={`change ${app.deltaBytes >= 0 ? 'up' : 'down'}`}>
                <span className="change-name">{app.displayName}</span>
                <span className="change-bar">
                  <span
                    className="change-fill"
                    style={{ width: `${(Math.abs(app.deltaBytes) / maxDelta) * 100}%` }}
                  />
                </span>
                <span className="change-delta">{signed(app.deltaBytes)}</span>
              </li>
            ))}
          </ul>
        </>
      )}

      {growth.directories.length > 0 ? (
        <>
          <h3 className="subsection-title">By folder</h3>
          <p className="muted subsection-note">
            Each figure is the change that originated in that folder itself, with its
            subfolders' changes subtracted — so the folder actually responsible is named,
            not every parent above it.
          </p>
          <ul className="changes">
            {growth.directories.map((dir) => (
              <li key={dir.path} className={`change ${dir.selfDeltaBytes >= 0 ? 'up' : 'down'}`}>
                <span className="change-name" title={dir.path}>
                  {dir.path}
                  {dir.kind === 'Added' && <span className="tag tag-added">new</span>}
                  {dir.kind === 'Removed' && <span className="tag tag-removed">gone</span>}
                </span>
                <span className="change-bar">
                  <span
                    className="change-fill"
                    style={{ width: `${(Math.abs(dir.selfDeltaBytes) / maxDelta) * 100}%` }}
                  />
                </span>
                <span className="change-delta">{signed(dir.selfDeltaBytes)}</span>
              </li>
            ))}
          </ul>
        </>
      ) : (
        <p className="muted">No folder changed by more than 16 MiB in this window.</p>
      )}
    </>
  )
}

const LOCATION_LABELS: Record<string, string> = {
  Install: 'installed',
  LocalData: 'local data',
  RoamingData: 'roaming data',
  PackageData: 'store app data',
  MachineData: 'all-users data',
}

function AppFootprints({ scanId }: { scanId: string }) {
  const [data, setData] = useState<AppsResponse | null>(null)
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .getApps(scanId)
      .then(setData)
      .catch((e: Error) => setError(e.message))
  }, [scanId])

  const toggle = (key: string) =>
    setExpanded((previous) => {
      const next = new Set(previous)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })

  if (error) return <div className="error">{error}</div>
  if (!data) return <p className="muted">Attributing space to applications…</p>
  if (data.apps.length === 0) return null

  const split = data.apps.filter((a) => a.isSplitAcrossLocations).length
  const max = Math.max(...data.apps.map((a) => a.totalAllocatedBytes), 1)

  return (
    <section>
      <h2 className="section-title">By application</h2>
      <p className="muted waterfall-intro">
        Windows scatters one application across Program Files, Local, Roaming, Packages and
        ProgramData. {split > 0 && <>{split} of these are split across more than one place. </>}
        Sizes below are the real totals.
      </p>

      <ul className="apps">
        {data.apps.map((app) => (
          <AppRow
            key={app.key}
            app={app}
            max={max}
            open={expanded.has(app.key)}
            onToggle={() => toggle(app.key)}
          />
        ))}
      </ul>

      <p className="note">
        Grouping is a heuristic. Expand any row to see exactly which folders were counted, so a
        wrong grouping is visible rather than silently baked into the number.
      </p>
    </section>
  )
}

function AppRow({
  app,
  max,
  open,
  onToggle,
}: {
  app: AppFootprint
  max: number
  open: boolean
  onToggle: () => void
}) {
  return (
    <li className={open ? 'app open' : 'app'}>
      <button className="app-head" onClick={onToggle} aria-expanded={open}>
        <span className="app-caret" aria-hidden="true">
          {open ? '▾' : '▸'}
        </span>
        <span className="app-name">
          {app.displayName}
          {app.isSplitAcrossLocations && (
            <span className="tag tag-split">{app.locations.length} places</span>
          )}
        </span>
        <span className="app-size">{formatBytes(app.totalAllocatedBytes)}</span>
      </button>

      <div className="bar">
        <div
          className="bar-fill"
          style={{ width: `${(app.totalAllocatedBytes / max) * 100}%` }}
        />
      </div>

      {app.publisher && <div className="app-publisher">{app.publisher}</div>}

      {open && (
        <ul className="app-locations">
          {app.locations.map((location) => (
            <li key={location.path}>
              <span className="loc-size">{formatBytes(location.allocatedBytes)}</span>
              <span className="loc-kind">{LOCATION_LABELS[location.kind] ?? location.kind}</span>
              <span className="loc-path" title={location.path}>
                {location.path}
              </span>
            </li>
          ))}
        </ul>
      )}
    </li>
  )
}

function SummaryStats({ summary }: { summary: ScanSummary }) {
  return (
    <section className="summary">
      <div className="stat">
        <span className="stat-value">{formatBytes(summary.totalAllocatedBytes)}</span>
        <span className="stat-label">measured</span>
      </div>
      <div className="stat">
        <span className="stat-value">{formatCount(summary.totalFiles)}</span>
        <span className="stat-label">files</span>
      </div>
      <div className="stat">
        <span className="stat-value">{formatCount(summary.totalDirectories)}</span>
        <span className="stat-label">folders</span>
      </div>
      <div className="stat">
        <span className="stat-value">{formatDuration(summary.durationSeconds)}</span>
        <span className="stat-label">scan time</span>
      </div>

      <div className="caveats">
        {summary.hardLinkFilesDeduplicated > 0 && (
          <p>
            <strong>{formatBytes(summary.hardLinkBytesDeduplicated)}</strong> of hardlinked
            content was counted once rather than {formatCount(summary.hardLinkFilesDeduplicated)}{' '}
            times. Tools that skip this over-report, mostly inside Windows itself.
          </p>
        )}
        {summary.accessDeniedCount > 0 && (
          <p>
            <strong>{formatCount(summary.accessDeniedCount)} folders</strong> could not be read,
            so their contents are missing from the figures above.
          </p>
        )}
        {summary.skippedSurrogateCount > 0 && (
          <p>
            {formatCount(summary.skippedSurrogateCount)} junctions and symlinks were not
            followed, so nothing is counted twice.
          </p>
        )}
      </div>
    </section>
  )
}

function Waterfall({ r }: { r: Reconciliation }) {
  const max = Math.max(...r.lines.map((l) => Math.abs(l.bytes)), 1)

  return (
    <section>
      <h2 className="section-title">Where the used space went</h2>
      <p className="muted waterfall-intro">
        {formatBytes(r.usedBytes)} used of {formatBytes(r.capacityBytes)}. Every line below is
        accounted for, including the part that is not.
      </p>
      <ul className="waterfall">
        {r.lines.map((line) => (
          <li key={line.label} className={`wf wf-${line.kind.toLowerCase()}`}>
            <div className="wf-head">
              <span className="wf-label">{line.label}</span>
              <span className="wf-bytes">{formatBytes(line.bytes)}</span>
            </div>
            <div className="bar">
              <div
                className="bar-fill"
                style={{ width: `${Math.max(0, (Math.abs(line.bytes) / max) * 100)}%` }}
              />
            </div>
            <p className="wf-detail">{line.detail}</p>
          </li>
        ))}
      </ul>
      {r.unaccountedFraction > 0.05 && (
        <p className="note">
          {formatPercent(r.unaccountedFraction)} of used space is unexplained. Scanning as
          administrator would let Silt read the {formatCount(r.inaccessibleDirectoryCount)}{' '}
          folders it was denied and query shadow copies.
        </p>
      )}
    </section>
  )
}

function TreeBrowser({
  tree,
  rootPath,
  currentPath,
  onNavigate,
}: {
  tree: TreeResponse | null
  rootPath: string
  currentPath: string
  onNavigate: (path: string | undefined) => void
}) {
  if (!tree) return <p className="muted">Loading folders…</p>

  const crumbs = buildCrumbs(rootPath, currentPath)
  const max = Math.max(...tree.children.map((c) => c.allocatedBytes), 1)

  return (
    <section>
      <h2 className="section-title">Folders</h2>

      <nav className="crumbs">
        {crumbs.map((c, i) => (
          <span key={c.path}>
            {i > 0 && <span className="crumb-sep">›</span>}
            <button
              className="crumb"
              onClick={() => onNavigate(i === 0 ? undefined : c.path)}
              disabled={c.path === currentPath}
            >
              {c.label}
            </button>
          </span>
        ))}
      </nav>

      {tree.children.length === 0 ? (
        <p className="muted">No subfolders here.</p>
      ) : (
        <ul className="tree">
          {tree.children.map((node) => (
            <TreeRow key={node.path} node={node} max={max} onOpen={onNavigate} />
          ))}
        </ul>
      )}

      {tree.truncated && (
        <p className="note">
          Showing the largest {formatCount(tree.children.length)} of{' '}
          {formatCount(tree.totalChildCount)} subfolders.
        </p>
      )}
    </section>
  )
}

function TreeRow({
  node,
  max,
  onOpen,
}: {
  node: TreeNode
  max: number
  onOpen: (path: string) => void
}) {
  const width = (node.allocatedBytes / max) * 100

  return (
    <li className="tree-row">
      <button
        className="tree-button"
        onClick={() => onOpen(node.path)}
        disabled={!node.hasChildren}
        title={node.path}
      >
        <span className="tree-name">
          {node.name}
          {node.conditions.map((c) => (
            <span key={c} className={`tag tag-${c}`}>
              {c}
            </span>
          ))}
        </span>
        <span className="tree-size">{formatBytes(node.allocatedBytes)}</span>
      </button>
      <div className="bar">
        <div className="bar-fill" style={{ width: `${width}%` }} />
      </div>
      <div className="tree-meta">
        {formatCount(node.fileCount)} files
        {node.directoryCount > 0 && ` · ${formatCount(node.directoryCount)} folders`}
      </div>
    </li>
  )
}

function buildCrumbs(rootPath: string, currentPath: string) {
  const crumbs = [{ label: rootPath, path: rootPath }]
  if (currentPath === rootPath) return crumbs

  const remainder = currentPath.slice(rootPath.length).replace(/^[\\/]+/, '')
  let accumulated = rootPath.replace(/[\\/]+$/, '')

  for (const segment of remainder.split(/[\\/]/).filter(Boolean)) {
    accumulated = `${accumulated}\\${segment}`
    crumbs.push({ label: segment, path: accumulated })
  }

  return crumbs
}
