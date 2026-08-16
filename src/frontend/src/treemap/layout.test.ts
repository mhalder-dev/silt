import { describe, expect, it } from 'vitest'
import {
  childBearingNodes,
  layoutTreemap,
  MIN_AREA,
  pathOf,
  resolveOpenTarget,
  type TreemapNodeDto,
  type TreemapRect,
} from './layout'

/**
 * Regression gate for the treemap's geometry.
 *
 * §5c of docs/PLAN.md records these invariants as *measured* — child rectangles escaping
 * their parent: 0, overlapping siblings: 0, hit tests disagreeing with brute force over
 * 4,000 random points: 0. That measurement was taken by driving the module from a browser
 * console once. It was evidence, and it was not a gate: nothing would have caught a later
 * edit that reintroduced any of it, because build, typecheck and lint all pass on a treemap
 * whose rectangles overlap. These tests turn each measured number into an assertion.
 *
 * The layout is deliberately DOM-free precisely so this file can exist, so there is no
 * canvas, no jsdom and no renderer here.
 */

/** Deterministic PRNG. Math.random would make a failure unreproducible, which for a
 *  geometry property test is the difference between a bug report and a shrug. */
function rng(seed: number): () => number {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

/**
 * Builds a projection with the property the real backend guarantees: a directory's children
 * sum to *exactly* its own byte count. `TreemapProjector` achieves that with synthetic
 * `(files here)` and `(smaller items)` siblings; the fixture models the same shape, because
 * a layout tested against trees that do not conserve area would not be testing the code that
 * ships.
 */
function makeTree(seed: number, maxDepth: number, rootBytes = 160 * 1024 ** 3): TreemapNodeDto[] {
  const random = rng(seed)
  const nodes: TreemapNodeDto[] = [
    { p: -1, n: 'C:\\', b: rootBytes, k: 'Directory', x: false },
  ]

  const expand = (parent: number, depth: number) => {
    const budget = nodes[parent].b
    if (depth >= maxDepth || budget < 1024) return

    const count = 2 + Math.floor(random() * 6)
    const weights = Array.from({ length: count }, () => random() ** 2 + 0.01)
    const total = weights.reduce((a, b) => a + b, 0)

    // Hand out integer bytes and give the remainder to the last child, so the children sum
    // to the parent exactly rather than to within a rounding error.
    let handed = 0
    const first = nodes.length
    for (let i = 0; i < count; i++) {
      const bytes =
        i === count - 1 ? budget - handed : Math.floor((weights[i] / total) * budget)
      handed += bytes
      const leaf = i === count - 1
      nodes.push({
        p: parent,
        n: leaf ? '(files here)' : `dir-${depth}-${i}`,
        b: bytes,
        k: leaf ? 'Files' : 'Directory',
        x: false,
      })
    }

    // Depth-first after the whole level is pushed, so every parent index is strictly less
    // than its children's - the ordering the wire format promises and the layout relies on.
    for (let i = 0; i < count - 1; i++) expand(first + i, depth + 1)
  }

  expand(0, 0)
  return nodes
}

function rectByNode(rects: readonly TreemapRect[]): Map<number, TreemapRect> {
  const map = new Map<number, TreemapRect>()
  for (const r of rects) map.set(r.index, r)
  return map
}

const WIDTH = 810
const HEIGHT = 460

describe('layoutTreemap geometry', () => {
  // Several seeds rather than one: a single tree can satisfy every invariant by luck of its
  // proportions, and squarify's row-closing branch is only exercised by some shapes.
  const seeds = [1, 7, 42, 1337, 90210]

  it('never emits a rectangle below the minimum drawable area', () => {
    for (const seed of seeds) {
      const { rects } = layoutTreemap(makeTree(seed, 5), WIDTH, HEIGHT)
      for (const r of rects) {
        // The root is the viewport itself and is not subject to the floor.
        if (r.index === 0) continue
        expect(r.w * r.h, `seed ${seed}, node ${r.index}`).toBeGreaterThanOrEqual(MIN_AREA)
      }
    }
  })

  it('keeps every child rectangle inside its parent', () => {
    for (const seed of seeds) {
      const nodes = makeTree(seed, 5)
      const { rects } = layoutTreemap(nodes, WIDTH, HEIGHT)
      const byNode = rectByNode(rects)

      for (const r of rects) {
        if (r.index === 0) continue
        const parent = byNode.get(nodes[r.index].p)
        expect(parent, `seed ${seed}: child ${r.index} drawn without its parent`).toBeDefined()
        if (!parent) continue

        // 1e-9, not 0: the coordinates are accumulated in floating point on purpose (see the
        // comment in squarify about seams), so exact comparison would fail on ULP dust.
        expect(r.x).toBeGreaterThanOrEqual(parent.x - 1e-9)
        expect(r.y).toBeGreaterThanOrEqual(parent.y - 1e-9)
        expect(r.x + r.w).toBeLessThanOrEqual(parent.x + parent.w + 1e-9)
        expect(r.y + r.h).toBeLessThanOrEqual(parent.y + parent.h + 1e-9)
      }
    }
  })

  it('never overlaps two siblings', () => {
    for (const seed of seeds) {
      const nodes = makeTree(seed, 5)
      const { rects } = layoutTreemap(nodes, WIDTH, HEIGHT)

      const byParent = new Map<number, typeof rects>()
      for (const r of rects) {
        if (r.index === 0) continue
        const p = nodes[r.index].p
        const list = byParent.get(p)
        if (list) list.push(r)
        else byParent.set(p, [r])
      }

      for (const [parent, group] of byParent) {
        for (let i = 0; i < group.length; i++) {
          for (let j = i + 1; j < group.length; j++) {
            const a = group[i]
            const b = group[j]
            const overlapW = Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)
            const overlapH = Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y)
            const area = Math.max(0, overlapW) * Math.max(0, overlapH)
            expect(area, `seed ${seed}: siblings of ${parent} overlap`).toBeLessThan(1e-6)
          }
        }
      }
    }
  })

  it('gives each rectangle an area proportional to its byte count', () => {
    // The point of a treemap. Checked between siblings, since that is where the reader
    // actually compares two boxes, and only for rectangles comfortably above the cull floor
    // where the 9 px² quantum is not itself the dominant error.
    const nodes = makeTree(42, 4)
    const { rects } = layoutTreemap(nodes, WIDTH, HEIGHT)
    const byNode = rectByNode(rects)

    for (const r of rects) {
      if (r.index === 0) continue
      const parentIndex = nodes[r.index].p
      const parent = byNode.get(parentIndex)
      if (!parent || r.w * r.h < 400) continue

      const siblings = rects.filter((s) => s.index !== 0 && nodes[s.index].p === parentIndex)
      // Only meaningful where nothing in the group was culled away.
      const drawnBytes = siblings.reduce((sum, s) => sum + nodes[s.index].b, 0)
      if (drawnBytes !== nodes[parentIndex].b) continue

      const drawnArea = siblings.reduce((sum, s) => sum + s.w * s.h, 0)
      const expected = (nodes[r.index].b / drawnBytes) * drawnArea
      expect(Math.abs(r.w * r.h - expected) / expected).toBeLessThan(0.02)
    }
  })

  it('produces squarish rectangles rather than slivers', () => {
    // This is the assertion that distinguishes a real squarified layout from slice-and-dice,
    // which passes every containment and overlap check above while producing slivers in the
    // hundreds. §5c measured median 1.45 / p95 2.60 on the dev fixture; the bounds here are
    // loose enough to survive a different fixture and tight enough to fail slice-and-dice.
    const { rects } = layoutTreemap(makeTree(42, 5), WIDTH, HEIGHT)
    const ratios = rects
      .filter((r) => r.index !== 0 && r.w > 0 && r.h > 0)
      .map((r) => Math.max(r.w / r.h, r.h / r.w))
      .sort((a, b) => a - b)

    expect(ratios.length).toBeGreaterThan(100)
    const median = ratios[Math.floor(ratios.length * 0.5)]
    const p95 = ratios[Math.floor(ratios.length * 0.95)]

    expect(median).toBeLessThan(3)
    expect(p95).toBeLessThan(12)
  })

  it('draws a few thousand rectangles, not a hundred thousand', () => {
    // The plan's budget, stated as a test. A regression that stopped culling would show up
    // here long before it showed up as a dropped frame.
    const nodes = makeTree(42, 7)
    const { rects, culled } = layoutTreemap(nodes, WIDTH, HEIGHT)

    expect(nodes.length).toBeGreaterThan(3000)
    expect(rects.length).toBeLessThan(3000)
    expect(culled).toBeGreaterThan(0)
    expect(rects.length + culled).toBeLessThanOrEqual(nodes.length)
  })

  it('is deterministic for identical input', () => {
    const nodes = makeTree(7, 5)
    const a = layoutTreemap(nodes, WIDTH, HEIGHT).rects
    const b = layoutTreemap(nodes, WIDTH, HEIGHT).rects
    expect(a).toEqual(b)
  })
})

describe('SpatialIndex.pick', () => {
  it('agrees with brute force on every sampled point', () => {
    // The grid is the only reason picking is not O(rects) per pointer move, and a bucketing
    // error is invisible: it returns a plausible neighbour, not an exception. §5c sampled
    // 4,000 points by hand; this samples them on every run.
    const nodes = makeTree(42, 5)
    const { rects, index } = layoutTreemap(nodes, WIDTH, HEIGHT)
    const random = rng(99)

    const brute = (x: number, y: number) => {
      let found: (typeof rects)[number] | null = null
      for (const r of rects) {
        if (
          x >= r.x &&
          x < r.x + r.w &&
          y >= r.y &&
          y < r.y + r.h &&
          (found === null || r.depth > found.depth)
        ) {
          found = r
        }
      }
      return found
    }

    for (let i = 0; i < 4000; i++) {
      const x = random() * WIDTH
      const y = random() * HEIGHT
      expect(index.pick(x, y), `point ${x},${y}`).toBe(brute(x, y))
    }
  })

  it('clamps points outside the viewport instead of throwing', () => {
    const { index } = layoutTreemap(makeTree(1, 3), WIDTH, HEIGHT)
    expect(() => index.pick(-500, -500)).not.toThrow()
    expect(() => index.pick(WIDTH * 3, HEIGHT * 3)).not.toThrow()
    expect(index.pick(-1, -1)).toBeNull()
  })
})

describe('layoutTreemap degenerate input', () => {
  it('returns an empty layout rather than throwing', () => {
    expect(layoutTreemap([], WIDTH, HEIGHT).rects).toEqual([])
    expect(layoutTreemap(makeTree(1, 3), 0, HEIGHT).rects).toEqual([])
    expect(layoutTreemap(makeTree(1, 3), WIDTH, 0).rects).toEqual([])
    expect(layoutTreemap(makeTree(1, 3), -10, -10).rects).toEqual([])
  })

  it('survives a tree whose bytes are all zero', () => {
    const nodes: TreemapNodeDto[] = [
      { p: -1, n: 'C:\\', b: 0, k: 'Directory', x: false },
      { p: 0, n: 'a', b: 0, k: 'Directory', x: false },
      { p: 0, n: 'b', b: 0, k: 'Directory', x: false },
    ]
    // The guard this covers is real: squarify's row loop consumes nothing when a whole run
    // has zero bytes, so without the explicit break it never terminates. A hang is a far
    // worse failure than a wrong rectangle - it takes the UI thread with it.
    const { rects } = layoutTreemap(nodes, WIDTH, HEIGHT)
    expect(rects.length).toBeGreaterThanOrEqual(1)
  })

  it('ignores a forward or self-referential parent link instead of recursing forever', () => {
    const nodes: TreemapNodeDto[] = [
      { p: -1, n: 'C:\\', b: 300, k: 'Directory', x: false },
      { p: 5, n: 'forward', b: 100, k: 'Directory', x: false },
      { p: 2, n: 'self', b: 100, k: 'Directory', x: false },
      { p: 0, n: 'sane', b: 100, k: 'Directory', x: false },
    ]
    const { rects } = layoutTreemap(nodes, WIDTH, HEIGHT)
    expect(rects.some((r) => r.index === 3)).toBe(true)
    expect(rects.some((r) => r.index === 1 || r.index === 2)).toBe(false)
  })
})

describe('resolveOpenTarget', () => {
  const nodes: TreemapNodeDto[] = [
    { p: -1, n: 'C:\\', b: 400, k: 'Directory', x: false },
    { p: 0, n: 'Users', b: 200, k: 'Directory', x: false }, // has a child below
    { p: 1, n: '(files here)', b: 200, k: 'Files', x: false },
    { p: 0, n: 'Windows', b: 100, k: 'Directory', x: true }, // childless but expandable
    { p: 0, n: 'Empty', b: 50, k: 'Directory', x: false }, // childless and not expandable
    { p: 0, n: '(files here)', b: 50, k: 'Files', x: false }, // root's own loose files
  ]
  const has = childBearingNodes(nodes)

  it('resolves a Files box to the folder that owns it', () => {
    expect(resolveOpenTarget(nodes, has, 2)).toBe(1)
  })

  it("returns null for the root's own Files box, which has nowhere to go", () => {
    // This one node is the entire difference between the measured 83 % and 100 %, and it is
    // correct: the root is already the view, so there is nothing above it to open.
    expect(resolveOpenTarget(nodes, has, 5)).toBeNull()
  })

  it('opens a childless folder the backend marked expandable', () => {
    // `x` means the projection withheld this folder's contents, so opening it fetches them.
    // Treating it as a dead end would make exactly the deep folders a user is hunting for
    // the unreachable ones.
    expect(resolveOpenTarget(nodes, has, 3)).toBe(3)
  })

  it('climbs past a folder with genuinely nothing to show', () => {
    expect(resolveOpenTarget(nodes, has, 4)).toBeNull()
  })

  it('returns null for the root and for out-of-range indices', () => {
    expect(resolveOpenTarget(nodes, has, 0)).toBeNull()
    expect(resolveOpenTarget(nodes, has, -1)).toBeNull()
    expect(resolveOpenTarget(nodes, has, 99)).toBeNull()
  })

  it('leaves the great majority of the canvas clickable', () => {
    // The §5c regression, as a gate. Targeting the rectangle under the cursor measured 0 %
    // clickable and passed build, typecheck and lint. Any future change that reintroduces
    // that behaviour fails here instead of shipping an inert map.
    const tree = makeTree(42, 5)
    const { index } = layoutTreemap(tree, WIDTH, HEIGHT)
    const hasKids = childBearingNodes(tree)
    const random = rng(2024)

    let sampled = 0
    let clickable = 0
    for (let i = 0; i < 2000; i++) {
      const rect = index.pick(random() * WIDTH, random() * HEIGHT)
      if (!rect) continue
      sampled++
      if (resolveOpenTarget(tree, hasKids, rect.index) !== null) clickable++
    }

    expect(sampled).toBeGreaterThan(1500)
    expect(clickable / sampled).toBeGreaterThan(0.75)
  })
})

describe('pathOf', () => {
  const nodes: TreemapNodeDto[] = [
    { p: -1, n: 'C:\\Users', b: 300, k: 'Directory', x: false },
    { p: 0, n: 'mhalder', b: 200, k: 'Directory', x: false },
    { p: 1, n: 'AppData', b: 150, k: 'Directory', x: false },
    { p: 2, n: '(files here)', b: 150, k: 'Files', x: false },
  ]

  it('rebuilds a full path from parent links alone', () => {
    expect(pathOf(nodes, 'C:\\Users', 2)).toBe('C:\\Users\\mhalder\\AppData')
  })

  it('returns the root path unchanged for the root node', () => {
    expect(pathOf(nodes, 'C:\\Users', 0)).toBe('C:\\Users')
  })

  it('does not double the separator at a volume root', () => {
    // 'C:\' is the case that bites: the naive join yields 'C:\\Windows', which no API
    // resolves, and the failure surfaces as an empty scan rather than an error.
    expect(pathOf([nodes[0], nodes[1]], 'C:\\', 1)).toBe('C:\\mhalder')
    expect(pathOf([nodes[0], nodes[1]], 'C:/', 1)).toBe('C:\\mhalder')
  })

  it('refuses synthetic nodes, which stand for a set rather than a path', () => {
    expect(pathOf(nodes, 'C:\\Users', 3)).toBeNull()
  })

  it('refuses out-of-range indices', () => {
    expect(pathOf(nodes, 'C:\\Users', -1)).toBeNull()
    expect(pathOf(nodes, 'C:\\Users', 99)).toBeNull()
  })
})
