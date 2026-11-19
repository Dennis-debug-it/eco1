import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, fmt, isoDate } from '../lib/api'
import { useAuth } from '../lib/auth'
import { useHrMe } from '../lib/lookups'
import type { EmployeeDetail, LeaveBalance, LeaveType, Payslip } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, FileLink, Loading, PageHead, Stat, Stars, Tabs, UploadButton } from '../components/ui'
import { PayslipList } from './EmployeeDetail'
import { ReviewDialog } from './Performance'
import { MyClaims } from './Expenses'

type Tab = 'overview' | 'payslips' | 'leave' | 'performance' | 'learning' | 'expenses' | 'records' | 'profile'

export default function SelfServicePage() {
  const { user } = useAuth()
  const me = useHrMe()
  const id = user?.employeeId
  const [tab, setTab] = useState<Tab>('overview')
  if (!id) return <div className="card"><Empty>Your account is not linked to an employee record. Ask HR to link it.</Empty></div>
  const perfBadge = (me.data?.feedbackRequested ?? 0) + (me.data?.myReviewOpen ? 1 : 0)
  return (
    <>
      <PageHead title="My workspace" sub="Payslips, leave, performance, learning, expenses and your records" />
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'overview', label: 'Overview' }, { id: 'payslips', label: 'Payslips & tax' }, { id: 'leave', label: 'Leave' }, { id: 'performance', label: `Performance${perfBadge ? ` (${perfBadge})` : ''}` }, { id: 'learning', label: 'Learning' }, { id: 'expenses', label: 'Expenses' }, { id: 'records', label: 'Documents & benefits' }, { id: 'profile', label: 'My details' }]} />
      {tab === 'overview' && <Overview employeeId={id} />}
      {tab === 'payslips' && <><PayslipList employeeId={id} /><P9 employeeId={id} /></>}
      {tab === 'leave' && <MyLeave employeeId={id} />}
      {tab === 'performance' && <MyPerformance employeeId={id} />}
      {tab === 'learning' && <MyLearning />}
      {tab === 'expenses' && <MyClaims />}
      {tab === 'records' && <MyRecords employeeId={id} />}
      {tab === 'profile' && <Profile employeeId={id} />}
    </>
  )
}

function Overview({ employeeId }: { employeeId: string }) {
  const slips = useQuery({ queryKey: ['payslips', employeeId], queryFn: () => api.get<(Payslip & { reversed?: boolean })[]>(`/api/payroll/employees/${employeeId}/payslips`) })
  const bal = useQuery({ queryKey: ['leave-balances', employeeId], queryFn: () => api.get<LeaveBalance[]>(`/api/payroll/leave/balances/${employeeId}`) })
  const me = useHrMe()
  const latest = slips.data?.find(s => !s.reversed)
  const annual = bal.data?.find(b => b.code === 'ANNUAL') ?? bal.data?.[0]
  const todo = [
    me.data?.myReviewOpen && 'Your performance review needs your input or acknowledgement.',
    (me.data?.feedbackRequested ?? 0) > 0 && `${me.data!.feedbackRequested} colleague(s) asked for your 360 feedback.`,
    (me.data?.pendingLeave ?? 0) > 0 && `${me.data!.pendingLeave} leave request(s) from your team await your approval.`,
    (me.data?.pendingExpenses ?? 0) > 0 && `${me.data!.pendingExpenses} expense claim(s) from your team await your approval.`,
    (me.data?.pendingNominations ?? 0) > 0 && `${me.data!.pendingNominations} training nomination(s) await your approval.`,
    (me.data?.reviewsToComplete ?? 0) > 0 && `${me.data!.reviewsToComplete} team review(s) to complete.`,
  ].filter(Boolean) as string[]
  return (
    <>
      {todo.length > 0 && <div className="alert info" role="status"><b>To do:</b><ul className="issue-list">{todo.map(t => <li key={t}>{t}</li>)}</ul></div>}
      <div className="grid c4">
        <Stat label="Latest net pay" value={latest ? fmt.money(latest.netPay, latest.currencyCode) : '—'} hint={latest ? `${fmt.date(latest.periodStart)} – ${fmt.date(latest.periodEnd)}` : 'No payslip yet'} />
        <Stat label="Latest gross pay" value={latest ? fmt.money(latest.grossPay, latest.currencyCode) : '—'} />
        <Stat label="Deductions" value={latest ? fmt.money(latest.grossPay - latest.netPay, latest.currencyCode) : '—'} hint="Statutory and other deductions" />
        <Stat label={annual ? `${annual.leaveType} available` : 'Leave available'} value={annual ? `${annual.available} days` : '—'} hint={annual ? `${annual.taken} taken of ${annual.carriedOver + annual.accrued} accrued` : undefined} />
      </div>
      <div className="grid c2" style={{ marginTop: 16 }}>
        <div className="card"><h2>Recent payslips</h2>{slips.isLoading ? <Loading /> : !slips.data?.length ? <Empty>No payslips yet.</Empty> : <table><tbody>{slips.data.slice(0, 6).map(p => <tr key={p.id}><td>{fmt.date(p.periodEnd)}</td><td className="mono">{p.payslipNumber} {p.reversed && <Badge status="Reversed">Reversed</Badge>}</td><td className="num">{fmt.num(p.netPay)}</td><td><button className="btn sm" onClick={() => api.download(`/api/payroll/payslips/${p.id}/pdf`)}>PDF</button></td></tr>)}</tbody></table>}</div>
        <div className="card"><h2>Leave balances {bal.data?.[0] && <span className="muted small">{bal.data[0].year}</span>}</h2>{bal.isLoading ? <Loading /> : !bal.data?.length ? <Empty>No balances.</Empty> : <table><thead><tr><th>Type</th><th className="num">Entitlement</th><th className="num">Accrued</th><th className="num">Taken</th><th className="num">Available</th></tr></thead><tbody>{bal.data.map(b => <tr key={b.id}><td>{b.leaveType}</td><td className="num">{b.entitlement}</td><td className="num">{b.accrued + b.carriedOver}</td><td className="num">{b.taken}</td><td className="num"><b>{b.available}</b></td></tr>)}</tbody></table>}</div>
      </div>
    </>
  )
}

type MyReq = { id: string; leaveType: string; requiresAttachment: boolean; startDate: string; endDate: string; days: number; status: string; approverComment?: string; attachmentFileId?: string; fullPayDays: number; halfPayDays: number; unpaidDays: number }

function MyLeave({ employeeId }: { employeeId: string }) {
  const qc = useQueryClient()
  const types = useQuery({ queryKey: ['leave-types'], queryFn: () => api.get<LeaveType[]>('/api/payroll/leave/types') })
  const reqs = useQuery({ queryKey: ['my-leave'], queryFn: () => api.get<MyReq[]>('/api/payroll/leave/requests?scope=mine') })
  const [f, setF] = useState({ leaveTypeId: '', startDate: '', endDate: '', halfDay: false, reason: '' })
  const typeId = f.leaveTypeId || types.data?.find(t => t.isActive)?.id
  const days = useQuery({ queryKey: ['leave-days', typeId, f.startDate, f.endDate], enabled: !!typeId && !!f.startDate && !!f.endDate, queryFn: () => api.get<{ days: number }>(`/api/payroll/leave/days?leaveTypeId=${typeId}&start=${f.startDate}&end=${f.endDate}`) })
  const [result, setResult] = useState<{ warnings: string[]; clashes: { employeeName: string; startDate: string; endDate: string }[] } | null>(null)
  const refresh = () => { qc.invalidateQueries({ queryKey: ['my-leave'] }); qc.invalidateQueries({ queryKey: ['leave-balances', employeeId] }) }
  const m = useMutation({
    mutationFn: () => api.post<{ warnings: string[]; clashes: { employeeName: string; startDate: string; endDate: string }[] }>('/api/payroll/leave/requests', { leaveTypeId: typeId, startDate: f.startDate, endDate: f.endDate, reason: f.reason, days: f.halfDay ? (days.data?.days ?? 1) - 0.5 : undefined }),
    onSuccess: r => { setResult(r); setF({ ...f, reason: '', startDate: '', endDate: '', halfDay: false }); refresh() },
  })
  const cancel = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/leave/requests/${id}/cancel`), onSuccess: refresh })
  const type = types.data?.find(t => t.id === typeId) as (LeaveType & { requiresAttachment: boolean }) | undefined
  return (
    <div className="grid c2">
      <div className="card"><h2>Apply for leave</h2><ErrorBox error={m.error} />
        <Field label="Leave type"><select value={typeId ?? ''} onChange={e => setF({ ...f, leaveTypeId: e.target.value })}>{types.data?.filter(t => t.isActive).map(t => <option key={t.id} value={t.id}>{t.name}{t.isPaid ? '' : ' (unpaid)'}</option>)}</select></Field>
        <div className="form-grid">
          <Field label="First day"><input type="date" value={f.startDate} onChange={e => setF({ ...f, startDate: e.target.value, endDate: f.endDate && f.endDate >= e.target.value ? f.endDate : e.target.value })} /></Field>
          <Field label="Last day"><input type="date" value={f.endDate} min={f.startDate} onChange={e => setF({ ...f, endDate: e.target.value })} /></Field>
        </div>
        {days.data && <p className="small">{f.halfDay ? days.data.days - 0.5 : days.data.days} working day(s) — weekends and public holidays are excluded automatically.</p>}
        <label className="field inline"><input type="checkbox" checked={f.halfDay} onChange={e => setF({ ...f, halfDay: e.target.checked })} /> Last day is a half day</label>
        <Field label="Reason"><input value={f.reason} onChange={e => setF({ ...f, reason: e.target.value })} /></Field>
        {type?.requiresAttachment && <p className="small muted">This leave needs supporting evidence: attach it to the request below once submitted.</p>}
        <button className="btn primary" disabled={!f.startDate || !f.endDate || m.isPending} onClick={() => m.mutate()}>Submit request</button>
        {result && (result.warnings.length > 0 || result.clashes.length > 0) && <div className="alert warn" style={{ marginTop: 10 }}>{result.warnings.map(w => <div key={w}>{w}</div>)}{result.clashes.length > 0 && <div>Also away: {result.clashes.map(c => `${c.employeeName} (${fmt.date(c.startDate)} – ${fmt.date(c.endDate)})`).join(', ')}</div>}</div>}
        {result && !result.warnings.length && !result.clashes.length && <div className="alert ok" style={{ marginTop: 10 }}>Submitted to your manager.</div>}
      </div>
      <div className="card"><h2>My requests</h2>{reqs.isLoading ? <Loading /> : !reqs.data?.length ? <Empty>No leave requests.</Empty> : <table><thead><tr><th>Type</th><th>Dates</th><th className="num">Days</th><th>Status</th><th /></tr></thead>
        <tbody>{reqs.data.map(r => <tr key={r.id}><td>{r.leaveType}</td><td>{fmt.date(r.startDate)} – {fmt.date(r.endDate)}</td><td className="num">{r.days}</td><td><Badge status={r.status} />{r.halfPayDays + r.unpaidDays > 0 && <div className="small muted">{r.halfPayDays > 0 && `${r.halfPayDays} half-pay `}{r.unpaidDays > 0 && `${r.unpaidDays} unpaid`}</div>}{r.approverComment && <div className="muted small">{r.approverComment}</div>}</td>
          <td className="actions">{r.status === 'Pending' && (r.attachmentFileId ? <FileLink id={r.attachmentFileId} label="Evidence" /> : <UploadButton label={r.requiresAttachment ? 'Attach evidence' : 'Attach'} url={`/api/payroll/leave/requests/${r.id}/attachment`} onDone={refresh} />)}
            {(r.status === 'Pending' || r.status === 'Approved') && <button className="btn sm" onClick={() => cancel.mutate(r.id)}>Cancel</button>}</td></tr>)}</tbody></table>}
        <ErrorBox error={cancel.error} />
      </div>
    </div>
  )
}

function MyPerformance({ employeeId }: { employeeId: string }) {
  const qc = useQueryClient()
  const cycles = useQuery({ queryKey: ['cycles'], queryFn: () => api.get<{ id: string; name: string; status: string }[]>('/api/payroll/performance/cycles') })
  const cycle = cycles.data?.[0]
  const reviews = useQuery({ queryKey: ['my-reviews', cycle?.id], enabled: !!cycle, queryFn: () => api.get<{ id: string; status: string; managerScore?: number; finalRating?: number }[]>(`/api/payroll/performance/reviews?cycleId=${cycle!.id}&scope=mine`) })
  const goals = useQuery({ queryKey: ['goals', cycle?.id, employeeId], enabled: !!cycle, queryFn: () => api.get<{ id: string; title: string; target?: string; weightPercent: number; progressPercent: number; status: string }[]>(`/api/payroll/performance/goals?cycleId=${cycle!.id}&employeeId=${employeeId}`) })
  const feedback = useQuery({ queryKey: ['feedback-mine'], queryFn: () => api.get<{ id: string; relationship: string; subject: string; jobTitle?: string }[]>('/api/payroll/performance/feedback/mine') })
  const [open, setOpen] = useState<string | null>(null)
  const [g, setG] = useState({ title: '', target: '', weightPercent: 0 })
  const refresh = () => { qc.invalidateQueries({ queryKey: ['goals'] }); qc.invalidateQueries({ queryKey: ['my-reviews'] }); qc.invalidateQueries({ queryKey: ['hr-me'] }) }
  const addGoal = useMutation({ mutationFn: () => api.post('/api/payroll/performance/goals', { reviewCycleId: cycle!.id, employeeId, ...g }), onSuccess: () => { setG({ title: '', target: '', weightPercent: 0 }); refresh() } })
  const [fb, setFb] = useState<Record<string, { rating: number; strengths: string; improvements: string }>>({})
  const give = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/performance/feedback/${id}`, fb[id] ?? { rating: 3 }), onSuccess: () => { qc.invalidateQueries({ queryKey: ['feedback-mine'] }); refresh() } })
  const review = reviews.data?.[0]
  const total = (goals.data ?? []).reduce((s, x) => s + x.weightPercent, 0)
  if (!cycle) return <div className="card"><Empty>No review cycle is running.</Empty></div>
  return (
    <div className="grid c2">
      <div className="card"><h2>{cycle.name}</h2>
        {!review ? <Empty>You're not included in this cycle.</Empty> : <dl className="kv"><dt>Status</dt><dd><Badge status="neutral">{review.status.replace(/([A-Z])/g, ' $1').trim()}</Badge></dd><dt>Manager score</dt><dd><Stars value={review.managerScore} /></dd><dt>Final rating</dt><dd><Stars value={review.finalRating} /></dd></dl>}
        {review && <button className="btn primary" style={{ marginTop: 10 }} onClick={() => setOpen(review.id)}>{review.status === 'SelfAssessment' ? 'Complete self assessment' : review.status === 'Completed' ? 'Read & acknowledge' : 'View review'}</button>}
      </div>
      <div className="card"><h2>My goals <Badge status={total === 100 ? 'Active' : 'Warning'}>{total}%</Badge></h2>
        <ErrorBox error={addGoal.error} />
        {!goals.data?.length ? <Empty>No goals yet — propose them and your manager will agree them.</Empty> : <table><tbody>{goals.data.map(x => <tr key={x.id}><td style={{ whiteSpace: 'normal' }}>{x.title}{x.target && <div className="muted small">Target {x.target}</div>}</td><td className="num">{x.weightPercent}%</td><td><Badge status="neutral">{x.status}</Badge></td><td className="num">{x.progressPercent}%</td></tr>)}</tbody></table>}
        {(cycle.status === 'GoalSetting' || cycle.status === 'Review') && <div className="toolbar" style={{ marginTop: 8 }}><input placeholder="Goal" value={g.title} onChange={e => setG({ ...g, title: e.target.value })} aria-label="Goal" style={{ flex: 1 }} /><input placeholder="Target" value={g.target} onChange={e => setG({ ...g, target: e.target.value })} aria-label="Target" style={{ width: 100 }} /><input type="number" placeholder="Weight %" value={g.weightPercent || ''} onChange={e => setG({ ...g, weightPercent: Number(e.target.value) })} aria-label="Weight" style={{ width: 90 }} /><button className="btn sm" disabled={!g.title} onClick={() => addGoal.mutate()}>Propose</button></div>}
      </div>
      <div className="card" style={{ gridColumn: '1 / -1' }}><h2>360 feedback requested from you</h2>
        <ErrorBox error={give.error} />
        {!feedback.data?.length ? <Empty>No requests.</Empty> : feedback.data.map(r => <div key={r.id} className="toolbar" style={{ borderBottom: '1px solid var(--border)', paddingBottom: 8 }}>
          <b>{r.subject}</b><span className="muted small">{r.jobTitle} · as {r.relationship}</span>
          <select value={fb[r.id]?.rating ?? 3} onChange={e => setFb({ ...fb, [r.id]: { ...(fb[r.id] ?? { strengths: '', improvements: '' }), rating: Number(e.target.value) } })} aria-label="Rating">{[5, 4, 3, 2, 1].map(n => <option key={n} value={n}>{n}</option>)}</select>
          <input placeholder="Strengths" onChange={e => setFb({ ...fb, [r.id]: { ...(fb[r.id] ?? { rating: 3, improvements: '' }), strengths: e.target.value } })} aria-label="Strengths" />
          <input placeholder="Could improve" onChange={e => setFb({ ...fb, [r.id]: { ...(fb[r.id] ?? { rating: 3, strengths: '' }), improvements: e.target.value } })} aria-label="Improvements" />
          <button className="btn sm primary" onClick={() => give.mutate(r.id)}>Send</button></div>)}
        <p className="small muted">Feedback is shown to the employee unattributed, after their review is complete.</p>
      </div>
      {open && <ReviewDialog id={open} onClose={() => { setOpen(null); refresh() }} />}
    </div>
  )
}

function MyLearning() {
  const qc = useQueryClient()
  const { user } = useAuth()
  const mine = useQuery({ queryKey: ['my-learning'], queryFn: () => api.get<{ id: string; course: string; durationHours: number; startDate: string; endDate: string; status: string; certificateNumber?: string; expiresOn?: string }[]>('/api/payroll/learning/mine') })
  const sessions = useQuery({ queryKey: ['sessions'], queryFn: () => api.get<{ id: string; course: string; startDate: string; venue?: string; capacity: number; enrolled: number; status: string }[]>('/api/payroll/learning/sessions') })
  const [err, setErr] = useState<unknown>(null)
  const join = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/learning/sessions/${id}/nominate`, { employeeId: user!.employeeId }), onSuccess: () => { qc.invalidateQueries({ queryKey: ['my-learning'] }); qc.invalidateQueries({ queryKey: ['sessions'] }) }, onError: setErr })
  const soon = isoDate(60)
  const upcoming = sessions.data?.filter(s => s.status === 'Planned') ?? []
  return (
    <div className="grid c2">
      <div className="card"><h2>My training & certificates</h2>{!mine.data?.length ? <Empty>No training recorded.</Empty> : <table><thead><tr><th>Course</th><th>Dates</th><th>Status</th><th>Certificate</th><th>Expires</th></tr></thead>
        <tbody>{mine.data.map(e => <tr key={e.id}><td>{e.course}</td><td>{fmt.date(e.startDate)}</td><td><Badge status={e.status === 'Completed' ? 'Active' : e.status === 'Nominated' ? 'Pending' : 'neutral'}>{e.status}</Badge></td><td className="mono small">{e.certificateNumber}</td><td>{e.expiresOn ? (e.expiresOn <= soon ? <Badge status="Warning">{fmt.date(e.expiresOn)}</Badge> : fmt.date(e.expiresOn)) : ''}</td></tr>)}</tbody></table>}</div>
      <div className="card"><h2>Upcoming sessions</h2><ErrorBox error={err instanceof ApiError ? err : null} />{!upcoming.length ? <Empty>Nothing scheduled.</Empty> : <table><tbody>{upcoming.map(s => <tr key={s.id}><td>{s.course}<div className="muted small">{fmt.date(s.startDate)} · {s.venue}</div></td><td className="num">{s.enrolled}/{s.capacity}</td><td>{mine.data?.some(m => m.course === s.course && m.startDate === s.startDate) ? <Badge status="Pending">Requested</Badge> : <button className="btn sm" onClick={() => join.mutate(s.id)}>Request a place</button>}</td></tr>)}</tbody></table>}
        <p className="small muted">Requests go to your line manager for approval.</p></div>
    </div>
  )
}

function MyRecords({ employeeId }: { employeeId: string }) {
  const qc = useQueryClient()
  const docs = useQuery({ queryKey: ['my-docs'], queryFn: () => api.get<{ id: string; category: string; title: string; storedFileId: string; expiresOn?: string; version: number }[]>(`/api/payroll/documents?employeeId=${employeeId}`) })
  const deps = useQuery({ queryKey: ['dependants', employeeId], queryFn: () => api.get<{ id: string; fullName: string; relationship: string; dateOfBirth?: string; isBeneficiary: boolean; beneficiaryPercent: number }[]>(`/api/payroll/benefits/dependants/${employeeId}`) })
  const cover = useQuery({ queryKey: ['my-cover'], queryFn: () => api.get<{ id: string; scheme: string; provider: string; cover: string; startDate: string; membershipNumber?: string; premiumByCover: Record<string, number>; employeeSharePercent: number }[]>(`/api/payroll/benefits/enrolments?employeeId=${employeeId}`) })
  const assets = useQuery({ queryKey: ['my-assets'], queryFn: () => api.get<{ assignmentId: string; tag: string; name: string; serialNumber?: string; issuedOn: string; returnedOn?: string }[]>(`/api/payroll/assets?employeeId=${employeeId}`) })
  const [d, setD] = useState({ fullName: '', relationship: 'Spouse', dateOfBirth: '', isBeneficiary: false, beneficiaryPercent: 0 })
  const add = useMutation({ mutationFn: () => api.post('/api/payroll/benefits/dependants', { ...d, employeeId, dateOfBirth: d.dateOfBirth || null }), onSuccess: () => { setD({ fullName: '', relationship: 'Spouse', dateOfBirth: '', isBeneficiary: false, beneficiaryPercent: 0 }); qc.invalidateQueries({ queryKey: ['dependants'] }) } })
  return (
    <div className="grid c2">
      <div className="card"><h2>My documents</h2>{!docs.data?.length ? <Empty>No documents shared with you.</Empty> : <table><tbody>{docs.data.map(x => <tr key={x.id}><td>{x.title}<div className="muted small">{x.category} · v{x.version}{x.expiresOn && ` · expires ${fmt.date(x.expiresOn)}`}</div></td><td><FileLink id={x.storedFileId} label="Download" /></td></tr>)}</tbody></table>}</div>
      <div className="card"><h2>Medical cover</h2>{!cover.data?.length ? <Empty>Not enrolled.</Empty> : cover.data.map(c => <dl key={c.id} className="kv"><dt>Scheme</dt><dd>{c.scheme} ({c.provider})</dd><dt>Cover</dt><dd>{c.cover} since {fmt.date(c.startDate)}</dd><dt>Member no.</dt><dd className="mono">{c.membershipNumber ?? '—'}</dd><dt>Your share</dt><dd>{fmt.money((c.premiumByCover[c.cover] ?? 0) * c.employeeSharePercent / 100)} / month via payroll</dd></dl>)}</div>
      <div className="card"><h2>Dependants & beneficiaries</h2><ErrorBox error={add.error} />
        {!deps.data?.length ? <Empty>None recorded.</Empty> : <table><tbody>{deps.data.map(x => <tr key={x.id}><td>{x.fullName}</td><td>{x.relationship}</td><td>{fmt.date(x.dateOfBirth)}</td><td>{x.isBeneficiary ? `Beneficiary ${x.beneficiaryPercent}%` : ''}</td></tr>)}</tbody></table>}
        <div className="form-grid" style={{ marginTop: 8 }}><Field label="Full name"><input value={d.fullName} onChange={e => setD({ ...d, fullName: e.target.value })} /></Field><Field label="Relationship"><select value={d.relationship} onChange={e => setD({ ...d, relationship: e.target.value })}>{['Spouse', 'Child', 'Parent', 'Sibling', 'Other'].map(r => <option key={r}>{r}</option>)}</select></Field><Field label="Date of birth"><input type="date" value={d.dateOfBirth} onChange={e => setD({ ...d, dateOfBirth: e.target.value })} /></Field></div>
        <label className="field inline"><input type="checkbox" checked={d.isBeneficiary} onChange={e => setD({ ...d, isBeneficiary: e.target.checked })} /> Nominate as beneficiary</label>
        {d.isBeneficiary && <Field label="Share %"><input type="number" value={d.beneficiaryPercent} onChange={e => setD({ ...d, beneficiaryPercent: Number(e.target.value) })} /></Field>}
        <button className="btn" disabled={!d.fullName || add.isPending} onClick={() => add.mutate()}>Add</button></div>
      <div className="card"><h2>Company property with me</h2>{!assets.data?.filter(a => !a.returnedOn).length ? <Empty>None.</Empty> : <table><tbody>{assets.data.filter(a => !a.returnedOn).map(a => <tr key={a.assignmentId}><td className="mono">{a.tag}</td><td>{a.name}</td><td className="mono small">{a.serialNumber}</td><td>since {fmt.date(a.issuedOn)}</td></tr>)}</tbody></table>}</div>
    </div>
  )
}

function Profile({ employeeId }: { employeeId: string }) {
  const q = useQuery({ queryKey: ['employee', employeeId], queryFn: () => api.get<EmployeeDetail>(`/api/payroll/employees/${employeeId}`) })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const e = q.data!.employee
  return (
    <div className="grid c2">
      <div className="card"><h2>Personal details</h2><dl className="kv"><dt>Employee number</dt><dd className="mono">{e.employeeNumber}</dd><dt>Name</dt><dd>{e.firstName} {e.middleName} {e.lastName}</dd><dt>Job title</dt><dd>{e.jobTitle}</dd><dt>Email</dt><dd>{e.email}</dd><dt>Phone</dt><dd>{e.phone}</dd><dt>Hire date</dt><dd>{fmt.date(e.hireDate)}</dd><dt>Employment</dt><dd>{e.employmentType} · <Badge status={e.status} /></dd></dl>
        {e.terminationDate && <button className="btn" style={{ marginTop: 10 }} onClick={() => api.download(`/api/payroll/employees/${employeeId}/certificate-of-service.pdf`)}>Certificate of service</button>}
        <p className="muted small">To change personal details, contact HR — changes are audited.</p>
        <h2 style={{ marginTop: 14 }}>Your data</h2>
        <p className="small">Under the Data Protection Act 2019 you can get a copy of the personal data held about you, and ask HR to correct anything that's wrong.</p>
        <button className="btn" onClick={() => api.download('/api/payroll/self-service/data-export')}>Download my data (JSON)</button></div>
      <div className="card"><h2>Bank accounts</h2>{!q.data!.bankAccounts.length ? <Empty>No bank account on file.</Empty> : <table><tbody>{q.data!.bankAccounts.map(b => <tr key={b.id}><td>{b.bankName}</td><td className="mono">{b.accountNumber}</td><td>{b.isPrimary && <Badge status="Active">Primary</Badge>}</td><td>{b.isVerified ? <Badge status="Active">Verified</Badge> : <Badge status="Pending">Awaiting verification</Badge>}</td></tr>)}</tbody></table>}
        <h2 style={{ marginTop: 14 }}>Statutory numbers</h2><dl className="kv"><dt>KRA PIN</dt><dd className="mono">{q.data!.statutory?.kraPin ?? '—'}</dd><dt>NSSF</dt><dd className="mono">{q.data!.statutory?.nssfNumber ?? '—'}</dd><dt>SHIF</dt><dd className="mono">{q.data!.statutory?.shifNumber ?? '—'}</dd></dl></div>
    </div>
  )
}

function P9({ employeeId }: { employeeId: string }) {
  const q = useQuery({ queryKey: ['employee', employeeId], queryFn: () => api.get<EmployeeDetail>(`/api/payroll/employees/${employeeId}`) })
  const [year, setYear] = useState(new Date().getFullYear())
  const [err, setErr] = useState<unknown>(null)
  const le = q.data?.employee.legalEntityId
  return (
    <div className="card"><h2>Tax deduction card (P9)</h2>
      <p className="muted small">Your annual summary of pay, tax and statutory deductions for filing your KRA return.</p>
      <ErrorBox error={err} />
      <div className="toolbar"><input type="number" value={year} onChange={e => setYear(Number(e.target.value))} style={{ width: 90 }} aria-label="Year" />
        <button className="btn" disabled={!le} onClick={async () => { setErr(null); try { await api.download(`/api/payroll/reports/annual?legalEntityId=${le}&year=${year}&employeeId=${employeeId}&format=pdf`) } catch (e) { setErr(e) } }}>Download PDF</button></div>
    </div>
  )
}
