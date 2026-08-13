/**
 * Byte formatting.
 *
 * Binary units (KiB/MiB/GiB) are used throughout and labelled as such. Windows shows
 * binary quantities under decimal labels ("GB" for 2^30 bytes), which is where a large
 * part of "my drive is the wrong size" confusion comes from. A tool whose entire purpose
 * is explaining disk space should not add to that.
 */

const UNITS = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'] as const

export function formatBytes(bytes: number, decimals = 1): string {
  if (!Number.isFinite(bytes)) return '—'
  const negative = bytes < 0
  let value = Math.abs(bytes)
  let unit = 0

  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024
    unit++
  }

  const rendered = unit === 0 ? String(Math.round(value)) : value.toFixed(decimals)
  return `${negative ? '-' : ''}${rendered} ${UNITS[unit]}`
}

export function formatCount(n: number): string {
  return n.toLocaleString()
}

export function formatDuration(seconds: number): string {
  if (seconds < 1) return `${Math.round(seconds * 1000)} ms`
  if (seconds < 60) return `${seconds.toFixed(2)} s`
  const m = Math.floor(seconds / 60)
  const s = Math.round(seconds % 60)
  return `${m}m ${s}s`
}

export function formatPercent(fraction: number, decimals = 1): string {
  return `${(fraction * 100).toFixed(decimals)}%`
}
