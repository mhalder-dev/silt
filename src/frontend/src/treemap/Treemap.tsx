import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { formatBytes } from '../format'
import { applyCanvasGeometry, canvasGeometry, dprQuery, normalizeDpr } from './canvas'
import {
  childBearingNodes,
  layoutTreemap,
  pathOf,
  resolveOpenTarget,
  type Layout,
  type TreemapNodeDto,
  type TreemapRect,
  type TreemapResponse,
} from './layout'

/**
 * The treemap, drawn on ONE canvas.
 *
 * Not three, and not two. Picking goes through the layout's spatial index rather than a
 * second id-encoded canvas — `layout.ts` explains why that technique fails silently — and
 * the hover highlight is a stroke on the same canvas rather than an overlay element. Each
 * additional full-viewport backing store is tens of megabytes at DPR 2 on a 4K display,
 * against a footprint the plan has already measured as over budget.
 */

export const HEIGHT = 460

/**
 * A rectangle must be at least this big to be labelled.
 *
 * `fillText` costs roughly an order of magnitude more than `fillRect`, so labelling every
 * rectangle would make text rather than geometry the cost of a frame. Below this size the
 * label is an ellipsis with nothing in front of it anyway.
 */
export const LABEL_MIN_W = 44
export const LABEL_MIN_H = 16

/** Reported upward so the panel can say what was drawn, and what was not. */
export type TreemapStats = {
  rects: number
  culled: number
}

/** Hue is keyed to the top-level folder, so one subtree reads as one region of colour. */
function hueOf(name: string): number {
  let h = 0
  for (let i = 0; i < name.length; i++) {
    h = (Math.imul(h, 31) + name.charCodeAt(i)) | 0
  }
  return Math.abs(h) % 360
}

function fillFor(node: TreemapNodeDto, groupName: string, depth: number): string {
  const lightness = Math.min(52, 22 + depth * 6)
  if (node.k === 'Other') return `hsl(220 6% ${lightness}%)`
  if (node.k === 'Files') return `hsl(205 26% ${lightness}%)`
  return `hsl(${hueOf(groupName)} 40% ${lightness}%)`
}

export function Treemap({
  data,
  onNavigate,
  onStats,
}: {
  data: TreemapResponse
  onNavigate: (path: string | undefined) => void
  onStats?: (stats: TreemapStats) => void
}) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const frameRef = useRef<HTMLDivElement | null>(null)

  const [width, setWidth] = useState(0)
  const [hover, setHover] = useState<{ rect: TreemapRect; x: number; y: number } | null>(null)

  // Measured from the container, not the window. The layout is recomputed on every resize,
  // and the window would be the wrong number the moment this panel stops being full width.
  useEffect(() => {
    const element = frameRef.current
    if (!element) return

    const observer = new ResizeObserver(([entry]) => {
      setWidth(Math.floor(entry.contentRect.width))
    })
    observer.observe(element)
    setWidth(Math.floor(element.getBoundingClientRect().width))
    return () => observer.disconnect()
  }, [])

  // Held in state rather than read inline in the draw effect. The ratio can change with
  // nothing else changing — drag the window to a monitor at a different scale factor and the
  // data, the layout and the CSS width are all identical — so an inline read leaves the
  // backing store stale until some unrelated re-render happens to fix it. Observed: after a
  // simulated 2 → 1.5 change the canvas kept its 1620 x 920 store until a pointer move.
  const [dpr, setDpr] = useState(() => normalizeDpr(window.devicePixelRatio))
  useEffect(() => {
    const query = window.matchMedia(dprQuery(dpr))
    const onChange = () => setDpr(normalizeDpr(window.devicePixelRatio))
    query.addEventListener('change', onChange)
    return () => query.removeEventListener('change', onChange)
  }, [dpr])

  const layout: Layout | null = useMemo(
    () => (width > 0 ? layoutTreemap(data.nodes, width, HEIGHT) : null),
    [data, width],
  )

  // Cleared during render, not in an effect.
  //
  // Hover holds an index into `data.nodes`. Effects run after render, so clearing it there
  // left exactly one render in which a hover captured against the previous view was read
  // against the new one — and since zooming in returns a much smaller projection, that index
  // is routinely past the end of the new array. It threw on the first real click-to-zoom and
  // blanked the whole results page. Adjusting state during render is React's documented fix
  // for state derived from props: the re-render happens before anything is committed, so the
  // stale index is never observable.
  const [renderedFor, setRenderedFor] = useState(data)
  if (renderedFor !== data) {
    setRenderedFor(data)
    setHover(null)
  }

  const report = useRef(onStats)
  report.current = onStats

  useEffect(() => {
    if (layout) report.current?.({ rects: layout.rects.length, culled: layout.culled })
  }, [layout])

  /** Nodes that have at least one child in this projection. */
  const hasChildren = useMemo(() => childBearingNodes(data.nodes), [data])

  // The resolution rule itself lives in layout.ts, where it can be regression-tested; see
  // resolveOpenTarget for why "the rectangle under the cursor" is the wrong target.
  const openTarget = useCallback(
    (index: number): number | null => resolveOpenTarget(data.nodes, hasChildren, index),
    [data, hasChildren],
  )

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas || !layout || width <= 0) return

    // devicePixelRatio, not a hardcoded 2. On a 150 % display a 2x backing store is resampled
    // by the compositor and every edge in the map goes soft. The arithmetic, the guards and
    // the reason this must not reallocate on every hover all live in canvas.ts, where they
    // are testable without a renderer.
    const geometry = canvasGeometry(width, HEIGHT, dpr)
    applyCanvasGeometry(canvas, geometry)

    const ctx = canvas.getContext('2d')
    if (!ctx) return

    // Unconditional, because a frame that did not reallocate still carries the previous
    // frame's transform, and one that did has had it reset to identity.
    ctx.setTransform(geometry.scale, 0, 0, geometry.scale, 0, 0)
    ctx.clearRect(0, 0, width, HEIGHT)
    ctx.font = '11px "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif'
    ctx.textBaseline = 'top'
    ctx.lineWidth = 1

    // Fills first, labels second. Batching this way costs one text-state setup for the whole
    // frame instead of interleaving fill and text state per rectangle.
    for (const rect of layout.rects) {
      const node = data.nodes[rect.index]
      const groupName = rect.group >= 0 ? data.nodes[rect.group].n : node.n
      ctx.fillStyle = fillFor(node, groupName, rect.depth)
      ctx.fillRect(rect.x, rect.y, rect.w, rect.h)

      if (rect.w > 4 && rect.h > 4) {
        ctx.strokeStyle = 'rgba(11,14,20,0.85)'
        ctx.strokeRect(rect.x + 0.5, rect.y + 0.5, rect.w - 1, rect.h - 1)
      }
    }

    ctx.fillStyle = '#e6edf3'
    ctx.shadowColor = 'rgba(0,0,0,0.6)'
    ctx.shadowBlur = 3
    for (const rect of layout.rects) {
      if (rect.w < LABEL_MIN_W || rect.h < LABEL_MIN_H) continue

      const node = data.nodes[rect.index]
      const label = fit(ctx, node.n, rect.w - 8)
      if (label === null) continue

      ctx.save()
      ctx.beginPath()
      ctx.rect(rect.x, rect.y, rect.w, rect.h)
      ctx.clip()
      ctx.fillText(label, rect.x + 4, rect.y + 2)
      if (rect.h >= 32) {
        ctx.fillText(formatBytes(node.b), rect.x + 4, rect.y + 16)
      }
      ctx.restore()
    }
    ctx.shadowBlur = 0

    if (hover) {
      ctx.strokeStyle = '#4a9eff'
      ctx.lineWidth = 2
      ctx.strokeRect(hover.rect.x + 1, hover.rect.y + 1, hover.rect.w - 2, hover.rect.h - 2)
    }
  }, [data, layout, width, hover, dpr])

  const onMove = useCallback(
    (event: React.MouseEvent<HTMLCanvasElement>) => {
      if (!layout) return
      const bounds = event.currentTarget.getBoundingClientRect()
      const x = event.clientX - bounds.left
      const y = event.clientY - bounds.top
      const rect = layout.index.pick(x, y)
      setHover(rect ? { rect, x, y } : null)
    },
    [layout],
  )

  const onOpen = useCallback(() => {
    if (!hover) return
    const target = openTarget(hover.rect.index)
    if (target === null) return
    const next = pathOf(data.nodes, data.path, target)
    if (next) onNavigate(next)
  }, [hover, data, openTarget, onNavigate])

  const hovered = hover ? data.nodes[hover.rect.index] : null
  const target = hover ? openTarget(hover.rect.index) : null
  const canOpen = target !== null

  return (
    <div className="treemap" ref={frameRef}>
      <canvas
        ref={canvasRef}
        className={canOpen ? 'treemap-canvas can-open' : 'treemap-canvas'}
        onMouseMove={onMove}
        onMouseLeave={() => setHover(null)}
        onClick={onOpen}
      />
      {hover && hovered && (
        <div
          className="treemap-tip"
          style={{
            left: `${Math.min(hover.x + 14, Math.max(0, width - 240))}px`,
            top: `${Math.min(hover.y + 14, HEIGHT - 66)}px`,
          }}
        >
          <strong>{hovered.n}</strong>
          <span>{formatBytes(hovered.b)}</span>
          {hovered.k === 'Files' && (
            <span className="muted">files sitting directly in this folder</span>
          )}
          {hovered.k === 'Other' && (
            <span className="muted">items too small to draw separately</span>
          )}
          {/* Names the destination, because it is often the folder CONTAINING the box under
              the cursor rather than the box itself. */}
          {target !== null && (
            <span className="muted">
              {target === hover.rect.index
                ? 'click to zoom in'
                : `click to open ${data.nodes[target].n}`}
            </span>
          )}
        </div>
      )}
    </div>
  )
}

/** Shortens text to fit, with an ellipsis. Returns null when not even one character fits. */
function fit(ctx: CanvasRenderingContext2D, text: string, maxWidth: number): string | null {
  if (maxWidth <= 0) return null
  if (ctx.measureText(text).width <= maxWidth) return text

  // Binary search rather than trimming one character at a time: measureText is not free and
  // this runs for every labelled rectangle on every frame.
  let low = 0
  let high = text.length
  while (low < high) {
    const mid = Math.ceil((low + high) / 2)
    if (ctx.measureText(`${text.slice(0, mid)}…`).width <= maxWidth) low = mid
    else high = mid - 1
  }

  return low > 0 ? `${text.slice(0, low)}…` : null
}
