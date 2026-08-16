/**
 * Squarified treemap layout and hit-testing.
 *
 * Deliberately free of any canvas or DOM reference. The layout is the part with real
 * geometry in it, and keeping it pure means it can be reasoned about, measured and driven
 * from a console without a rendering context.
 *
 * Two decisions here are load-bearing and both come from the plan:
 *
 * 1. **Rectangles below `MIN_AREA` are never produced.** The renderer would draw them as a
 *    sub-pixel smear and no pointer could ever land on one, so they cost fill time and
 *    index space to communicate nothing. The backend already culls by *byte share*, which
 *    is the same rule expressed before the viewport is known; this is the pixel-accurate
 *    second half of it.
 *
 * 2. **Picking goes through a spatial index, not a second canvas.** Reading ids back out of
 *    an off-screen canvas via `getImageData` is unreliable: the 2D context premultiplies
 *    alpha, and any rectangle whose edges are not integer-aligned is antialiased, so the
 *    boundary pixels between two rectangles blend into a third value that is itself a
 *    perfectly plausible id. The failure is silent and position-dependent — the worst kind.
 */

export type TreemapKind = 'Directory' | 'Files' | 'Other'

/**
 * One node as it arrives on the wire.
 *
 * The single-letter fields are the API's, not an abbreviation invented here: this is the
 * one response whose size is capped, and `parentIndex` spelled out 20,000 times is a
 * quarter of a megabyte of field names.
 */
export type TreemapNodeDto = {
  p: number
  n: string
  b: number
  k: TreemapKind
  x: boolean
  c?: string[] | null
}

export type TreemapResponse = {
  path: string
  totalAllocatedBytes: number
  minimumBytes: number
  aggregatedNodeCount: number
  truncated: boolean
  nodes: TreemapNodeDto[]
}

export type TreemapRect = {
  /** Index into the response's `nodes` array. */
  index: number
  x: number
  y: number
  w: number
  h: number
  /** 0 for the view root. Used for shading and to resolve nested hits. */
  depth: number
  /**
   * Index of the depth-1 ancestor, or -1 for the root itself. Colour is keyed off this so
   * everything under one top-level folder reads as one region instead of a confetti of
   * unrelated hues.
   */
  group: number
}

/**
 * Smallest rectangle worth drawing, in CSS pixels squared.
 *
 * 9 px² is about a 3x3 patch. Below that a rectangle is not distinguishable from its
 * border and cannot be clicked.
 */
export const MIN_AREA = 9

/** Gap left inside a parent before its children are laid out. */
const PADDING = 1

/** Height of a parent's label band, when the parent is big enough to deserve one. */
const HEADER = 15

/** A parent smaller than this gets no header band — the label would not fit anyway. */
const HEADER_MIN_W = 70
const HEADER_MIN_H = 44

export type Layout = {
  rects: TreemapRect[]
  index: SpatialIndex
  /** Rectangles that existed in the data but were too small to draw. Reported, not hidden. */
  culled: number
}

/**
 * Lays a projected subtree out as nested squarified rectangles.
 *
 * Children are scaled against the sum of their own byte counts rather than against the
 * parent's recorded size. The backend guarantees those are equal, but if that ever stopped
 * being true the honest failure is a slightly wrong parent, not children that overflow
 * their parent's box and are drawn on top of a sibling.
 */
export function layoutTreemap(
  nodes: readonly TreemapNodeDto[],
  width: number,
  height: number,
): Layout {
  const rects: TreemapRect[] = []
  let culled = 0

  if (nodes.length === 0 || width <= 0 || height <= 0) {
    return { rects, index: new SpatialIndex(width, height, rects), culled }
  }

  const children: number[][] = nodes.map(() => [])
  for (let i = 1; i < nodes.length; i++) {
    const parent = nodes[i].p
    // A malformed parent link would otherwise throw deep inside the recursion, where the
    // stack says nothing about which node was bad.
    if (parent >= 0 && parent < i) children[parent].push(i)
  }

  const visit = (
    node: number,
    x: number,
    y: number,
    w: number,
    h: number,
    depth: number,
    group: number,
  ) => {
    rects.push({ index: node, x, y, w, h, depth, group })

    const kids = children[node]
    if (kids.length === 0) return

    // Reserve the label band only when there is room for it; otherwise the children lose
    // 15 px of a 20 px box to a header nobody can read.
    const banded = w >= HEADER_MIN_W && h >= HEADER_MIN_H
    const ix = x + PADDING
    const iy = y + (banded ? HEADER : PADDING)
    const iw = w - PADDING * 2
    const ih = h - (banded ? HEADER : PADDING) - PADDING

    if (iw <= 0 || ih <= 0 || iw * ih < MIN_AREA) {
      culled += kids.length
      return
    }

    // Descending, because squarify's aspect-ratio heuristic assumes it and because the
    // largest thing landing top-left is what makes a treemap scannable.
    const ordered = [...kids].sort((a, b) => nodes[b].b - nodes[a].b)

    let total = 0
    for (const k of ordered) total += nodes[k].b
    if (total <= 0) return

    squarify(ordered, nodes, total, ix, iy, iw, ih, (child, cx, cy, cw, ch) => {
      if (cw * ch < MIN_AREA) {
        culled++
        return
      }
      visit(child, cx, cy, cw, ch, depth + 1, depth === 0 ? child : group)
    })
  }

  visit(0, 0, 0, width, height, 0, -1)
  return { rects, index: new SpatialIndex(width, height, rects), culled }
}

/**
 * Bruls–Huizing–van Wijk squarification.
 *
 * Rows are accumulated greedily against the shorter side of the remaining space and closed
 * as soon as adding the next item would make the row's worst aspect ratio worse. Laying
 * along the shorter side is the whole trick: it is what keeps rectangles near-square
 * instead of producing the slivers a naive slice-and-dice gives.
 */
function squarify(
  order: readonly number[],
  nodes: readonly TreemapNodeDto[],
  total: number,
  x0: number,
  y0: number,
  w0: number,
  h0: number,
  emit: (index: number, x: number, y: number, w: number, h: number) => void,
): void {
  let x = x0
  let y = y0
  let w = w0
  let h = h0
  let remaining = total
  let start = 0

  while (start < order.length && w > 0 && h > 0 && remaining > 0) {
    const side = Math.min(w, h)
    const scale = (w * h) / remaining

    // Grow the row while the worst aspect ratio in it keeps improving.
    let end = start
    let rowSum = 0
    let best = Number.POSITIVE_INFINITY

    while (end < order.length) {
      const next = rowSum + nodes[order[end]].b
      const ratio = worstRatio(
        nodes[order[start]].b,
        nodes[order[end]].b,
        next,
        side,
        scale,
      )
      if (end > start && ratio > best) break
      best = ratio
      rowSum = next
      end++
    }

    const thickness = side > 0 ? (rowSum * scale) / side : 0

    // Positions accumulate along the row rather than being computed per item, so rounding
    // error cannot open a seam between two neighbours.
    let along = 0
    const horizontal = w >= h

    for (let i = start; i < end; i++) {
      const value = nodes[order[i]].b
      const length = rowSum > 0 ? (value / rowSum) * side : 0
      if (horizontal) {
        emit(order[i], x, y + along, thickness, length)
      } else {
        emit(order[i], x + along, y, length, thickness)
      }
      along += length
    }

    if (horizontal) {
      x += thickness
      w -= thickness
    } else {
      y += thickness
      h -= thickness
    }

    remaining -= rowSum
    start = end

    // rowSum can be 0 when a whole run of nodes has zero bytes; without this the outer
    // loop would spin forever laying out nothing.
    if (rowSum <= 0) break
  }
}

function worstRatio(
  maxValue: number,
  minValue: number,
  sum: number,
  side: number,
  scale: number,
): number {
  const area = sum * scale
  if (area <= 0 || side <= 0) return Number.POSITIVE_INFINITY

  const thickness = area / side
  if (thickness <= 0) return Number.POSITIVE_INFINITY

  const maxLength = (maxValue * scale) / thickness
  const minLength = (minValue * scale) / thickness
  if (minLength <= 0) return Number.POSITIVE_INFINITY

  return Math.max(maxLength / thickness, thickness / minLength)
}

/** Edge length of one bucket, in CSS pixels. */
const CELL = 48

/**
 * Uniform grid over the laid-out rectangles.
 *
 * A grid rather than an interval or R-tree because the input is already bounded to a few
 * thousand rectangles spread over a known, small, fixed area — the regime where the
 * constant factor decides and a grid's is about as low as it goes. Build is a single pass;
 * a hit test touches one bucket.
 */
export class SpatialIndex {
  private readonly cols: number
  private readonly rows: number
  private readonly buckets: TreemapRect[][]

  constructor(width: number, height: number, rects: readonly TreemapRect[]) {
    this.cols = Math.max(1, Math.ceil(width / CELL))
    this.rows = Math.max(1, Math.ceil(height / CELL))
    this.buckets = Array.from({ length: this.cols * this.rows }, () => [])

    for (const rect of rects) {
      const c0 = this.clampCol(Math.floor(rect.x / CELL))
      const c1 = this.clampCol(Math.floor((rect.x + rect.w) / CELL))
      const r0 = this.clampRow(Math.floor(rect.y / CELL))
      const r1 = this.clampRow(Math.floor((rect.y + rect.h) / CELL))

      for (let r = r0; r <= r1; r++) {
        for (let c = c0; c <= c1; c++) {
          this.buckets[r * this.cols + c].push(rect)
        }
      }
    }
  }

  /**
   * The deepest rectangle containing the point.
   *
   * Deepest, not first: rectangles nest, so a point inside a leaf is also inside every one
   * of its ancestors. Depth is the tie-break that makes the answer the thing under the
   * cursor rather than the thing that happens to enclose it.
   */
  pick(x: number, y: number): TreemapRect | null {
    const col = this.clampCol(Math.floor(x / CELL))
    const row = this.clampRow(Math.floor(y / CELL))

    let found: TreemapRect | null = null
    for (const rect of this.buckets[row * this.cols + col]) {
      if (
        x >= rect.x &&
        x < rect.x + rect.w &&
        y >= rect.y &&
        y < rect.y + rect.h &&
        (found === null || rect.depth > found.depth)
      ) {
        found = rect
      }
    }
    return found
  }

  private clampCol(c: number): number {
    return c < 0 ? 0 : c >= this.cols ? this.cols - 1 : c
  }

  private clampRow(r: number): number {
    return r < 0 ? 0 : r >= this.rows ? this.rows - 1 : r
  }
}

/**
 * The set of nodes that have at least one child in this projection.
 *
 * Separate from `resolveOpenTarget` so the component can memoize it against the response
 * rather than rebuilding it on every pointer move.
 */
export function childBearingNodes(nodes: readonly TreemapNodeDto[]): Set<number> {
  const set = new Set<number>()
  for (let i = 1; i < nodes.length; i++) set.add(nodes[i].p)
  return set
}

/**
 * Resolves a pointed-at node to the folder a click should open, or null for nowhere.
 *
 * Not simply "the node under the cursor", and the difference is the whole feature. A
 * subdivided folder is almost entirely covered by its own children, so the only part of it a
 * pointer can ever reach is the thin label band at its top. Measured on the dev fixture,
 * targeting the node itself left **0 %** of sampled points clickable — a map that reads as
 * broken rather than as deliberately restricted. Resolving upward to the nearest folder that
 * has something to show took that to 83 %, the remainder being the root's own `(files here)`
 * box, which correctly has nowhere to go.
 *
 * Lives here, in the pure module, rather than in the component: it is the piece of click
 * behaviour with actual logic in it, and only out here can it be regression-tested.
 */
export function resolveOpenTarget(
  nodes: readonly TreemapNodeDto[],
  hasChildren: ReadonlySet<number>,
  index: number,
): number | null {
  let i = index
  if (i < 0 || i >= nodes.length) return null

  // Files and Other stand for a set of things rather than one path, so they can never be a
  // destination; the folder they belong to is what the user actually pointed at.
  while (i > 0 && nodes[i].k !== 'Directory') i = nodes[i].p

  // A folder with nothing under it in this view would open onto an empty map. `x` marks a
  // node the backend says is expandable, i.e. it has content that this projection did not
  // send — opening that is exactly the point.
  if (i > 0 && !hasChildren.has(i) && !nodes[i].x) i = nodes[i].p

  return i > 0 ? i : null
}

/**
 * Rebuilds a full filesystem path for a node by walking parent links.
 *
 * Only the view root carries a full path on the wire; every descendant carries a bare
 * segment. That is the whole reason a 20,000-node response is not mostly repeated prefixes,
 * so reconstruction lives here rather than being sent.
 *
 * Returns null for the synthetic `Files`/`Other` nodes, which stand for a set of things
 * rather than one path and therefore cannot be navigated to.
 */
export function pathOf(
  nodes: readonly TreemapNodeDto[],
  rootPath: string,
  index: number,
): string | null {
  if (index < 0 || index >= nodes.length) return null
  if (nodes[index].k !== 'Directory') return null

  const segments: string[] = []
  let current = index
  while (current > 0) {
    segments.push(nodes[current].n)
    current = nodes[current].p
  }

  if (segments.length === 0) return rootPath

  const base = rootPath.replace(/[\\/]+$/, '')
  return `${base}\\${segments.reverse().join('\\')}`
}
