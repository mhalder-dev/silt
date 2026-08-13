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

/**
 * Treemap fixture.
 *
 * A real nested tree is generated and then flattened by the same rules the backend's
 * projector uses, rather than hand-writing a flat node list. That matters for two reasons:
 * the layout is only exercised properly by sizes spanning orders of magnitude at varying
 * depth, and navigation is only real if clicking a folder returns that folder's own
 * children. A mock must mirror the contract, not a guess at it - a mock that got
 * `ScanStatusDto.State` casing wrong once stranded the whole UI on the progress screen.
 */

type MockNode = { name: string; own: number; children: MockNode[]; total: number }

/** Deterministic, so a layout defect looks the same on every reload. */
function mulberry32(seed: number): () => number {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

const FOLDER_WORDS = [
  'cache', 'bin', 'obj', 'node_modules', 'dist', 'src', 'assets', 'logs', 'temp',
  'packages', 'runtime', 'lib', 'data', 'profiles', 'Code Cache', 'GPUCache',
  'shaders', 'index', 'blobs', 'staging', 'snapshots', 'extensions', 'plugins',
]

function buildMockTree(name: string, bytes: number, depth: number, rand: () => number): MockNode {
  // Fan-out shrinks with depth, the way real trees do, and stops entirely at depth 5 so
  // module load stays within a few tens of milliseconds.
  const fanOut = depth >= 5 ? 0 : Math.max(2, Math.round((6 - depth) * (0.6 + rand())))

  if (fanOut === 0 || bytes < 64 * 1024) {
    return { name, own: bytes, children: [], total: bytes }
  }

  // A real directory keeps some bytes for itself; a purely container-shaped tree would
  // never exercise the loose-files rectangle.
  const own = Math.floor(bytes * rand() * 0.3)
  let remaining = bytes - own

  const children: MockNode[] = []
  for (let i = 0; i < fanOut; i++) {
    const last = i === fanOut - 1
    // Skewed split, so sizes span orders of magnitude the way they really do rather than
    // producing a uniform grid that would make any layout look correct.
    const share = last ? remaining : Math.floor(remaining * (0.15 + rand() * 0.55))
    remaining -= share
    const word = FOLDER_WORDS[Math.floor(rand() * FOLDER_WORDS.length)]
    children.push(buildMockTree(`${word}-${i}`, Math.max(0, share), depth + 1, rand))
  }

  const total = own + children.reduce((sum, c) => sum + c.total, 0)
  return { name, own, children, total }
}

const treemapRoot: MockNode = (() => {
  const rand = mulberry32(20260814)
  const children = tree.children
    .filter((c) => c.allocatedBytes > 0)
    .map((c) => buildMockTree(c.name, c.allocatedBytes, 1, rand))
  const own = summary.totalAllocatedBytes - children.reduce((s, c) => s + c.total, 0)
  return {
    name: 'C:\\',
    own: Math.max(0, own),
    children,
    total: Math.max(0, own) + children.reduce((s, c) => s + c.total, 0),
  }
})()

function findMockNode(node: MockNode, prefix: string, target: string): MockNode | null {
  if (prefix.toLowerCase() === target.toLowerCase()) return node
  for (const child of node.children) {
    const childPath = `${prefix.replace(/[\\/]+$/, '')}\\${child.name}`
    if (target.toLowerCase().startsWith(childPath.toLowerCase())) {
      const found = findMockNode(child, childPath, target)
      if (found) return found
    }
  }
  return null
}

const MIN_FRACTION = 1e-5
const MAX_NODES = 20_000
const MAX_DEPTH = 8

/** Largest-first flattening, matching the backend's projection semantics. */
function projectMock(root: MockNode, rootPath: string) {
  const total = root.total
  const minimumBytes = Math.floor(total * MIN_FRACTION)

  const nodes: { p: number; n: string; b: number; k: string; x: boolean }[] = [
    {
      p: -1,
      n: rootPath.replace(/[\\/]+$/, '').split('\\').pop() || rootPath,
      b: total,
      k: 'Directory',
      x: false,
    },
  ]

  let aggregated = 0
  let truncated = false

  // A sorted frontier rather than a real priority queue: the mock runs once per navigation
  // over a few tens of thousands of nodes, where the difference is unmeasurable.
  const frontier: { node: MockNode; self: number; depth: number }[] = [
    { node: root, self: 0, depth: 0 },
  ]

  while (frontier.length > 0) {
    frontier.sort((a, b) => b.node.total - a.node.total)
    const { node, self, depth } = frontier.shift() as (typeof frontier)[number]

    let unresolved = 0
    let unresolvedCount = 0

    if (node.own > 0) {
      if (node.own >= minimumBytes && nodes.length + 1 < MAX_NODES) {
        nodes.push({ p: self, n: '(files here)', b: node.own, k: 'Files', x: false })
      } else {
        unresolved += node.own
        unresolvedCount++
      }
    }

    for (const child of [...node.children].sort((a, b) => b.total - a.total)) {
      if (child.total < minimumBytes || nodes.length + 1 >= MAX_NODES) {
        unresolved += child.total
        unresolvedCount++
        truncated ||= child.total >= minimumBytes
        continue
      }

      const hasChildren = child.children.length > 0
      const willExpand = hasChildren && depth + 1 < MAX_DEPTH
      const index = nodes.length
      nodes.push({
        p: self,
        n: child.name,
        b: child.total,
        k: 'Directory',
        x: hasChildren && !willExpand,
      })
      if (willExpand) frontier.push({ node: child, self: index, depth: depth + 1 })
    }

    // Rolled up rather than dropped, so an expanded node's children still sum to exactly
    // its own size - the invariant the renderer scales against.
    if (unresolved > 0) {
      nodes.push({ p: self, n: '(smaller items)', b: unresolved, k: 'Other', x: false })
      aggregated += unresolvedCount
    }
  }

  return {
    path: rootPath,
    totalAllocatedBytes: total,
    minimumBytes,
    aggregatedNodeCount: aggregated,
    truncated,
    nodes,
  }
}

let executeCount = 0

const cleanupPlan = {
  planId: 'plan-mock-1',
  createdAt: '2026-08-14T09:30:00Z',
  totalAllocatedBytes: Math.round(3.63 * GIB),
  totalFileCount: 40479,
  totalItemCount: 239,
  rules: [
    {
      ruleId: 'pkgmgr.caches',
      displayName: 'Package manager download caches',
      description:
        'Downloaded package archives for NuGet, pip and uv. These are HTTP caches, not the installed packages themselves.',
      tier: 'SafeWithCaveat',
      regeneration:
        'Packages are re-downloaded on the next restore. Installed packages are untouched; only the download cache is cleared.',
      regenerationCommand: 'dotnet nuget locals http-cache --clear',
      totalAllocatedBytes: Math.round(1.8 * GIB),
      totalFileCount: 27017,
      itemCount: 14,
      exclusionCount: 0,
      topItems: [],
      sampleExclusions: [],
    },
    {
      ruleId: 'chrome.cache',
      displayName: 'Chrome browsing caches',
      description:
        'Cached page resources for each Chrome profile. History, bookmarks, passwords and extensions live elsewhere and are not touched.',
      tier: 'SafeWithCaveat',
      regeneration:
        'Pages re-download their resources, so browsing is briefly slower. You stay signed in and nothing is removed from your history or bookmarks.',
      regenerationCommand: null,
      totalAllocatedBytes: Math.round(1.19 * GIB),
      totalFileCount: 10449,
      itemCount: 31,
      exclusionCount: 0,
      topItems: [],
      sampleExclusions: [],
    },
    {
      ruleId: 'crashdumps.thumbcache',
      displayName: 'Crash dumps and thumbnail caches',
      description:
        'Memory dumps written when an application crashed, and Explorer thumbnail databases.',
      tier: 'AlwaysSafe',
      regeneration:
        'Thumbnails are regenerated as you browse folders. Crash dumps are only useful to whoever was debugging that crash.',
      regenerationCommand: null,
      totalAllocatedBytes: Math.round(0.22 * GIB),
      totalFileCount: 40,
      itemCount: 40,
      exclusionCount: 0,
      topItems: [],
      sampleExclusions: [],
    },
    {
      ruleId: 'temp.user.aged',
      displayName: 'Temporary files older than 7 days',
      description:
        'Windows and every application write scratch files here and frequently never remove them.',
      tier: 'AlwaysSafe',
      regeneration:
        'Applications recreate what they need on demand. Nothing here is expected to survive a reboot.',
      regenerationCommand: null,
      totalAllocatedBytes: Math.round(0.03 * GIB),
      totalFileCount: 1306,
      itemCount: 46,
      exclusionCount: 747,
      topItems: [],
      sampleExclusions: [],
    },
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

        if (path === '/api/cleanup/safety') {
          return send(res, { healthy: true, failures: [] })
        }

        if (path === '/api/cleanup/plans' && req.method === 'POST') {
          return send(res, cleanupPlan, 201)
        }

        if (path.startsWith('/api/cleanup/plans/') && path.endsWith('/execute')) {
          // Alternates so both the success and the refusal path can be reviewed. The
          // refusal is the more important of the two to get right visually.
          executeCount++
          return send(
            res,
            executeCount % 2 === 1
              ? {
                  operationId: 'op-mock-1',
                  ruleId: 'temp.user.aged',
                  executed: true,
                  refusal: 'None',
                  refusalMessage: null,
                  itemsDeleted: 46,
                  itemsFailed: 0,
                  bytesDeleted: Math.round(1.9 * GIB),
                  recycleBinAvailableBytes: Math.round(20 * GIB),
                  failures: [],
                }
              : {
                  operationId: 'op-mock-2',
                  ruleId: 'npm.cache',
                  executed: false,
                  refusal: 'ExceedsRecycleBinCapacity',
                  refusalMessage:
                    'This batch is 44.00 GiB but only 20.00 GiB will fit in the Recycle Bin. Windows would permanently destroy the overflow rather than fail, so nothing was deleted. Empty the Recycle Bin or clean this in smaller batches.',
                  itemsDeleted: 0,
                  itemsFailed: 0,
                  bytesDeleted: 0,
                  recycleBinAvailableBytes: Math.round(20 * GIB),
                  failures: [],
                },
          )
        }
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

        if (path === `/api/scans/${SCAN_ID}/treemap`) {
          // Zooming re-requests with ?path=. Resolving it against the same fixture tree the
          // root view came from is what makes navigation real: the folder you clicked is the
          // folder you get, with its own children, rather than a relabelled copy.
          const requested = new URLSearchParams(url.split('?')[1] ?? '').get('path')
          if (!requested) return send(res, projectMock(treemapRoot, 'C:\\'))

          const found = findMockNode(treemapRoot, 'C:\\', requested)
          return found
            ? send(res, projectMock(found, requested))
            : send(res, { message: 'No such scan or path.' }, 404)
        }
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
