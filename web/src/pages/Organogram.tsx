import { useCallback, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../lib/api'
import { Empty, ErrorBox, Loading, PageHead } from '../components/ui'

type Person = { id: string; name: string; employeeNumber: string; jobTitle?: string; departmentId?: string; department?: string; managerId?: string; formerManager?: string; grade?: string; positionId?: string; status: string }
type Seat = { id: string; code: string; title: string; grade?: string; departmentId?: string; department?: string; parentId?: string; parentInferred: boolean; budgeted: number; holders: { id: string; name: string }[]; vacancies: number }
type Chart = { people: Person[]; positions: Seat[] | null }

/** A generic tree node so people and positions share one layout. */
type Node = { id: string; label: string; parentId?: string; departmentId?: string; department?: string; render: () => ReactNode; search: string }
type Tree = { roots: string[]; children: Map<string, string[]>; depth: Map<string, number>; looped: string[] }

// Department stripes: mid-tone hues that read on light and dark surfaces.
const palette = ['#2f7ed8', '#0b8a6f', '#c98a1e', '#8e5bd0', '#d0574f', '#13879a', '#7a8a22', '#b8558f', '#5b6b7c']

function buildTree(nodes: Node[]): Tree {
  const ids = new Set(nodes.map(n => n.id))
  const children = new Map<string, string[]>()
  for (const n of nodes) if (n.parentId && ids.has(n.parentId)) children.set(n.parentId, [...(children.get(n.parentId) ?? []), n.id])
  const roots = nodes.filter(n => !n.parentId || !ids.has(n.parentId)).map(n => n.id)
  const depth = new Map<string, number>()
  const walk = (id: string, d: number) => { if (depth.has(id)) return; depth.set(id, d); for (const c of children.get(id) ?? []) walk(c, d + 1) }
  roots.forEach(r => walk(r, 1))
  // Anyone not reached sits in a reporting loop (A reports to B reports to A): show them at the top, flagged.
  const looped = nodes.filter(n => !depth.has(n.id)).map(n => n.id)
  for (const id of looped) { if (!depth.has(id)) { roots.push(id); walk(id, 1) } }
  return { roots, children, depth, looped }
}

/** Biggest teams first, then by name, so the chart reads left to right from the largest branch. */
function sizeOf(tree: Tree, id: string, memo = new Map<string, number>()): number {
  if (memo.has(id)) return memo.get(id)!
  memo.set(id, 1)
  const n = 1 + (tree.children.get(id) ?? []).reduce((s, c) => s + sizeOf(tree, c, memo), 0)
  memo.set(id, n)
  return n
}

export default function OrganogramPage() {
  const q = useQuery({ queryKey: ['org-chart-v2'], queryFn: () => api.get<Chart>('/api/payroll/organisation/chart') })
  const [view, setView] = useState<'people' | 'positions'>('people')
  const [search, setSearch] = useState('')
  const [zoom, setZoom] = useState(1)
  const [collapsed, setCollapsed] = useState<Set<string> | null>(null)
  const scrollRef = useRef<HTMLDivElement>(null)
  const chartRef = useRef<HTMLDivElement>(null)

  /** Shrink to the width of the screen (not below 50%), then centre the top of the chart. */
  const fit = useCallback(() => {
    const box = scrollRef.current, chart = chartRef.current
    if (!box || !chart) return
    const current = Number(chart.style.zoom || 1)
    const natural = chart.scrollWidth / current
    const z = Math.max(0.5, Math.min(1, +((box.clientWidth - 24) / natural).toFixed(2)))
    setZoom(z)
    requestAnimationFrame(() => { box.scrollLeft = (box.scrollWidth - box.clientWidth) / 2; box.scrollTop = 0 })
  }, [])

  const data = q.data
  const deptColour = useMemo(() => {
    const ids = [...new Set((data?.people ?? []).map(p => p.departmentId).filter(Boolean) as string[])]
    return new Map(ids.map((id, i) => [id, palette[i % palette.length]]))
  }, [data])
  const deptNames = useMemo(() => new Map((data?.people ?? []).filter(p => p.departmentId).map(p => [p.departmentId!, p.department ?? 'Department'])), [data])

  const nodes: Node[] = useMemo(() => {
    if (!data) return []
    if (view === 'positions' && data.positions) return data.positions.map(s => ({
      id: s.id, label: s.title, parentId: s.parentId, departmentId: s.departmentId, department: s.department, search: `${s.title} ${s.code} ${s.holders.map(h => h.name).join(' ')}`.toLowerCase(),
      render: () => <SeatCard s={s} colour={s.departmentId ? deptColour.get(s.departmentId) : undefined} />,
    }))
    return data.people.map(p => ({
      id: p.id, label: p.name, parentId: p.managerId, departmentId: p.departmentId, department: p.department, search: `${p.name} ${p.jobTitle ?? ''} ${p.employeeNumber} ${p.department ?? ''}`.toLowerCase(),
      render: () => <PersonCard p={p} colour={p.departmentId ? deptColour.get(p.departmentId) : undefined} />,
    }))
  }, [data, view, deptColour])

  const byId = useMemo(() => new Map(nodes.map(n => [n.id, n])), [nodes])
  const tree = useMemo(() => buildTree(nodes), [nodes])
  const sizes = useMemo(() => { const memo = new Map<string, number>(); nodes.forEach(n => sizeOf(tree, n.id, memo)); return memo }, [nodes, tree])
  const levels = useMemo(() => {
    const m = new Map<number, string[]>()
    for (const [id, d] of tree.depth) m.set(d, [...(m.get(d) ?? []), id])
    return [...m.entries()].sort((a, b) => a[0] - b[0])
  }, [tree])

  // Large organisations open three levels deep; small ones fully open.
  const defaultCollapsed = useMemo(() => new Set(nodes.length > 40 ? nodes.filter(n => (tree.depth.get(n.id) ?? 1) >= 3 && tree.children.get(n.id)?.length).map(n => n.id) : []), [nodes, tree])
  const closed = collapsed ?? defaultCollapsed

  const term = search.trim().toLowerCase()
  const matches = useMemo(() => new Set(term.length >= 2 ? nodes.filter(n => n.search.includes(term)).map(n => n.id) : []), [nodes, term])
  // Open the path to every match.
  const forcedOpen = useMemo(() => {
    const open = new Set<string>()
    for (const id of matches) for (let p = byId.get(id)?.parentId; p && byId.has(p) && !open.has(p); p = byId.get(p)?.parentId) open.add(p)
    return open
  }, [matches, byId])

  const toggle = (id: string) => { const next = new Set(closed); if (next.has(id)) next.delete(id); else next.add(id); setCollapsed(next) }
  const isOpen = (id: string) => forcedOpen.has(id) || !closed.has(id)

  const ordered = (ids: string[]) => [...ids].sort((a, b) => (sizes.get(b) ?? 0) - (sizes.get(a) ?? 0) || (byId.get(a)?.label ?? '').localeCompare(byId.get(b)?.label ?? ''))

  const renderBranch = (id: string): ReactNode => {
    const node = byId.get(id)!
    const kids = ordered(tree.children.get(id) ?? [])
    const open = isOpen(id)
    // A wide row of people with no reports underneath is stacked into a column to keep the chart readable.
    const stack = open && kids.length > 3 && kids.every(k => !(tree.children.get(k)?.length))
    return (
      <li key={id}>
        <div className={`org-node${matches.has(id) ? ' match' : ''}${tree.looped.includes(id) ? ' looped' : ''}`}>
          {node.render()}
          {kids.length > 0 && <button type="button" className="org-toggle" aria-expanded={open} onClick={() => toggle(id)} title={open ? 'Hide team' : 'Show team'}>
            {open ? '−' : '+'} {kids.length} direct{kids.length === 1 ? '' : 's'}{!open && sizes.get(id)! - 1 > kids.length ? ` · ${sizes.get(id)! - 1} in team` : ''}</button>}
        </div>
        {open && kids.length > 0 && (stack
          ? <ul className="org-stack">{kids.map(k => <li key={k}><div className={`org-node${matches.has(k) ? ' match' : ''}`}>{byId.get(k)!.render()}</div></li>)}</ul>
          : <ul>{kids.map(renderBranch)}</ul>)}
      </li>
    )
  }

  // Fit when the chart first appears or the view changes.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useLayoutEffect(() => { if (nodes.length) fit() }, [nodes.length, view])

  if (q.isLoading) return <Loading />
  if (q.error) return <><PageHead title="Organogram" /><ErrorBox error={q.error} /></>
  const orphans = view === 'people' ? (data?.people ?? []).filter(p => p.formerManager) : []
  const tops = tree.roots.filter(r => !tree.looped.includes(r))

  return (
    <>
      <PageHead title="Organogram" sub="Who reports to whom, from the most senior down. Each row is one level of seniority." >
        <button className="btn no-print" onClick={() => window.print()}>Print</button>
      </PageHead>

      <div className="card no-print" style={{ marginBottom: 12 }}>
        <div className="toolbar" style={{ flexWrap: 'wrap', gap: 8 }}>
          {data?.positions && <div className="tabs" role="tablist" style={{ margin: 0, border: 0 }}>
            <button role="tab" aria-selected={view === 'people'} className={view === 'people' ? 'active' : ''} onClick={() => { setView('people'); setCollapsed(null) }}>People</button>
            <button role="tab" aria-selected={view === 'positions'} className={view === 'positions' ? 'active' : ''} onClick={() => { setView('positions'); setCollapsed(null) }}>Positions & vacancies</button>
          </div>}
          <input type="search" placeholder="Find a person or title" value={search} onChange={e => setSearch(e.target.value)} aria-label="Find a person or title" style={{ minWidth: 200 }} />
          {term.length >= 2 && <span className="small muted">{matches.size} found</span>}
          <span style={{ flex: 1 }} />
          <button className="btn sm" onClick={() => setCollapsed(new Set())}>Expand all</button>
          <button className="btn sm" onClick={() => setCollapsed(new Set(nodes.filter(n => (tree.depth.get(n.id) ?? 1) >= 2 && tree.children.get(n.id)?.length).map(n => n.id)))}>Top two levels</button>
          <span className="small muted" aria-hidden>Zoom</span>
          <button className="btn sm" aria-label="Zoom out" onClick={() => setZoom(z => Math.max(0.4, +(z - 0.1).toFixed(1)))}>−</button>
          <button className="btn sm" aria-label="Reset zoom to 100%" onClick={() => setZoom(1)}>{Math.round(zoom * 100)}%</button>
          <button className="btn sm" aria-label="Zoom in" onClick={() => setZoom(z => Math.min(1.5, +(z + 0.1).toFixed(1)))}>+</button>
          <button className="btn sm" onClick={fit}>Fit</button>
        </div>
      </div>

      <div className="grid" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(min(260px, 100%), 1fr))', marginBottom: 12 }}>
        <div className="card">
          <h3 style={{ marginTop: 0 }}>Levels of seniority</h3>
          <ol className="small" style={{ margin: 0, paddingLeft: 18 }}>
            {levels.map(([d, ids]) => <li key={d}><b>{ids.length}</b> {view === 'people' ? (ids.length === 1 ? 'person' : 'people') : (ids.length === 1 ? 'position' : 'positions')}
              {ids.length <= 3 && <span className="muted"> · {ids.map(i => byId.get(i)?.label).join(', ')}</span>}</li>)}
          </ol>
        </div>
        {deptColour.size > 0 && <div className="card">
          <h3 style={{ marginTop: 0 }}>Departments</h3>
          <div className="small" style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 12px' }}>
            {[...deptColour.entries()].map(([id, c]) => <span key={id} style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}><span aria-hidden style={{ width: 10, height: 10, borderRadius: 2, background: c }} />{deptNames.get(id)}</span>)}
          </div>
        </div>}
        {(orphans.length > 0 || tree.looped.length > 0 || tops.length > 1) && <div className="card">
          <h3 style={{ marginTop: 0 }}>Check reporting lines</h3>
          <ul className="small" style={{ margin: 0, paddingLeft: 18 }}>
            {tops.length > 1 && <li>{tops.length} {view === 'people' ? 'people have' : 'positions have'} no one above them. If there is one head, set the others' {view === 'people' ? 'line manager on their employee record' : '"reports to" on the position'}.</li>}
            {orphans.length > 0 && <li>{orphans.length} report{orphans.length === 1 ? 's' : ''} to someone who has left: {orphans.slice(0, 4).map(o => `${o.name} (was ${o.formerManager})`).join('; ')}{orphans.length > 4 ? '…' : ''}</li>}
            {tree.looped.length > 0 && <li>{tree.looped.length} sit in a reporting loop (each reports, through others, to themselves). They are shown at the top, outlined in red.</li>}
          </ul>
        </div>}
      </div>

      {nodes.length === 0 ? <Empty>No one on the books yet. Add employees, and their line managers, to build the organogram.</Empty> : (
        <div className="card org-scroll" ref={scrollRef}>
          <div className="orgchart" style={{ zoom }} ref={chartRef}>
            <ul>{ordered(tree.roots).map(renderBranch)}</ul>
          </div>
        </div>
      )}
      {view === 'positions' && data?.positions?.some(s => s.parentInferred) && <p className="small muted">Dotted outline: no "reports to" is set on the position, so it is placed under the position its holders' managers hold. Set it under People records → Positions & headcount.</p>}
    </>
  )
}

const initials = (name: string) => name.split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0]!.toUpperCase()).join('')

function PersonCard({ p, colour }: { p: Person; colour?: string }) {
  return (
    <div className="org-card" style={{ borderTopColor: colour }}>
      <div className="org-avatar" aria-hidden style={{ background: colour ?? 'var(--muted)' }}>{initials(p.name)}</div>
      <div style={{ minWidth: 0 }}>
        <Link to={`/employees/${p.id}`} className="org-name">{p.name}</Link>
        <div className="org-title">{p.jobTitle ?? '—'}</div>
        <div className="org-meta">{p.department ?? 'No department'}{p.grade && <span className="org-grade">{p.grade}</span>}{p.status !== 'Active' && <span className="org-grade">{p.status === 'OnLeave' ? 'On leave' : p.status}</span>}</div>
      </div>
    </div>
  )
}

function SeatCard({ s, colour }: { s: Seat; colour?: string }) {
  return (
    <div className={`org-card seat${s.holders.length === 0 ? ' vacant' : ''}${s.parentInferred ? ' inferred' : ''}`} style={{ borderTopColor: colour }}>
      <div style={{ minWidth: 0 }}>
        <div className="org-name">{s.title}</div>
        <div className="org-meta"><span className="mono">{s.code}</span>{s.grade && <span className="org-grade">{s.grade}</span>}</div>
        <div className="org-title">{s.holders.length ? s.holders.slice(0, 3).map(h => h.name).join(', ') + (s.holders.length > 3 ? ` +${s.holders.length - 3}` : '') : 'Vacant'}</div>
        {s.vacancies > 0 && s.holders.length > 0 && <div className="org-meta" style={{ color: 'var(--warn)' }}>{s.vacancies} vacant of {s.budgeted}</div>}
      </div>
    </div>
  )
}
