/**
 * Shared treemap test fixture.
 *
 * Lives beside the modules it exercises rather than in a test file so that the layout tests
 * and the component test drive the *same* tree. Two suites asserting different invariants
 * against two different fixtures agree with each other by luck; against one fixture the
 * numbers in docs/PLAN.md §5e stay comparable across both.
 *
 * Nothing in `main.tsx` reaches this module, so it is not in the shipped bundle.
 */

import type { TreemapNodeDto } from './layout'

/**
 * Deterministic PRNG. `Math.random` would make a failure unreproducible, which for a geometry
 * property test is the difference between a bug report and a shrug.
 */
export function rng(seed: number): () => number {
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
 * `(files here)` and `(smaller items)` siblings; the fixture models the same shape, because a
 * layout tested against trees that do not conserve area would not be testing the code that
 * ships.
 */
export function makeTree(
  seed: number,
  maxDepth: number,
  rootBytes = 160 * 1024 ** 3,
): TreemapNodeDto[] {
  const random = rng(seed)
  const nodes: TreemapNodeDto[] = [{ p: -1, n: 'C:\\', b: rootBytes, k: 'Directory', x: false }]

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
      const bytes = i === count - 1 ? budget - handed : Math.floor((weights[i] / total) * budget)
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

/** Wraps a node list as the response shape the component takes. */
export function asResponse(nodes: TreemapNodeDto[], path = 'C:\\') {
  return {
    path,
    totalAllocatedBytes: nodes[0].b,
    minimumBytes: 0,
    aggregatedNodeCount: nodes.length,
    truncated: false,
    nodes,
  }
}
