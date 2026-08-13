import type { Connect, Plugin } from 'vite'
import type { ServerResponse } from 'node:http'

/**
 * Dev-only mock of the Silt API.
 *
 * In production the API is served by the WPF shell intercepting WebView2 resource requests,
 * so `npm run dev` has no backend at all and the UI cannot be worked on or reviewed in a
 * browser. This plugin fills that gap with fixtures modelled on real measurements from the
 * machine that motivated the project.
 *
 * It is registered only for `command === 'serve'`, so it cannot reach a production build.
 */

const GIB = 1024 * 1024 * 1024
const MIB = 1024 * 1024

const SCAN_ID = 'mock00000001'

const volumes = [
  {
    root: 'C:\\',
    label: 'C:',
    fileSystem: 'NTFS',
    capacityBytes: Math.round(425.06 * GIB),
    freeBytes: Math.round(241.33 * GIB),
    isReady: true,
  },
  {
    root: 'D:\\',
    label: 'D:',
    fileSystem: 'NTFS',
    capacityBytes: 50 * GIB,
    freeBytes: Math.round(47.2 * GIB),
    isReady: true,
  },
]

const summary = {
  scanId: SCAN_ID,
  root: 'C:\\',
  durationSeconds: 6.96,
  totalFiles: 611481,
  totalDirectories: 155163,
  totalAllocatedBytes: Math.round(160.9 * GIB),
  totalLogicalBytes: Math.round(161.5 * GIB),
  accessDeniedCount: 296,
  failedCount: 2,
  skippedSurrogateCount: 47,
  hardLinkFilesDeduplicated: 82441,
  hardLinkBytesDeduplicated: Math.round(25.3 * GIB),
  reconciliation: {
    volumeRoot: 'C:\\',
    capacityBytes: Math.round(425.06 * GIB),
    freeBytes: Math.round(241.33 * GIB),
    usedBytes: Math.round(183.74 * GIB),
    scannedBytes: Math.round(160.9 * GIB),
    unaccountedBytes: Math.round(22.88 * GIB),
    unaccountedFraction: 0.125,
    inaccessibleDirectoryCount: 296,
    lines: [
      {
        label: 'Files and folders',
        bytes: Math.round(135.57 * GIB),
        kind: 'Measured',
        detail: '611,481 files across 155,163 directories, counted by allocated size.',
      },
      {
        label: 'pagefile.sys',
        bytes: 19 * GIB,
        kind: 'Known',
        detail:
          'Virtual memory. Windows manages its size. Disabling it on a machine with limited RAM trades disk space for instability.',
      },
      {
        label: 'hiberfil.sys',
        bytes: Math.round(6.28 * GIB),
        kind: 'Known',
        detail:
          'Hibernation image, sized from installed RAM. Reclaimable by disabling hibernation, which also turns off Fast Startup.',
      },
      {
        label: 'Unreadable directories',
        bytes: 0,
        kind: 'Unmeasured',
        detail:
          '296 directories could not be opened, so their contents are absent from the measured total.',
      },
      {
        label: 'Unaccounted',
        bytes: Math.round(22.88 * GIB),
        kind: 'Unaccounted',
        detail:
          '22.88 GiB (12.5% of used space) is not explained by anything above. Likely the 296 directories that could not be read, NTFS metadata such as the $MFT, and Volume Shadow Copy snapshots.',
      },
    ],
  },
}

const tree = {
  path: 'C:\\',
  totalAllocatedBytes: Math.round(160.9 * GIB),
  totalChildCount: 18,
  truncated: false,
  children: [
    { name: 'Users', path: 'C:\\Users', allocatedBytes: Math.round(66.39 * GIB), logicalBytes: Math.round(66.01 * GIB), fileCount: 301668, directoryCount: 48016, hasChildren: true, conditions: [] },
    { name: 'Windows', path: 'C:\\Windows', allocatedBytes: Math.round(24.82 * GIB), logicalBytes: Math.round(24.9 * GIB), fileCount: 210334, directoryCount: 78221, hasChildren: true, conditions: [] },
    { name: 'Program Files', path: 'C:\\Program Files', allocatedBytes: Math.round(18.66 * GIB), logicalBytes: Math.round(18.7 * GIB), fileCount: 61002, directoryCount: 12844, hasChildren: true, conditions: [] },
    { name: 'WORK_ALL', path: 'C:\\WORK_ALL', allocatedBytes: Math.round(18.05 * GIB), logicalBytes: Math.round(18.0 * GIB), fileCount: 30221, directoryCount: 8410, hasChildren: true, conditions: [] },
    { name: 'ProgramData', path: 'C:\\ProgramData', allocatedBytes: Math.round(3.4 * GIB), logicalBytes: Math.round(3.4 * GIB), fileCount: 12004, directoryCount: 3110, hasChildren: true, conditions: [] },
    { name: 'System Volume Information', path: 'C:\\System Volume Information', allocatedBytes: 0, logicalBytes: 0, fileCount: 0, directoryCount: 0, hasChildren: false, conditions: ['access-denied'] },
    { name: 'Documents and Settings', path: 'C:\\Documents and Settings', allocatedBytes: 0, logicalBytes: 0, fileCount: 0, directoryCount: 0, hasChildren: false, conditions: ['junction'] },
  ],
}

const apps = {
  minimumBytes: 50 * MIB,
  totalAttributedBytes: Math.round(60 * GIB),
  apps: [
    {
      key: 'claude',
      displayName: 'Claude',
      publisher: 'Anthropic',
      totalAllocatedBytes: Math.round(18.87 * GIB),
      totalFileCount: 92110,
      isSplitAcrossLocations: true,
      locations: [
        { path: 'C:\\Users\\you\\AppData\\Roaming\\Claude', allocatedBytes: Math.round(11.28 * GIB), fileCount: 41003, kind: 'RoamingData' },
        { path: 'C:\\Users\\you\\AppData\\Local\\Claude-3p', allocatedBytes: Math.round(7.58 * GIB), fileCount: 50110, kind: 'LocalData' },
        { path: 'C:\\ProgramData\\Claude', allocatedBytes: Math.round(0.01 * GIB), fileCount: 900, kind: 'MachineData' },
        { path: 'C:\\Users\\you\\AppData\\Local\\Packages\\Claude_pzs8sxrjxfjjc', allocatedBytes: Math.round(0.005 * GIB), fileCount: 97, kind: 'PackageData' },
      ],
    },
    {
      key: 'jetbrains',
      displayName: 'JetBrains',
      publisher: 'JetBrains s.r.o.',
      totalAllocatedBytes: Math.round(14.74 * GIB),
      totalFileCount: 180220,
      isSplitAcrossLocations: true,
      locations: [
        { path: 'C:\\Program Files\\JetBrains', allocatedBytes: Math.round(10.16 * GIB), fileCount: 120000, kind: 'Install' },
        { path: 'C:\\Users\\you\\AppData\\Local\\JetBrains', allocatedBytes: Math.round(2.68 * GIB), fileCount: 42000, kind: 'LocalData' },
        { path: 'C:\\Users\\you\\AppData\\Roaming\\JetBrains', allocatedBytes: Math.round(1.9 * GIB), fileCount: 18220, kind: 'RoamingData' },
      ],
    },
    {
      key: 'google',
      displayName: 'Google',
      publisher: 'Google LLC',
      totalAllocatedBytes: Math.round(8.43 * GIB),
      totalFileCount: 74110,
      isSplitAcrossLocations: true,
      locations: [
        { path: 'C:\\Users\\you\\AppData\\Local\\Google', allocatedBytes: Math.round(7.44 * GIB), fileCount: 70000, kind: 'LocalData' },
        { path: 'C:\\Program Files\\Google', allocatedBytes: Math.round(0.49 * GIB), fileCount: 3110, kind: 'Install' },
      ],
    },
    {
      key: 'dotnet',
      displayName: 'dotnet',
      totalAllocatedBytes: Math.round(1.56 * GIB),
      totalFileCount: 9004,
      isSplitAcrossLocations: false,
      locations: [
        { path: 'C:\\Program Files\\dotnet', allocatedBytes: Math.round(1.56 * GIB), fileCount: 9004, kind: 'Install' },
      ],
    },
  ],
}

/** The scenario the product exists for: a temp directory that quietly gained 12 GiB. */
const growth = {
  available: true,
  unavailable: null,
  fromTakenAt: '2026-08-06T09:12:00Z',
  toTakenAt: '2026-08-13T09:30:00Z',
  spanDays: 7.01,
  fromTotalBytes: Math.round(146.2 * GIB),
  toTotalBytes: Math.round(160.9 * GIB),
  deltaBytes: Math.round(14.7 * GIB),
  freeDeltaBytes: -Math.round(14.7 * GIB),
  floorsDiffer: false,
  snapshotCount: 6,
  apps: [
    { key: 'claude', displayName: 'Claude', beforeBytes: Math.round(11.9 * GIB), afterBytes: Math.round(18.87 * GIB), deltaBytes: Math.round(6.97 * GIB), kind: 'Grown' },
    { key: 'google', displayName: 'Google', beforeBytes: Math.round(6.1 * GIB), afterBytes: Math.round(8.43 * GIB), deltaBytes: Math.round(2.33 * GIB), kind: 'Grown' },
    { key: 'jetbrains', displayName: 'JetBrains', beforeBytes: Math.round(15.4 * GIB), afterBytes: Math.round(14.74 * GIB), deltaBytes: -Math.round(0.66 * GIB), kind: 'Shrunk' },
  ],
  directories: [
    { path: 'C:\\Users\\you\\AppData\\Local\\Temp', beforeBytes: Math.round(32.1 * GIB), afterBytes: Math.round(44.2 * GIB), deltaBytes: Math.round(12.1 * GIB), selfDeltaBytes: Math.round(12.1 * GIB), kind: 'Grown' },
    { path: 'C:\\Users\\you\\AppData\\Roaming\\Claude', beforeBytes: Math.round(7.2 * GIB), afterBytes: Math.round(11.28 * GIB), deltaBytes: Math.round(4.08 * GIB), selfDeltaBytes: Math.round(4.08 * GIB), kind: 'Grown' },
    { path: 'C:\\Users\\you\\AppData\\Local\\npm-cache', beforeBytes: Math.round(4.5 * GIB), afterBytes: Math.round(6.75 * GIB), deltaBytes: Math.round(2.25 * GIB), selfDeltaBytes: Math.round(2.25 * GIB), kind: 'Grown' },
    { path: 'C:\\Users\\you\\Downloads\\installers', beforeBytes: 0, afterBytes: Math.round(1.8 * GIB), deltaBytes: Math.round(1.8 * GIB), selfDeltaBytes: Math.round(1.8 * GIB), kind: 'Added' },
    { path: 'C:\\Users\\you\\AppData\\Local\\JetBrains\\caches', beforeBytes: Math.round(2.9 * GIB), afterBytes: Math.round(2.24 * GIB), deltaBytes: -Math.round(0.66 * GIB), selfDeltaBytes: -Math.round(0.66 * GIB), kind: 'Shrunk' },
    { path: 'C:\\Users\\you\\Desktop\\Android\\imgwork', beforeBytes: Math.round(3.72 * GIB), afterBytes: 0, deltaBytes: -Math.round(3.72 * GIB), selfDeltaBytes: -Math.round(3.72 * GIB), kind: 'Removed' },
  ],
}

function send(res: ServerResponse, body: unknown, status = 200) {
  const json = JSON.stringify(body)
  res.statusCode = status
  res.setHeader('Content-Type', 'application/json; charset=utf-8')
  res.setHeader('Cache-Control', 'no-store')
  res.end(json)
}

export function mockApi(): Plugin {
  return {
    name: 'silt-mock-api',
    apply: 'serve',
    configureServer(server) {
      const handler: Connect.NextHandleFunction = (req, res, next) => {
        const url = req.url ?? ''
        if (!url.startsWith('/api/')) {
          next()
          return
        }

        const path = url.split('?')[0]

        if (path === '/api/volumes') return send(res, volumes)
        if (path === '/api/scans' && req.method === 'POST') {
          return send(res, { scanId: SCAN_ID }, 202)
        }

        if (path === `/api/scans/${SCAN_ID}`) {
          return send(res, {
            scanId: SCAN_ID,
            // Lower-case deliberately. ScanStatusDto.State is a real enum, so the API
            // serializes it through JsonStringEnumConverter(CamelCase) and emits
            // "completed". The Kind fields elsewhere are plain strings produced by
            // ToString(), so those stay PascalCase. Getting this wrong here silently
            // strands the UI on the progress screen forever.
            state: 'completed',
            root: 'C:\\',
            directoriesScanned: summary.totalDirectories,
            filesScanned: summary.totalFiles,
            bytesScanned: summary.totalAllocatedBytes,
            currentPath: 'C:\\Windows\\WinSxS',
            elapsedSeconds: 6.96,
            error: null,
          })
        }

        if (path === `/api/scans/${SCAN_ID}/summary`) return send(res, summary)
        if (path === `/api/scans/${SCAN_ID}/tree`) return send(res, tree)
        if (path === `/api/scans/${SCAN_ID}/apps`) return send(res, apps)
        if (path === `/api/scans/${SCAN_ID}/growth`) {
          // A 24-hour window has no baseline in this fixture's history, which is also the
          // real first-run experience. Exposing it here lets the panel's empty branch be
          // reviewed without hand-editing fixtures.
          if (url.includes('days=1')) {
            return send(res, {
              ...growth,
              available: false,
              unavailable:
                'This is the first recorded scan of this volume. Scan again in a few days and Silt will show what changed.',
              snapshotCount: 1,
              directories: [],
              apps: [],
            })
          }
          return send(res, growth)
        }

        send(res, { message: `No mock for ${path}` }, 404)
      }

      server.middlewares.use(handler)
    },
  }
}
