import { useCallback, useEffect, useState } from 'react'

export type Source = 'live' | 'claude-local' | 'history' | 'demo'
export type LocalSource = 'claude-local'
export const SOURCE_ORDER: Source[] = ['live', 'claude-local', 'history', 'demo']
export const isLocal = (source: Source): source is LocalSource => source === 'claude-local'
export const usageLabel = (source: Source) => isLocal(source) ? 'usage records' : 'requests'
export type ChartMetric = 'cost' | 'requests'
export type Range = '1h' | '24h' | '7d' | '30d' | '90d'
export const RANGE_SECONDS: Record<Range, number> = { '1h': 3600, '24h': 86400, '7d': 604800, '30d': 2592000, '90d': 7776000 }
export const SOURCES: Record<Source, { label: string; short: string; description: string }> = {
  'claude-local': { label: 'Claude history', short: 'Claude Code', description: 'Usage read from local Claude Code files: existing history and subsequent file updates. Kept separate from telemetry and manual imports, which may describe the same activity.' },
  live: { label: 'Live telemetry', short: 'Live', description: 'Claude Code request events received through OTLP. Enable telemetry in a new session to collect new requests; earlier sessions are not backfilled here.' },
  history: { label: 'Manual import', short: 'Imported', description: 'Usage manually imported from Claude Code JSONL files. Kept separate from telemetry and automatic history collection, which may overlap.' },
  demo: { label: 'Demo team', short: 'Demo', description: 'Synthetic requests for query benchmarks and repeatable demonstrations at a larger scale. These are not your usage.' },
}
export type Totals = { costUsdMicros: number; requests: number; inputTokens: number; outputTokens: number; cacheReadTokens: number; cacheCreationTokens: number }
export type Summary = { servedFrom: string; queryMs: number; totals: Totals; pricedRequests: number; unpricedRequests: number; syntheticRequests: number; windowStart: string; windowEnd: string }
export type Point = { bucket: string; model: string; costUsdMicros: number; requests: number }
export type Timeseries = { servedFrom: string; bucketSeconds: number; queryMs: number; points: Point[] }
export type Slice = { label: string; costUsdMicros: number; requests: number; pricedRequests?: number; unpricedRequests?: number }
export type Live = { minutes: Point[]; today: Slice; lastEventAt: string | null }
export type Session = { sessionId: string; developer: string; startedAt: string; endedAt: string; requests: number; costUsdMicros: number; pricedRequests?: number; unpricedRequests?: number }
export type Bench = { rawMs: number; rollupMs: number; rawRows: number; rollupRows: number; equivalent: boolean; matchingRows: number; samples: number; statistic: string; windowStart: string; windowEnd: string }
export type ImportPreview = {
  previewId: string; expiresAt: string; records: number; requests: number; duplicates: number; skipped: number; unknownModels: string[];
  estimatedCostUsdMicros: number; pricedRequests: number; unpricedRequests: number; priceSource: string;
  issues: string[]; inserted?: number | null; alreadyImported?: number | null; synthetic?: boolean;
}
export type Status = {
  database: { connected: boolean; postgresVersion?: string; timescaleVersion?: string }
  ingestion: { queued: number; accepted: number; persisted: number; duplicates: number; rejected: number; lastPersistedAt?: string; lastError?: string }
  sources: { source: Source; requests: number; pricedRequests: number; unpricedRequests: number; syntheticRequests: number; lastEventAt: string | null; agentRequests: number; skillRequests: number; mcpRequests: number }[]
  aggregates: { name: string; materializedOnly: boolean }[]
  chunks: { total: number; rowstore: number; columnstore: number }
  jobs: { procName: string; scheduleInterval: string; lastRunStatus: string }[]
  history: { maxBytes: number; priceSource: string; previewRequired: boolean }
  limitations: string[]
}
export type Resource<T> = { data: T | undefined; error: string | null; loading: boolean; updatedAt: Date | null; retry: () => void }
export type LocalUsageStatus = {
  enabled: boolean; scanning: boolean; initialScanComplete: boolean; pendingRefresh: boolean; pollSeconds: number;
  lastScanAt: string | null; lastPersistedAt: string | null; filesDiscovered: number; filesProcessed: number;
  failedFiles: number; inserted: number; updated: number; skippedRecords: number; lastError: string | null;
  clients: { source: LocalSource; available: boolean; files: number }[];
}

export async function getJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init)
  if (!response.ok) {
    // Do not display server exceptions: they can contain connection strings or private identifiers.
    throw new Error(response.status === 413 ? 'This file is too large. Choose a JSONL file under 10 MiB.' :
      response.status === 400 ? 'The request was not accepted. Check the input, or preview the file again.' :
      response.status === 429 ? 'The receiver is busy. Try again in a moment.' :
      response.status >= 500 ? 'The API could not complete this request. Check that the database is running.' :
      `Request failed (${response.status}). Please try again.`)
  }
  return response.json() as Promise<T>
}

export function usePolling<T>(url: string, intervalMs = 5000, refreshKey = 0, enabled = true): Resource<T> {
  const [state, setState] = useState<Omit<Resource<T>, 'retry'>>({ data: undefined, error: null, loading: true, updatedAt: null })
  const [attempt, setAttempt] = useState(0)
  const retry = useCallback(() => setAttempt(n => n + 1), [])
  useEffect(() => {
    if (!enabled) { setState(previous => ({ ...previous, loading: false })); return }
    const controller = new AbortController()
    let busy = false
    // Never present the previous source's numbers under a new source.
    setState({ data: undefined, error: null, loading: true, updatedAt: null })
    const load = async () => {
      if (busy || document.hidden) return
      busy = true
      try {
        const data = await getJson<T>(url, { signal: controller.signal })
        if (!controller.signal.aborted) setState({ data, error: null, loading: false, updatedAt: new Date() })
      } catch (error) {
        if (!controller.signal.aborted) setState(previous => ({ ...previous, error: error instanceof Error ? error.message : 'Connection unavailable.', loading: false }))
      } finally { busy = false }
    }
    void load()
    const id = window.setInterval(load, intervalMs)
    const onVisibility = () => { if (!document.hidden) void load() }
    document.addEventListener('visibilitychange', onVisibility)
    return () => { controller.abort(); window.clearInterval(id); document.removeEventListener('visibilitychange', onVisibility) }
  }, [url, intervalMs, refreshKey, attempt, enabled])
  return { ...state, retry }
}

export const usd = (micros: number) => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(micros / 1_000_000)
export const compact = (n: number) => Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 }).format(n)
export const number = (n: number) => Intl.NumberFormat('en-US').format(n)
export const ago = (iso: string | null) => {
  if (!iso) return 'No events yet'
  const seconds = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000))
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.round(seconds / 3600)}h ago`
  return `${Math.round(seconds / 86400)}d ago`
}
export const MODEL_FAMILIES = ['opus', 'sonnet', 'haiku', 'fable', 'other'] as const
export type ModelFamily = (typeof MODEL_FAMILIES)[number]
export const familyOf = (model: string): ModelFamily => MODEL_FAMILIES.find(f => f !== 'other' && model.toLowerCase().includes(f)) ?? 'other'
export const FAMILY_LABELS: Record<ModelFamily, string> = { opus: 'Opus', sonnet: 'Sonnet', haiku: 'Haiku', fable: 'Fable', other: 'Other / unknown' }
export function pivotByFamily(points: Point[], bucketSeconds: number, windowSeconds: number, metric: ChartMetric = 'cost') {
  const step = bucketSeconds * 1000
  const end = Math.floor(Date.now() / step) * step
  const start = Math.ceil((Date.now() - windowSeconds * 1000) / step) * step
  const rows = new Map<number, Record<string, number>>()
  const emptyRow = (t: number): Record<string, number> => Object.fromEntries([['t', t], ...MODEL_FAMILIES.map(f => [f, 0])])
  for (let t = start; t <= end; t += step) rows.set(t, emptyRow(t))
  for (const p of points) {
    const t = Math.floor(new Date(p.bucket).getTime() / step) * step
    const row = rows.get(t) ?? emptyRow(t)
    row[familyOf(p.model)] += metric === 'requests' ? p.requests : p.costUsdMicros / 1_000_000
    rows.set(t, row)
  }
  return [...rows.values()].sort((a, b) => a.t - b.t)
}
// Stable display aliases; raw values never appear in titles or accessibility labels.
export function alias(value: string, kind: string, privacy: boolean): string {
  if (!privacy) return value
  const neutral: Record<string, string> = { 'main thread': 'Unattributed', 'no skill': 'Not provided', 'no MCP': 'Not provided', unavailable: 'Not provided', unknown: 'Unknown', '': 'Not provided' }
  if (neutral[value] !== undefined) return neutral[value]
  let hash = 2166136261
  for (const char of `${kind}:${value}`) hash = Math.imul(hash ^ char.charCodeAt(0), 16777619) >>> 0
  return `${kind} ${hash.toString(16).slice(-4).toUpperCase()}`
}
