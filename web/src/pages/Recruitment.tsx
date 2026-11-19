import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt, isoDate } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useDepartments, useEmployeeOptions, useLegalEntities, usePayrollGroups } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, FileLink, Loading, Modal, PageHead, ReasonDialog, Tabs } from '../components/ui'

type Req = { id: string; reference: string; title: string; positionId?: string; departmentId?: string; openings: number; salaryMin?: number; salaryMax?: number; employmentType: string; justification?: string; status: string; closingDate?: string; requestedBy: string }
type App = { id: string; firstName: string; lastName: string; email: string; phone?: string; source?: string; stage: string; cvFileId?: string; interviews: { scheduledAt: string; panel: string[]; score?: number; recommendation?: string; notes?: string }[]; offerBasic?: number; offerStartDate?: string; offerJobTitle?: string; offerStatus?: string; rejectionReason?: string; hiredEmployeeId?: string }
const stages = ['Applied', 'Screening', 'Interview', 'Offer', 'Hired', 'Rejected']

export default function RecruitmentPage() {
  const [tab, setTab] = useState<'pipeline' | 'onboarding'>('pipeline')
  return (
    <>
      <PageHead title="Recruitment & onboarding" sub="Approved requisitions, applicant pipeline, offers and hire into the employee record with an onboarding checklist." />
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'pipeline', label: 'Requisitions & pipeline' }, { id: 'onboarding', label: 'Onboarding' }]} />
      {tab === 'pipeline' ? <Pipeline /> : <Onboarding />}
    </>
  )
}

function Pipeline() {
  const { can, user } = useAuth()
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['requisitions'], queryFn: () => api.get<{ requisition: Req; pipeline: Record<string, number> }[]>('/api/payroll/recruitment/requisitions') })
  const [sel, setSel] = useState<string | null>(null)
  const [show, setShow] = useState(false)
  const approve = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/recruitment/requisitions/${id}/approve`), onSuccess: () => qc.invalidateQueries({ queryKey: ['requisitions'] }) })
  const current = q.data?.find(r => r.requisition.id === sel)?.requisition ?? q.data?.[0]?.requisition
  return (
    <div className="grid c2">
      <div className="card" style={{ gridColumn: '1 / -1' }}>
        <div className="toolbar"><h2 style={{ margin: 0 }}>Requisitions</h2><span style={{ flex: 1 }} /><button className="btn primary" onClick={() => setShow(true)}>New requisition</button></div>
        <ErrorBox error={q.error ?? approve.error} />
        {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No requisitions.</Empty> : <table><thead><tr><th>Reference</th><th>Title</th><th className="num">Openings</th><th>Salary range</th><th>Pipeline</th><th>Status</th><th /></tr></thead>
          <tbody>{q.data.map(({ requisition: r, pipeline }) => <tr key={r.id} className="clickable" onClick={() => setSel(r.id)} style={r.id === current?.id ? { fontWeight: 600 } : undefined}><td className="mono">{r.reference}</td><td>{r.title}{r.justification?.startsWith('[Over budget') && <div><Badge status="Warning">Over headcount budget</Badge></div>}</td><td className="num">{r.openings}</td><td>{r.salaryMin || r.salaryMax ? `${fmt.int(r.salaryMin)} – ${fmt.int(r.salaryMax)}` : '—'}</td>
            <td className="small">{stages.filter(s => pipeline[s]).map(s => `${s} ${pipeline[s]}`).join(' · ') || '—'}</td><td><Badge status={r.status === 'Open' ? 'Open' : r.status === 'Filled' ? 'Active' : r.status === 'PendingApproval' ? 'Pending' : 'neutral'}>{r.status}</Badge></td>
            <td>{r.status === 'PendingApproval' && can(P.PayrollApprove) && r.requestedBy !== user?.id && <button className="btn sm primary" onClick={e => { e.stopPropagation(); approve.mutate(r.id) }}>Approve</button>}</td></tr>)}</tbody></table>}
      </div>
      {current && <Applicants req={current} />}
      {show && <NewRequisition onClose={() => { setShow(false); qc.invalidateQueries({ queryKey: ['requisitions'] }) }} />}
    </div>
  )
}

function NewRequisition({ onClose }: { onClose: () => void }) {
  const les = useLegalEntities(); const depts = useDepartments()
  const positions = useQuery({ queryKey: ['positions'], queryFn: () => api.get<{ id: string; code: string; title: string; vacancies: number }[]>('/api/payroll/positions') })
  const [f, setF] = useState({ title: '', positionId: '', departmentId: '', openings: 1, salaryMin: '', salaryMax: '', employmentType: 'Permanent', justification: '', description: '' })
  const m = useMutation({ mutationFn: () => api.post('/api/payroll/recruitment/requisitions', { ...f, legalEntityId: les.data?.[0]?.id, positionId: f.positionId || null, departmentId: f.departmentId || null, salaryMin: f.salaryMin ? Number(f.salaryMin) : null, salaryMax: f.salaryMax ? Number(f.salaryMax) : null }), onSuccess: onClose })
  return (
    <Modal title="New requisition" onClose={onClose}>
      <ErrorBox error={m.error} />
      <div className="form-grid">
        <Field label="Job title *"><input value={f.title} onChange={e => setF({ ...f, title: e.target.value })} /></Field>
        <Field label="Position"><select value={f.positionId} onChange={e => { const p = positions.data?.find(x => x.id === e.target.value); setF({ ...f, positionId: e.target.value, title: f.title || p?.title || '' }) }}><option value="">—</option>{positions.data?.map(p => <option key={p.id} value={p.id}>{p.code} {p.title} ({p.vacancies} vacant)</option>)}</select></Field>
        <Field label="Department"><select value={f.departmentId} onChange={e => setF({ ...f, departmentId: e.target.value })}><option value="">—</option>{depts.data?.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}</select></Field>
        <Field label="Openings"><input type="number" min={1} value={f.openings} onChange={e => setF({ ...f, openings: Number(e.target.value) })} /></Field>
        <Field label="Salary min"><input type="number" value={f.salaryMin} onChange={e => setF({ ...f, salaryMin: e.target.value })} /></Field>
        <Field label="Salary max"><input type="number" value={f.salaryMax} onChange={e => setF({ ...f, salaryMax: e.target.value })} /></Field>
        <Field label="Employment type"><select value={f.employmentType} onChange={e => setF({ ...f, employmentType: e.target.value })}>{['Permanent', 'Contract', 'Casual', 'Intern'].map(t => <option key={t}>{t}</option>)}</select></Field>
      </div>
      <Field label="Justification"><textarea rows={2} value={f.justification} onChange={e => setF({ ...f, justification: e.target.value })} /></Field>
      <p className="small muted">The requisition needs approval from a budget holder (payroll approver) other than you.</p>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={!f.title || m.isPending} onClick={() => m.mutate()}>Submit for approval</button></div>
    </Modal>
  )
}

function Applicants({ req }: { req: Req }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['applications', req.id], queryFn: () => api.get<App[]>(`/api/payroll/recruitment/requisitions/${req.id}/applications`) })
  const [open, setOpen] = useState<App | null>(null)
  const [add, setAdd] = useState(false)
  const refresh = () => { qc.invalidateQueries({ queryKey: ['applications', req.id] }); qc.invalidateQueries({ queryKey: ['requisitions'] }) }
  return (
    <div className="card" style={{ gridColumn: '1 / -1' }}>
      <div className="toolbar"><h2 style={{ margin: 0 }}>Applicants — {req.title} <span className="muted small mono">{req.reference}</span></h2><span style={{ flex: 1 }} />{req.status === 'Open' && <button className="btn" onClick={() => setAdd(true)}>Add applicant</button>}</div>
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No applicants yet.</Empty> : (
        <div style={{ display: 'grid', gridTemplateColumns: `repeat(${stages.length}, minmax(140px, 1fr))`, gap: 8, overflowX: 'auto' }}>
          {stages.map(s => <div key={s}><h3>{s} ({q.data!.filter(a => a.stage === s).length})</h3>
            {q.data!.filter(a => a.stage === s).map(a => <div key={a.id} className="card" style={{ padding: 10, marginBottom: 6, cursor: 'pointer' }} onClick={() => setOpen(a)}><b>{a.firstName} {a.lastName}</b><div className="muted small">{a.source ?? '—'}{a.interviews.length > 0 && ` · ${a.interviews.length} interview(s)`}</div>{a.offerStatus && <Badge status={a.offerStatus === 'Accepted' ? 'Active' : a.offerStatus === 'Declined' ? 'Error' : 'Pending'}>Offer {a.offerStatus}</Badge>}</div>)}
          </div>)}
        </div>)}
      {open && <ApplicantDialog a={open} req={req} onClose={() => { setOpen(null); refresh() }} />}
      {add && <AddApplicant req={req} onClose={() => { setAdd(false); refresh() }} />}
    </div>
  )
}

function AddApplicant({ req, onClose }: { req: Req; onClose: () => void }) {
  const [f, setF] = useState({ firstName: '', lastName: '', email: '', phone: '', source: '', consent: false })
  const [cv, setCv] = useState<File | null>(null)
  const m = useMutation({ mutationFn: () => { const form = new FormData(); Object.entries(f).forEach(([k, v]) => form.append(k, String(v))); if (cv) form.append('cv', cv); return api.upload(`/api/payroll/recruitment/requisitions/${req.id}/applications`, form) }, onSuccess: onClose })
  return (
    <Modal title="Add applicant" onClose={onClose}>
      <ErrorBox error={m.error} />
      <div className="form-grid">{(['firstName', 'lastName', 'email', 'phone', 'source'] as const).map(k => <Field key={k} label={({ firstName: 'First name *', lastName: 'Last name *', email: 'Email *', phone: 'Phone', source: 'Source' })[k]}><input value={f[k]} onChange={e => setF({ ...f, [k]: e.target.value })} /></Field>)}</div>
      <Field label="CV (PDF/Word)"><input type="file" accept=".pdf,.docx" onChange={e => setCv(e.target.files?.[0] ?? null)} /></Field>
      <label className="field inline"><input type="checkbox" checked={f.consent} onChange={e => setF({ ...f, consent: e.target.checked })} /> Applicant consents to us keeping their data for future vacancies (Data Protection Act 2019)</label>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={!f.firstName || !f.lastName || !f.email || m.isPending} onClick={() => m.mutate()}>Add</button></div>
    </Modal>
  )
}

function ApplicantDialog({ a, req, onClose }: { a: App; req: Req; onClose: () => void }) {
  const les = useLegalEntities()
  const groups = usePayrollGroups(les.data?.[0]?.id)
  const emps = useEmployeeOptions()
  const stage = useMutation({ mutationFn: (s: string) => api.post(`/api/payroll/recruitment/applications/${a.id}/stage`, { stage: s }), onSuccess: onClose })
  const [iv, setIv] = useState({ scheduledAt: '', panel: '', score: '', recommendation: '' })
  const interview = useMutation({ mutationFn: () => api.post(`/api/payroll/recruitment/applications/${a.id}/interviews`, { scheduledAt: new Date(iv.scheduledAt).toISOString(), panel: iv.panel.split(',').map(x => x.trim()).filter(Boolean), score: iv.score ? Number(iv.score) : null, recommendation: iv.recommendation }), onSuccess: onClose })
  const [offer, setOffer] = useState({ basic: a.offerBasic ?? req.salaryMin ?? 0, startDate: a.offerStartDate ?? '', jobTitle: a.offerJobTitle ?? req.title, status: a.offerStatus ?? 'Sent' })
  const saveOffer = useMutation({ mutationFn: () => api.post(`/api/payroll/recruitment/applications/${a.id}/offer`, offer), onSuccess: onClose })
  const [hire, setHire] = useState({ employeeNumber: '', payrollGroupId: '', reportingManagerId: '', probationMonths: 6 })
  const doHire = useMutation({ mutationFn: () => api.post<{ employeeNumber: string }>(`/api/payroll/recruitment/applications/${a.id}/hire`, { ...hire, payrollGroupId: hire.payrollGroupId || groups.data?.[0]?.id, reportingManagerId: hire.reportingManagerId || null }), onSuccess: onClose })
  const [reject, setReject] = useState(false)
  const closed = a.stage === 'Hired' || a.stage === 'Rejected'
  return (
    <Modal title={`${a.firstName} ${a.lastName}`} onClose={onClose} width={760}>
      <dl className="kv"><dt>Contact</dt><dd>{a.email} · {a.phone ?? '—'}</dd><dt>Source</dt><dd>{a.source ?? '—'}</dd><dt>Stage</dt><dd><Badge status="neutral">{a.stage}</Badge> {a.rejectionReason && <span className="muted small">({a.rejectionReason})</span>}</dd><dt>CV</dt><dd>{a.cvFileId ? <FileLink id={a.cvFileId} label="Download CV" /> : '—'}</dd></dl>
      <ErrorBox error={stage.error ?? interview.error ?? saveOffer.error ?? doHire.error} />
      {!closed && <div className="toolbar" style={{ marginTop: 10 }}><span className="small muted">Move to</span>{['Screening', 'Interview'].filter(s => s !== a.stage).map(s => <button key={s} className="btn sm" onClick={() => stage.mutate(s)}>{s}</button>)}<button className="btn sm danger" onClick={() => setReject(true)}>Reject</button></div>}
      <h3 style={{ marginTop: 12 }}>Interviews</h3>
      {a.interviews.length === 0 ? <p className="small muted">None recorded.</p> : <table><tbody>{a.interviews.map((i, k) => <tr key={k}><td>{fmt.dateTime(i.scheduledAt)}</td><td>{i.panel.join(', ')}</td><td>{i.score ? `${i.score}/5` : ''}</td><td style={{ whiteSpace: 'normal' }}>{i.recommendation}</td></tr>)}</tbody></table>}
      {!closed && <div className="toolbar"><input type="datetime-local" value={iv.scheduledAt} onChange={e => setIv({ ...iv, scheduledAt: e.target.value })} aria-label="Interview time" /><input placeholder="Panel (comma separated)" value={iv.panel} onChange={e => setIv({ ...iv, panel: e.target.value })} aria-label="Panel" /><input type="number" min={1} max={5} placeholder="Score" value={iv.score} onChange={e => setIv({ ...iv, score: e.target.value })} style={{ width: 70 }} aria-label="Score" /><input placeholder="Recommendation" value={iv.recommendation} onChange={e => setIv({ ...iv, recommendation: e.target.value })} aria-label="Recommendation" /><button className="btn sm" disabled={!iv.scheduledAt} onClick={() => interview.mutate()}>Record</button></div>}
      {!closed && <><h3 style={{ marginTop: 12 }}>Offer</h3>
        <div className="toolbar"><input type="number" value={offer.basic} onChange={e => setOffer({ ...offer, basic: Number(e.target.value) })} aria-label="Basic salary" style={{ width: 120 }} /><input type="date" value={offer.startDate} onChange={e => setOffer({ ...offer, startDate: e.target.value })} aria-label="Start date" /><input value={offer.jobTitle} onChange={e => setOffer({ ...offer, jobTitle: e.target.value })} aria-label="Job title" /><select value={offer.status} onChange={e => setOffer({ ...offer, status: e.target.value })} aria-label="Offer status">{['Draft', 'Sent', 'Accepted', 'Declined'].map(s => <option key={s}>{s}</option>)}</select><button className="btn sm" disabled={!offer.startDate} onClick={() => saveOffer.mutate()}>Save offer</button></div>
        {req.salaryMax && <p className="small muted">Approved range {fmt.int(req.salaryMin)} – {fmt.int(req.salaryMax)}.</p>}</>}
      {a.stage === 'Offer' && a.offerStatus === 'Accepted' && <div className="card" style={{ marginTop: 12 }}><h3>Hire</h3>
        <div className="form-grid"><Field label="Employee number *"><input value={hire.employeeNumber} onChange={e => setHire({ ...hire, employeeNumber: e.target.value })} /></Field>
          <Field label="Payroll group"><select value={hire.payrollGroupId || groups.data?.[0]?.id} onChange={e => setHire({ ...hire, payrollGroupId: e.target.value })}>{groups.data?.map(g => <option key={g.id} value={g.id}>{g.name}</option>)}</select></Field>
          <Field label="Reports to"><select value={hire.reportingManagerId} onChange={e => setHire({ ...hire, reportingManagerId: e.target.value })}><option value="">—</option>{emps.data?.map(e => <option key={e.id} value={e.id}>{e.employeeNumber} {e.fullName}</option>)}</select></Field>
          <Field label="Probation (months, max 12 incl. extension — s.42)"><input type="number" min={0} max={12} value={hire.probationMonths} onChange={e => setHire({ ...hire, probationMonths: Number(e.target.value) })} /></Field></div>
        <button className="btn primary" disabled={!hire.employeeNumber || doHire.isPending} onClick={() => doHire.mutate()}>Create employee &amp; onboarding</button></div>}
      {a.hiredEmployeeId && <div className="alert ok">Hired. Complete the onboarding checklist and statutory details on the employee record.</div>}
      {reject && <ReasonDialog title="Reject applicant" label="Reason" required danger confirmLabel="Reject" onClose={() => setReject(false)} onConfirm={t => api.post(`/api/payroll/recruitment/applications/${a.id}/stage`, { stage: 'Rejected', reason: t }).then(onClose)} />}
    </Modal>
  )
}

function Onboarding() {
  const qc = useQueryClient()
  const [openOnly, setOpenOnly] = useState(true)
  const q = useQuery({ queryKey: ['onboarding', openOnly], queryFn: () => api.get<{ id: string; employee: string; employeeNumber: string; title: string; owner: string; dueDate: string; done: boolean; doneBy?: string }[]>(`/api/payroll/recruitment/onboarding?openOnly=${openOnly}`) })
  const toggle = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/recruitment/onboarding/${id}/done`), onSuccess: () => qc.invalidateQueries({ queryKey: ['onboarding'] }) })
  const today = isoDate()
  return (
    <div className="card">
      <label className="field inline"><input type="checkbox" checked={openOnly} onChange={e => setOpenOnly(e.target.checked)} /> Open tasks only</label>
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No onboarding tasks.</Empty> : <table><thead><tr><th /><th>Employee</th><th>Task</th><th>Owner</th><th>Due</th></tr></thead>
        <tbody>{q.data.map(t => <tr key={t.id}><td><input type="checkbox" checked={t.done} onChange={() => toggle.mutate(t.id)} aria-label={t.title} /></td><td>{t.employeeNumber} {t.employee}</td><td>{t.title}{t.doneBy && <span className="muted small"> · {t.doneBy}</span>}</td><td>{t.owner}</td><td>{!t.done && t.dueDate < today ? <Badge status="Error">{fmt.date(t.dueDate)}</Badge> : fmt.date(t.dueDate)}</td></tr>)}</tbody></table>}
    </div>
  )
}
