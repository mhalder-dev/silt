import { describe, expect, it } from 'vitest'
import {
  applyCanvasGeometry,
  canvasGeometry,
  dprQuery,
  MAX_BACKING_PIXELS,
  normalizeDpr,
} from './canvas'

/**
 * The gate for the pixel-ratio path.
 *
 * docs/PLAN.md carried "`devicePixelRatio` remains exercised only at DPR 1" as an unverified
 * item three times, through §5c, §5d and §5e, because the review browser kept reporting 1 and
 * the arithmetic was buried in a `useEffect`. Waiting for a 150 % display to appear was never
 * going to close it. The ratio is an input, so it is supplied as one here and the 1.25, 1.5,
 * 2 and 3 paths are asserted at the values real displays report.
 *
 * No jsdom and no canvas stub: `applyCanvasGeometry` touches four properties, so the double
 * below is the whole of the surface it uses. A stubbed 2D context would prove nothing about
 * the draw loop, which is why the draw loop is deliberately not tested here — see §5e.
 */

/** The four properties applyCanvasGeometry touches, and a count of real reallocations. */
function fakeCanvas() {
  const state = { width: 300, height: 150, allocations: 0, style: { width: '', height: '' } }
  return {
    canvas: {
      get width() {
        return state.width
      },
      set width(value: number) {
        state.width = value
        state.allocations++
      },
      get height() {
        return state.height
      },
      set height(value: number) {
        state.height = value
      },
      style: state.style,
    } as unknown as HTMLCanvasElement,
    state,
  }
}

describe('normalizeDpr', () => {
  it('passes real pixel ratios through untouched', () => {
    for (const dpr of [1, 1.25, 1.5, 2, 2.5, 3]) {
      expect(normalizeDpr(dpr)).toBe(dpr)
    }
  })

  it('falls back to 1 for every value that cannot be multiplied by', () => {
    // Infinity and -1 are the two the `|| 1` idiom lets through, and both produce a
    // canvas.width that throws IndexSizeError rather than merely drawing wrong.
    for (const bad of [0, Number.NaN, Number.POSITIVE_INFINITY, -1, undefined]) {
      expect(normalizeDpr(bad as number | undefined)).toBe(1)
    }
  })
})

describe('dprQuery', () => {
  it('asks for the exact resolution currently in use', () => {
    // `dppx`, not `dpi` or `dpcm`: only dppx is 1:1 with devicePixelRatio, and the others
    // would silently never match, leaving the listener permanently deaf.
    expect(dprQuery(1)).toBe('(resolution: 1dppx)')
    expect(dprQuery(1.5)).toBe('(resolution: 1.5dppx)')
    expect(dprQuery(2)).toBe('(resolution: 2dppx)')
  })

  it('never emits a query that cannot parse', () => {
    // matchMedia does not throw on a malformed query, it returns something that never
    // matches — so an unguarded NaN here would disable the subscription without a trace.
    expect(dprQuery(Number.NaN)).toBe('(resolution: 1dppx)')
    expect(dprQuery(Number.POSITIVE_INFINITY)).toBe('(resolution: 1dppx)')
  })
})

describe('canvasGeometry', () => {
  it('sizes the backing store in device pixels and the element in CSS pixels', () => {
    // 810 x 460 is the fixture §5c and §5e both measured on.
    const cases: [number, number, number][] = [
      [1, 810, 460],
      [1.25, 1013, 575],
      [1.5, 1215, 690],
      [2, 1620, 920],
      [3, 2430, 1380],
    ]

    for (const [dpr, backingWidth, backingHeight] of cases) {
      const geometry = canvasGeometry(810, 460, dpr)
      expect(geometry.backingWidth).toBe(backingWidth)
      expect(geometry.backingHeight).toBe(backingHeight)
      // The element must stay the same size on screen at every ratio. If this ever tracked
      // the backing store the map would grow off the page on a HiDPI display.
      expect(geometry.cssWidth).toBe(810)
      expect(geometry.cssHeight).toBe(460)
      expect(geometry.scale).toBe(dpr)
    }
  })

  it('rounds rather than truncating a half device pixel', () => {
    // 811 * 1.5 = 1216.5. Flooring loses the right-hand half pixel on every odd width, and
    // only on 150 % displays.
    expect(canvasGeometry(811, 460, 1.5).backingWidth).toBe(1217)
  })

  it('keeps the drawing scale consistent with the backing store', () => {
    for (const dpr of [1, 1.25, 1.5, 2, 3]) {
      const g = canvasGeometry(810, 460, dpr)
      // The draw loop works in CSS pixels and relies on scale * cssWidth landing on the
      // backing width. Anything else leaves an unpainted strip at the right edge.
      expect(Math.round(g.cssWidth * g.scale)).toBe(g.backingWidth)
      expect(Math.round(g.cssHeight * g.scale)).toBe(g.backingHeight)
    }
  })

  it('caps the backing store by area, so a huge ratio cannot allocate without bound', () => {
    const g = canvasGeometry(3840, 2160, 4)
    expect(g.backingWidth * g.backingHeight).toBeLessThanOrEqual(MAX_BACKING_PIXELS)
    // Degraded, not distorted: the aspect ratio survives the clamp.
    expect(g.backingWidth / g.backingHeight).toBeCloseTo(3840 / 2160, 2)
    expect(g.scale).toBeLessThan(4)
  })

  it('does not clamp a full-width 4K window at DPR 2', () => {
    // The cap must be a backstop against absurdity, not something a real display hits.
    const g = canvasGeometry(1920, 800, 2)
    expect(g.scale).toBe(2)
    expect(g.backingWidth).toBe(3840)
  })

  it('never returns a negative or fractional backing store', () => {
    for (const [w, h, dpr] of [
      [-10, 460, 2],
      [0, 0, 2],
      [810, 460, Number.NaN],
      [810.7, 460.2, 1],
    ] as const) {
      const g = canvasGeometry(w, h, dpr)
      expect(g.backingWidth).toBeGreaterThanOrEqual(0)
      expect(g.backingHeight).toBeGreaterThanOrEqual(0)
      expect(Number.isInteger(g.backingWidth)).toBe(true)
      expect(Number.isInteger(g.backingHeight)).toBe(true)
    }
  })
})

describe('applyCanvasGeometry', () => {
  it('writes both the backing store and the CSS size on the first frame', () => {
    const { canvas, state } = fakeCanvas()
    expect(applyCanvasGeometry(canvas, canvasGeometry(810, 460, 2))).toBe(true)

    expect(state.width).toBe(1620)
    expect(state.height).toBe(920)
    expect(state.style.width).toBe('810px')
    expect(state.style.height).toBe('460px')
  })

  it('does not reallocate the backing store when nothing changed', () => {
    // The regression this exists for: the draw effect depends on `hover`, so it runs on every
    // mousemove. Assigning canvas.width discards and reallocates the backing store even when
    // the value is identical — 24 MB per pointer event at DPR 2 on a 4K window.
    const { canvas, state } = fakeCanvas()
    const geometry = canvasGeometry(810, 460, 2)

    applyCanvasGeometry(canvas, geometry)
    const afterFirst = state.allocations

    for (let i = 0; i < 100; i++) {
      expect(applyCanvasGeometry(canvas, geometry)).toBe(false)
    }

    expect(state.allocations).toBe(afterFirst)
  })

  it('reallocates when the pixel ratio changes, as it does on a monitor move', () => {
    const { canvas, state } = fakeCanvas()
    applyCanvasGeometry(canvas, canvasGeometry(810, 460, 1))
    const afterFirst = state.allocations

    expect(applyCanvasGeometry(canvas, canvasGeometry(810, 460, 2))).toBe(true)
    expect(state.allocations).toBe(afterFirst + 1)
    expect(state.width).toBe(1620)
    // Same element size on screen; only the backing store got denser.
    expect(state.style.width).toBe('810px')
  })

  it('reallocates when the container is resized', () => {
    const { canvas } = fakeCanvas()
    applyCanvasGeometry(canvas, canvasGeometry(810, 460, 1))
    expect(applyCanvasGeometry(canvas, canvasGeometry(1024, 460, 1))).toBe(true)
  })
})
