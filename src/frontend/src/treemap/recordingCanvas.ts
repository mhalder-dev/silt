/**
 * A 2D context that records the decisions the draw loop makes.
 *
 * docs/PLAN.md §5e left `Treemap.tsx` untested and gave the reason: "a stubbed canvas would
 * prove nothing about the draw loop, which is the part with cost in it." That is true of
 * *cost* — no stub measures a frame — and it does not follow that the draw loop is untestable,
 * because the properties the plan actually requires of it are not timings. They are decisions:
 *
 * - exactly ONE canvas exists (§2.4 — the single-canvas rule the memory budget rests on),
 * - nothing below `MIN_AREA` is painted (§2.4 — "cull before upload"),
 * - `fillText` runs only on rectangles big enough to read (§5c — text costs ~10x a fill),
 * - the backing store is not reallocated per pointer move (§5f, the 1,137 MiB defect),
 * - a zoom re-render does not read a stale hover index (§5c defect 2, which blanked the page).
 *
 * Every one of those is a countable fact about which calls were issued with which arguments,
 * and every one currently ships with nothing enforcing it. This records them.
 *
 * `measureText` returns a deterministic 6 px per character rather than a real font metric.
 * The `fit` binary search only needs monotonicity in length to be exercised, and a real metric
 * would make the label assertions depend on whichever fonts the CI runner happens to have.
 */

export type FillRectCall = { x: number; y: number; w: number; h: number; style: string }
export type FillTextCall = {
  text: string
  x: number
  y: number
  /** The clip rectangle pushed immediately before this call — i.e. the rect being labelled. */
  clip: { x: number; y: number; w: number; h: number } | null
}

export class RecordingContext {
  readonly fillRects: FillRectCall[] = []
  /**
   * `lineWidth` is recorded because it is the only thing separating the two kinds of stroke
   * the draw loop issues: 1 px rectangle borders, and the 2 px hover highlight. Telling them
   * apart is what makes "did this frame paint a hover captured against the previous view?"
   * answerable — see the zoom test.
   */
  readonly strokeRects: { x: number; y: number; w: number; h: number; lineWidth: number }[] = []
  readonly fillTexts: FillTextCall[] = []
  readonly clearRects: { x: number; y: number; w: number; h: number }[] = []
  readonly transforms: number[][] = []

  fillStyle = ''
  strokeStyle = ''
  lineWidth = 1
  font = ''
  textBaseline = ''
  shadowColor = ''
  shadowBlur = 0

  private pendingClip: { x: number; y: number; w: number; h: number } | null = null
  private clip_: { x: number; y: number; w: number; h: number } | null = null
  private readonly stack: (typeof this.clip_)[] = []

  setTransform(a: number, b: number, c: number, d: number, e: number, f: number): void {
    this.transforms.push([a, b, c, d, e, f])
  }

  clearRect(x: number, y: number, w: number, h: number): void {
    this.clearRects.push({ x, y, w, h })
  }

  fillRect(x: number, y: number, w: number, h: number): void {
    this.fillRects.push({ x, y, w, h, style: this.fillStyle })
  }

  strokeRect(x: number, y: number, w: number, h: number): void {
    this.strokeRects.push({ x, y, w, h, lineWidth: this.lineWidth })
  }

  /** Drops everything recorded so far, so a single frame can be examined in isolation. */
  reset(): void {
    this.fillRects.length = 0
    this.strokeRects.length = 0
    this.fillTexts.length = 0
    this.clearRects.length = 0
    this.transforms.length = 0
  }

  fillText(text: string, x: number, y: number): void {
    this.fillTexts.push({ text, x, y, clip: this.clip_ })
  }

  measureText(text: string): { width: number } {
    return { width: text.length * 6 }
  }

  beginPath(): void {
    this.pendingClip = null
  }

  rect(x: number, y: number, w: number, h: number): void {
    this.pendingClip = { x, y, w, h }
  }

  clip(): void {
    this.clip_ = this.pendingClip
  }

  save(): void {
    this.stack.push(this.clip_)
  }

  restore(): void {
    this.clip_ = this.stack.pop() ?? null
  }
}

/**
 * Installs the recorder and the browser APIs jsdom does not implement.
 *
 * `ResizeObserver` deliberately does **not** fire on `observe`. The real one is asynchronous,
 * and the component sets the width a second time from `getBoundingClientRect` — which jsdom
 * reports as 0 — immediately after observing. A fake that fired synchronously would have that
 * 0 land last and the map would never draw, i.e. the harness would be testing the harness.
 */
export type CanvasHarness = {
  /** The 2D context handed to whichever canvas asked for one. */
  context: RecordingContext
  /** Every canvas element that has ever requested a context. The single-canvas rule. */
  canvases: HTMLCanvasElement[]
  /** Backing-store assignments that actually changed a dimension, per §5f's guard. */
  backingStoreWrites: number
  /** Drives the observed width, the way a real resize would. */
  resize: (width: number) => void
  /** Replaces `devicePixelRatio` and fires the media query the component subscribed to. */
  setDpr: (dpr: number) => void
  restore: () => void
}

export function installCanvasHarness(): CanvasHarness {
  const context = new RecordingContext()
  const canvases: HTMLCanvasElement[] = []
  const harness = {
    context,
    canvases,
    backingStoreWrites: 0,
  } as CanvasHarness

  const originalGetContext = HTMLCanvasElement.prototype.getContext
  const originalMatchMedia = window.matchMedia
  const originalObserver = (globalThis as Record<string, unknown>).ResizeObserver
  const originalDpr = window.devicePixelRatio

  // Through `unknown`: the real signature is an overload set covering webgl and
  // bitmaprenderer, and the recorder deliberately implements only the 2d shape the draw loop
  // actually uses. Widening it to satisfy the overloads would be inventing members no test
  // asserts on.
  HTMLCanvasElement.prototype.getContext = function (this: HTMLCanvasElement) {
    if (!canvases.includes(this)) canvases.push(this)
    return context as unknown as CanvasRenderingContext2D
  } as unknown as typeof HTMLCanvasElement.prototype.getContext

  // Shadowed on the prototype, not on the element at getContext time.
  //
  // The draw effect calls applyCanvasGeometry *before* it asks for a context, so an
  // instance-level accessor installed from getContext arrives after the first sizing write
  // and reports the default rather than the real backing store. That is not a detail of the
  // harness: it means "was the store reallocated?" has to be observable from the moment the
  // element exists, which only a prototype accessor gives.
  const sizes = new WeakMap<HTMLCanvasElement, { w: number; h: number }>()
  const sizeOf = (element: HTMLCanvasElement) => {
    let size = sizes.get(element)
    if (!size) {
      // jsdom's own defaults, so an unsized canvas reads the way a real one does.
      size = { w: 300, h: 150 }
      sizes.set(element, size)
    }
    return size
  }
  const originalWidth = Object.getOwnPropertyDescriptor(HTMLCanvasElement.prototype, 'width')
  const originalHeight = Object.getOwnPropertyDescriptor(HTMLCanvasElement.prototype, 'height')

  for (const axis of ['width', 'height'] as const) {
    const key = axis === 'width' ? 'w' : 'h'
    Object.defineProperty(HTMLCanvasElement.prototype, axis, {
      configurable: true,
      get(this: HTMLCanvasElement) {
        return sizeOf(this)[key]
      },
      set(this: HTMLCanvasElement, value: number) {
        // Counts EVERY assignment, not only the ones that change the value.
        //
        // That distinction is the whole of §5f: a real canvas discards and reallocates its
        // backing store even when the value assigned is identical — that is how the
        // clear-the-canvas idiom works. A counter that ignored same-value writes would be
        // re-implementing the guard it exists to test, and would report a green run against
        // code with the guard deleted. It did exactly that before this comment existed.
        harness.backingStoreWrites++
        sizeOf(this)[key] = value
      },
    })
  }

  let observed: ((entries: { contentRect: { width: number } }[]) => void) | null = null
  class FakeResizeObserver {
    constructor(callback: (entries: { contentRect: { width: number } }[]) => void) {
      observed = callback
    }
    observe(): void {}
    disconnect(): void {
      observed = null
    }
  }
  ;(globalThis as Record<string, unknown>).ResizeObserver = FakeResizeObserver

  // jsdom implements no matchMedia at all, and the component's pixel-ratio subscription is
  // built on one. `matches` is always true because the component only ever queries the ratio
  // it currently believes in — see dprQuery for why that subscription is one-shot.
  const listeners = new Set<() => void>()
  window.matchMedia = ((query: string) => ({
    media: query,
    matches: true,
    addEventListener: (_type: string, listener: () => void) => {
      listeners.add(listener)
    },
    removeEventListener: (_type: string, listener: () => void) => {
      listeners.delete(listener)
    },
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
    onchange: null,
  })) as unknown as typeof window.matchMedia

  harness.resize = (width: number) => {
    observed?.([{ contentRect: { width } }])
  }

  harness.setDpr = (dpr: number) => {
    Object.defineProperty(window, 'devicePixelRatio', { configurable: true, value: dpr })
    for (const fn of [...listeners]) fn()
  }

  harness.restore = () => {
    HTMLCanvasElement.prototype.getContext = originalGetContext
    if (originalWidth) Object.defineProperty(HTMLCanvasElement.prototype, 'width', originalWidth)
    if (originalHeight) Object.defineProperty(HTMLCanvasElement.prototype, 'height', originalHeight)
    window.matchMedia = originalMatchMedia
    ;(globalThis as Record<string, unknown>).ResizeObserver = originalObserver
    Object.defineProperty(window, 'devicePixelRatio', {
      configurable: true,
      value: originalDpr,
    })
  }

  return harness
}
