import { useEffect, useState } from 'react'
import { SOURCE_ORDER, SOURCES, usePolling, type LocalUsageStatus, type Range, type Source, type Status } from './api'
import { CostNote, Dashboard, DatabaseLab, DataSetup, Icon, type IconName } from './components'
import { AppearanceControl } from './AppearanceControl'

type Tab = 'dashboard' | 'setup' | 'database'
const TABS: { key: Tab; title: string; icon: IconName; subtitle: string }[] = [
  { key: 'dashboard', title: 'Dashboard', icon: 'grid', subtitle: 'Claude Code requests, tokens and estimated cost.' },
  { key: 'setup', title: 'Data & setup', icon: 'data', subtitle: 'Connect Claude Code and bring in existing usage.' },
  { key: 'database', title: 'Database', icon: 'database', subtitle: 'Inspect storage, ingestion and query performance.' },
]
const RANGES: Range[] = ['1h', '24h', '7d', '30d', '90d']
const initialTab = (): Tab => {
  const hash = window.location.hash.replace('#', '')
  return TABS.find(t => t.key === hash)?.key ?? 'dashboard'
}
const initialSource = (): Source | null => {
  try {
    const saved = window.localStorage.getItem('tokenscope-source')
    return SOURCE_ORDER.find(source => source === saved) ?? null
  } catch { return null }
}

export default function App() {
  const [tab, setTab] = useState<Tab>(initialTab)
  const [selectedSource, setSelectedSource] = useState<Source | null>(initialSource)
  const [range, setRange] = useState<Range>('30d')
  const [privacy, setPrivacy] = useState(true)
  const [refreshKey, setRefreshKey] = useState(0)
  const status = usePolling<Status>('/api/status', 30000, refreshKey)
  const source = selectedSource ?? 'live'
  const sourceReady = selectedSource !== null || status.error !== null
  const localUsage = usePolling<LocalUsageStatus>('/api/local-usage/status', 5000, refreshKey, tab === 'setup' || (tab === 'dashboard' && source === 'claude-local'))
  const current = TABS.find(t => t.key === tab)!
  const refresh = () => setRefreshKey(k => k + 1)
  const chooseSource = (next: Source) => {
    setSelectedSource(next)
    try { window.localStorage.setItem('tokenscope-source', next) } catch { /* Storage can be unavailable in private browsing. */ }
  }
  const navigate = (next: Tab) => { setTab(next); window.location.hash = next; window.scrollTo({ top: 0, behavior: 'instant' }) }
  useEffect(() => {
    if (!status.data) return
    const hasHistory = status.data.sources.some(item => item.source === 'claude-local' && item.requests > 0)
    // An explicit choice made while status loads always takes precedence.
    setSelectedSource(previous => previous ?? (hasHistory ? 'claude-local' : 'live'))
  }, [status.data])
  useEffect(() => {
    const onHashChange = () => setTab(initialTab())
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])

  return <div className="app-shell">
    <a className="skip-link" href="#main">Skip to content</a>
    <aside className="sidebar">
      <button className="brand" onClick={() => navigate('dashboard')} aria-label="TokenScope dashboard"><span className="brand-mark"><span /><span /><span /></span><span>Token<span className="brand-light">Scope</span></span></button>
      <nav aria-label="Main navigation">{TABS.map(item => <button key={item.key} onClick={() => navigate(item.key)} aria-label={item.title} title={item.title} aria-current={tab === item.key ? 'page' : undefined} className={`nav-item ${tab === item.key ? 'selected' : ''}`}><Icon name={item.icon} size={19} /><span>{item.title}</span>{tab === item.key && <span className="nav-indicator" />}</button>)}</nav>
      <div className="sidebar-bottom"><AppearanceControl /></div>
    </aside>
    <div className="main-shell">
      <header className="topbar"><div className="breadcrumbs">{current.title}</div><div className="topbar-actions"><span className={`connection ${status.data?.database.connected && !status.error ? '' : 'offline'}`} role="status"><span className="status-dot" />{status.loading ? 'Connecting' : status.error ? 'API unavailable' : status.data?.database.connected ? 'Database connected' : 'Database unavailable'}</span><button className={`privacy-toggle ${privacy ? 'enabled' : ''}`} aria-pressed={privacy} onClick={() => setPrivacy(value => !value)} title={privacy ? 'Identifiers are replaced by stable aliases' : 'Original identifiers are visible'}><Icon name="shield" size={16} /><span>Hide identifiers {privacy ? 'on' : 'off'}</span></button></div></header>
      <main id="main" tabIndex={-1}>
        <div className="page-heading"><div><h1>{tab === 'dashboard' ? 'Usage overview' : current.title}</h1><p>{current.subtitle}</p></div><button className="icon-button refresh-button" onClick={refresh} aria-label="Refresh visible data" title="Refresh visible data"><Icon name="refresh" size={18} /></button></div>
        {(tab === 'dashboard' || tab === 'database') && <div className="view-controls">
          <div className="source-selection">
            <label className="source-control"><span>Data source</span><select className="source-select" value={sourceReady ? source : ''} onChange={event => chooseSource(event.target.value as Source)}>
              {!sourceReady && <option value="" disabled>Loading sources…</option>}
              <optgroup label="Your usage">{(['claude-local', 'live', 'history'] as const).map(item => <option key={item} value={item}>{SOURCES[item].label}</option>)}</optgroup>
              <optgroup label="Database examples"><option value="demo">{SOURCES.demo.label}</option></optgroup>
            </select></label>
            {sourceReady && source === 'demo' && <span className="badge orange">Synthetic</span>}
          </div>
          {tab === 'dashboard' && <div className="segmented range-switch" role="group" aria-label="Time range">{RANGES.map(r => <button key={r} className={range === r ? 'selected' : ''} aria-pressed={range === r} onClick={() => setRange(r)}>{r}</button>)}</div>}
        </div>}
        {tab === 'dashboard' && (sourceReady ? <Dashboard key={`${source}:${range}`} source={source} range={range} refreshKey={refreshKey} privacy={privacy} status={status.data} localUsage={localUsage.data} onSetup={() => navigate('setup')} /> : <div className="loading-state" role="status">Loading your usage sources…</div>)}
        {tab === 'setup' && <DataSetup privacy={privacy} status={status.data} localUsage={localUsage} refresh={refresh} onView={s => { chooseSource(s); setRange(s === 'live' ? '1h' : '90d'); navigate('dashboard') }} />}
        {tab === 'database' && (sourceReady ? <DatabaseLab source={source} status={status} /> : <div className="loading-state" role="status">Loading your usage sources…</div>)}
        <footer className="app-footer"><span>TokenScope <span className="footer-dot">·</span> Claude Code usage analytics</span><span><CostNote /> <span className="footer-dot">·</span> Not billing data</span></footer>
      </main>
    </div>
  </div>
}
