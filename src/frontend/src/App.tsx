import { useCallback, useEffect, useRef, useState } from 'react'
import {
  api,
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
