import { lazy, Suspense, useEffect, useState, type ReactNode } from 'react'
import {
  FAMILY_LABELS, MODEL_FAMILIES, RANGE_SECONDS, SOURCE_ORDER, SOURCES, ago, alias, compact, familyOf, getJson, isLocal, number, pivotByFamily, usd, usageLabel, usePolling,
  type Bench, type ChartMetric, type ImportPreview, type Live, type LocalUsageStatus, type Range, type Resource, type Session, type Slice, type Source, type Status, type Summary, type Timeseries,
} from './api'

export type IconName = 'grid' | 'data' | 'database' | 'play' | 'shield' | 'arrow' | 'refresh' | 'check' | 'copy' | 'activity' | 'upload' | 'terminal' | 'clock' | 'external'
export function Icon({ name, size = 20 }: { name: IconName; size?: number }) {
  const paths: Record<IconName, ReactNode> = {
    grid: <><rect x="3" y="3" width="7" height="7" rx="1.5" /><rect x="14" y="3" width="7" height="7" rx="1.5" /><rect x="3" y="14" width="7" height="7" rx="1.5" /><rect x="14" y="14" width="7" height="7" rx="1.5" /></>,
    data: <><path d="M4 7h16M4 12h16M4 17h16" /><circle cx="8" cy="7" r="2" /><circle cx="16" cy="12" r="2" /><circle cx="10" cy="17" r="2" /></>,
    database: <><ellipse cx="12" cy="5" rx="8" ry="3" /><path d="M4 5v14c0 4 16 4 16 0V5M4 12c0 4 16 4 16 0" /></>,
    play: <><rect x="3" y="4" width="18" height="16" rx="3" /><path d="m10 8 6 4-6 4Z" /></>,
    shield: <><path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6Z" /><path d="m8 12 3 3 5-6" /></>,
    arrow: <path d="M4 12h16m-6-6 6 6-6 6" />,
    refresh: <><path d="M20 7v5h-5M4 17v-5h5" /><path d="M6 7a7 7 0 0 1 12-2l2 3M4 16l2 3a7 7 0 0 0 12-2" /></>,
    check: <path d="m5 12 4 4L19 6" />,
    copy: <><rect x="8" y="8" width="12" height="13" rx="2" /><path d="M15 8V3H3v13h5" /></>,
    activity: <path d="M2 12h5l3-8 4 16 3-8h5" />,
    upload: <><path d="M12 16V3m-5 5 5-5 5 5M4 15v6h16v-6" /></>,
    terminal: <><rect x="2" y="3" width="20" height="18" rx="3" /><path d="m6 8 4 4-4 4m7 0h5" /></>,
    clock: <><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></>,
    external: <><path d="M14 3h7v7m0-7-11 11M10 3H3v18h18v-7" /></>,
  }
  return <svg aria-hidden="true" width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">{paths[name]}</svg>
}

export function Card({ title, eyebrow, right, children, className = '' }: { title?: string; eyebrow?: string; right?: ReactNode; children: ReactNode; className?: string }) {
  return <section className={`panel ${className}`}>
    {(title || right) && <div className="panel-heading"><div>{eyebrow && <div className="eyebrow">{eyebrow}</div>}{title && <h2>{title}</h2>}</div>{right}</div>}
    {children}
  </section>
}
export function CostNote() { return <span className="cost-note">Estimates, based on API pricing</span> }
export function ErrorNotice({ message, retry }: { message: string; retry?: () => void }) {
  return <div className="notice error" role="alert"><span>{message}</span>{retry && <button className="text-button" onClick={retry}>Retry <Icon name="refresh" size={14} /></button>}</div>
}
function Loading({ label = 'Loading usage…' }: { label?: string }) {
  return <div className="loading-state" role="status"><span className="spinner" />{label}</div>
}
export function ResourceState<T>({ resource, empty, children }: { resource: Resource<T>; empty?: boolean; children: ReactNode }) {
  if (resource.loading && !resource.data) return <Loading />
  if (resource.error && !resource.data) return <ErrorNotice message={resource.error} retry={resource.retry} />
  return <>{resource.error && <ErrorNotice message={`Showing the last response. ${resource.error}`} retry={resource.retry} />}{empty ? <div className="empty-state"><Icon name="activity" size={27} /><h3>No usage in this window</h3><p>Try a wider range, or add data in Data &amp; setup.</p></div> : children}</>
}
const modelColor = (family: string) => `var(--chart-${family})`
const tooltipStyle = {
  contentStyle: { background: 'var(--tooltip-bg)', border: '1px solid var(--tooltip-border)', borderRadius: 8, fontSize: 12, color: 'var(--tooltip-text)' },
  labelStyle: { color: 'var(--tooltip-muted)', marginBottom: 8 },
}
const formatTick = (range: Range) => (t: number) => {
  const d = new Date(t)
  if (range === '1h' || range === '24h') return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  if (range === '7d') return d.toLocaleDateString([], { weekday: 'short' })
  return d.toLocaleDateString([], { month: 'short', day: 'numeric' })
}
export type DataProps = { source: Source; range: Range; refreshKey: number; privacy: boolean; allUnpriced?: boolean }
const costValue = (row: { costUsdMicros: number; requests: number; pricedRequests?: number }, allUnpriced = false) => row.requests > 0 && (allUnpriced || row.pricedRequests === 0) ? 'Unpriced' : usd(row.costUsdMicros)

export function Dashboard({ source, range, refreshKey, privacy, status, localUsage, onSetup }: DataProps & { status?: Status; localUsage?: LocalUsageStatus; onSetup: () => void }) {
  const [chartMetric, setChartMetric] = useState<ChartMetric>('cost')
  const summary = usePolling<Summary>(`/api/summary?source=${source}&range=${range}`, 5000, refreshKey)
  const series = usePolling<Timeseries>(`/api/timeseries?source=${source}&range=${range}`, 5000, refreshKey)
  const t = summary.data?.totals
  const inputTokens = t ? t.inputTokens + t.cacheReadTokens + t.cacheCreationTokens : 0
  const cacheHitRate = inputTokens > 0 ? (t!.cacheReadTokens / inputTokens) * 100 : 0
  const sourceStatus = status?.sources.find(s => s.source === source)
  const syntheticRequests = Math.max(sourceStatus?.syntheticRequests ?? 0, summary.data?.syntheticRequests ?? 0)
  const allUnpriced = (t?.requests ?? 0) > 0 && summary.data?.pricedRequests === 0
  useEffect(() => { if (allUnpriced) setChartMetric('requests') }, [allUnpriced])
  const bucketLabel = series.data?.bucketSeconds === 60 ? 'minute' : series.data?.bucketSeconds === 3600 ? 'hour' : 'day'
  const context = {
    'claude-local': 'Your Claude Code history, updated as local files change.',
    live: 'New requests received from Claude Code telemetry.',
    history: 'Manually imported Claude Code session files.',
    demo: 'Synthetic data for benchmarks and repeatable demos. Not your usage.',
  }[source]
  const collectorStatus = !localUsage ? 'Checking collection' : !localUsage.enabled ? 'Collector disabled' : localUsage.pendingRefresh ? 'Updating totals' : localUsage.scanning ? 'Reading updates' : 'Following local files'
  if (t?.requests === 0 && !summary.error) {
    const hasOlderUsage = (sourceStatus?.requests ?? 0) > 0
    const emptyTitle = hasOlderUsage ? 'No usage in this time range' : {
      'claude-local': 'Bring your Claude history into view',
      live: 'Ready for your first Claude request',
      history: 'Import your existing usage',
      demo: 'No benchmark data yet',
    }[source]
    const emptyDescription = hasOlderUsage
      ? 'This source has records outside the selected window. Choose a wider range to see them.'
      : {
        'claude-local': 'Enable local collection to read existing session files and follow new usage as you work.',
        live: 'Connect Claude Code telemetry, then send a request. Its tokens and estimated cost will appear here after export.',
        history: 'Preview a Claude Code session file before importing its usage. Imported data stays separate from live telemetry.',
        demo: 'Generate sample requests in Data & setup when you want to compare queries at a larger scale.',
      }[source]
    return <div className="page-content"><div className={`dashboard-context ${source === 'demo' ? 'synthetic' : ''}`}><p>{context}</p></div><section className="empty-dashboard"><Icon name={hasOlderUsage ? 'clock' : 'activity'} size={32} /><h2>{emptyTitle}</h2><p>{emptyDescription}</p>{!hasOlderUsage && <button className="button primary" onClick={onSetup}>{source === 'live' ? 'Connect Claude Code' : 'Open Data & setup'}<Icon name="arrow" size={16} /></button>}</section></div>
  }
  return <div className="page-content">
    <div className={`dashboard-context ${source === 'demo' ? 'synthetic' : ''}`}>
      <p className="dataset-context">{context}</p>
      <button className="text-button" onClick={onSetup}>{isLocal(source) ? <><span className={`status-dot ${localUsage?.enabled ? '' : 'quiet'}`} />{collectorStatus}</> : <>Manage data <Icon name="arrow" size={15} /></>}</button>
    </div>
    {source === 'history' && syntheticRequests > 0 && <div className="notice warning">This dataset includes {number(syntheticRequests)} synthetic fixture requests. It is not exclusively personal usage.</div>}
    {summary.error && <ErrorNotice message={summary.data ? `Showing the last response. ${summary.error}` : summary.error} retry={summary.retry} />}
    <div className="metric-row dashboard-metrics" aria-label="Usage summary">
      <Metric label="Estimated cost" value={t ? allUnpriced ? 'Unpriced' : usd(t.costUsdMicros) : '—'} detail={<CostNote />} className="metric-featured" />
      <Metric label={isLocal(source) ? 'Usage records' : 'Requests'} value={t ? compact(t.requests) : '—'} detail={isLocal(source) ? 'From Claude Code session files' : 'Individual API request events'} />
      <Metric label="Input tokens" value={t ? compact(inputTokens) : '—'} detail="Includes cache reads and writes" />
      <Metric label="Output tokens" value={t ? compact(t.outputTokens) : '—'} detail="Generated by Claude" />
    </div>
    {(summary.data?.unpricedRequests ?? 0) > 0 && <div className="notice warning">{number(summary.data!.unpricedRequests)} {usageLabel(source)} have no known price. {allUnpriced ? 'Token counts and usage remain visible.' : 'The estimate shows known cost only, not the full cost.'}</div>}
    <div className="dashboard-analysis">
      <Card title="Usage over time" className="usage-chart" right={<div className="small-tabs chart-metric" role="group" aria-label="Chart metric"><button className={chartMetric === 'cost' ? 'selected' : ''} aria-pressed={chartMetric === 'cost'} disabled={allUnpriced} title={allUnpriced ? 'No priced usage in this window' : 'Estimated cost'} onClick={() => setChartMetric('cost')}>Cost</button><button className={chartMetric === 'requests' ? 'selected' : ''} aria-pressed={chartMetric === 'requests'} onClick={() => setChartMetric('requests')}>{isLocal(source) ? 'Records' : 'Requests'}</button></div>}>
        <div className="chart-heading"><span className="subtle">{chartMetric === 'cost' ? 'Estimated USD' : isLocal(source) ? 'Usage records' : 'Requests'} per {bucketLabel}</span>{chartMetric === 'cost' && <CostNote />}</div>
        <ResourceState resource={series} empty={series.data?.points.length === 0}>
          <SpendChart series={series.data} range={range} metric={chartMetric} source={source} />
        </ResourceState>
        <div className="chart-footer"><div className="legend">{MODEL_FAMILIES.filter(f => series.data?.points.some(p => familyOf(p.model) === f)).map(f => <span key={f}><i style={{ background: modelColor(f) }} />{FAMILY_LABELS[f]}</span>)}</div>{t && inputTokens > 0 && <span className="subtle" title="Cache read tokens divided by all input tokens, including cache reads and writes">{cacheHitRate.toFixed(1)}% cache hit rate</span>}</div>
      </Card>
      <Breakdown source={source} range={range} refreshKey={refreshKey} privacy={privacy} allUnpriced={allUnpriced} />
    </div>
    <Sessions source={source} range={range} refreshKey={refreshKey} privacy={privacy} allUnpriced={allUnpriced} />
    <LiveStrip key={source} source={source} refreshKey={refreshKey} />
    <details className="dashboard-details"><summary>About this data</summary><p>{SOURCES[source].description}</p>{isLocal(source) && <p>Local files are checked every {localUsage?.pollSeconds ?? 5} seconds. Updates appear when Claude Code writes usage; this is not a per-token stream.</p>}<p>Visible dashboard views refresh every 5 seconds. Dollar amounts are API-equivalent estimates, not your subscription bill.</p><div className="query-detail-grid"><div><span>Summary query</span><code>{summary.data?.servedFrom ?? 'Waiting for data'}</code><span>{summary.data ? `${summary.data.queryMs.toFixed(1)} ms` : '—'}</span></div><div><span>Chart query</span><code>{series.data?.servedFrom ?? 'Waiting for data'}</code><span>{series.data ? `${series.data.queryMs.toFixed(1)} ms` : '—'}</span></div></div><p>Query timings describe the reported database queries, not full dashboard load time. Price coverage and session details also read raw events.</p></details>
  </div>
}

function Metric({ label, value, detail, className = '' }: { label: string; value: string; detail: ReactNode; className?: string }) {
  return <div className={`metric ${className}`}><span className="metric-label">{label}</span><strong>{value}</strong><span className="subtle metric-detail">{detail}</span></div>
}
type SpendChartProps = { series?: Timeseries; range: Range; metric: ChartMetric; source: Source }
function SpendChart(props: SpendChartProps) {
  return <Suspense fallback={<Loading label="Loading chart…" />}><LazySpendChart {...props} /></Suspense>
}
const LazySpendChart = lazy(async () => {
  const { Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } = await import('recharts')
  return { default: function ModelChart({ series, range, metric, source }: SpendChartProps) {
  const data = series ? pivotByFamily(series.points, series.bucketSeconds, RANGE_SECONDS[range], metric) : []
  return <div className="main-chart" role="img" aria-label={`${metric === 'cost' ? 'Estimated cost' : usageLabel(source)} by model over ${range}`}>
    <ResponsiveContainer width="100%" height="100%">
      <AreaChart data={data} margin={{ top: 15, right: 8, left: -12, bottom: 0 }}>
        <CartesianGrid stroke="var(--chart-grid)" vertical={false} />
        <XAxis dataKey="t" type="number" scale="time" domain={['dataMin', 'dataMax']} tickFormatter={formatTick(range)} tick={{ fill: 'var(--chart-axis)', fontSize: 12 }} axisLine={false} tickLine={false} minTickGap={35} />
        <YAxis tickFormatter={v => `${metric === 'cost' ? '$' : ''}${compact(Number(v))}`} allowDecimals={metric === 'cost'} tick={{ fill: 'var(--chart-axis)', fontSize: 12 }} axisLine={false} tickLine={false} width={58} />
        <Tooltip {...tooltipStyle} labelFormatter={t => new Date(Number(t)).toLocaleString()} formatter={(v, name) => [metric === 'cost' ? `${usd(Number(v) * 1_000_000)} estimated` : `${number(Number(v))} ${usageLabel(source)}`, FAMILY_LABELS[name as keyof typeof FAMILY_LABELS] ?? String(name)]} />
        {MODEL_FAMILIES.filter(f => series?.points.some(point => familyOf(point.model) === f)).map(f => <Area key={f} type="linear" dataKey={f} stackId="usage" stroke={modelColor(f)} fill={modelColor(f)} fillOpacity={0.35} strokeWidth={1.5} isAnimationActive={false} />)}
      </AreaChart>
    </ResponsiveContainer>
  </div>
  } }
})

const LazyActivityChart = lazy(async () => {
  const { Bar, BarChart, ResponsiveContainer, Tooltip } = await import('recharts')
  return { default: function ActivityChart({ minutes, metric, source }: { minutes: Record<string, number>[]; metric: ChartMetric; source: Source }) {
    return <ResponsiveContainer width="100%" height="100%"><BarChart data={minutes}><Tooltip {...tooltipStyle} labelFormatter={(_, payload) => payload?.[0]?.payload?.t ? formatTick('1h')(Number(payload[0].payload.t)) : ''} formatter={v => metric === 'cost' ? `${usd(Number(v) * 1_000_000)} estimated` : `${number(Number(v))} ${usageLabel(source)}`} cursor={{ fill: 'var(--chart-cursor)' }} />{MODEL_FAMILIES.map(f => <Bar key={f} dataKey={f} stackId="usage" fill={modelColor(f)} isAnimationActive={false} />)}</BarChart></ResponsiveContainer>
  } }
})

export function LiveStrip({ source = 'live', refreshKey = 0 }: { source?: Source; refreshKey?: number }) {
  const live = usePolling<Live>(`/api/live?source=${source}`, 5000, refreshKey)
  const fresh = !!live.data?.lastEventAt && Date.now() - new Date(live.data.lastEventAt).getTime() < 30_000
  const unpriced = (live.data?.today.requests ?? 0) > 0 && live.data?.today.pricedRequests === 0
  const metric: ChartMetric = isLocal(source) || unpriced ? 'requests' : 'cost'
  const minutes = live.data ? pivotByFamily(live.data.minutes, 60, 3600, metric) : []
  return <section className="live-strip">
    <div className="live-label"><span className={`status-dot ${fresh ? 'active' : 'quiet'}`} /><strong>Recent activity</strong><span className="subtle">{SOURCES[source].label}{source === 'demo' ? ' · synthetic' : ''}</span></div>
    <div className="live-cost"><strong>{live.loading || (!live.data && live.error) ? '—' : unpriced ? 'Unpriced' : usd(live.data?.today.costUsdMicros ?? 0)}</strong><span className="subtle">Today · UTC</span><CostNote />{!unpriced && (live.data?.today.unpricedRequests ?? 0) > 0 && <span className="small-warning">Known cost only</span>}</div>
    <div className="live-spark" role="img" aria-label={`${SOURCES[source].label}: ${metric === 'requests' ? usageLabel(source) : 'estimated cost'} per minute, last hour`}>{live.data && (live.data.minutes.length > 0 ? <Suspense fallback={<span className="spinner" />}><LazyActivityChart minutes={minutes} metric={metric} source={source} /></Suspense> : <span className="subtle">No activity this hour</span>)}</div>
    <div className="live-status"><strong>{fresh ? 'Recent usage recorded' : live.data?.lastEventAt ? `Last activity ${ago(live.data.lastEventAt)}` : 'No usage recorded yet'}</strong><span className="subtle">{live.error ? 'API connection interrupted' : `${compact(live.data?.today.requests ?? 0)} ${usageLabel(source)} today · last hour shown`}</span>{live.error && <button className="text-button" onClick={live.retry}>Retry connection</button>}</div>
  </section>
}

const DIMENSIONS = [{ key: 'model', label: 'Model', alias: 'Model' }, { key: 'agent', label: 'Subagent', alias: 'Agent' }, { key: 'skill', label: 'Skill', alias: 'Skill' }, { key: 'mcp', label: 'MCP', alias: 'MCP' }, { key: 'developer', label: 'Developer', alias: 'Developer' }, { key: 'query', label: 'Request type', alias: 'Request type' }] as const
export function Breakdown({ source, range, refreshKey, privacy, allUnpriced = false }: DataProps) {
  const [by, setBy] = useState<(typeof DIMENSIONS)[number]['key']>('model')
  const data = usePolling<Slice[]>(`/api/breakdown?source=${source}&range=${range}&by=${by}&metric=${allUnpriced ? 'requests' : 'cost'}`, 5000, refreshKey)
  const max = Math.max(1, ...(data.data ?? []).map(s => allUnpriced ? s.requests : s.costUsdMicros))
  return <Card title="Breakdown" className="model-breakdown" right={<select className="dimension-select" aria-label="Breakdown dimension" value={by} onChange={event => setBy(event.target.value as typeof by)}>{DIMENSIONS.map(d => <option key={d.key} value={d.key}>{d.label}</option>)}</select>}>
    <div className="breakdown-controls"><span className="subtle">{allUnpriced ? 'Ranked by usage' : 'Ranked by estimated cost'}</span>{!allUnpriced && <CostNote />}</div>
    <ResourceState resource={data} empty={data.data?.length === 0}><ol className="breakdown-list model-list">{data.data?.map(s => <li className="model-row" key={s.label}><div className="breakdown-label"><span className="model-name">{by === 'model' ? s.label : alias(s.label, DIMENSIONS.find(d => d.key === by)!.alias, privacy)}</span><strong>{allUnpriced ? number(s.requests) : costValue(s)}</strong></div><div className="bar-track"><div style={{ width: `${((allUnpriced ? s.requests : s.costUsdMicros) / max) * 100}%`, background: by === 'model' ? modelColor(familyOf(s.label)) : undefined }} /></div><span className="row-detail">{number(s.requests)} {usageLabel(source)}{!allUnpriced && (s.unpricedRequests ?? 0) > 0 ? ` · ${number(s.unpricedRequests!)} unpriced` : ''}</span></li>)}</ol></ResourceState>
    <p className="panel-footnote">{by === 'model' ? 'Up to 8 models in this window. Colors match the model families in the chart.' : 'Attribution is optional. Missing names are shown as unattributed or not provided; local files may not identify every tool or agent.'}</p>
  </Card>
}
export function Sessions({ source, range, refreshKey, privacy, allUnpriced = false }: DataProps) {
  const sessions = usePolling<Session[]>(`/api/sessions?source=${source}&range=${range}&metric=${allUnpriced ? 'requests' : 'cost'}`, 5000, refreshKey)
  return <Card title={allUnpriced ? 'Top sessions by usage' : 'Top sessions by cost'} className="sessions-panel" right={<CostNote />}>
    <ResourceState resource={sessions} empty={sessions.data?.length === 0}><div className="table-scroll"><table className="data-table"><thead><tr><th>Session</th><th>Developer</th><th>Started</th><th>{isLocal(source) ? 'Records' : 'Requests'}</th><th>Est. cost</th></tr></thead><tbody>{sessions.data?.map(s => <tr key={`${s.sessionId}:${s.developer}`}><td><span className="table-main">{alias(s.sessionId, 'Session', privacy)}</span></td><td>{alias(s.developer, 'Developer', privacy)}</td><td>{new Date(s.startedAt).toLocaleDateString([], { month: 'short', day: 'numeric' })}</td><td>{number(s.requests)}</td><td className="table-cost">{costValue(s, allUnpriced)}{!allUnpriced && (s.unpricedRequests ?? 0) > 0 && s.pricedRequests !== 0 && <span className="table-sub">Partial cost</span>}</td></tr>)}</tbody></table></div></ResourceState>
    <p className="panel-footnote">{privacy ? 'Identifiers are hidden. Session and developer names use stable aliases.' : 'Identifiers are visible. Original session and developer values are shown.'} Sessions use retained raw events; older rollup totals may contain more history.</p>
  </Card>
}

export function CopyBlock({ label, code }: { label: string; code: string }) {
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState(false)
  return <div className="copy-block"><div><span>{label}</span><button className="text-button" onClick={async () => { try { await navigator.clipboard.writeText(code); setCopied(true); window.setTimeout(() => setCopied(false), 2000) } catch { setError(true) } }}><Icon name={copied ? 'check' : 'copy'} size={14} />{copied ? 'Copied' : 'Copy'}</button></div><pre><code>{code}</code></pre>{error && <span className="small-warning">Copy unavailable. Select the command to copy it.</span>}</div>
}

export function LocalCollection({ resource, refresh, onView }: { resource: Resource<LocalUsageStatus>; refresh: () => void; onView: (source: Source) => void }) {
  const [syncing, setSyncing] = useState(false)
  const [syncError, setSyncError] = useState<string | null>(null)
  const [syncMessage, setSyncMessage] = useState<string | null>(null)
  const s = resource.data
  const mode = resource.loading ? 'Checking collector' : resource.error && !s ? 'Status unavailable' : !s?.enabled ? 'Disabled' : s.pendingRefresh ? 'Refreshing totals' : s.scanning ? (s.initialScanComplete ? 'Reading updates' : 'Importing history') : s.initialScanComplete ? 'Following updates' : 'Waiting for first scan'
  const sync = async () => {
    setSyncing(true); setSyncError(null); setSyncMessage(null)
    try {
      const result = await getJson<{ accepted: boolean }>('/api/local-usage/sync', { method: 'POST' })
      if (!result.accepted) throw new Error('Local collection is disabled. Enable the collector and its read-only folder mount in this deployment, then try again.')
      setSyncMessage('Sync requested. Collection status and dashboard totals refresh automatically.')
      resource.retry(); refresh()
    } catch (e) { setSyncError(e instanceof Error ? e.message : 'Sync could not start.') } finally { setSyncing(false) }
  }
  return <Card title="Bring in Claude history" eyebrow="OPTIONAL / LOCAL FILES" className="local-collection" right={<span className={`badge ${s?.enabled ? 'teal' : ''}`}>{mode}</span>}>
    <p className="body-copy">Backfill existing Claude Code usage, then optionally follow new file updates while the app runs. Claude history stays separate from Live telemetry and Manual import because they may describe the same activity.</p>
    <ResourceState resource={resource}>
      <div className="collector-clients">{(['claude-local'] as const).map(source => {
        const client = s?.clients.find(c => c.source === source)
        return <div className="collector-client" key={source}><div><span className={`status-dot ${s?.enabled && client?.available ? '' : 'quiet'}`} /><strong>{SOURCES[source].short}</strong><span className="badge">{!s?.enabled ? 'Not enabled' : client?.available ? 'Available' : 'Folder unavailable'}</span></div><p>{number(client?.files ?? 0)} files discovered · read-only access</p><button className="text-button" onClick={() => onView(source)}>Open Claude history <Icon name="arrow" size={14} /></button></div>
      })}</div>
      {s?.enabled && <><div className="collector-stats"><div><strong>{number(s.filesProcessed)} / {number(s.filesDiscovered)}</strong><span>files processed / discovered</span></div><div><strong>{number(s.inserted)}</strong><span>usage records inserted</span></div><div><strong>{number(s.updated)}</strong><span>usage records updated</span></div><div><strong>{number(s.skippedRecords)}</strong><span>records skipped</span></div><div><strong>{number(s.failedFiles)}</strong><span>files with errors</span></div></div><div className="collector-timing"><span>Last scan: {ago(s.lastScanAt)}</span><span>Last write: {ago(s.lastPersistedAt)}</span><span>{s.initialScanComplete ? 'Initial import complete' : 'Initial import not yet complete'}</span></div></>}
      {s?.pendingRefresh && <div className="notice warning">Imported usage is being rolled up. Dashboard totals may still be catching up.</div>}
      {(s?.lastError || (s?.failedFiles ?? 0) > 0) && <div className="notice warning">Some local files could not be processed. Check the local API log for details; file paths are kept out of this view.</div>}
      {s?.enabled && <p className="collector-counter-note">Skipped records include normal metadata, non-usage lines, unchanged duplicates and unsupported entries. This is not an error count; read failures are reported separately. File progress describes the current scan, while usage counters resume from saved checkpoints.</p>}
    </ResourceState>
    {!resource.loading && !s?.enabled && <>
      <CopyBlock label="Bundled local Compose setup · PowerShell, from the TokenScope folder" code={'.\\scripts\\start.ps1 -LocalUsage'} />
      <p className="body-copy">This helper starts the app and local PostgreSQL with a read-only Claude projects mount. Configuration changes recreate the app. For an existing deployment, enable the collector and mount there, preserving its database connection.</p>
    </>}
    {syncError && <ErrorNotice message={syncError} retry={() => void sync()} />}
    {syncMessage && <div className="notice success" role="status">{syncMessage}</div>}
    <div className="collector-actions"><button className="button primary" disabled={!s?.enabled || s.scanning || syncing} onClick={() => void sync()}>{syncing || s?.scanning ? <><span className="spinner" />{syncing ? 'Requesting sync…' : 'Scanning…'}</> : <><Icon name="refresh" size={16} />Sync now</>}</button><p>Checks files every {s?.pollSeconds ?? 5}s while the app is running. Visible dashboard views refresh every 5s. Activity appears when Claude Code writes usage; this is not a per-token stream.</p></div>
    <p className="panel-footnote">Usage metadata is saved to this app’s configured database, including when it is hosted in the cloud. Source files are mounted read-only; prompts, responses and tool contents are not retained. Local files may not include every tool or agent attribution. Costs use current standard API rates, not your subscription bill or historical invoice. Unknown models remain unpriced.</p>
  </Card>
}

function LiveTelemetrySetup({ onView }: { onView: (source: Source) => void }) {
  const origin = import.meta.env.DEV ? 'http://localhost:5299' : window.location.origin
  const configuration = JSON.stringify({ env: {
    CLAUDE_CODE_ENABLE_TELEMETRY: '1',
    OTEL_LOGS_EXPORTER: 'otlp',
    OTEL_METRICS_EXPORTER: 'none',
    OTEL_TRACES_EXPORTER: 'none',
    OTEL_LOG_USER_PROMPTS: '0',
    OTEL_LOG_ASSISTANT_RESPONSES: '0',
    OTEL_LOG_TOOL_DETAILS: '0',
    OTEL_LOG_TOOL_CONTENT: '0',
    OTEL_LOG_RAW_API_BODIES: '0',
    OTEL_EXPORTER_OTLP_LOGS_PROTOCOL: 'http/json',
    OTEL_EXPORTER_OTLP_LOGS_ENDPOINT: `${origin}/v1/logs`,
  } }, null, 2)
  return <Card title="Connect Claude Code telemetry" eyebrow="START HERE / NEW REQUESTS" className="telemetry-setup" right={<span className="badge teal">Recommended</span>}>
    <p className="body-copy">Send new request events from Claude Code to this TokenScope instance. Save the settings below as <code>claude-telemetry.json</code>, then launch a new session with that file.</p>
    <div className="telemetry-grid"><CopyBlock label="claude-telemetry.json" code={configuration} /><div><CopyBlock label="Launch a new Claude Code session" code="claude --settings ./claude-telemetry.json" /><p className="body-copy">Run a request in that session, then check Live telemetry. Events appear after the exporter flushes; the dashboard refreshes every 5 seconds while visible.</p><button className="button primary" onClick={() => onView('live')}>Open live telemetry <Icon name="arrow" size={16} /></button></div></div>
    <p className="panel-footnote">This collects new events only. Agent, skill and MCP attribution appears only when those fields are emitted. Telemetry and local files may overlap, so each dataset is queried separately. <CostNote /></p>
  </Card>
}

export function DataSetup({ privacy, status, localUsage, refresh, onView }: { privacy: boolean; status?: Status; localUsage: Resource<LocalUsageStatus>; refresh: () => void; onView: (source: Source) => void }) {
  const [file, setFile] = useState<File>()
  const [preview, setPreview] = useState<ImportPreview>()
  const [busy, setBusy] = useState<'preview' | 'import' | 'seed' | 'pulse' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [developers, setDevelopers] = useState(25)
  const [days, setDays] = useState(90)
  const act = async (kind: NonNullable<typeof busy>, fn: () => Promise<void>) => {
    setBusy(kind); setError(null); setMessage(null)
    try { await fn() } catch (e) { setError(e instanceof Error ? e.message : 'The action could not complete.') } finally { setBusy(null) }
  }
  const maxBytes = status?.history.maxBytes ?? 10 * 1024 * 1024
  return <div className="page-content">
    <div className="section-intro"><span className="eyebrow">CLAUDE CODE USAGE</span><h2>Start with live telemetry.</h2><p>Connect new Claude Code requests, then add existing history if you need it. Every dashboard query stays within one source; overlapping datasets are never added together.</p></div>
    <LiveTelemetrySetup onView={onView} />
    <LocalCollection resource={localUsage} refresh={refresh} onView={onView} />
    <div className="source-inventory">{SOURCE_ORDER.map(source => {
      const data = status?.sources.find(s => s.source === source)
      return <button className={`inventory-item ${source}`} key={source} onClick={() => onView(source)}><span className="eyebrow">{SOURCES[source].label}</span><strong>{data ? compact(data.requests) : '—'} <small>{isLocal(source) ? 'records' : 'requests'}</small></strong><span className="subtle">{source === 'demo' ? 'Synthetic · for demonstration' : source === 'history' ? 'Manual imports · kept separate' : `Last activity: ${ago(data?.lastEventAt ?? null)}`}</span><Icon name="arrow" size={17} /></button>
    })}</div>
    {error && <ErrorNotice message={error} />}
    {message && <div className="notice success" role="status"><Icon name="check" size={18} />{message}</div>}
    <div className="setup-grid">
      <Card title="Import a session file" eyebrow="OPTIONAL / MANUAL IMPORT" className="import-panel" right={<span className="badge">JSONL</span>}>
        <p className="body-copy">Preview a Claude Code session file before importing it into the separate Manual import dataset. Automatic history collection already includes these files when enabled; do not add the two datasets together.</p>
        <label className="file-drop"><Icon name="upload" size={26} /><strong>{file ? (privacy ? 'Session file selected' : file.name) : 'Choose a session file'}</strong><span>{file ? `${(file.size / 1024).toFixed(1)} KiB · click to choose another` : `JSONL · up to ${Math.round(maxBytes / 1048576)} MiB per file`}</span><input type="file" accept=".jsonl,application/x-ndjson" aria-label="Choose Claude Code JSONL history file" disabled={!!busy} onChange={event => { setFile(event.target.files?.[0]); setPreview(undefined); setError(null); setMessage(null) }} /></label>
        <p className="subtle file-hint">Look in <code>~/.claude/projects/</code> for a session’s <code>.jsonl</code> file. The preview uploads it to this TokenScope instance; only allowlisted usage fields are retained.</p>
        <button className="button primary" disabled={!file || !!busy} onClick={() => void act('preview', async () => {
          if (!file) return
          if (file.size > maxBytes) throw new Error(`Choose a file smaller than ${Math.round(maxBytes / 1048576)} MiB.`)
          setPreview(await getJson<ImportPreview>('/api/history/preview', { method: 'POST', headers: { 'Content-Type': 'application/x-ndjson' }, body: file }))
        })}>{busy === 'preview' ? <><span className="spinner" />Reading usage…</> : <><Icon name="upload" size={16} />Preview import</>}</button>
        {preview && <div className="import-preview">
          <div className="preview-heading"><h3>{preview.inserted != null ? 'Import complete' : 'Review this import'}</h3><span className={`badge ${preview.synthetic ? 'orange' : ''}`}>{preview.synthetic ? 'Synthetic fixture' : 'Imported history'}</span></div>
          <div className="preview-metrics"><div><strong>{number(preview.requests)}</strong><span>requests found</span></div><div><strong>{number(preview.duplicates)}</strong><span>duplicates skipped</span></div><div><strong>{preview.requests > 0 && preview.pricedRequests === 0 ? 'Unpriced' : usd(preview.estimatedCostUsdMicros)}</strong><span>estimated cost</span></div></div>
          <CostNote />
          <p className="subtle">{number(preview.records)} records read · {number(preview.skipped)} skipped · {number(preview.pricedRequests)} priced · {number(preview.unpricedRequests)} without a known price.</p>
          {preview.unpricedRequests > 0 && <div className="notice warning">Some requests have no known model price. Their tokens are retained, but cost totals will be incomplete.</div>}
          {preview.unknownModels.length > 0 && <p className="subtle">Unpriced models: {preview.unknownModels.join(', ')}</p>}
          <p className="subtle">Price source: {preview.priceSource}</p>
          {preview.issues.length > 0 && <ul className="issues">{preview.issues.slice(0, 8).map((issue, i) => <li key={i}>{issue}</li>)}</ul>}
          {preview.inserted == null ? <div className="action-row"><button className="button primary" disabled={!!busy || preview.requests === 0} onClick={() => void act('import', async () => {
            const result = await getJson<ImportPreview>('/api/history/import', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ previewId: preview.previewId }) })
            setPreview(result); refresh(); setMessage(`${number(result.inserted ?? 0)} requests imported. ${number(result.alreadyImported ?? 0)} already stored requests skipped.`)
          })}>{busy === 'import' ? 'Importing…' : `Import ${number(preview.requests)} requests`}</button><span className="subtle">Preview expires after 10 minutes.</span></div> : <button className="button secondary" onClick={() => onView('history')}>Open imported usage <Icon name="arrow" size={16} /></button>}
        </div>}
      </Card>
      <div className="setup-side">
        <Card title="Ask Claude about usage" eyebrow="OPTIONAL / READ-ONLY MCP">
          <CopyBlock label="Connect TokenScope" code={`claude mcp add --transport http tokenscope ${import.meta.env.DEV ? 'http://localhost:5299' : window.location.origin}/mcp`} />
          <CopyBlock label="Try this prompt" code="Using TokenScope, compare model usage and estimated API costs in my Claude history over the last 30 days. Which days were busiest? State the data source, note any unpriced usage, and distinguish evidence from inference." />
        </Card>
      </div>
    </div>
    <details className="sample-data-details"><summary><span>Sample data for benchmarks</span><span className="badge">Optional · synthetic</span></summary>
      <div className="demo-generator"><p className="body-copy">Use a controlled dataset to compare raw queries with rollups at a larger scale, or try an MCP investigation with known model and subagent attribution. Your own Claude history is enough for the dashboard; sample data is not needed to use it.</p><p className="body-copy">The generator creates a simulated team with a deliberate spend spike. Generating again replaces only the Demo team dataset. A pulse adds a synthetic event now to test live updates.</p><div className="form-row"><label>Developers<input type="number" min="1" max="25" value={developers} onChange={e => setDevelopers(Math.max(1, Math.min(25, Number(e.target.value))))} /></label><label>Days of history<input type="number" min="7" max="90" value={days} onChange={e => setDays(Math.max(7, Math.min(90, Number(e.target.value))))} /></label><button className="button primary" disabled={!!busy} onClick={() => void act('seed', async () => { await getJson('/api/demo/seed?developers=' + developers + '&days=' + days, { method: 'POST' }); refresh(); setMessage('Sample data generated in Demo team. Your Claude history and telemetry are unchanged.') })}>{busy === 'seed' ? <><span className="spinner" />Generating…</> : 'Generate demo team'}</button><button className="button secondary" disabled={!!busy} onClick={() => void act('pulse', async () => { await getJson('/api/demo/pulse', { method: 'POST' }); refresh(); setMessage('Synthetic pulse added to Demo team. Select the 1h range to see it.') })}>{busy === 'pulse' ? 'Adding pulse…' : 'Send demo pulse'}<Icon name="activity" size={16} /></button></div><p className="panel-footnote">Synthetic requests stay in Demo team. They are never added to your Claude history, manual imports or live telemetry.</p></div>
    </details>
  </div>
}

export function DatabaseLab({ source, status }: { source: Source; status: Resource<Status> }) {
  const s = status.data
  return <div className="page-content">
    <div className="section-intro"><span className="eyebrow">UNDER THE DASHBOARD</span><h2>Follow the data through Postgres.</h2><p>Usage records feed continuous aggregates. Inspect the database state and measure the same question against two query paths.</p></div>
    <ResourceState resource={status}>
      <div className="database-banner"><Icon name="database" size={30} /><div><strong>{s?.database.connected ? 'Database connected' : 'Database not connected'}</strong><span className="subtle">PostgreSQL {s?.database.postgresVersion?.split(' ')[0] ?? '—'} · TimescaleDB {s?.database.timescaleVersion ?? '—'}</span></div><span className={`badge ${s?.database.connected ? 'teal' : 'orange'}`}>{s?.database.connected ? 'Healthy connection' : 'Check database'}</span></div>
      <div className="pipeline"><div><span className="eyebrow">INGEST</span><strong>Usage records</strong><span>Client files / OTLP / import</span></div><Icon name="arrow" /><div><span className="eyebrow">STORE</span><strong>api_requests</strong><span>Time-partitioned hypertable</span></div><Icon name="arrow" /><div><span className="eyebrow">ROLL UP</span><strong>Raw → minute &amp; hour<br />Hour → day</strong><span>Continuous aggregates</span></div><Icon name="arrow" /><div><span className="eyebrow">EXPLORE</span><strong>Dashboard + MCP</strong><span>Source-filtered queries</span></div></div>
      <div className="two-column">
        <Card title="Ingestion" eyebrow="THIS API PROCESS" right={<span className="badge">{s?.ingestion.queued ?? 0} queued</span>}>
          <div className="stat-grid"><Metric label="Accepted" value={number(s?.ingestion.accepted ?? 0)} detail="Events accepted" /><Metric label="Persisted" value={number(s?.ingestion.persisted ?? 0)} detail="Written to database" /><Metric label="Duplicates" value={number(s?.ingestion.duplicates ?? 0)} detail="Skipped on ingestion" /><Metric label="Rejected" value={number(s?.ingestion.rejected ?? 0)} detail="Not accepted" /></div>
          <p className="panel-footnote">Last persisted event: {ago(s?.ingestion.lastPersistedAt ?? null)}. These counters reset when the API restarts.</p>
          {s?.ingestion.lastError && <div className="notice warning">The ingestion worker reported an error. Check the API log for details.</div>}
        </Card>
        <Card title="Storage & aggregates" eyebrow="DATABASE STATE">
          <div className="chunk-summary"><div><strong>{s?.chunks.total ?? '—'}</strong><span>chunks</span></div><div><i className="chunk-swatch" /><strong>{s?.chunks.rowstore ?? '—'}</strong><span>rowstore</span></div><div><i className="chunk-swatch columnstore" /><strong>{s?.chunks.columnstore ?? '—'}</strong><span>columnstore</span></div></div>
          <div className="chunk-map" aria-label={`${s?.chunks.rowstore ?? 0} rowstore chunks and ${s?.chunks.columnstore ?? 0} columnstore chunks`}>{Array.from({ length: Math.min(120, s?.chunks.total ?? 0) }, (_, i) => <span key={i} className={i < (s?.chunks.columnstore ?? 0) ? 'compressed' : ''} />)}</div>
          {(s?.chunks.total ?? 0) > 120 && <p className="subtle">Showing 120 of {number(s!.chunks.total)} chunks.</p>}
          <div className="aggregate-list">{s?.aggregates.map(a => <div key={a.name}><code>{a.name}</code><span className={`badge ${a.materializedOnly ? '' : 'teal'}`}>{a.materializedOnly ? 'Materialized only' : 'Real-time enabled'}</span></div>)}</div>
          <p className="panel-footnote">Columnstore chunk counts describe storage state. They do not measure compression savings.</p>
        </Card>
      </div>
      <BenchCard key={source} source={source} />
      <Card title="Background jobs" eyebrow="POLICIES"><div className="table-scroll"><table className="data-table jobs-table"><thead><tr><th>Procedure</th><th>Schedule</th><th>Last run</th></tr></thead><tbody>{s?.jobs.map((job, i) => <tr key={`${job.procName}:${i}`}><td><code>{job.procName}</code></td><td>{job.scheduleInterval}</td><td>{job.lastRunStatus || 'Not run yet'}</td></tr>)}</tbody></table></div>{s?.jobs.length === 0 && <p className="subtle">No background jobs reported.</p>}</Card>
      {!!s?.limitations.length && <div className="lab-notes"><span className="eyebrow">INTERPRETING THE RESULTS</span><ul>{s.limitations.map(note => <li key={note}>{note}</li>)}</ul></div>}
    </ResourceState>
  </div>
}

export function BenchCard({ source }: { source: Source }) {
  const [bench, setBench] = useState<Bench>()
  const [running, setRunning] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { setBench(undefined); setError(null) }, [source])
  const run = async () => {
    setRunning(true); setError(null); setBench(undefined)
    try { setBench(await getJson<Bench>(`/api/bench?source=${source}`, { method: 'POST' })) } catch (e) { setError(e instanceof Error ? e.message : 'Benchmark failed.') } finally { setRunning(false) }
  }
  const max = bench ? Math.max(bench.rawMs, bench.rollupMs, 0.01) : 1
  return <Card title="One question. Two query paths." eyebrow={`QUERY LAB / ${SOURCES[source].label.toUpperCase()}`} right={<button className="button primary" disabled={running} onClick={() => void run()}>{running ? <><span className="spinner" />Measuring…</> : <><Icon name="play" size={17} />Run comparison</>}</button>}>
    <p className="body-copy">The last 90 complete UTC days of estimated spend by model, queried from raw events and the daily aggregate. Today is excluded. Equality is checked before comparing timings.</p>
    {error && <ErrorNotice message={error} retry={() => void run()} />}
    {!bench && <div className="benchmark-idle"><code>api_requests</code><span>vs</span><code>spend_1d</code><p>{running ? 'Warming both queries and measuring five samples…' : 'Run a measurement on the selected dataset.'}</p></div>}
    {bench && <><div className={`notice ${bench.equivalent && bench.matchingRows > 0 ? 'success' : 'warning'}`}><Icon name={bench.equivalent ? 'check' : 'activity'} size={18} />{bench.matchingRows === 0 ? 'No grouped results in this window. Add historical data before interpreting timings.' : bench.equivalent ? `Results match: ${number(bench.matchingRows)} daily model totals, including usage counts.` : 'Results differ. Do not use this run as a speed comparison; inspect the aggregate refresh and retained history.'}</div><div className="benchmark-bars">{[{ label: 'Raw events', query: 'api_requests', ms: bench.rawMs, rows: bench.rawRows }, { label: 'Daily aggregate', query: 'spend_1d', ms: bench.rollupMs, rows: bench.rollupRows }].map((row, i) => <div key={row.query}><div className="benchmark-label"><span>{row.label} <code>{row.query}</code></span><strong>{row.ms.toFixed(2)} <small>ms</small></strong></div><div className="bar-track"><div className={i === 0 ? 'raw-bar' : ''} style={{ width: `${Math.max(1, (row.ms / max) * 100)}%` }} /></div><p className="subtle">{number(row.rows)} matching source rows · not physical rows scanned</p></div>)}</div>{bench.equivalent && bench.matchingRows > 0 && <p className="benchmark-result">{bench.rawMs > bench.rollupMs && bench.rollupMs > 0 ? <>The aggregate was <strong>{(bench.rawMs / bench.rollupMs).toFixed(2)}× faster</strong> in this run.</> : <>The raw query was as fast or faster in this run.</>} <span>Timings depend on dataset size, cache state and the machine.</span></p>}<p className="subtle">Median of {bench.samples} runs after warm-up, in one database snapshot. Window: {new Date(bench.windowStart).toISOString().slice(0, 10)} to {new Date(bench.windowEnd).toISOString().slice(0, 10)} UTC (end excluded).</p></>}
    <p className="panel-footnote">A measured demonstration, not a guaranteed speedup. Empty or very small datasets may show little difference. <CostNote /></p>
  </Card>
}
