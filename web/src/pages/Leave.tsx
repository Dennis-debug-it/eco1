import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useHrMe, useLegalEntities } from '../lib/lookups'
import type { LeaveType } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, FileLink, Loading, Modal, PageHead, ReasonDialog, Tabs } from '../components/ui'

type Req = { id: string; employeeId: string; employee: string; employeeNumber: string; leaveType: string; isPaid: boolean; requiresAttachment: boolean; startDate: string; endDate: string; days: number; reason?: string; status: string; approverComment?: string; fullPayDays: number; halfPayDays: number; unpaidDays: number; attachmentFileId?: string; createdAt: string }
type Clash = { employeeName: string; startDate: string; endDate: string; status: string }

export default function LeavePage() {
  const { can } = useAuth()
  const me = useHrMe()
  const hr = can(P.LeaveApprove)
  const [tab, setTab] = useState<'approvals' | 'calendar' | 'holidays' | 'liability' | 'types'>('approvals')
  const tabs = [
    { id: 'approvals' as const, label: hr ? 'Requests' : `Team approvals${me.data?.pendingLeave ? ` (${me.data.pendingLeave})` : ''}` },
    { id: 'calendar' as const, label: 'Team calendar' },
    ...(can(P.OrganisationManage) ? [{ id: 'holidays' as const, label: 'Public holidays' }, { id: 'types' as const, label: 'Leave types' }] : []),
    ...(can(P.SalaryRead) && can(P.ReportsRead) ? [{ id: 'liability' as const, label: 'Leave liability' }] : []),
  ]
  return (
    <>
      <PageHead title="Leave management" sub="Requests route to the line manager. Working days exclude weekends and gazetted public holidays; sick leave follows the Employment Act pay tiers and unpaid days flow into payroll." />
      <Tabs value={tab} onChange={setTab} tabs={tabs} />
      {tab === 'approvals' && <Approvals scope={hr ? 'all' : 'team'} />}
      {tab === 'calendar' && <Calendar />}
      {tab === 'holidays' && <Holidays />}
      {tab === 'types' && <Types />}
      {tab === 'liability' && <Liability />}
    </>
  )
}

function Approvals({ scope }: { scope: 'all' | 'team' }) {
  const qc = useQueryClient()
  const [status, setStatus] = useState('Pending')
  const q = useQuery({ queryKey: ['leave-requests', scope, status], queryFn: () => api.get<Req[]>(`/api/payroll/leave/requests?scope=${scope}${status ? `&status=${status}` : ''}`) })
  const [decide, setDecide] = useState<Req | null>(null)
  const [reject, setReject] = useState<Req | null>(null)
  const refresh = () => { qc.invalidateQueries({ queryKey: ['leave-requests'] }); qc.invalidateQueries({ queryKey: ['hr-me'] }) }
  return (
    <div className="card">
      <div className="toolbar"><select value={status} onChange={e => setStatus(e.target.value)} aria-label="Status"><option value="">All</option>{['Pending', 'Approved', 'Rejected', 'Cancelled'].map(s => <option key={s}>{s}</option>)}</select></div>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No {status.toLowerCase()} requests{scope === 'team' ? ' from your team' : ''}.</Empty> : (
        <div className="table-wrap"><table><thead><tr><th>Employee</th><th>Type</th><th>Dates</th><th className="num">Days</th><th>Pay split</th><th>Evidence</th><th>Reason</th><th>Status</th><th /></tr></thead>
          <tbody>{q.data.map(r => <tr key={r.id}>
            <td>{r.employeeNumber} {r.employee}</td><td>{r.leaveType} {!r.isPaid && <Badge status="Warning">Unpaid</Badge>}</td><td>{fmt.date(r.startDate)} – {fmt.date(r.endDate)}</td><td className="num">{r.days}</td>
            <td className="small">{r.status === 'Approved' ? <>{r.fullPayDays > 0 && `${r.fullPayDays} full `}{r.halfPayDays > 0 && <Badge status="Warning">{r.halfPayDays} half</Badge>} {r.unpaidDays > 0 && <Badge status="Error">{r.unpaidDays} unpaid</Badge>}</> : '—'}</td>
            <td>{r.attachmentFileId ? <FileLink id={r.attachmentFileId} /> : r.requiresAttachment ? <Badge status="Warning">Required</Badge> : ''}</td>
            <td style={{ whiteSpace: 'normal', maxWidth: 220 }}>{r.reason}{r.approverComment && <div className="muted small">{r.approverComment}</div>}</td><td><Badge status={r.status} /></td>
            <td className="actions">{r.status === 'Pending' && <><button className="btn sm primary" onClick={() => setDecide(r)}>Review</button><button className="btn sm danger" onClick={() => setReject(r)}>Reject</button></>}</td></tr>)}</tbody></table></div>)}
      {decide && <ApproveDialog r={decide} onClose={() => { setDecide(null); refresh() }} />}
      {reject && <ReasonDialog title={`Reject leave for ${reject.employee}`} label="Reason" required confirmLabel="Reject" danger onClose={() => setReject(null)} onConfirm={t => api.post(`/api/payroll/leave/requests/${reject.id}/reject`, { comment: t }).then(refresh)} />}
    </div>
  )
}

function ApproveDialog({ r, onClose }: { r: Req; onClose: () => void }) {
  const clashes = useQuery({ queryKey: ['clashes', r.id], queryFn: () => api.get<Clash[]>(`/api/payroll/leave/requests/${r.id}/clashes`) })
  const [comment, setComment] = useState('')
  const m = useMutation({ mutationFn: () => api.post(`/api/payroll/leave/requests/${r.id}/approve`, { comment }), onSuccess: onClose })
  return (
    <Modal title={`Approve ${r.leaveType.toLowerCase()} — ${r.employee}`} onClose={onClose}>
      <dl className="kv"><dt>Dates</dt><dd>{fmt.date(r.startDate)} – {fmt.date(r.endDate)} ({r.days} working day{r.days === 1 ? '' : 's'})</dd><dt>Reason</dt><dd>{r.reason ?? '—'}</dd><dt>Evidence</dt><dd>{r.attachmentFileId ? <FileLink id={r.attachmentFileId} label="Open attachment" /> : r.requiresAttachment ? <Badge status="Error">Missing — approval will be refused</Badge> : 'Not required'}</dd></dl>
      <h3 style={{ marginTop: 12 }}>Team members away at the same time</h3>
      {clashes.isLoading ? <Loading /> : !clashes.data?.length ? <p className="small muted">No overlapping leave in the team.</p> : <ul className="issue-list">{clashes.data.map((c, i) => <li key={i}>{c.employeeName}: {fmt.date(c.startDate)} – {fmt.date(c.endDate)} <Badge status={c.status} /></li>)}</ul>}
      <p className="small muted">The pay split (full, half or unpaid days) is calculated on approval from the days of this type already taken this year.</p>
      <ErrorBox error={m.error} />
      <Field label="Comment"><input value={comment} onChange={e => setComment(e.target.value)} /></Field>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={m.isPending} onClick={() => m.mutate()}>Approve</button></div>
    </Modal>
  )
}

function Calendar() {
  const [month, setMonth] = useState(() => { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), 1) })
  const from = new Date(month.getFullYear(), month.getMonth(), 1), to = new Date(month.getFullYear(), month.getMonth() + 1, 0)
  const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
  const q = useQuery({ queryKey: ['leave-calendar', iso(from)], queryFn: () => api.get<{ holidays: { observedDate: string; name: string }[]; entries: { employeeId: string; employee: string; startDate: string; endDate: string; status: string; leaveType: string }[] }>(`/api/payroll/leave/calendar?from=${iso(from)}&to=${iso(to)}`) })
  const days = Array.from({ length: to.getDate() }, (_, i) => new Date(month.getFullYear(), month.getMonth(), i + 1))
  const people = [...new Map((q.data?.entries ?? []).map(e => [e.employeeId, e.employee])).entries()]
  const holidays = new Map((q.data?.holidays ?? []).map(h => [h.observedDate, h.name]))
  return (
    <div className="card">
      <div className="toolbar"><button className="btn sm" onClick={() => setMonth(new Date(month.getFullYear(), month.getMonth() - 1, 1))}>‹ Previous</button><b>{month.toLocaleDateString('en-GB', { month: 'long', year: 'numeric' })}</b><button className="btn sm" onClick={() => setMonth(new Date(month.getFullYear(), month.getMonth() + 1, 1))}>Next ›</button>
        <span className="small muted"><span className="badge ok">■</span> approved <span className="badge warn">■</span> pending <span className="badge neutral">H</span> public holiday</span></div>
      {q.isLoading ? <Loading /> : <div className="table-wrap"><table style={{ fontSize: 11, tableLayout: 'fixed', minWidth: 160 + days.length * 26 }}>
        <colgroup><col style={{ width: 160 }} />{days.map(d => <col key={d.getDate()} />)}</colgroup>
        <thead><tr><th>Employee</th>{days.map(d => <th key={d.getDate()} className="num" title={holidays.get(iso(d))} style={{ padding: '4px 0', textAlign: 'center', background: d.getDay() % 6 === 0 || holidays.has(iso(d)) ? 'var(--border)' : undefined }}>{d.getDate()}</th>)}</tr></thead>
        <tbody>{people.length === 0 ? <tr><td colSpan={days.length + 1}><Empty>Nobody is away this month.</Empty></td></tr> : people.map(([id, name]) => <tr key={id}><td style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{name}</td>{days.map(d => {
          const e = q.data!.entries.find(x => x.employeeId === id && x.startDate <= iso(d) && x.endDate >= iso(d))
          const off = d.getDay() % 6 === 0 || holidays.has(iso(d))
          return <td key={d.getDate()} title={e ? `${e.leaveType} (${e.status})` : holidays.get(iso(d))} style={{ padding: 2, textAlign: 'center', background: off ? 'color-mix(in srgb, var(--border) 45%, transparent)' : undefined }}>{e && !off ? <span className={`badge ${e.status === 'Approved' ? 'ok' : 'warn'}`} style={{ padding: '0 4px' }}>{e.status === 'Approved' ? '■' : '□'}</span> : holidays.has(iso(d)) ? <span className="muted">H</span> : ''}</td>
        })}</tr>)}</tbody></table></div>}
      {(q.data?.holidays.length ?? 0) > 0 && <p className="small muted">Holidays: {q.data!.holidays.map(h => `${fmt.date(h.observedDate)} ${h.name}`).join(' · ')}</p>}
    </div>
  )
}

function Holidays() {
  const qc = useQueryClient()
  const les = useLegalEntities(); const le = les.data?.[0]?.id
  const [year, setYear] = useState(new Date().getFullYear())
  const q = useQuery({ queryKey: ['holidays', le, year], enabled: !!le, queryFn: () => api.get<{ id: string; date: string; observedDate: string; name: string; isGazettedAdHoc: boolean }[]>(`/api/payroll/leave/holidays?legalEntityId=${le}&year=${year}`) })
  const [f, setF] = useState({ date: '', name: '' })
  const refresh = () => qc.invalidateQueries({ queryKey: ['holidays'] })
  const gen = useMutation({ mutationFn: () => api.post<{ added: number; note: string }>('/api/payroll/leave/holidays/generate', { legalEntityId: le, year }), onSuccess: refresh })
  const add = useMutation({ mutationFn: () => api.post('/api/payroll/leave/holidays', { legalEntityId: le, ...f }), onSuccess: () => { setF({ date: '', name: '' }); refresh() } })
  const del = useMutation({ mutationFn: (id: string) => api.del(`/api/payroll/leave/holidays/${id}`), onSuccess: refresh })
  return (
    <div className="card">
      <div className="toolbar"><input type="number" value={year} onChange={e => setYear(Number(e.target.value))} style={{ width: 90 }} aria-label="Year" /><button className="btn" onClick={() => gen.mutate()} disabled={gen.isPending}>Add Kenyan statutory holidays for {year}</button>
        <span style={{ flex: 1 }} /><input type="date" value={f.date} onChange={e => setF({ ...f, date: e.target.value })} aria-label="Date" /><input placeholder="e.g. Idd-ul-Fitr" value={f.name} onChange={e => setF({ ...f, name: e.target.value })} aria-label="Name" /><button className="btn primary" disabled={!f.date || !f.name || add.isPending} onClick={() => add.mutate()}>Add gazetted holiday</button></div>
      {gen.data && <div className="alert info">{gen.data.added} holiday(s) added. {gen.data.note}</div>}
      <ErrorBox error={gen.error ?? add.error ?? del.error} />
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No holidays for {year}.</Empty> : <table><thead><tr><th>Date</th><th>Observed</th><th>Holiday</th><th /></tr></thead>
        <tbody>{q.data.map(h => <tr key={h.id}><td>{fmt.date(h.date)}</td><td>{h.observedDate !== h.date ? <Badge status="Warning">{fmt.date(h.observedDate)}</Badge> : fmt.date(h.observedDate)}</td><td>{h.name} {h.isGazettedAdHoc && <span className="muted small">(gazetted)</span>}</td><td><button className="btn sm danger" onClick={() => del.mutate(h.id)}>Remove</button></td></tr>)}</tbody></table>}
      <p className="small muted">A holiday falling on a Sunday is observed on the next day that is not already a holiday (Public Holidays Act).</p>
    </div>
  )
}

function Types() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['leave-types'], queryFn: () => api.get<(LeaveType & { fullPayDaysPerYear?: number; halfPayDaysPerYear: number; minServiceMonths: number; countsWorkingDaysOnly: boolean })[]>('/api/payroll/leave/types') })
  const [edit, setEdit] = useState<Record<string, unknown> | null>(null)
  const m = useMutation({ mutationFn: (t: Record<string, unknown>) => api.put(`/api/payroll/leave/types/${t.id}`, t), onSuccess: () => { qc.invalidateQueries({ queryKey: ['leave-types'] }); setEdit(null) } })
  return (
    <div className="card">
      {q.isLoading ? <Loading /> : <table><thead><tr><th>Code</th><th>Name</th><th className="num">Entitlement</th><th>Paid</th><th>Pay tiers</th><th>Counts</th><th>Min. service</th><th>Evidence</th><th /></tr></thead>
        <tbody>{q.data?.map(t => <tr key={t.id}><td className="mono">{t.code}</td><td>{t.name}</td><td className="num">{t.annualEntitlementDays}</td><td>{t.isPaid ? 'Yes' : <Badge status="Warning">Unpaid</Badge>}</td>
          <td className="small">{t.fullPayDaysPerYear != null ? `${t.fullPayDaysPerYear} full + ${t.halfPayDaysPerYear} half` : 'Full pay'}</td><td className="small">{t.countsWorkingDaysOnly ? 'Working days' : 'Calendar days'}</td><td className="small">{t.minServiceMonths ? `${t.minServiceMonths} months` : '—'}</td><td>{t.requiresAttachment ? 'Required' : '—'}</td>
          <td><button className="btn sm" onClick={() => setEdit({ ...t })}>Edit</button></td></tr>)}</tbody></table>}
      {edit && <Modal title={`Edit ${String(edit.name)}`} onClose={() => setEdit(null)}>
        <ErrorBox error={m.error} />
        <div className="form-grid">
          <Field label="Name"><input value={String(edit.name)} onChange={e => setEdit({ ...edit, name: e.target.value })} /></Field>
          <Field label="Annual entitlement (days)"><input type="number" value={Number(edit.annualEntitlementDays)} onChange={e => setEdit({ ...edit, annualEntitlementDays: Number(e.target.value) })} /></Field>
          <Field label="Max carry-over"><input type="number" value={Number(edit.maxCarryOverDays)} onChange={e => setEdit({ ...edit, maxCarryOverDays: Number(e.target.value) })} /></Field>
          <Field label="Full-pay days per year (blank = all)"><input type="number" value={edit.fullPayDaysPerYear == null ? '' : Number(edit.fullPayDaysPerYear)} onChange={e => setEdit({ ...edit, fullPayDaysPerYear: e.target.value === '' ? null : Number(e.target.value) })} /></Field>
          <Field label="Half-pay days per year"><input type="number" value={Number(edit.halfPayDaysPerYear)} onChange={e => setEdit({ ...edit, halfPayDaysPerYear: Number(e.target.value) })} /></Field>
          <Field label="Minimum service (months)"><input type="number" value={Number(edit.minServiceMonths)} onChange={e => setEdit({ ...edit, minServiceMonths: Number(e.target.value) })} /></Field>
        </div>
        {(['isPaid', 'accruesMonthly', 'requiresAttachment', 'countsWorkingDaysOnly', 'isActive'] as const).map(k => <label key={k} className="field inline"><input type="checkbox" checked={!!edit[k]} onChange={e => setEdit({ ...edit, [k]: e.target.checked })} /> {({ isPaid: 'Paid', accruesMonthly: 'Accrues monthly', requiresAttachment: 'Requires evidence', countsWorkingDaysOnly: 'Count working days only (untick for calendar days, e.g. maternity)', isActive: 'Active' })[k]}</label>)}
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setEdit(null)}>Cancel</button><button className="btn primary" disabled={m.isPending} onClick={() => m.mutate(edit)}>Save</button></div>
      </Modal>}
    </div>
  )
}

function Liability() {
  const { can } = useAuth()
  const les = useLegalEntities(); const le = les.data?.[0]?.id
  const [asOf, setAsOf] = useState(new Date().toISOString().slice(0, 10))
  const q = useQuery({ queryKey: ['leave-liability', le, asOf], enabled: !!le, queryFn: () => api.get<{ rows: { employeeId: string; employeeNumber: string; employeeName: string; department?: string; daysAvailable: number; dailyRate: number; liability: number }[]; totalDays: number; total: number }>(`/api/payroll/leave/liability?legalEntityId=${le}&asOf=${asOf}`) })
  const post = useMutation({ mutationFn: () => api.post<{ reference: string; narrative: string; lines: { accountCode: string; description: string; debit: number; credit: number }[] }>('/api/payroll/leave/liability/post', { legalEntityId: le, asOf }) })
  return (
    <div className="card">
      <div className="toolbar"><label className="small muted">As of</label><input type="date" value={asOf} onChange={e => setAsOf(e.target.value)} aria-label="As of" /><span style={{ flex: 1 }} />
        {['xlsx', 'pdf'].map(f => <button key={f} className="btn sm" onClick={() => api.download(`/api/payroll/leave/liability?legalEntityId=${le}&asOf=${asOf}&format=${f}`)}>{f === 'xlsx' ? 'Excel' : 'PDF'}</button>)}
        {can(P.PayrollFinalise) && <button className="btn primary" disabled={post.isPending} onClick={() => post.mutate()}>Post provision journal</button>}</div>
      <ErrorBox error={q.error ?? post.error} />
      {post.data && <div className="alert ok">Posted {post.data.reference}: {post.data.narrative}. {post.data.lines.map(l => `${l.accountCode} ${l.debit ? 'Dr ' + fmt.num(l.debit) : 'Cr ' + fmt.num(l.credit)}`).join(' · ')}</div>}
      {q.isLoading ? <Loading /> : <><div className="table-wrap"><table><thead><tr><th>No.</th><th>Employee</th><th>Department</th><th className="num">Days available</th><th className="num">Daily rate</th><th className="num">Liability</th></tr></thead>
        <tbody>{q.data?.rows.map(r => <tr key={r.employeeId}><td className="mono">{r.employeeNumber}</td><td>{r.employeeName}</td><td>{r.department}</td><td className="num">{r.daysAvailable}</td><td className="num">{fmt.num(r.dailyRate)}</td><td className="num">{fmt.num(r.liability)}</td></tr>)}</tbody>
        <tfoot><tr><td colSpan={3}>Total</td><td className="num">{q.data?.totalDays}</td><td /><td className="num">{fmt.num(q.data?.total)}</td></tr></tfoot></table></div>
        <p className="small muted">Untaken accruing paid leave valued at basic ÷ working days. Posting records the movement since the last posting (Dr 6160 leave pay expense / Cr 2195 leave pay provision).</p></>}
    </div>
  )
}
