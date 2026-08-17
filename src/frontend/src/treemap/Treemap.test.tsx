// @vitest-environment jsdom

import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { canvasGeometry } from './canvas'
import { asResponse, makeTree } from './fixture'
import { layoutTreemap, MIN_AREA } from './layout'
import { installCanvasHarness, type CanvasHarness } from './recordingCanvas'
import { HEIGHT, LABEL_MIN_H, LABEL_MIN_W, Treemap } from './Treemap'

/**
 * The draw loop, as a gate.
 *
 * docs/PLAN.md §5e recorded "`Treemap.tsx` itself has no test" under *left unverified*, with
 * the reasoning that a stubbed canvas proves nothing about the draw loop. That reasoning holds
 * for **cost** — no stub measures a frame, and the browser sessions in §5c/§5e/§5f remain the
 * evidence for painting and for performance. It does not hold for the loop's **decisions**,
 * which are the plan's actual requirements and which shipped with nothing enforcing them:
 * one canvas, nothing sub-`MIN_AREA` painted, text only where it can be read, no backing-store
 * reallocation on hover, and no stale hover index across a zoom.
 *
 * Each of those failed loudly against deliberately broken code before being accepted here —
 * see §5g of the plan for the mutation table, per §9's requirement that a geometry invariant
 * be confirmed red rather than observed green.
 */

const WIDTH = 810

let harness: CanvasHarness
let container: HTMLDivElement
let root: Root

beforeEach(() => {
  ;(globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true
  harness = installCanvasHarness()
  container = document.createElement('div')
  document.body.appendChild(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
  harness.restore()
})

/** Mounts the treemap and lets the observed width arrive the way a real resize would. */
function mount(nodes = makeTree(42, 5), width = WIDTH) {
  const navigated: (string | undefined)[] = []
  act(() => {
    root.render(<Treemap data={asResponse(nodes)} onNavigate={(p) => navigated.push(p)} />)
  })
  act(() => harness.resize(width))
  return { navigated }
}

function canvas(): HTMLCanvasElement {
  const element = container.querySelector('canvas')
  if (!element) throw new Error('no canvas rendered')
  return element
}

/** Fires a pointer move at CSS coordinates, which jsdom cannot derive from a layout. */
function move(x: number, y: number) {
  const element = canvas()
  element.getBoundingClientRect = () => ({ left: 0, top: 0 }) as DOMRect
  act(() => {
    element.dispatchEvent(
      new MouseEvent('mousemove', { bubbles: true, clientX: x, clientY: y }),
    )
  })
}

describe('Treemap draw loop', () => {
  it('draws on exactly one canvas', () => {
    // §2.4's single-canvas rule, and the only assertion that enforces it anywhere. A second
    // full-viewport backing store is tens of megabytes at DPR 2 in an app already measured
    // over its 400 MB budget, and adding one passes build, lint and typecheck in silence.
    mount()
    move(300, 200)
    move(120, 90)

    expect(container.querySelectorAll('canvas')).toHaveLength(1)
    expect(harness.canvases).toHaveLength(1)
  })

  it('paints every rectangle the layout produced, and nothing else', () => {
    const nodes = makeTree(42, 5)
    mount(nodes)
    const expected = layoutTreemap(nodes, WIDTH, HEIGHT)

    expect(harness.context.fillRects).toHaveLength(expected.rects.length)
    expect(expected.culled).toBeGreaterThan(0)
  })

  it('never paints a rectangle below the cull floor', () => {
    // The pixel-accurate half of §2.4's "cull before upload". Asserted at the canvas rather
    // than at the layout, because this is the boundary the budget is actually about: a
    // regression that culled correctly and painted anyway would pass layout.test.ts.
    mount()
    for (const rect of harness.context.fillRects) {
      // The first fill is the view root, which is the viewport itself.
      if (rect.x === 0 && rect.y === 0 && rect.w === WIDTH && rect.h === HEIGHT) continue
      expect(rect.w * rect.h).toBeGreaterThanOrEqual(MIN_AREA)
    }
  })

  it('clears the whole viewport before drawing', () => {
    // A clear that is short by any amount leaves the previous frame showing through in the
    // uncleared strip, and only on the frames that follow a smaller layout.
    mount()
    expect(harness.context.clearRects).toContainEqual({ x: 0, y: 0, w: WIDTH, h: HEIGHT })
  })
})

describe('Treemap labelling', () => {
  it('labels only rectangles large enough to read', () => {
    // §5c: fillText costs roughly an order of magnitude more than fillRect, so the threshold
    // is what keeps text from becoming the cost of a frame rather than geometry.
    mount()
    const labels = harness.context.fillTexts
    expect(labels.length).toBeGreaterThan(0)

    for (const label of labels) {
      expect(label.clip, `"${label.text}" drawn without a clip rect`).not.toBeNull()
      if (!label.clip) continue
      expect(label.clip.w).toBeGreaterThanOrEqual(LABEL_MIN_W)
      expect(label.clip.h).toBeGreaterThanOrEqual(LABEL_MIN_H)
    }
  })

  it('labels a small minority of what it fills', () => {
    // The ratio is the actual defence. A threshold that admitted most rectangles would still
    // satisfy the assertion above while restoring the cost it exists to avoid.
    mount()
    const filled = harness.context.fillRects.length
    const labelled = new Set(harness.context.fillTexts.map((t) => `${t.clip?.x},${t.clip?.y}`)).size
    expect(labelled / filled).toBeLessThan(0.25)
  })

  it('truncates a name that does not fit rather than overflowing its box', () => {
    mount()
    let truncated = 0
    for (const label of harness.context.fillTexts) {
      // Only the name is measured and shortened. The byte-size second line, drawn at
      // rect.y + 16, is not: it is at most a few characters and the clip is its backstop.
      // That asymmetry is deliberate — measureText is not free and runs per labelled rect —
      // which is why every fillText is required to be inside a clip above.
      if (!label.clip || label.y !== label.clip.y + 2) continue
      // 6 px per character is the harness metric; the component budgets rect.w - 8.
      expect(label.text.length * 6).toBeLessThanOrEqual(label.clip.w - 8)
      if (label.text.endsWith('…')) truncated++
    }
    // The fixture has names long enough to force the binary search; without this the
    // assertion above would be satisfied by a run in which nothing was ever shortened.
    expect(truncated).toBeGreaterThan(0)
  })
})

describe('Treemap backing store', () => {
  it('sizes the backing store from the pixel ratio', () => {
    harness.setDpr(2)
    mount()

    const expected = canvasGeometry(WIDTH, HEIGHT, 2)
    expect(canvas().width).toBe(expected.backingWidth)
    expect(canvas().height).toBe(expected.backingHeight)
    expect(canvas().style.width).toBe(`${WIDTH}px`)

    // The transform must agree with the store exactly; §5f explains that a scale off by any
    // amount leaves precisely the last device column unpainted and nothing else looks wrong.
    const last = harness.context.transforms.at(-1)
    expect(last?.[0]).toBe(expected.scale)
    expect(last?.[3]).toBe(expected.scale)
  })

  it('does not reallocate the backing store on pointer moves', () => {
    // §5f measured 1,137 MiB of allocation churn over 200 pointer moves at DPR 2, caused by
    // the draw effect depending on `hover` and assigning canvas.width unconditionally.
    // canvas.ts tests the guard; this tests that the component still goes through it.
    harness.setDpr(2)
    mount()
    const afterMount = harness.backingStoreWrites

    for (let i = 0; i < 50; i++) move(40 + i * 8, 60 + (i % 30) * 4)

    expect(harness.backingStoreWrites).toBe(afterMount)
    expect(harness.context.fillRects.length).toBeGreaterThan(0)
  })

  it('resizes the backing store when the pixel ratio changes', () => {
    // There is no devicePixelRatio change event; the component subscribes to a one-shot media
    // query. Dragging to a differently-scaled monitor changes the ratio and nothing else, so
    // without the subscription the map stays at the old store until an unrelated re-render.
    harness.setDpr(2)
    mount()
    const before = canvas().width
    expect(before).toBe(canvasGeometry(WIDTH, HEIGHT, 2).backingWidth)

    act(() => harness.setDpr(1.5))
    expect(canvas().width).toBe(canvasGeometry(WIDTH, HEIGHT, 1.5).backingWidth)
    expect(canvas().style.width).toBe(`${WIDTH}px`)
  })
})

describe('Treemap interaction', () => {
  it('never paints a hover captured against the previous view', () => {
    // §5c defect 2, as a gate — and gated on the right thing.
    //
    // Hover holds an index into data.nodes. Clearing it in an effect leaves exactly one
    // render in which an index captured against the old view is read against the new one,
    // and a zoom returns a far smaller projection, so that index is routinely past the end.
    //
    // Asserting "the re-render does not throw" does NOT catch this any more: resolveOpenTarget
    // range-checks its index, so the original TypeError is now defended twice over. Verified
    // by mutation — reverting to the effect-based clear kept a not-throw assertion green.
    // What still differs is what gets painted: the stale frame strokes the old hover rect,
    // at coordinates from a layout that no longer exists. That is observable, so that is what
    // this asserts.
    const big = makeTree(42, 6)
    mount(big)
    move(400, 230)
    expect(harness.context.strokeRects.some((r) => r.lineWidth === 2)).toBe(true)

    const small = makeTree(3, 1)
    expect(small.length).toBeLessThan(big.length / 10)

    harness.context.reset()
    act(() => {
      root.render(<Treemap data={asResponse(small)} onNavigate={() => {}} />)
    })

    // Every stroke in every frame drawn since the data changed must be a 1 px rectangle
    // border. A 2 px stroke here is a highlight for a rectangle the user is not pointing at.
    expect(harness.context.strokeRects.filter((r) => r.lineWidth === 2)).toEqual([])
    expect(harness.context.fillRects.length).toBeGreaterThan(0)
    expect(container.querySelector('.treemap-tip')).toBeNull()
  })

  it('reports what it drew and what it culled', () => {
    const nodes = makeTree(42, 5)
    const stats: { rects: number; culled: number }[] = []
    act(() => {
      root.render(
        <Treemap data={asResponse(nodes)} onNavigate={() => {}} onStats={(s) => stats.push(s)} />,
      )
    })
    act(() => harness.resize(WIDTH))

    const expected = layoutTreemap(nodes, WIDTH, HEIGHT)
    expect(stats.at(-1)).toEqual({ rects: expected.rects.length, culled: expected.culled })
  })

  it('navigates to the folder a click resolves to', () => {
    const { navigated } = mount()
    move(400, 230)
    act(() => {
      canvas().dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })

    expect(navigated).toHaveLength(1)
    expect(navigated[0]).toMatch(/^C:\\/)
  })

  it('draws nothing at all before a width has been observed', () => {
    // The ResizeObserver has not fired yet, so there is no layout. Drawing against a width of
    // 0 would divide the viewport by zero and emit degenerate rectangles.
    act(() => {
      root.render(<Treemap data={asResponse(makeTree(42, 4))} onNavigate={() => {}} />)
    })
    expect(harness.context.fillRects).toHaveLength(0)
  })
})
