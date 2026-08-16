/**
 * Backing-store sizing for the one canvas.
 *
 * Split out of `Treemap.tsx` for the reason §5e gives for `resolveOpenTarget`: this is the
 * part of the render path with arithmetic in it, and inside a `useEffect` it is reachable
 * only by a React renderer plus a stubbed canvas — which would prove nothing. Out here it is
 * a pure function of three numbers.
 *
 * docs/PLAN.md has carried "`devicePixelRatio` exercised only at DPR 1" as unverified through
 * §5c, §5d and §5e. "Correct by construction" was the honest description of `round(css * dpr)`
 * and it was also the whole of the evidence. This module is the construction, stated once and
 * asserted at the ratios real displays actually report.
 */

/**
 * Largest backing store the treemap will ever allocate, in device pixels.
 *
 * A canvas costs 4 bytes per device pixel, so this is a 32 MB ceiling. §2.4 measures the app
 * as already over its 400 MB budget, and the single-canvas rule exists precisely because
 * backing store is the term that scales with the square of the pixel ratio.
 *
 * The cap is on **area, not on the ratio**. Clamping `devicePixelRatio` itself is the obvious
 * move and it is wrong: browser zoom multiplies the reported ratio while shrinking the CSS
 * pixel count by the same factor, so a hard DPR ceiling would blur the map for anyone zoomed
 * in without saving a single byte. Area is the quantity actually being defended.
 *
 * 3840 x 1600 device pixels — a full-width 4K window — is 6.1 M, comfortably inside this, so
 * no real display is degraded by it.
 */
export const MAX_BACKING_PIXELS = 8_000_000

/**
 * `window.devicePixelRatio` reduced to a number that can be multiplied by.
 *
 * The idiom in the wild is `window.devicePixelRatio || 1`, which handles `0`, `NaN` and
 * `undefined` and silently passes `Infinity` and negatives straight through to `canvas.width`.
 * A negative or non-finite width throws `IndexSizeError` and takes the whole panel with it.
 */
export function normalizeDpr(raw: number | undefined): number {
  return typeof raw === 'number' && Number.isFinite(raw) && raw > 0 ? raw : 1
}

/**
 * Media query that stops matching the moment the pixel ratio changes.
 *
 * There is no `devicePixelRatio` change event. The documented way to hear about one is to
 * match the ratio you last saw and wait for that query to *stop* being true — so the
 * subscription is one-shot by nature and has to be re-established against the new ratio each
 * time, which is why the caller keeps the ratio in state rather than reading it inline.
 *
 * Needed because the ratio can change with nothing else changing: dragging the window from a
 * 100 % monitor to a 150 % one leaves the CSS width, the data and the layout all identical.
 * Measured in the browser at DPR 1.5 — the backing store stayed at its DPR 2 size until a
 * pointer move happened to re-run the draw effect for an unrelated reason.
 */
export function dprQuery(dpr: number): string {
  return `(resolution: ${normalizeDpr(dpr)}dppx)`
}

export type CanvasGeometry = {
  /** Backing-store size, in device pixels — what goes on `canvas.width` / `canvas.height`. */
  backingWidth: number
  backingHeight: number
  /** Layout size, in CSS pixels — what goes on `canvas.style`. */
  cssWidth: number
  cssHeight: number
  /**
   * Scale to hand `setTransform`, so the draw loop keeps working in CSS pixels.
   *
   * Equal to the normalized ratio except when the area cap bites. It is returned rather than
   * recomputed at the call site because after clamping, `backingWidth / cssWidth` is not
   * exactly the ratio any more — rounding to whole device pixels moves it — and drawing with
   * a scale that disagrees with the backing store by even a fraction is how a map ends up
   * one row of pixels short on its right edge.
   */
  scale: number
}

/**
 * Device-pixel geometry for a CSS-pixel viewport at a given pixel ratio.
 *
 * Rounds rather than truncates: at DPR 1.5 a 811 px viewport wants 1216.5 device pixels, and
 * flooring loses half a pixel off the right edge on every odd width — a hairline gap between
 * the map and its frame that only appears on 150 % displays, which is exactly the class of
 * defect that goes unnoticed on the developer's machine.
 */
export function canvasGeometry(
  cssWidth: number,
  cssHeight: number,
  rawDpr: number | undefined,
): CanvasGeometry {
  const width = Math.max(0, Math.floor(cssWidth))
  const height = Math.max(0, Math.floor(cssHeight))
  const dpr = normalizeDpr(rawDpr)

  const wanted = width * height * dpr * dpr
  // sqrt because the cap is on area and the ratio applies to both axes.
  const scale = wanted > MAX_BACKING_PIXELS ? dpr * Math.sqrt(MAX_BACKING_PIXELS / wanted) : dpr

  return {
    backingWidth: Math.round(width * scale),
    backingHeight: Math.round(height * scale),
    cssWidth: width,
    cssHeight: height,
    scale,
  }
}

/**
 * Applies the geometry, and reports whether the backing store was actually reallocated.
 *
 * The guard is the point. Assigning `canvas.width` discards and reallocates the backing store
 * *even when the value is unchanged*, and the draw effect depends on `hover` — so before this
 * check every single mousemove over the map threw away and re-allocated the full backing
 * store. At DPR 1 on the dev fixture that is 1.5 MB per pointer event; at DPR 2 on a
 * full-width 4K window it is 24 MB per pointer event, handed to the garbage collector at
 * mousemove frequency, in an app §2.4 already measures as over its memory budget.
 *
 * The draw loop must therefore not rely on the assignment to clear the canvas. It calls
 * `clearRect` explicitly, which it needs anyway for the frames this returns false on.
 */
export function applyCanvasGeometry(
  canvas: HTMLCanvasElement,
  geometry: CanvasGeometry,
): boolean {
  const resized =
    canvas.width !== geometry.backingWidth || canvas.height !== geometry.backingHeight

  if (resized) {
    canvas.width = geometry.backingWidth
    canvas.height = geometry.backingHeight
  }

  const cssWidth = `${geometry.cssWidth}px`
  const cssHeight = `${geometry.cssHeight}px`
  if (canvas.style.width !== cssWidth) canvas.style.width = cssWidth
  if (canvas.style.height !== cssHeight) canvas.style.height = cssHeight

  return resized
}
