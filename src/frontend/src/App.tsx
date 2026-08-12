import './App.css'

/**
 * M0 shell verification screen.
 *
 * This exists to prove the WPF -> WebView2 -> React host path works end to end. It is
 * replaced by the real scan UI in M1.
 */

type Milestone = {
  id: string
  title: string
  detail: string
  done: boolean
}

const MILESTONES: Milestone[] = [
  { id: 'M0', title: 'Shell', detail: 'WPF + WebView2 hosting React', done: true },
  { id: 'M1', title: 'Scanner', detail: 'Parallel BFS + reconciliation waterfall', done: false },
  { id: 'M2', title: 'Attribution', detail: 'Per-app footprint across all locations', done: false },
  { id: 'M3', title: 'Growth', detail: 'Snapshots and week-over-week diffs', done: false },
  { id: 'M4', title: 'Cleanup', detail: 'Dry-run engine and the six rules', done: false },
  { id: 'M5', title: 'Treemap', detail: 'Canvas rendering with spatial-index picking', done: false },
  { id: 'M6', title: 'Release', detail: 'Installer, self-contained publish, updates', done: false },
]

export default function App() {
  return (
    <div className="app">
      <header className="header">
        <h1 className="wordmark">Silt</h1>
        <p className="tagline">
          Find out where your disk space actually went &mdash; and get it back safely.
        </p>
      </header>

      <section className="status" role="status">
        <span className="dot" aria-hidden="true" />
        <span>
          Shell online &middot; React {'→'} WebView2 {'→'} WPF
        </span>
      </section>

      <section>
        <h2 className="section-title">Roadmap</h2>
        <ol className="milestones">
          {MILESTONES.map((m) => (
            <li key={m.id} className={m.done ? 'milestone done' : 'milestone'}>
              <span className="milestone-id">{m.id}</span>
              <span className="milestone-body">
                <span className="milestone-title">{m.title}</span>
                <span className="milestone-detail">{m.detail}</span>
              </span>
              <span className="milestone-mark" aria-hidden="true">
                {m.done ? '✓' : ''}
              </span>
            </li>
          ))}
        </ol>
      </section>

      <footer className="footer">
        <p>
          v1 runs entirely unelevated. No UAC prompt, no privileged helper, no telemetry.
        </p>
      </footer>
    </div>
  )
}
