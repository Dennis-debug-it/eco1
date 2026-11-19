import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt, isoDate } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useHrMe, useLegalEntities } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, ReasonDialog, Stars, Tabs } from '../components/ui'

type Cycle = { id: string; name: string; type: string; periodStart: string; periodEnd: string; status: string; goalWeightPercent: number; competencies: string[] }
type ReviewRow = { id: string; employeeId: string; employeeNumber: string; employee: string; jobTitle?: string; status: string; managerScore?: number; finalRating?: number; reviewer?: string }
type Goal = { id: string; employeeId?: string; departmentId?: string; parentGoalId?: string; title: string; description?: string; measure?: string; target?: string; weightPercent: number; progressPercent: number; dueDate?: string; status: string }
const next: Record<string, string> = { Draft: 'GoalSetting', GoalSetting: 'Review', Review: 'Calibration', Calibration: 'Closed' }
const human = (s: string) => s.replace(/([A-Z])/g, ' $1').trim()

export default function PerformancePage() {
  const { can } = useAuth()
  const hr = can(P.HrPerformance)
  const cycles = useQuery({ queryKey: ['cycles'], queryFn: () => api.get<Cycle[]>('/api/payroll/performance/cycles') })
  const [sel, setSel] = useState('')
  const cycle = cycles.data?.find(c => c.id === sel) ?? cycles.data?.[0]
  const [tab, setTab] = useState<'reviews' | 'goals' | 'plans' | 'comp' | 'cycles'>('reviews')
  return (
    <>
      <PageHead title="Performance" sub="Goals → self assessment → manager review → calibration → acknowledgement. Final ratings drive the compensation review.">
        {cycles.data && cycles.data.length > 1 && <select value={cycle?.id} onChange={e => setSel(e.target.value)} aria-label="Cycle">{cycles.data.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select>}
      </PageHead>
      {cycle && <div className="steps">{['Draft', 'GoalSetting', 'Review', 'Calibration', 'Closed'].map((s, i, arr) => <span key={s} className={s === cycle.status ? 'current' : arr.indexOf(cycle.status) > i ? 'done' : ''}>{human(s)}</span>)}<span className="muted small">{cycle.name} · {fmt.date(cycle.periodStart)} – {fmt.date(cycle.periodEnd)} · goals {cycle.goalWeightPercent}% / competencies {100 - cycle.goalWeightPercent}%</span></div>}
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'reviews', label: hr ? 'Reviews' : 'My team' }, { id: 'goals', label: 'Goals' }, { id: 'plans', label: 'Improvement plans' }, ...(can(P.SalaryRead) ? [{ id: 'comp' as const, label: 'Compensation review' }] : []), ...(hr ? [{ id: 'cycles' as const, label: 'Cycles' }] : [])]} />
      {cycles.isLoading ? <Loading /> : !cycle && tab !== 'cycles' ? <div className="card"><Empty>No review cycles yet.</Empty></div> : <>
        {tab === 'reviews' && cycle && <Reviews cycle={cycle} hr={hr} />}
        {tab === 'goals' && cycle && <Goals cycle={cycle} hr={hr} />}
        {tab === 'plans' && <Plans />}
        {tab === 'comp' && cycle && <Compensation cycle={cycle} />}
        {tab === 'cycles' && <Cycles cycles={cycles.data ?? []} />}
      </>}
    </>
  )
}

function Reviews({ cycle, hr }: { cycle: Cycle; hr: boolean }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['reviews', cycle.id, hr], queryFn: () => api.get<ReviewRow[]>(`/api/payroll/performance/reviews?cycleId=${cycle.id}&scope=${hr ? 'all' : 'team'}`) })
  const dist = useQuery({ queryKey: ['distribution', cycle.id], enabled: hr, queryFn: () => api.get<{ byRating: { rating: number; count: number; proposed: number }[]; byDepartment: { department: string; count: number; average: number }[] }>(`/api/payroll/performance/cycles/${cycle.id}/distribution`) })
  const [open, setOpen] = useState<string | null>(null)
  const advance = useMutation({ mutationFn: () => api.post(`/api/payroll/performance/cycles/${cycle.id}/advance`, { to: next[cycle.status] }), onSuccess: () => qc.invalidateQueries() })
  return (
    <div className="grid c2">
      <div className="card" style={{ gridColumn: '1 / -1' }}>
        <div className="toolbar"><h2 style={{ margin: 0 }}>{hr ? 'All reviews' : 'Reviews for your direct reports'}</h2><span style={{ flex: 1 }} />{hr && next[cycle.status] && <button className="btn primary" disabled={advance.isPending} onClick={() => advance.mutate()}>Move cycle to {human(next[cycle.status])}</button>}</div>
        <ErrorBox error={q.error ?? advance.error} />
        {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>{hr ? 'Start goal setting to create reviews.' : 'No reviews for your team in this cycle.'}</Empty> : <div className="table-wrap"><table><thead><tr><th>No.</th><th>Employee</th><th>Job title</th><th>Reviewer</th><th>Status</th><th>Manager score</th><th>Final rating</th><th /></tr></thead>
          <tbody>{q.data.map(r => <tr key={r.id} className="clickable" onClick={() => setOpen(r.id)}><td className="mono">{r.employeeNumber}</td><td>{r.employee}</td><td>{r.jobTitle}</td><td>{r.reviewer ?? <span className="muted">—</span>}</td><td><Badge status={r.status === 'Completed' || r.status === 'Acknowledged' ? 'Active' : r.status === 'Calibration' ? 'Pending' : 'neutral'}>{human(r.status)}</Badge></td><td><Stars value={r.managerScore} /></td><td><Stars value={r.finalRating} /></td><td><button className="btn sm">Open</button></td></tr>)}</tbody></table></div>}
      </div>
      {hr && dist.data && <>
        <div className="card"><h2>Rating distribution</h2><table><thead><tr><th>Rating</th><th className="num">Proposed</th><th className="num">Calibrated</th></tr></thead><tbody>{dist.data.byRating.slice().reverse().map(r => <tr key={r.rating}><td><Stars value={r.rating} /></td><td className="num">{r.proposed}</td><td className="num">{r.count}</td></tr>)}</tbody></table></div>
        <div className="card"><h2>By department</h2>{dist.data.byDepartment.length === 0 ? <Empty>Nothing calibrated yet.</Empty> : <table><thead><tr><th>Department</th><th className="num">Calibrated</th><th className="num">Average</th></tr></thead><tbody>{dist.data.byDepartment.map(d => <tr key={d.department}><td>{d.department}</td><td className="num">{d.count}</td><td className="num">{d.average}</td></tr>)}</tbody></table>}</div>
      </>}
      {open && <ReviewDialog id={open} onClose={() => { setOpen(null); qc.invalidateQueries({ queryKey: ['reviews'] }); qc.invalidateQueries({ queryKey: ['distribution'] }); qc.invalidateQueries({ queryKey: ['hr-me'] }) }} />}
    </div>
  )
}

type ReviewDetail = {
  review: { id: string; status: string; goalRatings: { goalId: string; selfRating?: number; managerRating?: number; comment?: string }[]; competencyRatings: { competency: string; selfRating?: number; managerRating?: number }[]; selfComments?: string; managerComments?: string; managerScore?: number; finalRating?: number; calibrationNote?: string; employeeResponse?: string }
  cycle: Cycle; goals: Goal[]; employee: { name: string; employeeNumber: string; jobTitle?: string }; feedback: { relationship: string; rating?: number; strengths?: string; improvements?: string }[]
  canSelf: boolean; canManage: boolean; canCalibrate: boolean; canAcknowledge: boolean
}

function RatingSelect({ value, onChange, label }: { value?: number; onChange: (v?: number) => void; label: string }) {
  return <select value={value ?? ''} onChange={e => onChange(e.target.value ? Number(e.target.value) : undefined)} aria-label={label}><option value="">—</option>{[5, 4, 3, 2, 1].map(n => <option key={n} value={n}>{n} — {['Unsatisfactory', 'Needs improvement', 'Meets expectations', 'Exceeds', 'Outstanding'][n - 1]}</option>)}</select>
}

/** Shared by employees (self assessment, acknowledgement), managers (review, 360 requests) and HR (calibration). */
export function ReviewDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const q = useQuery({ queryKey: ['review', id], queryFn: () => api.get<ReviewDetail>(`/api/payroll/performance/reviews/${id}`) })
  const me = useHrMe()
  const [goalR, setGoalR] = useState<Record<string, number | undefined>>({})
  const [compR, setCompR] = useState<Record<string, number | undefined>>({})
  const [comments, setComments] = useState('')
  const [final, setFinal] = useState<number | undefined>()
  const [note, setNote] = useState('')
  const [response, setResponse] = useState('')
  const [fbProvider, setFbProvider] = useState('')
  const options = useQuery({ queryKey: ['feedback-candidates'], queryFn: async () => (await api.get<{ items: { id: string; employeeNumber: string; fullName: string }[] }>('/api/payroll/employees?limit=500').catch(() => ({ items: [] }))).items })
  const d = q.data
  const mode = d?.canSelf ? 'self' : d?.canManage ? 'manager' : null
  const submit = useMutation({
    mutationFn: () => api.post(`/api/payroll/performance/reviews/${id}/${mode}`, {
      goalRatings: d!.goals.map(g => ({ goalId: g.id, [mode === 'self' ? 'selfRating' : 'managerRating']: goalR[g.id] })),
      competencyRatings: d!.cycle.competencies.map(c => ({ competency: c, [mode === 'self' ? 'selfRating' : 'managerRating']: compR[c] })), comments,
    }), onSuccess: onClose,
  })
  const calibrate = useMutation({ mutationFn: () => api.post(`/api/payroll/performance/reviews/${id}/calibrate`, { finalRating: final, note }), onSuccess: onClose })
  const ack = useMutation({ mutationFn: () => api.post(`/api/payroll/performance/reviews/${id}/acknowledge`, { response }), onSuccess: onClose })
  const ask = useMutation({ mutationFn: () => api.post(`/api/payroll/performance/reviews/${id}/feedback`, { providerEmployeeId: fbProvider, relationship: 'Peer' }), onSuccess: () => setFbProvider('') })
  if (q.isLoading) return <Modal title="Review" onClose={onClose}><Loading /></Modal>
  if (!d) return <Modal title="Review" onClose={onClose}><ErrorBox error={q.error} /></Modal>
  const r = d.review
  const rating = (goalId: string) => r.goalRatings.find(x => x.goalId === goalId)
  const comp = (c: string) => r.competencyRatings.find(x => x.competency === c)
  return (
    <Modal title={`${d.employee.name} — ${d.cycle.name}`} onClose={onClose} width={860}>
      <p className="small muted">{d.employee.employeeNumber} · {d.employee.jobTitle} · <Badge status="neutral">{human(r.status)}</Badge> {r.managerScore != null && <>· manager score <b>{r.managerScore}</b></>} {r.finalRating != null && <>· final <Stars value={r.finalRating} /></>}</p>
      <h3>Goals ({d.cycle.goalWeightPercent}% of score)</h3>
      {d.goals.length === 0 ? <p className="small muted">No goals agreed.</p> : <table><thead><tr><th>Goal</th><th className="num">Weight</th><th className="num">Progress</th><th>Self</th><th>Manager</th></tr></thead>
        <tbody>{d.goals.map(g => <tr key={g.id}><td style={{ whiteSpace: 'normal' }}>{g.title}{g.target && <div className="muted small">Target: {g.target}</div>}</td><td className="num">{g.weightPercent}%</td><td className="num">{g.progressPercent}%</td>
          <td>{mode === 'self' ? <RatingSelect label="Self rating" value={goalR[g.id] ?? rating(g.id)?.selfRating} onChange={v => setGoalR({ ...goalR, [g.id]: v })} /> : <Stars value={rating(g.id)?.selfRating} />}</td>
          <td>{mode === 'manager' ? <RatingSelect label="Manager rating" value={goalR[g.id] ?? rating(g.id)?.managerRating} onChange={v => setGoalR({ ...goalR, [g.id]: v })} /> : <Stars value={rating(g.id)?.managerRating} />}</td></tr>)}</tbody></table>}
      <h3 style={{ marginTop: 12 }}>Competencies ({100 - d.cycle.goalWeightPercent}% of score)</h3>
      <table><thead><tr><th>Competency</th><th>Self</th><th>Manager</th></tr></thead><tbody>{d.cycle.competencies.map(c => <tr key={c}><td>{c}</td>
        <td>{mode === 'self' ? <RatingSelect label={`Self ${c}`} value={compR[c] ?? comp(c)?.selfRating} onChange={v => setCompR({ ...compR, [c]: v })} /> : <Stars value={comp(c)?.selfRating} />}</td>
        <td>{mode === 'manager' ? <RatingSelect label={`Manager ${c}`} value={compR[c] ?? comp(c)?.managerRating} onChange={v => setCompR({ ...compR, [c]: v })} /> : <Stars value={comp(c)?.managerRating} />}</td></tr>)}</tbody></table>
      {r.selfComments && <p className="small"><b>Employee:</b> {r.selfComments}</p>}
      {r.managerComments && <p className="small"><b>Manager:</b> {r.managerComments}</p>}
      {r.calibrationNote && <p className="small"><b>Calibration:</b> {r.calibrationNote}</p>}
      {r.employeeResponse && <p className="small"><b>Employee response:</b> {r.employeeResponse}</p>}
      {d.feedback.length > 0 && <><h3>360 feedback (anonymous)</h3><ul className="issue-list">{d.feedback.map((f, i) => <li key={i}>{f.relationship}: <Stars value={f.rating} /> {f.strengths && <>— strengths: {f.strengths}</>} {f.improvements && <>; to improve: {f.improvements}</>}</li>)}</ul></>}
      <ErrorBox error={submit.error ?? calibrate.error ?? ack.error ?? ask.error} />
      {mode && <><Field label={mode === 'self' ? 'Your comments' : 'Manager comments'}><textarea rows={3} value={comments} onChange={e => setComments(e.target.value)} /></Field>
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn primary" disabled={submit.isPending} onClick={() => submit.mutate()}>{mode === 'self' ? 'Submit self assessment' : 'Submit manager review'}</button></div></>}
      {d.canManage && <div className="toolbar" style={{ marginTop: 10 }}><span className="small muted">Request 360 feedback from</span><select value={fbProvider} onChange={e => setFbProvider(e.target.value)} aria-label="Feedback provider"><option value="">Choose colleague…</option>{(options.data?.length ? options.data : me.data?.team ?? []).map(o => <option key={o.id} value={o.id}>{o.employeeNumber} {o.fullName}</option>)}</select><button className="btn sm" disabled={!fbProvider || ask.isPending} onClick={() => ask.mutate()}>Request</button>{ask.isSuccess && <span className="small" style={{ color: 'var(--ok)' }}>Requested</span>}</div>}
      {d.canCalibrate && <div className="card" style={{ marginTop: 12 }}><h3>Calibrate</h3><div className="toolbar"><RatingSelect label="Final rating" value={final ?? r.finalRating ?? (r.managerScore ? Math.round(r.managerScore) : undefined)} onChange={setFinal} /><input placeholder="Reason if different from manager score" value={note} onChange={e => setNote(e.target.value)} style={{ flex: 1 }} aria-label="Calibration note" /><button className="btn primary" disabled={calibrate.isPending} onClick={() => calibrate.mutate()}>Save rating</button></div></div>}
      {d.canAcknowledge && <div className="card" style={{ marginTop: 12 }}><h3>Acknowledge</h3><Field label="Your response (optional)"><textarea rows={2} value={response} onChange={e => setResponse(e.target.value)} /></Field><button className="btn primary" onClick={() => ack.mutate()} disabled={ack.isPending}>Acknowledge review</button></div>}
    </Modal>
  )
}

function Goals({ cycle, hr }: { cycle: Cycle; hr: boolean }) {
  const qc = useQueryClient()
  const me = useHrMe()
  const people = me.data?.team ?? []
  const [emp, setEmp] = useState('')
  const empId = emp || people[0]?.id
  const dept = useQuery({ queryKey: ['goals', cycle.id, 'dept'], queryFn: () => api.get<Goal[]>(`/api/payroll/performance/goals?cycleId=${cycle.id}`) })
  const mine = useQuery({ queryKey: ['goals', cycle.id, empId], enabled: !!empId, queryFn: () => api.get<Goal[]>(`/api/payroll/performance/goals?cycleId=${cycle.id}&employeeId=${empId}`) })
  const [f, setF] = useState({ title: '', target: '', weightPercent: 0, parentGoalId: '' })
  const refresh = () => qc.invalidateQueries({ queryKey: ['goals'] })
  const add = useMutation({ mutationFn: (department: boolean) => api.post('/api/payroll/performance/goals', { reviewCycleId: cycle.id, employeeId: department ? null : empId, title: f.title, target: f.target, weightPercent: department ? 0 : f.weightPercent, parentGoalId: f.parentGoalId || null }), onSuccess: () => { setF({ title: '', target: '', weightPercent: 0, parentGoalId: '' }); refresh() } })
  const agree = useMutation({ mutationFn: () => api.post(`/api/payroll/performance/cycles/${cycle.id}/employees/${empId}/agree-goals`), onSuccess: refresh })
  const progress = useMutation({ mutationFn: (g: { id: string; p: number }) => api.post(`/api/payroll/performance/goals/${g.id}/progress`, { progressPercent: g.p }), onSuccess: refresh })
  const total = (mine.data ?? []).reduce((s, g) => s + g.weightPercent, 0)
  const locked = cycle.status === 'Calibration' || cycle.status === 'Closed'
  return (
    <div className="grid c2">
      <div className="card"><h2>Department goals</h2>
        {!dept.data?.length ? <Empty>No department goals.</Empty> : <ul className="issue-list">{dept.data.map(g => <li key={g.id}><b>{g.title}</b>{g.target && ` — target ${g.target}`}</li>)}</ul>}
        {hr && !locked && <div className="toolbar" style={{ marginTop: 8 }}><input placeholder="New department goal" value={f.title} onChange={e => setF({ ...f, title: e.target.value })} aria-label="Department goal" /><button className="btn sm" disabled={!f.title} onClick={() => add.mutate(true)}>Add</button></div>}
      </div>
      <div className="card"><h2>Individual goals</h2>
        {people.length === 0 ? <Empty>Goals for direct reports appear here. Employees manage their own goals under My workspace.</Empty> : <>
          <div className="toolbar"><select value={empId} onChange={e => setEmp(e.target.value)} aria-label="Employee">{people.map(p => <option key={p.id} value={p.id}>{p.employeeNumber} {p.fullName}</option>)}</select>
            <Badge status={total === 100 ? 'Active' : 'Warning'}>Weights {total}%</Badge>{!locked && <button className="btn sm primary" disabled={total !== 100 || agree.isPending} onClick={() => agree.mutate()}>Agree goals</button>}</div>
          <ErrorBox error={add.error ?? agree.error ?? progress.error} />
          {!mine.data?.length ? <Empty>No goals yet.</Empty> : <table><thead><tr><th>Goal</th><th className="num">Weight</th><th>Status</th><th>Progress</th></tr></thead>
            <tbody>{mine.data.map(g => <tr key={g.id}><td style={{ whiteSpace: 'normal' }}>{g.title}{g.parentGoalId && <div className="muted small">↳ supports {dept.data?.find(x => x.id === g.parentGoalId)?.title}</div>}</td><td className="num">{g.weightPercent}%</td><td><Badge status="neutral">{human(g.status)}</Badge></td>
              <td><input type="range" min={0} max={100} step={10} defaultValue={g.progressPercent} onMouseUp={e => progress.mutate({ id: g.id, p: Number((e.target as HTMLInputElement).value) })} onKeyUp={e => progress.mutate({ id: g.id, p: Number((e.target as HTMLInputElement).value) })} aria-label={`Progress ${g.title}`} /> {g.progressPercent}%</td></tr>)}</tbody></table>}
          {!locked && <div className="toolbar" style={{ marginTop: 8 }}><input placeholder="Goal" value={f.title} onChange={e => setF({ ...f, title: e.target.value })} aria-label="Goal" style={{ flex: 1 }} /><input placeholder="Target" value={f.target} onChange={e => setF({ ...f, target: e.target.value })} aria-label="Target" style={{ width: 110 }} />
            <input type="number" placeholder="Weight %" value={f.weightPercent || ''} onChange={e => setF({ ...f, weightPercent: Number(e.target.value) })} aria-label="Weight" style={{ width: 90 }} />
            <select value={f.parentGoalId} onChange={e => setF({ ...f, parentGoalId: e.target.value })} aria-label="Supports"><option value="">Supports…</option>{dept.data?.map(g => <option key={g.id} value={g.id}>{g.title}</option>)}</select>
            <button className="btn sm" disabled={!f.title || add.isPending} onClick={() => add.mutate(false)}>Add</button></div>}
        </>}
      </div>
    </div>
  )
}

type Plan = { plan: { id: string; employeeId: string; startDate: string; endDate: string; reason: string; objectives: { description: string; successMeasure?: string; dueDate: string; met: boolean }[]; status: string; outcome?: string }; employee: string; employeeNumber: string }

function Plans() {
  const qc = useQueryClient()
  const me = useHrMe()
  const q = useQuery({ queryKey: ['plans'], queryFn: () => api.get<Plan[]>('/api/payroll/performance/plans') })
  const [show, setShow] = useState(false)
  const [f, setF] = useState({ employeeId: '', startDate: isoDate(), endDate: '', reason: '', objective: '', measure: '' })
  const create = useMutation({ mutationFn: () => api.post('/api/payroll/performance/plans', { employeeId: f.employeeId || me.data?.team[0]?.id, startDate: f.startDate, endDate: f.endDate, reason: f.reason, objectives: [{ description: f.objective, successMeasure: f.measure, dueDate: f.endDate, met: false }] }), onSuccess: () => { setShow(false); qc.invalidateQueries({ queryKey: ['plans'] }) } })
  const close = useMutation({ mutationFn: ({ p, status }: { p: Plan['plan']; status: string }) => api.put(`/api/payroll/performance/plans/${p.id}`, { ...p, status, outcome: status === 'Successful' ? 'Objectives met' : 'Objectives not met' }), onSuccess: () => qc.invalidateQueries({ queryKey: ['plans'] }) })
  return (
    <div className="card">
      <div className="toolbar"><h2 style={{ margin: 0 }}>Performance improvement plans</h2><span style={{ flex: 1 }} />{(me.data?.directReports ?? 0) > 0 && <button className="btn primary" onClick={() => setShow(true)}>New plan</button>}</div>
      <ErrorBox error={q.error ?? close.error} />
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No improvement plans.</Empty> : <table><thead><tr><th>Employee</th><th>Period</th><th>Reason</th><th>Objectives</th><th>Status</th><th /></tr></thead>
        <tbody>{q.data.map(x => <tr key={x.plan.id}><td>{x.employeeNumber} {x.employee}</td><td>{fmt.date(x.plan.startDate)} – {fmt.date(x.plan.endDate)}</td><td style={{ whiteSpace: 'normal' }}>{x.plan.reason}</td><td style={{ whiteSpace: 'normal' }} className="small">{x.plan.objectives.map((o, i) => <div key={i}>{o.met ? '✓' : '○'} {o.description}</div>)}</td><td><Badge status={x.plan.status === 'Successful' ? 'Active' : x.plan.status === 'Unsuccessful' ? 'Error' : 'Pending'}>{x.plan.status}</Badge></td>
          <td className="actions">{(x.plan.status === 'Active' || x.plan.status === 'Extended') && <><button className="btn sm" onClick={() => close.mutate({ p: x.plan, status: 'Successful' })}>Successful</button><button className="btn sm danger" onClick={() => close.mutate({ p: x.plan, status: 'Unsuccessful' })}>Unsuccessful</button></>}</td></tr>)}</tbody></table>}
      {show && <Modal title="New improvement plan" onClose={() => setShow(false)}>
        <ErrorBox error={create.error} />
        <Field label="Employee"><select value={f.employeeId || me.data?.team[0]?.id} onChange={e => setF({ ...f, employeeId: e.target.value })}>{me.data?.team.map(p => <option key={p.id} value={p.id}>{p.employeeNumber} {p.fullName}</option>)}</select></Field>
        <div className="form-grid"><Field label="Start"><input type="date" value={f.startDate} onChange={e => setF({ ...f, startDate: e.target.value })} /></Field><Field label="Review date"><input type="date" value={f.endDate} onChange={e => setF({ ...f, endDate: e.target.value })} /></Field></div>
        <Field label="Reason (performance concern)"><textarea rows={2} value={f.reason} onChange={e => setF({ ...f, reason: e.target.value })} /></Field>
        <Field label="Objective"><input value={f.objective} onChange={e => setF({ ...f, objective: e.target.value })} /></Field>
        <Field label="How success is measured"><input value={f.measure} onChange={e => setF({ ...f, measure: e.target.value })} /></Field>
        <p className="small muted">Agree the plan with the employee, provide support, and review on the date set. A fair process matters if it later leads to termination for poor performance.</p>
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setShow(false)}>Cancel</button><button className="btn primary" disabled={!f.endDate || !f.reason || !f.objective || create.isPending} onClick={() => create.mutate()}>Create</button></div>
      </Modal>}
    </div>
  )
}

type CompDetail = { review: { id: string; name: string; effectiveDate: string; status: string; meritMatrix: Record<string, number>; bonusMatrix: Record<string, number>; increaseBudget?: number; bonusBudget?: number }; totals: { employees: number; monthlyIncrease: number; annualIncrease: number; bonuses: number }; canApprove: boolean; lines: { id: string; employeeNumber: string; employee: string; rating?: number; currentBasic: number; increasePercent: number; newBasic: number; bonusAmount: number; excluded: boolean; note?: string }[] }

function Compensation({ cycle }: { cycle: Cycle }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const list = useQuery({ queryKey: ['comp-reviews'], queryFn: () => api.get<{ id: string; name: string; reviewCycleId: string; effectiveDate: string; status: string; totals: CompDetail['totals'] }[]>('/api/payroll/performance/compensation-reviews') })
  const [open, setOpen] = useState<string | null>(null)
  const [f, setF] = useState({ name: `${cycle.name} merit review`, effectiveDate: '', increaseBudget: '', bonusBudget: '' })
  const create = useMutation({ mutationFn: () => api.post<{ id: string }>('/api/payroll/performance/compensation-reviews', { reviewCycleId: cycle.id, name: f.name, effectiveDate: f.effectiveDate, increaseBudget: f.increaseBudget ? Number(f.increaseBudget) : null, bonusBudget: f.bonusBudget ? Number(f.bonusBudget) : null }), onSuccess: r => { qc.invalidateQueries({ queryKey: ['comp-reviews'] }); setOpen(r.id) } })
  const ready = cycle.status === 'Calibration' || cycle.status === 'Closed'
  return (
    <>
      <div className="card">
        <h2>Compensation reviews</h2>
        {!list.data?.length ? <Empty>No compensation reviews.</Empty> : <table><thead><tr><th>Name</th><th>Effective</th><th>Status</th><th className="num">Employees</th><th className="num">Annual increase cost</th><th className="num">Bonuses</th></tr></thead>
          <tbody>{list.data.map(c => <tr key={c.id} className="clickable" onClick={() => setOpen(c.id)}><td>{c.name}</td><td>{fmt.date(c.effectiveDate)}</td><td><Badge status={c.status === 'Applied' ? 'Active' : c.status === 'Submitted' ? 'Pending' : c.status === 'Rejected' ? 'Error' : 'neutral'}>{c.status}</Badge></td><td className="num">{c.totals.employees}</td><td className="num">{fmt.num(c.totals.annualIncrease)}</td><td className="num">{fmt.num(c.totals.bonuses)}</td></tr>)}</tbody></table>}
        {can(P.SalaryWrite) && can(P.HrPerformance) && <div className="toolbar" style={{ marginTop: 12 }}>
          {!ready ? <span className="small muted">Available once the cycle reaches calibration.</span> : <>
            <input value={f.name} onChange={e => setF({ ...f, name: e.target.value })} aria-label="Name" style={{ minWidth: 220 }} /><label className="small muted">Effective</label><input type="date" value={f.effectiveDate} onChange={e => setF({ ...f, effectiveDate: e.target.value })} aria-label="Effective date" />
            <input type="number" placeholder="Increase budget / yr" value={f.increaseBudget} onChange={e => setF({ ...f, increaseBudget: e.target.value })} aria-label="Increase budget" style={{ width: 160 }} /><input type="number" placeholder="Bonus budget" value={f.bonusBudget} onChange={e => setF({ ...f, bonusBudget: e.target.value })} aria-label="Bonus budget" style={{ width: 130 }} />
            <button className="btn primary" disabled={!f.effectiveDate || create.isPending} onClick={() => create.mutate()}>Create from final ratings</button></>}
        </div>}
        <ErrorBox error={create.error} />
      </div>
      {open && <CompDialog id={open} onClose={() => { setOpen(null); qc.invalidateQueries({ queryKey: ['comp-reviews'] }) }} />}
    </>
  )
}

function CompDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['comp-review', id], queryFn: () => api.get<CompDetail>(`/api/payroll/performance/compensation-reviews/${id}`) })
  const refresh = () => qc.invalidateQueries({ queryKey: ['comp-review', id] })
  const line = useMutation({ mutationFn: (l: CompDetail['lines'][number]) => api.put(`/api/payroll/performance/compensation-reviews/${id}/lines/${l.id}`, l), onSuccess: refresh })
  const act = useMutation({ mutationFn: (a: string) => api.post(`/api/payroll/performance/compensation-reviews/${id}/${a}`), onSuccess: () => { refresh(); qc.invalidateQueries({ queryKey: ['comp-reviews'] }) } })
  const [reject, setReject] = useState(false)
  const d = q.data
  const editable = d && (d.review.status === 'Draft' || d.review.status === 'Rejected')
  return (
    <Modal title={d?.review.name ?? 'Compensation review'} onClose={onClose} width={960}>
      {!d ? <Loading /> : <>
        <p className="small">Effective {fmt.date(d.review.effectiveDate)} · <Badge status="neutral">{d.review.status}</Badge> · merit matrix {Object.entries(d.review.meritMatrix).sort().reverse().map(([k, v]) => `${k}★ ${v}%`).join(', ')} · bonus {Object.entries(d.review.bonusMatrix).sort().reverse().map(([k, v]) => `${k}★ ${v}%`).join(', ')}</p>
        <div className="grid c4"><div className="card stat"><div className="label">Employees</div><div className="value">{d.totals.employees}</div></div><div className="card stat"><div className="label">Monthly increase</div><div className="value">{fmt.num(d.totals.monthlyIncrease)}</div></div>
          <div className="card stat"><div className="label">Annual cost</div><div className="value">{fmt.num(d.totals.annualIncrease)}</div>{d.review.increaseBudget && <div className="hint">Budget {fmt.num(d.review.increaseBudget)}</div>}</div><div className="card stat"><div className="label">Bonuses</div><div className="value">{fmt.num(d.totals.bonuses)}</div>{d.review.bonusBudget && <div className="hint">Budget {fmt.num(d.review.bonusBudget)}</div>}</div></div>
        <ErrorBox error={line.error ?? act.error} />
        <div className="table-wrap" style={{ marginTop: 12 }}><table><thead><tr><th>Employee</th><th>Rating</th><th className="num">Current basic</th><th className="num">Increase %</th><th className="num">New basic</th><th className="num">Bonus</th><th>Exclude</th></tr></thead>
          <tbody>{d.lines.map(l => <tr key={l.id} style={l.excluded ? { opacity: 0.5 } : undefined}><td>{l.employeeNumber} {l.employee}{l.note && <div className="muted small">{l.note}</div>}</td><td><Stars value={l.rating} /></td><td className="num">{fmt.num(l.currentBasic)}</td>
            <td className="num">{editable ? <input type="number" step="0.5" defaultValue={l.increasePercent} style={{ width: 70 }} onBlur={e => Number(e.target.value) !== l.increasePercent && line.mutate({ ...l, increasePercent: Number(e.target.value) })} aria-label="Increase percent" /> : `${l.increasePercent}%`}</td>
            <td className="num">{fmt.num(l.newBasic)}</td>
            <td className="num">{editable ? <input type="number" step="500" defaultValue={l.bonusAmount} style={{ width: 100 }} onBlur={e => Number(e.target.value) !== l.bonusAmount && line.mutate({ ...l, bonusAmount: Number(e.target.value) })} aria-label="Bonus" /> : fmt.num(l.bonusAmount)}</td>
            <td>{editable ? <input type="checkbox" checked={l.excluded} onChange={e => line.mutate({ ...l, excluded: e.target.checked })} aria-label="Exclude" /> : l.excluded ? 'Excluded' : ''}</td></tr>)}</tbody></table></div>
        <div className="actions" style={{ justifyContent: 'flex-end', marginTop: 12 }}>
          {editable && <button className="btn primary" onClick={() => act.mutate('submit')}>Submit for approval</button>}
          {d.review.status === 'Submitted' && d.canApprove && <><button className="btn danger" onClick={() => setReject(true)}>Reject</button><button className="btn primary" onClick={() => act.mutate('approve')}>Approve &amp; apply</button></>}
          {d.review.status === 'Submitted' && !d.canApprove && <span className="small muted">Awaiting approval by a payroll approver other than the preparer.</span>}
          {d.review.status === 'Applied' && <span className="small" style={{ color: 'var(--ok)' }}>Applied: new salaries take effect {fmt.date(d.review.effectiveDate)}; bonuses are paid by the payroll for that period.</span>}
        </div>
      </>}
      {reject && <ReasonDialog title="Reject compensation review" label="Reason" required danger confirmLabel="Reject" onClose={() => setReject(false)} onConfirm={t => api.post(`/api/payroll/performance/compensation-reviews/${id}/reject`, { text: t }).then(refresh)} />}
    </Modal>
  )
}

function Cycles({ cycles }: { cycles: Cycle[] }) {
  const qc = useQueryClient()
  const les = useLegalEntities()
  const y = new Date().getFullYear()
  const [f, setF] = useState({ name: `FY${y + 1} Annual review`, type: 'Annual', periodStart: `${y + 1}-01-01`, periodEnd: `${y + 1}-12-31`, goalWeightPercent: 70, include360: false })
  const m = useMutation({ mutationFn: () => api.post('/api/payroll/performance/cycles', { ...f, legalEntityId: les.data?.[0]?.id }), onSuccess: () => qc.invalidateQueries({ queryKey: ['cycles'] }) })
  return (
    <div className="card">
      <table><thead><tr><th>Cycle</th><th>Type</th><th>Period</th><th>Status</th><th className="num">Goal weight</th></tr></thead><tbody>{cycles.map(c => <tr key={c.id}><td>{c.name}</td><td>{c.type}</td><td>{fmt.date(c.periodStart)} – {fmt.date(c.periodEnd)}</td><td><Badge status="neutral">{human(c.status)}</Badge></td><td className="num">{c.goalWeightPercent}%</td></tr>)}</tbody></table>
      <h3 style={{ marginTop: 14 }}>New cycle</h3>
      <ErrorBox error={m.error} />
      <div className="form-grid">
        <Field label="Name"><input value={f.name} onChange={e => setF({ ...f, name: e.target.value })} /></Field>
        <Field label="Type"><select value={f.type} onChange={e => setF({ ...f, type: e.target.value })}>{['Annual', 'MidYear', 'Probation'].map(t => <option key={t}>{t}</option>)}</select></Field>
        <Field label="From"><input type="date" value={f.periodStart} onChange={e => setF({ ...f, periodStart: e.target.value })} /></Field>
        <Field label="To"><input type="date" value={f.periodEnd} onChange={e => setF({ ...f, periodEnd: e.target.value })} /></Field>
        <Field label="Goal weight %"><input type="number" value={f.goalWeightPercent} onChange={e => setF({ ...f, goalWeightPercent: Number(e.target.value) })} /></Field>
      </div>
      <button className="btn primary" disabled={m.isPending} onClick={() => m.mutate()}>Create cycle</button>
    </div>
  )
}
