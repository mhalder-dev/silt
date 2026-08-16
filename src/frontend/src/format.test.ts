import { describe, expect, it } from 'vitest'
import { formatBytes, formatCount, formatDuration, formatPercent } from './format'

/**
 * These are one-line functions, and they are still worth a gate: every number Silt shows a
 * user passes through here, and the unit convention is a deliberate product decision (see
 * the module comment). A change from KiB to KB would be a silent 2.4 % lie at GiB scale in
 * a tool whose entire job is telling you how big things are.
 */

describe('formatBytes', () => {
  it('uses binary units at binary thresholds', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(1023)).toBe('1023 B')
    expect(formatBytes(1024)).toBe('1.0 KiB')
    expect(formatBytes(1024 ** 2)).toBe('1.0 MiB')
    expect(formatBytes(1024 ** 3)).toBe('1.0 GiB')
    expect(formatBytes(1024 ** 4)).toBe('1.0 TiB')
    expect(formatBytes(1024 ** 5)).toBe('1.0 PiB')
  })

  it('renders whole bytes without a decimal', () => {
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(512.4)).toBe('512 B')
  })

  it('stops at the largest unit rather than inventing one', () => {
    expect(formatBytes(1024 ** 6)).toBe('1024.0 PiB')
  })

  it('keeps the sign on a negative delta', () => {
    // Growth diffing reports shrinkage, so negatives are a normal input here, not an error.
    expect(formatBytes(-(1024 ** 3))).toBe('-1.0 GiB')
    expect(formatBytes(-512)).toBe('-512 B')
  })

  it('returns a dash rather than "NaN GiB"', () => {
    expect(formatBytes(Number.NaN)).toBe('—')
    expect(formatBytes(Number.POSITIVE_INFINITY)).toBe('—')
  })

  it('honours the decimals argument', () => {
    expect(formatBytes(1536, 2)).toBe('1.50 KiB')
    expect(formatBytes(1536, 0)).toBe('2 KiB')
  })

  it('reports the measured 44 GB Temp payload recognisably', () => {
    expect(formatBytes(44 * 1024 ** 3)).toBe('44.0 GiB')
  })
})

describe('formatDuration', () => {
  it('switches units at the thresholds a scan actually spans', () => {
    expect(formatDuration(0.25)).toBe('250 ms')
    expect(formatDuration(9)).toBe('9.00 s')
    // The measured whole-C: scan.
    expect(formatDuration(9.1)).toBe('9.10 s')
    expect(formatDuration(75)).toBe('1m 15s')
  })

  it('does not print a bare 60 seconds', () => {
    expect(formatDuration(60)).toBe('1m 0s')
  })
})

describe('formatPercent and formatCount', () => {
  it('formats a fraction as a percentage', () => {
    expect(formatPercent(0.5)).toBe('50.0%')
    expect(formatPercent(0.12345, 2)).toBe('12.35%')
    expect(formatPercent(1)).toBe('100.0%')
  })

  it('groups large counts', () => {
    // 1.2 M Temp files is a real figure from the plan; an ungrouped run of digits is
    // unreadable at exactly the scale that matters.
    expect(formatCount(1200000)).toMatch(/^1\D200\D000$/)
    expect(formatCount(0)).toBe('0')
  })
})
