/**
 * Typed client for the in-process Silt API.
 *
 * Requests go to the app's own origin and are intercepted by the shell's
 * WebResourceRequested handler. Nothing here ever crosses the network, and there is no
 * socket for anything else on the machine to connect to.
 */

export type Volume = {
  root: string
  label: string
  fileSystem: string
  capacityBytes: number
  freeBytes: number
  isReady: boolean
}

export type ScanState = 'running' | 'completed' | 'failed' | 'cancelled'

export type ScanStatus = {
  scanId: string
  state: ScanState
  root: string
  directoriesScanned: number
  filesScanned: number
  bytesScanned: number
  currentPath: string
  elapsedSeconds: number
  error?: string
}

export type TreeNode = {
  name: string
  path: string
  allocatedBytes: number
  logicalBytes: number
  fileCount: number
  directoryCount: number
  hasChildren: boolean
  conditions: string[]
}

export type TreeResponse = {
  path: string
  totalAllocatedBytes: number
  children: TreeNode[]
  totalChildCount: number
  truncated: boolean
}

// The treemap's wire types live with the layout code, since the short field names only make
// sense next to the reason for them. Re-exported here so callers still have one API module.
import type { TreemapResponse } from './treemap/layout'
export type { TreemapNodeDto, TreemapResponse } from './treemap/layout'

export type ReconciliationKind = 'Measured' | 'Known' | 'Unmeasured' | 'Unaccounted'

export type ReconciliationLine = {
  label: string
  bytes: number
  kind: ReconciliationKind
  detail: string
}

export type Reconciliation = {
  volumeRoot: string
  capacityBytes: number
  freeBytes: number
  usedBytes: number
  scannedBytes: number
  unaccountedBytes: number
  unaccountedFraction: number
  inaccessibleDirectoryCount: number
  lines: ReconciliationLine[]
}

export type ScanSummary = {
  scanId: string
  root: string
  durationSeconds: number
  totalFiles: number
  totalDirectories: number
  totalAllocatedBytes: number
  totalLogicalBytes: number
  accessDeniedCount: number
  failedCount: number
  skippedSurrogateCount: number
  hardLinkFilesDeduplicated: number
  hardLinkBytesDeduplicated: number
  reconciliation?: Reconciliation
}

export type AppLocationKind =
  | 'Install'
  | 'LocalData'
  | 'RoamingData'
  | 'PackageData'
  | 'MachineData'

export type AppLocation = {
  path: string
  allocatedBytes: number
  fileCount: number
  kind: AppLocationKind
}

export type AppFootprint = {
  key: string
  displayName: string
  publisher?: string
  totalAllocatedBytes: number
  totalFileCount: number
  isSplitAcrossLocations: boolean
  locations: AppLocation[]
}

export type AppsResponse = {
  apps: AppFootprint[]
  minimumBytes: number
  totalAttributedBytes: number
}

export type ChangeKind = 'Added' | 'Removed' | 'Grown' | 'Shrunk' | 'Unchanged'

export type DirectoryChange = {
  path: string
  beforeBytes: number
  afterBytes: number
  deltaBytes: number
  selfDeltaBytes: number
  kind: ChangeKind
}

export type AppChange = {
  key: string
  displayName: string
  beforeBytes: number
  afterBytes: number
  deltaBytes: number
  kind: ChangeKind
}

export type Growth = {
  available: boolean
  unavailable?: string
  fromTakenAt?: string
  toTakenAt?: string
  spanDays: number
  fromTotalBytes: number
  toTotalBytes: number
  deltaBytes: number
  freeDeltaBytes: number
  floorsDiffer: boolean
  snapshotCount: number
  directories: DirectoryChange[]
  apps: AppChange[]
}

export type SafetyTier = 'AlwaysSafe' | 'SafeWithCaveat' | 'RequiresReview'

export type PlanItem = {
  path: string
  allocatedBytes: number
  isDirectory: boolean
  lastWriteUtc: string
}

export type PlanExclusion = { path: string; reason: string }

export type RulePlan = {
  ruleId: string
  displayName: string
  description: string
  tier: SafetyTier
  regeneration: string
  regenerationCommand?: string
  totalAllocatedBytes: number
  totalFileCount: number
  itemCount: number
  exclusionCount: number
  topItems: PlanItem[]
  sampleExclusions: PlanExclusion[]
}

export type CleanupPlan = {
  planId: string
  createdAt: string
  totalAllocatedBytes: number
  totalFileCount: number
  totalItemCount: number
  rules: RulePlan[]
}

export type ExecutionResult = {
  operationId: string
  ruleId: string
  executed: boolean
  refusal: string
  refusalMessage?: string
  itemsDeleted: number
  itemsFailed: number
  bytesDeleted: number
  recycleBinAvailableBytes: number
  failures: { path: string; reason: string }[]
}

export type SafetyStatus = {
  healthy: boolean
  failures: { path: string; expectation: string }[]
}

class ApiError extends Error {
  // Declared and assigned explicitly rather than as a constructor parameter property:
  // the tsconfig enables `erasableSyntaxOnly`, which forbids TypeScript-only syntax that
  // has to emit runtime code.
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, init)
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`
    try {
      const body = (await response.json()) as { message?: string }
      if (body.message) {
        message = body.message
      }
    } catch {
      // Non-JSON error body; the status line is the best we have.
    }
    throw new ApiError(message, response.status)
  }
  return (await response.json()) as T
}

export const api = {
  listVolumes: () => request<Volume[]>('/api/volumes'),

  startScan: (root: string) =>
    request<{ scanId: string }>('/api/scans', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ root }),
    }),

  getStatus: (scanId: string) => request<ScanStatus>(`/api/scans/${scanId}`),

  getSummary: (scanId: string) => request<ScanSummary>(`/api/scans/${scanId}/summary`),

  getTree: (scanId: string, path?: string) =>
    request<TreeResponse>(
      `/api/scans/${scanId}/tree${path ? `?path=${encodeURIComponent(path)}` : ''}`,
    ),

  getTreemap: (scanId: string, path?: string) =>
    request<TreemapResponse>(
      `/api/scans/${scanId}/treemap${path ? `?path=${encodeURIComponent(path)}` : ''}`,
    ),

  getApps: (scanId: string, minimumBytes?: number) =>
    request<AppsResponse>(
      `/api/scans/${scanId}/apps${minimumBytes === undefined ? '' : `?min=${minimumBytes}`}`,
    ),

  getGrowth: (scanId: string, days = 7) =>
    request<Growth>(`/api/scans/${scanId}/growth?days=${days}`),

  getSafety: () => request<SafetyStatus>('/api/cleanup/safety'),

  createCleanupPlan: () => request<CleanupPlan>('/api/cleanup/plans', { method: 'POST' }),

  // Execution names a rule from an already-issued plan. There is no endpoint that accepts
  // paths, so nothing can be deleted that a dry run has not already shown the user.
  executeRule: (planId: string, ruleId: string) =>
    request<ExecutionResult>(`/api/cleanup/plans/${planId}/execute`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ruleId }),
    }),

  cancel: (scanId: string) =>
    request<{ cancelled: boolean }>(`/api/scans/${scanId}/cancel`, { method: 'POST' }),
}

export { ApiError }
