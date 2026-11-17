import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import type { AuditLog, Page, RunListItem } from '../lib/types'
import { Badge, Empty, ErrorBox, Loading, PageHead, Tabs } from '../components/ui'

type Tab = 'statutory' | 'register' | 'variance' | 'audit'
const statutoryCodes = [['KE.PAYE', 'PAYE (P10)'], ['KE.NSSF', 'NSSF'], ['KE.SHIF', 'SHIF'], ['KE.AHL', 'Housing Levy'], ['KE.NITA', 'NITA']]

export default function ReportsPage() {
  const { can } = useAuth()
  const [tab, setTab] = useState<Tab>(can(P.ReportsRead) ? 'statutory' : 'audit')
  const runs = useQuery({ queryKey: ['runs-finalised'], queryFn: () => api.get<Page<RunListItem>>('/api/payroll/runs?limit=100'), enabled: can(P.SalaryRead) })
  const usable = runs.data?.items.filter(r => ['Calculated', 'UnderReview', 'Approved', 'PaymentProcessing', 'Paid', 'Finalised'].includes(r.status)) ?? []
  const [runId, setRunId] = useState('')
  const rid = runId || usable[0]?.id
  const tabs: { id: Tab; label: string }[] = [...(can(P.ReportsRead) && can(P.SalaryRead) ? [{ id: 'statutory' as Tab, label: 'Statutory returns' }, { id: 'register' as Tab, label: 'Payroll register' }, { id: 'variance' as Tab, label: 'Variance' }] : []), ...(can(P.AuditRead) ? [{ id: 'audit' as Tab, label: 'Audit trail' }] : [])]
  return (
    <>
      <PageHead title="Reports & audit" sub="Statutory returns, gross-to-net register, period-on-period variance and the immutable audit trail" />
      <Tabs value={tab} onChange={setTab} tabs={tabs} />
      {tab !== 'audit' && <div className="toolbar"><label className="muted small">Payroll run</label><select value={rid ?? ''} onChange={e => setRunId(e.target.value)} aria-label="Payroll run">{usable.map(r => <option key={r.id} value={r.id}>{r.reference} — {r.period} ({r.status})</option>)}</select></div>}
      {tab === 'statutory' && rid && <Statutory runId={rid} />}
      {tab === 'register' && rid && <Register runId={rid} />}
      {tab === 'variance' && rid && <Variance runId={rid} />}
      {tab === 'audit' && <Audit />}
      {tab !== 'audit' && !rid && !runs.isLoading && <div className="card"><Empty>No calculated payroll runs yet.</Empty></div>}
    </>
  )
}

function Statutory({ runId }: { runId: string }) {
  const [code, setCode] = useState('KE.PAYE')
  const q = useQuery({ queryKey: ['stat-report', runId, code], queryFn: () => api.get<{ title: string; runReference: string; period: string; rows: { employeeNumber: string; employeeName: string; pin?: string; memberNumber?: string; gross: number; base: number; employee: number; employer: number; total: number }[]; totalEmployee: number; totalEmployer: number }>(`/api/payroll/reports/statutory?runId=${runId}&ruleCode=${code}`) })
  return (
    <div className="card">
      <div className="toolbar">{statutoryCodes.map(([c, l]) => <button key={c} className={`btn sm ${c === code ? 'primary' : ''}`} onClick={() => setCode(c)}>{l}</button>)}<span style={{ flex: 1 }} /><button className="btn sm" onClick={() => api.download(`/api/payroll/reports/statutory?runId=${runId}&ruleCode=${code}&format=csv`)}>Export CSV</button></div>
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
      <div className="toolbar"><h2 style={{ margin: 0 }}>Gross-to-net register</h2><span style={{ flex: 1 }} /><button className="btn sm" onClick={() => api.download(`/api/payroll/reports/payroll-register?runId=${runId}&format=csv`)}>Export CSV</button></div>
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
    <div className="card"><h2>Variance: {d.current} vs {d.previous ?? 'no previous finalised run'}</h2>
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
      <div className="toolbar"><input placeholder="Filter by action prefix, e.g. payroll.run" value={action} onChange={e => { setAction(e.target.value); setOffset(0) }} aria-label="Action filter" /></div>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : !q.data?.items.length ? <Empty>No audit entries.</Empty> : <>
        <div className="table-wrap"><table><thead><tr><th>When</th><th>Actor</th><th>Action</th><th>Entity</th><th>Details</th><th>IP</th></tr></thead>
          <tbody>{q.data.items.map(a => <tr key={a.id}><td>{fmt.dateTime(a.occurredAt)}</td><td>{a.actorName}</td><td className="mono">{a.action}</td><td>{a.entityType} <span className="muted mono small">{a.entityId?.slice(0, 8)}</span></td><td className="mono small" style={{ whiteSpace: 'normal', maxWidth: 480 }}>{a.details}</td><td className="muted small">{a.ipAddress}</td></tr>)}</tbody></table></div>
        <div className="actions" style={{ marginTop: 12, justifyContent: 'flex-end' }}><span className="muted small">{offset + 1}–{Math.min(offset + 100, q.data.total)} of {q.data.total}</span><button className="btn sm" disabled={offset === 0} onClick={() => setOffset(offset - 100)}>Previous</button><button className="btn sm" disabled={offset + 100 >= q.data.total} onClick={() => setOffset(offset + 100)}>Next</button></div></>}
    </div>
  )
}
