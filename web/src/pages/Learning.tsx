import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt, isoDate } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useDepartments, useEmployeeOptions, useHrMe, useLegalEntities } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, Stat, Tabs } from '../components/ui'

type Course = { id: string; code: string; title: string; provider?: string; delivery: string; durationHours: number; costPerParticipant: number; nitaApproved: boolean; nitaCourseCode?: string; certificateValidityMonths?: number; isActive: boolean }
type Session = { id: string; trainingCourseId: string; course: string; code: string; nitaApproved: boolean; startDate: string; endDate: string; venue?: string; trainer?: string; capacity: number; enrolled: number; status: string; actualTotalCost?: number; costPerParticipant: number }
type Enrolment = { id: string; employeeId: string; employeeNumber: string; employee: string; jobTitle?: string; status: string; score?: number; certificateNumber?: string; certifiedOn?: string; expiresOn?: string; costAllocated: number }

export default function LearningPage() {
  const { can } = useAuth()
  const hr = can(P.HrLearning)
  const [tab, setTab] = useState<'sessions' | 'catalogue' | 'compliance' | 'nita' | 'bonds'>('sessions')
  return (
    <>
      <PageHead title="Learning & development" sub="Plan training, record results and certificates, keep roles compliant, and claim NITA reimbursement for approved courses." />
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'sessions', label: 'Sessions' }, { id: 'catalogue', label: 'Catalogue' }, ...(hr ? [{ id: 'compliance' as const, label: 'Compliance' }, { id: 'nita' as const, label: 'NITA claims' }, { id: 'bonds' as const, label: 'Training bonds' }] : [])]} />
      {tab === 'sessions' && <Sessions hr={hr} />}
      {tab === 'catalogue' && <Catalogue hr={hr} />}
      {tab === 'compliance' && <Compliance />}
      {tab === 'nita' && <Nita />}
      {tab === 'bonds' && <Bonds />}
    </>
  )
}

function Sessions({ hr }: { hr: boolean }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['sessions'], queryFn: () => api.get<Session[]>('/api/payroll/learning/sessions') })
  const courses = useQuery({ queryKey: ['courses'], queryFn: () => api.get<Course[]>('/api/payroll/learning/courses') })
  const [open, setOpen] = useState<Session | null>(null)
  const [f, setF] = useState({ trainingCourseId: '', startDate: '', endDate: '', venue: '', trainer: '', capacity: 15 })
  const create = useMutation({ mutationFn: () => api.post('/api/payroll/learning/sessions', { ...f, trainingCourseId: f.trainingCourseId || courses.data?.[0]?.id, endDate: f.endDate || f.startDate }), onSuccess: () => qc.invalidateQueries({ queryKey: ['sessions'] }) })
  return (
    <>
      <div className="card">
        <ErrorBox error={q.error ?? create.error} />
        {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No sessions scheduled.</Empty> : <div className="table-wrap"><table><thead><tr><th>Course</th><th>Dates</th><th>Venue</th><th>Trainer</th><th className="num">Places</th><th>Status</th><th /></tr></thead>
          <tbody>{q.data.map(s => <tr key={s.id} className="clickable" onClick={() => setOpen(s)}><td>{s.course} {s.nitaApproved && <Badge status="Active">NITA</Badge>}</td><td>{fmt.date(s.startDate)}{s.endDate !== s.startDate && ` – ${fmt.date(s.endDate)}`}</td><td>{s.venue}</td><td>{s.trainer}</td><td className="num">{s.enrolled} / {s.capacity}</td><td><Badge status={s.status === 'Completed' ? 'Active' : s.status === 'Cancelled' ? 'Cancelled' : 'Open'}>{s.status}</Badge></td><td><button className="btn sm">{s.status === 'Planned' ? 'Nominate / results' : 'Results'}</button></td></tr>)}</tbody></table></div>}
        {hr && <div className="toolbar" style={{ marginTop: 12 }}>
          <select value={f.trainingCourseId || courses.data?.[0]?.id} onChange={e => setF({ ...f, trainingCourseId: e.target.value })} aria-label="Course">{courses.data?.filter(c => c.isActive).map(c => <option key={c.id} value={c.id}>{c.title}</option>)}</select>
          <input type="date" value={f.startDate} onChange={e => setF({ ...f, startDate: e.target.value })} aria-label="Start" /><input type="date" value={f.endDate} onChange={e => setF({ ...f, endDate: e.target.value })} aria-label="End" />
          <input placeholder="Venue" value={f.venue} onChange={e => setF({ ...f, venue: e.target.value })} aria-label="Venue" style={{ width: 120 }} /><input placeholder="Trainer" value={f.trainer} onChange={e => setF({ ...f, trainer: e.target.value })} aria-label="Trainer" style={{ width: 120 }} />
          <input type="number" value={f.capacity} onChange={e => setF({ ...f, capacity: Number(e.target.value) })} aria-label="Capacity" style={{ width: 70 }} /><button className="btn primary" disabled={!f.startDate || create.isPending} onClick={() => create.mutate()}>Schedule</button></div>}
      </div>
      {open && <SessionDialog s={open} hr={hr} onClose={() => { setOpen(null); qc.invalidateQueries({ queryKey: ['sessions'] }) }} />}
    </>
  )
}

function SessionDialog({ s, hr, onClose }: { s: Session; hr: boolean; onClose: () => void }) {
  const qc = useQueryClient()
  const me = useHrMe()
  const all = useEmployeeOptions(hr)
  const q = useQuery({ queryKey: ['enrolments', s.id], queryFn: () => api.get<Enrolment[]>(`/api/payroll/learning/sessions/${s.id}/enrolments`) })
  const refresh = () => { qc.invalidateQueries({ queryKey: ['enrolments', s.id] }); qc.invalidateQueries({ queryKey: ['hr-me'] }) }
  const [who, setWho] = useState('')
  const people = hr ? all.data ?? [] : [...(me.data?.team ?? []), ...(me.data?.employeeId ? [{ id: me.data.employeeId, employeeNumber: '', fullName: 'Myself' }] : [])]
  const nominate = useMutation({ mutationFn: () => api.post(`/api/payroll/learning/sessions/${s.id}/nominate`, { employeeId: who || people[0]?.id }), onSuccess: refresh })
  const approve = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/learning/enrolments/${id}/approve`), onSuccess: refresh })
  const [results, setResults] = useState<Record<string, { status: string; score?: number; certificateNumber?: string }>>({})
  const [cost, setCost] = useState('')
  const complete = useMutation({ mutationFn: () => api.post(`/api/payroll/learning/sessions/${s.id}/complete`, { results: (q.data ?? []).filter(e => e.status !== 'Withdrawn').map(e => ({ enrolmentId: e.id, status: results[e.id]?.status ?? 'Completed', score: results[e.id]?.score, certificateNumber: results[e.id]?.certificateNumber })), actualTotalCost: cost ? Number(cost) : null }), onSuccess: () => { refresh(); onClose() } })
  const bond = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/learning/enrolments/${id}/bond`, { months: 12 }), onSuccess: refresh })
  const planned = s.status === 'Planned'
  return (
    <Modal title={`${s.course} — ${fmt.date(s.startDate)}`} onClose={onClose} width={860}>
      <ErrorBox error={q.error ?? nominate.error ?? approve.error ?? complete.error ?? bond.error} />
      {planned && people.length > 0 && <div className="toolbar"><select value={who || people[0]?.id} onChange={e => setWho(e.target.value)} aria-label="Employee">{people.map(p => <option key={p.id} value={p.id}>{p.employeeNumber} {p.fullName}</option>)}</select><button className="btn" disabled={nominate.isPending} onClick={() => nominate.mutate()}>Nominate</button><span className="small muted">Managers' and HR nominations are approved straight away; self-nominations need the manager.</span></div>}
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No participants yet.</Empty> : <table><thead><tr><th>Participant</th><th>Status</th>{planned && hr && <><th>Result</th><th>Score</th><th>Certificate no.</th></>}{!planned && <><th>Certificate</th><th>Expires</th><th className="num">Cost</th></>}<th /></tr></thead>
        <tbody>{q.data.map(e => <tr key={e.id}><td>{e.employeeNumber} {e.employee}</td><td><Badge status={e.status === 'Completed' ? 'Active' : e.status === 'Nominated' ? 'Pending' : e.status === 'Failed' || e.status === 'NoShow' ? 'Error' : 'neutral'}>{e.status}</Badge></td>
          {planned && hr && e.status !== 'Withdrawn' && <><td><select value={results[e.id]?.status ?? 'Completed'} onChange={x => setResults({ ...results, [e.id]: { ...results[e.id], status: x.target.value } })} aria-label="Result">{['Completed', 'Attended', 'Failed', 'NoShow'].map(o => <option key={o}>{o}</option>)}</select></td>
            <td><input type="number" style={{ width: 60 }} onChange={x => setResults({ ...results, [e.id]: { ...results[e.id], status: results[e.id]?.status ?? 'Completed', score: Number(x.target.value) } })} aria-label="Score" /></td>
            <td><input style={{ width: 120 }} onChange={x => setResults({ ...results, [e.id]: { ...results[e.id], status: results[e.id]?.status ?? 'Completed', certificateNumber: x.target.value } })} aria-label="Certificate number" /></td></>}
          {!planned && <><td>{e.certificateNumber}</td><td>{e.expiresOn ? fmt.date(e.expiresOn) : e.status === 'Completed' ? 'No expiry' : ''}</td><td className="num">{fmt.num(e.costAllocated)}</td></>}
          <td className="actions">{e.status === 'Nominated' && <button className="btn sm primary" onClick={() => approve.mutate(e.id)}>Approve</button>}{!planned && hr && e.status === 'Completed' && e.costAllocated > 0 && <button className="btn sm" onClick={() => bond.mutate(e.id)} title="12-month bond for the training cost">Bond 12m</button>}</td></tr>)}</tbody></table>}
      {planned && hr && (q.data?.length ?? 0) > 0 && <div className="toolbar" style={{ marginTop: 12 }}><input type="number" placeholder={`Actual total cost (default ${fmt.num(s.costPerParticipant)} each)`} value={cost} onChange={e => setCost(e.target.value)} aria-label="Actual total cost" style={{ width: 280 }} /><button className="btn primary" disabled={complete.isPending} onClick={() => complete.mutate()}>Record results &amp; close session</button></div>}
      {planned && hr && <p className="small muted">Closing records certificates (with expiry from the course validity), shares the cost across attendees and posts a training-cost journal (Dr 6180 / Cr 2196).</p>}
    </Modal>
  )
}

function Catalogue({ hr }: { hr: boolean }) {
  const qc = useQueryClient()
  const les = useLegalEntities()
  const depts = useDepartments()
  const q = useQuery({ queryKey: ['courses'], queryFn: () => api.get<Course[]>('/api/payroll/learning/courses') })
  const reqs = useQuery({ queryKey: ['requirements'], queryFn: () => api.get<{ id: string; course: string; jobTitle?: string; departmentId?: string; mandatory: boolean }[]>('/api/payroll/learning/requirements') })
  const [edit, setEdit] = useState<Partial<Course> | null>(null)
  const save = useMutation({ mutationFn: (c: Partial<Course>) => c.id ? api.put(`/api/payroll/learning/courses/${c.id}`, c) : api.post('/api/payroll/learning/courses', { ...c, legalEntityId: les.data?.[0]?.id }), onSuccess: () => { qc.invalidateQueries({ queryKey: ['courses'] }); setEdit(null) } })
  const [r, setR] = useState({ trainingCourseId: '', jobTitle: '', departmentId: '' })
  const addReq = useMutation({ mutationFn: () => api.post('/api/payroll/learning/requirements', { legalEntityId: les.data?.[0]?.id, trainingCourseId: r.trainingCourseId || q.data?.[0]?.id, jobTitle: r.jobTitle || null, departmentId: r.departmentId || null, mandatory: true }), onSuccess: () => { qc.invalidateQueries({ queryKey: ['requirements'] }); setR({ trainingCourseId: '', jobTitle: '', departmentId: '' }) } })
  const delReq = useMutation({ mutationFn: (id: string) => api.del(`/api/payroll/learning/requirements/${id}`), onSuccess: () => qc.invalidateQueries({ queryKey: ['requirements'] }) })
  return (
    <div className="grid c2">
      <div className="card" style={{ gridColumn: '1 / -1' }}>
        <div className="toolbar"><h2 style={{ margin: 0 }}>Courses</h2><span style={{ flex: 1 }} />{hr && <button className="btn primary" onClick={() => setEdit({ delivery: 'Classroom', durationHours: 8, costPerParticipant: 0, nitaApproved: false, isActive: true })}>New course</button>}</div>
        {q.isLoading ? <Loading /> : <table><thead><tr><th>Code</th><th>Title</th><th>Provider</th><th>Delivery</th><th className="num">Hours</th><th className="num">Cost / person</th><th>NITA</th><th>Certificate valid</th><th /></tr></thead>
          <tbody>{q.data?.map(c => <tr key={c.id}><td className="mono">{c.code}</td><td>{c.title}</td><td>{c.provider}</td><td>{c.delivery}</td><td className="num">{c.durationHours}</td><td className="num">{fmt.num(c.costPerParticipant)}</td><td>{c.nitaApproved ? <Badge status="Active">{c.nitaCourseCode ?? 'Approved'}</Badge> : '—'}</td><td>{c.certificateValidityMonths ? `${c.certificateValidityMonths} months` : 'No expiry'}</td><td>{hr && <button className="btn sm" onClick={() => setEdit(c)}>Edit</button>}</td></tr>)}</tbody></table>}
      </div>
      <div className="card" style={{ gridColumn: '1 / -1' }}>
        <h2>Role requirements</h2>
        <ErrorBox error={addReq.error} />
        {!reqs.data?.length ? <Empty>No requirements set.</Empty> : <table><thead><tr><th>Course</th><th>Job title</th><th>Department</th><th /></tr></thead><tbody>{reqs.data.map(x => <tr key={x.id}><td>{x.course}</td><td>{x.jobTitle ?? 'Any'}</td><td>{depts.data?.find(d => d.id === x.departmentId)?.name ?? 'Any'}</td><td>{hr && <button className="btn sm danger" onClick={() => delReq.mutate(x.id)}>Remove</button>}</td></tr>)}</tbody></table>}
        {hr && <div className="toolbar" style={{ marginTop: 10 }}><select value={r.trainingCourseId || q.data?.[0]?.id} onChange={e => setR({ ...r, trainingCourseId: e.target.value })} aria-label="Course">{q.data?.map(c => <option key={c.id} value={c.id}>{c.title}</option>)}</select><input placeholder="Job title (optional)" value={r.jobTitle} onChange={e => setR({ ...r, jobTitle: e.target.value })} aria-label="Job title" /><select value={r.departmentId} onChange={e => setR({ ...r, departmentId: e.target.value })} aria-label="Department"><option value="">Any department</option>{depts.data?.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}</select><button className="btn" disabled={(!r.jobTitle && !r.departmentId) || addReq.isPending} onClick={() => addReq.mutate()}>Require</button></div>}
      </div>
      {edit && <Modal title={edit.id ? `Edit ${edit.code}` : 'New course'} onClose={() => setEdit(null)}>
        <ErrorBox error={save.error} />
        <div className="form-grid">
          <Field label="Code *"><input value={edit.code ?? ''} disabled={!!edit.id} onChange={e => setEdit({ ...edit, code: e.target.value })} /></Field>
          <Field label="Title *"><input value={edit.title ?? ''} onChange={e => setEdit({ ...edit, title: e.target.value })} /></Field>
          <Field label="Provider"><input value={edit.provider ?? ''} onChange={e => setEdit({ ...edit, provider: e.target.value })} /></Field>
          <Field label="Delivery"><select value={edit.delivery} onChange={e => setEdit({ ...edit, delivery: e.target.value })}>{['Classroom', 'Online', 'OnTheJob', 'External', 'Blended'].map(d => <option key={d}>{d}</option>)}</select></Field>
          <Field label="Hours"><input type="number" value={edit.durationHours ?? 0} onChange={e => setEdit({ ...edit, durationHours: Number(e.target.value) })} /></Field>
          <Field label="Cost per participant"><input type="number" value={edit.costPerParticipant ?? 0} onChange={e => setEdit({ ...edit, costPerParticipant: Number(e.target.value) })} /></Field>
          <Field label="Certificate validity (months, blank = none)"><input type="number" value={edit.certificateValidityMonths ?? ''} onChange={e => setEdit({ ...edit, certificateValidityMonths: e.target.value ? Number(e.target.value) : undefined })} /></Field>
          <Field label="NITA course code"><input value={edit.nitaCourseCode ?? ''} onChange={e => setEdit({ ...edit, nitaCourseCode: e.target.value })} /></Field>
        </div>
        <label className="field inline"><input type="checkbox" checked={!!edit.nitaApproved} onChange={e => setEdit({ ...edit, nitaApproved: e.target.checked })} /> NITA-approved (costs can be claimed against the training levy)</label>
        <label className="field inline"><input type="checkbox" checked={!!edit.isActive} onChange={e => setEdit({ ...edit, isActive: e.target.checked })} /> Active</label>
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setEdit(null)}>Cancel</button><button className="btn primary" disabled={!edit.code || !edit.title || save.isPending} onClick={() => save.mutate(edit)}>Save</button></div>
      </Modal>}
    </div>
  )
}

function Compliance() {
  const les = useLegalEntities(); const le = les.data?.[0]?.id
  const q = useQuery({ queryKey: ['compliance', le], enabled: !!le, queryFn: () => api.get<{ rows: { employeeId: string; employeeNumber: string; employeeName: string; jobTitle?: string; course: string; status: string; completedOn?: string; expiresOn?: string }[]; summary: Record<string, number> }>(`/api/payroll/learning/compliance?legalEntityId=${le}`) })
  const tone: Record<string, string> = { Compliant: 'Active', Expiring: 'Warning', Expired: 'Error', Missing: 'Error' }
  return (
    <>
      <div className="grid c4">{['Compliant', 'Expiring', 'Expired', 'Missing'].map(s => <Stat key={s} label={s} value={q.data?.summary[s] ?? 0} hint={s === 'Expiring' ? 'within 60 days' : undefined} />)}</div>
      <div className="card" style={{ marginTop: 16 }}>
        <div className="toolbar"><h2 style={{ margin: 0 }}>Required training by employee</h2><span style={{ flex: 1 }} /><button className="btn sm" onClick={() => api.download(`/api/payroll/learning/compliance?legalEntityId=${le}&format=xlsx`)}>Excel</button></div>
        {q.isLoading ? <Loading /> : !q.data?.rows.length ? <Empty>No role requirements defined.</Empty> : <div className="table-wrap"><table><thead><tr><th>Employee</th><th>Job title</th><th>Course</th><th>Status</th><th>Completed</th><th>Expires</th></tr></thead>
          <tbody>{q.data.rows.map((r, i) => <tr key={i}><td>{r.employeeNumber} {r.employeeName}</td><td>{r.jobTitle}</td><td>{r.course}</td><td><Badge status={tone[r.status]}>{r.status}</Badge></td><td>{fmt.date(r.completedOn)}</td><td>{fmt.date(r.expiresOn)}</td></tr>)}</tbody></table></div>}
      </div>
    </>
  )
}

function Nita() {
  const qc = useQueryClient()
  const les = useLegalEntities(); const le = les.data?.[0]?.id
  const year = new Date().getFullYear()
  const pos = useQuery({ queryKey: ['nita-position', le, year], enabled: !!le, queryFn: () => api.get<{ levyPaid: number; claimed: number; approved: number; received: number }>(`/api/payroll/learning/nita/position?legalEntityId=${le}&year=${year}`) })
  const q = useQuery({ queryKey: ['nita-claims'], queryFn: () => api.get<{ id: string; reference: string; periodStart: string; periodEnd: string; status: string; totalClaimed: number; amountApproved?: number; nitaReference?: string; lines: unknown[] }[]>('/api/payroll/learning/nita/claims') })
  const [f, setF] = useState({ from: `${year}-01-01`, to: isoDate() })
  const refresh = () => { qc.invalidateQueries({ queryKey: ['nita-claims'] }); qc.invalidateQueries({ queryKey: ['nita-position'] }) }
  const create = useMutation({ mutationFn: () => api.post('/api/payroll/learning/nita/claims', { legalEntityId: le, ...f }), onSuccess: refresh })
  const status = useMutation({ mutationFn: (x: { id: string; status: string; amountApproved?: number; nitaReference?: string }) => api.post(`/api/payroll/learning/nita/claims/${x.id}/status`, x), onSuccess: refresh })
  return (
    <>
      <div className="grid c4"><Stat label={`NITA levy paid ${year}`} value={fmt.num(pos.data?.levyPaid)} hint="From finalised payrolls" /><Stat label="Claimed" value={fmt.num(pos.data?.claimed)} /><Stat label="Approved by NITA" value={fmt.num(pos.data?.approved)} /><Stat label="Received" value={fmt.num(pos.data?.received)} /></div>
      <div className="card" style={{ marginTop: 16 }}>
        <div className="toolbar"><h2 style={{ margin: 0 }}>Reimbursement claims</h2><span style={{ flex: 1 }} /><input type="date" value={f.from} onChange={e => setF({ ...f, from: e.target.value })} aria-label="From" /><input type="date" value={f.to} onChange={e => setF({ ...f, to: e.target.value })} aria-label="To" /><button className="btn primary" disabled={create.isPending} onClick={() => create.mutate()}>Build claim</button></div>
        <ErrorBox error={create.error ?? status.error} />
        {!q.data?.length ? <Empty>No claims yet. A claim collects completed NITA-approved training in the period that hasn't been claimed before.</Empty> : <table><thead><tr><th>Reference</th><th>Period</th><th className="num">Trainees</th><th className="num">Claimed</th><th>Status</th><th className="num">Approved</th><th>NITA ref</th><th /></tr></thead>
          <tbody>{q.data.map(c => <tr key={c.id}><td className="mono">{c.reference}</td><td>{fmt.date(c.periodStart)} – {fmt.date(c.periodEnd)}</td><td className="num">{c.lines.length}</td><td className="num">{fmt.num(c.totalClaimed)}</td><td><Badge status={c.status === 'Paid' ? 'Active' : c.status === 'Rejected' ? 'Error' : 'Pending'}>{c.status}</Badge></td><td className="num">{fmt.num(c.amountApproved)}</td><td>{c.nitaReference}</td>
            <td className="actions"><button className="btn sm" onClick={() => api.download(`/api/payroll/learning/nita/claims/${c.id}/schedule?format=xlsx`)}>Schedule</button>
              {c.status === 'Draft' && <button className="btn sm primary" onClick={() => status.mutate({ id: c.id, status: 'Submitted' })}>Mark submitted</button>}
              {c.status === 'Submitted' && <><button className="btn sm" onClick={() => { const a = prompt('Amount approved by NITA', String(c.totalClaimed)); const ref = prompt('NITA reference'); if (a) status.mutate({ id: c.id, status: 'Approved', amountApproved: Number(a), nitaReference: ref ?? undefined }) }}>Approved…</button><button className="btn sm danger" onClick={() => status.mutate({ id: c.id, status: 'Rejected' })}>Rejected</button></>}
              {c.status === 'Approved' && <button className="btn sm" onClick={() => status.mutate({ id: c.id, status: 'Paid' })}>Mark received</button>}</td></tr>)}</tbody></table>}
      </div>
    </>
  )
}

function Bonds() {
  const q = useQuery({ queryKey: ['bonds'], queryFn: () => api.get<{ id: string; employee: string; employeeNumber: string; amount: number; startDate: string; months: number; endsOn: string; status: string; outstanding: number }[]>('/api/payroll/learning/bonds') })
  return (
    <div className="card"><h2>Training bonds</h2>
      {!q.data?.length ? <Empty>No bonds. Create one from a completed session's results.</Empty> : <table><thead><tr><th>Employee</th><th className="num">Amount</th><th>From</th><th>To</th><th>Status</th><th className="num">Outstanding today</th></tr></thead>
        <tbody>{q.data.map(b => <tr key={b.id}><td>{b.employeeNumber} {b.employee}</td><td className="num">{fmt.num(b.amount)}</td><td>{fmt.date(b.startDate)}</td><td>{fmt.date(b.endsOn)}</td><td><Badge status="neutral">{b.status}</Badge></td><td className="num">{fmt.num(b.outstanding)}</td></tr>)}</tbody></table>}
      <p className="small muted">If the employee leaves during the bond, the unexpired share is recovered in their final dues.</p>
    </div>
  )
}
