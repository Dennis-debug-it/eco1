import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useLegalEntities } from '../lib/lookups'
import type { AuditLog, Page, RunListItem } from '../lib/types'
import { Badge, Empty, ErrorBox, Loading, PageHead, Tabs } from '../components/ui'
import JournalExport from '../components/JournalExport'

type Tab = 'statutory' | 'register' | 'variance' | 'annual' | 'journals' | 'audit'
const statutoryCodes = [['KE.PAYE', 'PAYE (P10)'], ['KE.NSSF', 'NSSF'], ['KE.SHIF', 'SHIF'], ['KE.AHL', 'Housing Levy'], ['KE.NITA', 'NITA']]

export default function ReportsPage() {
  const { can } = useAuth()
  const [tab, setTab] = useState<Tab>(can(P.ReportsRead) ? 'statutory' : 'audit')
  const runs = useQuery({ queryKey: ['runs-finalised'], queryFn: () => api.get<Page<RunListItem>>('/api/payroll/runs?limit=100'), enabled: can(P.SalaryRead) })
  const usable = runs.data?.items.filter(r => ['Calculated', 'UnderReview', 'Approved', 'PaymentProcessing', 'Paid', 'Finalised'].includes(r.status)) ?? []
  const [runId, setRunId] = useState('')
  const rid = runId || usable[0]?.id
  const tabs: { id: Tab; label: string }[] = [...(can(P.ReportsRead) && can(P.SalaryRead) ? [{ id: 'statutory' as Tab, label: 'Statutory returns' }, { id: 'register' as Tab, label: 'Payroll register' }, { id: 'variance' as Tab, label: 'Variance' }, { id: 'annual' as Tab, label: 'Annual / P9' }] : []), ...(can(P.JournalsRead) ? [{ id: 'journals' as Tab, label: 'Accounting journals' }] : []), ...(can(P.AuditRead) ? [{ id: 'audit' as Tab, label: 'Audit trail' }] : [])]
  return (
    <>
      <PageHead title="Reports & audit" sub="Statutory returns, gross-to-net register, period-on-period variance and the immutable audit trail" />
      <Tabs value={tab} onChange={setTab} tabs={tabs} />
      {tab !== 'audit' && tab !== 'annual' && tab !== 'journals' && <div className="toolbar"><label className="muted small">Payroll run</label><select value={rid ?? ''} onChange={e => setRunId(e.target.value)} aria-label="Payroll run">{usable.map(r => <option key={r.id} value={r.id}>{r.reference} — {r.period} ({r.status})</option>)}</select></div>}
      {tab === 'statutory' && rid && <Statutory runId={rid} />}
      {tab === 'register' && rid && <Register runId={rid} />}
      {tab === 'variance' && rid && <Variance runId={rid} />}
      {tab === 'annual' && <Annual />}
      {tab === 'journals' && <Journals />}
      {tab === 'audit' && <Audit />}
      {tab !== 'audit' && tab !== 'annual' && tab !== 'journals' && !rid && !runs.isLoading && <div className="card"><Empty>No calculated payroll runs yet.</Empty></div>}
    </>
  )
}

function Statutory({ runId }: { runId: string }) {
  const [code, setCode] = useState('KE.PAYE')
  const q = useQuery({ queryKey: ['stat-report', runId, code], queryFn: () => api.get<{ title: string; runReference: string; period: string; rows: { employeeNumber: string; employeeName: string; pin?: string; memberNumber?: string; gross: number; base: number; employee: number; employer: number; total: number }[]; totalEmployee: number; totalEmployer: number }>(`/api/payroll/reports/statutory?runId=${runId}&ruleCode=${code}`) })
  return (
    <div className="card">
      <div className="toolbar">{statutoryCodes.map(([c, l]) => <button key={c} className={`btn sm ${c === code ? 'primary' : ''}`} onClick={() => setCode(c)}>{l}</button>)}<span style={{ flex: 1 }} /><Exports url={`/api/payroll/reports/statutory?runId=${runId}&ruleCode=${code}`} /></div>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : <>
        <h2>{q.data!.title} <span className="muted small">· {q.data!.runReference} · {q.data!.period}</span></h2>
        {!q.data!.rows.length ? <Empty>No contributions under this rule in this run.</Empty> : <div className="table-wrap"><table><thead><tr><th>No.</th><th>Employee</th><th>PIN</th><th>Member no.</th><th className="num">Gross</th><th className="num">Base</th><th className="num">Employee</th><th className="num">Employer</th><th className="num">Total</th></tr></thead>
          <tbody>{q.data!.rows.map(r => <tr key={r.employeeNumber}><td className="mono">{r.employeeNumber}</td><td>{r.employeeName}</td><td className="mono">{r.pin}</td><td className="mono">{r.memberNumber}</td><td className="num">{fmt.num(r.gross)}</td><td className="num">{fmt.num(r.base)}</td><td className="num">{fmt.num(r.employee)}</td><td className="num">{fmt.num(r.employer)}</td><td className="num">{fmt.num(r.total)}</td></tr>)}</tbody>
          <tfoot><tr><td colSpan={6}>Totals</td><td className="num">{fmt.num(q.data!.totalEmployee)}</td><td className="num">{fmt.num(q.data!.totalEmployer)}</td><td className="num">{fmt.num(q.data!.totalEmployee + q.data!.totalEmployer)}</td></tr></tfoot></table></div>}</>}
    </div>
  )
}

function Register({ runId }: { runId: string }) {
  const q = useQuery({ queryKey: ['register', runId], queryFn: () => api.get<{ reference: string; rows: Record<string, number | string | null>[]; totals: Record<string, number> }>(`/api/payroll/reports/payroll-register?runId=${runId}`) })
  const cols: [string, string][] = [['basicPay', 'Basic'], ['grossPay', 'Gross'], ['taxablePay', 'Taxable'], ['paye', 'PAYE'], ['nssf', 'NSSF'], ['shif', 'SHIF'], ['housingLevy', 'AHL'], ['totalOtherDeductions', 'Other'], ['totalEmployeeDeductions', 'Deductions'], ['netPay', 'Net'], ['totalEmployerContributions', 'Employer contrib.'], ['totalEmployerCost', 'Employer cost']]
  return (
    <div className="card">
      <div className="toolbar"><h2 style={{ margin: 0 }}>Gross-to-net register</h2><span style={{ flex: 1 }} /><Exports url={`/api/payroll/reports/payroll-register?runId=${runId}`} /></div>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : <div className="table-wrap"><table><thead><tr><th>No.</th><th>Employee</th><th>Department</th>{cols.map(([k, l]) => <th key={k} className="num">{l}</th>)}</tr></thead>
        <tbody>{q.data!.rows.map(r => <tr key={String(r.employeeNumber)}><td className="mono">{r.employeeNumber}</td><td>{r.employeeName}</td><td>{r.department}</td>{cols.map(([k]) => <td key={k} className="num">{fmt.num(r[k] as number)}</td>)}</tr>)}</tbody>
        <tfoot><tr><td colSpan={3}>Totals</td>{cols.map(([k]) => <td key={k} className="num">{k === 'basicPay' || k === 'taxablePay' || k === 'totalEmployeeDeductions' ? '' : fmt.num(q.data!.totals[k === 'nssf' ? 'totalNssfEmployee' : k === 'shif' ? 'totalShif' : k === 'housingLevy' ? 'totalHousingLevyEmployee' : k === 'paye' ? 'totalPaye' : k === 'grossPay' ? 'totalGross' : k === 'netPay' ? 'totalNet' : k])}</td>)}</tr></tfoot></table></div>}
    </div>
  )
}

function Variance({ runId }: { runId: string }) {
  const q = useQuery({ queryKey: ['variance', runId], queryFn: () => api.get<{ current: string; previous?: string; rows: { employeeNumber: string; employeeName: string; currentGross: number; previousGross?: number; currentNet: number; previousNet?: number; netChange?: number; netChangePercent?: number; status: string }[]; leavers: { employeeNumber: string; employeeName: string; previousNet: number }[]; totals: { currentNet: number; previousNet?: number } }>(`/api/payroll/reports/variance?runId=${runId}`) })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const d = q.data!
  return (
    <div className="card"><div className="toolbar"><h2 style={{ margin: 0 }}>Variance: {d.current} vs {d.previous ?? 'no previous finalised run'}</h2><span style={{ flex: 1 }} /><Exports url={`/api/payroll/reports/variance?runId=${runId}`} /></div>
      <table><thead><tr><th>No.</th><th>Employee</th><th className="num">Prev. gross</th><th className="num">Gross</th><th className="num">Prev. net</th><th className="num">Net</th><th className="num">Change</th><th className="num">%</th><th>Status</th></tr></thead>
        <tbody>{d.rows.map(r => <tr key={r.employeeNumber}><td className="mono">{r.employeeNumber}</td><td>{r.employeeName}</td><td className="num">{fmt.num(r.previousGross)}</td><td className="num">{fmt.num(r.currentGross)}</td><td className="num">{fmt.num(r.previousNet)}</td><td className="num">{fmt.num(r.currentNet)}</td><td className="num">{fmt.num(r.netChange)}</td><td className="num">{r.netChangePercent != null ? (Math.abs(r.netChangePercent) > 20 ? <Badge status="Warning">{fmt.pct(r.netChangePercent)}</Badge> : fmt.pct(r.netChangePercent)) : '—'}</td><td>{r.status === 'New' ? <Badge status="Open">New</Badge> : ''}</td></tr>)}
          {d.leavers.map(l => <tr key={l.employeeNumber} className="muted"><td className="mono">{l.employeeNumber}</td><td>{l.employeeName}</td><td /><td /><td className="num">{fmt.num(l.previousNet)}</td><td className="num">—</td><td className="num">{fmt.num(-l.previousNet)}</td><td /><td><Badge status="Terminated">Left</Badge></td></tr>)}</tbody>
        <tfoot><tr><td colSpan={4}>Total net</td><td className="num">{fmt.num(d.totals.previousNet)}</td><td className="num">{fmt.num(d.totals.currentNet)}</td><td className="num">{d.totals.previousNet != null ? fmt.num(d.totals.currentNet - d.totals.previousNet) : ''}</td><td colSpan={2} /></tr></tfoot></table></div>
  )
}

function Audit() {
  const [action, setAction] = useState('')
  const [offset, setOffset] = useState(0)
  const q = useQuery({ queryKey: ['audit', action, offset], queryFn: () => api.get<Page<AuditLog>>(`/api/payroll/reports/audit?limit=100&offset=${offset}${action ? `&action=${action}` : ''}`) })
  return (
    <div className="card">
      <div className="toolbar"><input placeholder="Filter by action prefix, e.g. payroll.run" value={action} onChange={e => { setAction(e.target.value); setOffset(0) }} aria-label="Action filter" /><span style={{ flex: 1 }} /><Exports url={`/api/payroll/reports/audit?${action ? `action=${encodeURIComponent(action)}` : ''}`} /></div>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : !q.data?.items.length ? <Empty>No audit entries.</Empty> : <>
        <div className="table-wrap"><table><thead><tr><th>When</th><th>Actor</th><th>Action</th><th>Entity</th><th>Details</th><th>IP</th></tr></thead>
          <tbody>{q.data.items.map(a => <tr key={a.id}><td>{fmt.dateTime(a.occurredAt)}</td><td>{a.actorName}</td><td className="mono">{a.action}</td><td>{a.entityType} <span className="muted mono small">{a.entityId?.slice(0, 8)}</span></td><td className="mono small" style={{ whiteSpace: 'normal', maxWidth: 480 }}>{a.details}</td><td className="muted small">{a.ipAddress}</td></tr>)}</tbody></table></div>
        <div className="actions" style={{ marginTop: 12, justifyContent: 'flex-end' }}><span className="muted small">{offset + 1}–{Math.min(offset + 100, q.data.total)} of {q.data.total}</span><button className="btn sm" disabled={offset === 0} onClick={() => setOffset(offset - 100)}>Previous</button><button className="btn sm" disabled={offset + 100 >= q.data.total} onClick={() => setOffset(offset + 100)}>Next</button></div></>}
    </div>
  )
}

function Exports({ url }: { url: string }) {
  const [busy, setBusy] = useState<string | null>(null)
  const sep = url.includes('?') ? '&' : '?'
  const go = async (f: string) => { setBusy(f); try { await api.download(`${url}${sep}format=${f}`) } finally { setBusy(null) } }
  return <span className="actions">{[['xlsx', 'Excel'], ['csv', 'CSV'], ['pdf', 'PDF']].map(([f, l]) => <button key={f} className="btn sm" disabled={!!busy} onClick={() => go(f)}>{busy === f ? '…' : l}</button>)}</span>
}

type AnnualMonth = { month: number; period: string; gross: number; allowableDeductions: number; taxablePay: number; grossTax: number; reliefs: number; paye: number; nssf: number; shif: number; housingLevy: number; netPay: number }
type AnnualEmployee = { employeeId: string; employeeNumber: string; employeeName: string; kraPin?: string; months: AnnualMonth[]; totals: AnnualMonth }

function Annual() {
  const les = useLegalEntities()
  const le = les.data?.[0]?.id
  const [year, setYear] = useState(new Date().getFullYear())
  const [emp, setEmp] = useState<string | null>(null)
  const q = useQuery({ queryKey: ['annual', le, year], queryFn: () => api.get<{ year: number; legalEntity: string; employerPin: string; employees: AnnualEmployee[] }>(`/api/payroll/reports/annual?legalEntityId=${le}&year=${year}`), enabled: !!le })
  const selected = q.data?.employees.find(e => e.employeeId === emp)
  const base = `/api/payroll/reports/annual?legalEntityId=${le}&year=${year}`
  return (
    <div className="card">
      <div className="toolbar"><label className="muted small">Year</label><input type="number" value={year} onChange={e => setYear(Number(e.target.value))} style={{ width: 90 }} aria-label="Year" /><span style={{ flex: 1 }} /><Exports url={base} /></div>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : !q.data?.employees.length ? <Empty>No finalised payroll in {year}.</Empty> : <>
        <h2>Annual payroll summary {year} <span className="muted small">· {q.data.legalEntity} · PIN {q.data.employerPin}</span></h2>
        <div className="table-wrap"><table><thead><tr><th>No.</th><th>Employee</th><th>KRA PIN</th><th className="num">Gross</th><th className="num">Taxable pay</th><th className="num">PAYE</th><th className="num">NSSF</th><th className="num">SHIF</th><th className="num">AHL</th><th className="num">Net</th><th /></tr></thead>
          <tbody>{q.data.employees.map(e => <tr key={e.employeeId}><td className="mono">{e.employeeNumber}</td><td>{e.employeeName}</td><td className="mono">{e.kraPin}</td><td className="num">{fmt.num(e.totals.gross)}</td><td className="num">{fmt.num(e.totals.taxablePay)}</td><td className="num">{fmt.num(e.totals.paye)}</td><td className="num">{fmt.num(e.totals.nssf)}</td><td className="num">{fmt.num(e.totals.shif)}</td><td className="num">{fmt.num(e.totals.housingLevy)}</td><td className="num">{fmt.num(e.totals.netPay)}</td>
            <td className="actions"><button className="btn sm" onClick={() => setEmp(e.employeeId === emp ? null : e.employeeId)}>{e.employeeId === emp ? 'Hide' : 'Months'}</button><button className="btn sm" onClick={() => api.download(`${base}&employeeId=${e.employeeId}&format=pdf`)}>P9 PDF</button></td></tr>)}</tbody>
          <tfoot><tr><td colSpan={3}>Totals</td>{(['gross', 'taxablePay', 'paye', 'nssf', 'shif', 'housingLevy', 'netPay'] as const).map(k => <td key={k} className="num">{fmt.num(q.data!.employees.reduce((s, e) => s + e.totals[k], 0))}</td>)}<td /></tr></tfoot></table></div>
        {selected && <><h3 style={{ marginTop: 14 }}>{selected.employeeName} — monthly</h3><table><thead><tr><th>Month</th><th className="num">Gross</th><th className="num">Allowable</th><th className="num">Taxable</th><th className="num">Tax charged</th><th className="num">Reliefs</th><th className="num">PAYE</th></tr></thead>
          <tbody>{selected.months.filter(m => m.gross !== 0).map(m => <tr key={m.month}><td>{m.period}</td><td className="num">{fmt.num(m.gross)}</td><td className="num">{fmt.num(m.allowableDeductions)}</td><td className="num">{fmt.num(m.taxablePay)}</td><td className="num">{fmt.num(m.grossTax)}</td><td className="num">{fmt.num(m.reliefs)}</td><td className="num">{fmt.num(m.paye)}</td></tr>)}</tbody></table></>}
      </>}
    </div>
  )
}

/** A company's journals for a month (payroll, leave provision, training, expenses) as one import file for QuickBooks or Sage. */
function Journals() {
  const entities = useLegalEntities()
  const [le, setLe] = useState('')
  const [month, setMonth] = useState(() => new Date().toISOString().slice(0, 7))
  const company = le || entities.data?.[0]?.id
  const [y, m] = month.split('-').map(Number)
  const from = `${month}-01`
  const to = `${month}-${String(new Date(y, m, 0).getDate()).padStart(2, '0')}`
  return (
    <div className="card">
      <h2>Accounting journals</h2>
      <p className="small">Every journal posted in the month, payroll and otherwise, as one file to import into QuickBooks or Sage. Each journal is marked when it's exported, so an import is never doubled by accident.</p>
      <div className="toolbar">
        {(entities.data?.length ?? 0) > 1 && <select value={company ?? ''} onChange={e => setLe(e.target.value)} aria-label="Company">{entities.data!.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select>}
        <input type="month" value={month} onChange={e => setMonth(e.target.value)} aria-label="Month" />
      </div>
      {company && <JournalExport query={`legalEntityId=${company}&from=${from}&to=${to}`} />}
    </div>
  )
}
