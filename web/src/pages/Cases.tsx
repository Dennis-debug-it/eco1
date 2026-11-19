import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt, isoDate } from '../lib/api'
import { useEmployeeOptions } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead } from '../components/ui'

type CaseRow = { id: string; reference: string; type: string; category: string; status: string; outcome: string; incidentDate: string; hearingAt?: string; employee: string; employeeNumber: string; createdAt: string }
type CaseDetail = { case: { id: string; reference: string; type: string; category: string; summary: string; incidentDate: string; status: string; showCauseIssuedOn?: string; responseDueOn?: string; hearingAt?: string; hearingHeld: boolean; companion?: string; outcome: string; outcomeReason?: string; appealDeadline?: string; events: { at: string; by: string; kind: string; note: string }[] }; employee: { employeeNumber: string; name: string; jobTitle?: string } }
const human = (s: string) => s.replace(/([A-Z])/g, ' $1').trim()

export default function CasesPage() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['cases'], queryFn: () => api.get<CaseRow[]>('/api/payroll/cases') })
  const [open, setOpen] = useState<string | null>(null)
  const [show, setShow] = useState(false)
  return (
    <>
      <PageHead title="Disciplinary & grievance" sub="Confidential. Every view is audited. A dismissal or final sanction is blocked until the employee has been told the grounds and heard (Employment Act s.41).">
        <button className="btn primary" onClick={() => setShow(true)}>Open a case</button>
      </PageHead>
      <div className="card">
        <ErrorBox error={q.error} />
        {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No cases.</Empty> : <table><thead><tr><th>Reference</th><th>Type</th><th>Employee</th><th>Category</th><th>Incident</th><th>Hearing</th><th>Status</th><th>Outcome</th></tr></thead>
          <tbody>{q.data.map(c => <tr key={c.id} className="clickable" onClick={() => setOpen(c.id)}><td className="mono">{c.reference}</td><td>{c.type}</td><td>{c.employeeNumber} {c.employee}</td><td>{c.category}</td><td>{fmt.date(c.incidentDate)}</td><td>{fmt.dateTime(c.hearingAt)}</td><td><Badge status={c.status === 'Closed' ? 'Closed' : 'Pending'}>{human(c.status)}</Badge></td><td>{c.outcome !== 'None' ? human(c.outcome) : ''}</td></tr>)}</tbody></table>}
      </div>
      {show && <NewCase onClose={() => { setShow(false); qc.invalidateQueries({ queryKey: ['cases'] }) }} />}
      {open && <CaseDialog id={open} onClose={() => { setOpen(null); qc.invalidateQueries({ queryKey: ['cases'] }) }} />}
    </>
  )
}

function NewCase({ onClose }: { onClose: () => void }) {
  const emps = useEmployeeOptions()
  const [f, setF] = useState({ type: 'Disciplinary', employeeId: '', category: '', summary: '', incidentDate: isoDate() })
  const m = useMutation({ mutationFn: () => api.post('/api/payroll/cases', { ...f, employeeId: f.employeeId || emps.data?.[0]?.id }), onSuccess: onClose })
  return (
    <Modal title="Open a case" onClose={onClose}>
      <ErrorBox error={m.error} />
      <div className="form-grid">
        <Field label="Type"><select value={f.type} onChange={e => setF({ ...f, type: e.target.value })}><option>Disciplinary</option><option>Grievance</option></select></Field>
        <Field label="Employee"><select value={f.employeeId || emps.data?.[0]?.id} onChange={e => setF({ ...f, employeeId: e.target.value })}>{emps.data?.map(e => <option key={e.id} value={e.id}>{e.employeeNumber} {e.fullName}</option>)}</select></Field>
        <Field label="Category"><input value={f.category} onChange={e => setF({ ...f, category: e.target.value })} placeholder="e.g. Absence, Misconduct, Harassment" /></Field>
        <Field label="Date of incident"><input type="date" value={f.incidentDate} onChange={e => setF({ ...f, incidentDate: e.target.value })} /></Field>
      </div>
      <Field label="Summary"><textarea rows={4} value={f.summary} onChange={e => setF({ ...f, summary: e.target.value })} /></Field>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={!f.category || !f.summary || m.isPending} onClick={() => m.mutate()}>Open case</button></div>
    </Modal>
  )
}

function CaseDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['case', id], queryFn: () => api.get<CaseDetail>(`/api/payroll/cases/${id}`) })
  const [s, setS] = useState({ to: 'Investigation', note: '', showCauseIssuedOn: '', responseDueOn: '', hearingAt: '', companion: '', outcome: 'WrittenWarning', hearingHeld: true })
  const step = useMutation({
    mutationFn: () => api.post(`/api/payroll/cases/${id}/step`, { to: s.to, note: s.note, showCauseIssuedOn: s.showCauseIssuedOn || null, responseDueOn: s.responseDueOn || null, hearingAt: s.hearingAt ? new Date(s.hearingAt).toISOString() : null, companion: s.companion || null, outcome: s.to === 'Decided' ? s.outcome : null, hearingHeld: s.to === 'Decided' ? s.hearingHeld : null }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['case', id] }); setS({ ...s, note: '' }) },
  })
  const d = q.data
  if (!d) return <Modal title="Case" onClose={onClose}>{q.isLoading ? <Loading /> : <ErrorBox error={q.error} />}</Modal>
  const c = d.case
  const disciplinary = c.type === 'Disciplinary'
  const steps = disciplinary ? ['Investigation', 'ShowCauseIssued', 'HearingScheduled', 'Decided', 'Appealed', 'Closed'] : ['Investigation', 'HearingScheduled', 'Decided', 'Appealed', 'Closed']
  const outcomes = disciplinary ? ['NoAction', 'VerbalWarning', 'WrittenWarning', 'FinalWarning', 'Suspension', 'Dismissal'] : ['GrievanceUpheld', 'GrievanceNotUpheld', 'NoAction']
  return (
    <Modal title={`${c.reference} — ${d.employee.name}`} onClose={onClose} width={820}>
      <dl className="kv"><dt>Type</dt><dd>{c.type} · {c.category}</dd><dt>Incident</dt><dd>{fmt.date(c.incidentDate)}</dd><dt>Summary</dt><dd style={{ whiteSpace: 'pre-wrap' }}>{c.summary}</dd>
        {c.showCauseIssuedOn && <><dt>Show cause</dt><dd>Issued {fmt.date(c.showCauseIssuedOn)}, response due {fmt.date(c.responseDueOn)}</dd></>}
        {c.hearingAt && <><dt>Hearing</dt><dd>{fmt.dateTime(c.hearingAt)} {c.companion && `· accompanied by ${c.companion}`} {c.hearingHeld && <Badge status="Active">Held</Badge>}</dd></>}
        {c.outcome !== 'None' && <><dt>Outcome</dt><dd><b>{human(c.outcome)}</b> — {c.outcomeReason} {c.appealDeadline && <span className="muted small">(appeal by {fmt.date(c.appealDeadline)})</span>}</dd></>}</dl>
      <h3 style={{ marginTop: 12 }}>Timeline</h3>
      <table><tbody>{c.events.map((e, i) => <tr key={i}><td className="small">{fmt.dateTime(e.at)}</td><td>{human(e.kind)}</td><td className="small">{e.by}</td><td style={{ whiteSpace: 'pre-wrap' }}>{e.note}</td></tr>)}</tbody></table>
      {c.status !== 'Closed' && <div className="card" style={{ marginTop: 12 }}>
        <h3>Next step</h3>
        <ErrorBox error={step.error} />
        <div className="form-grid">
          <Field label="Step"><select value={s.to} onChange={e => setS({ ...s, to: e.target.value })}>{steps.map(x => <option key={x} value={x}>{human(x)}</option>)}</select></Field>
          {s.to === 'ShowCauseIssued' && <><Field label="Letter issued"><input type="date" value={s.showCauseIssuedOn} onChange={e => setS({ ...s, showCauseIssuedOn: e.target.value })} /></Field><Field label="Response due"><input type="date" value={s.responseDueOn} onChange={e => setS({ ...s, responseDueOn: e.target.value })} /></Field></>}
          {s.to === 'HearingScheduled' && <><Field label="Hearing date & time"><input type="datetime-local" value={s.hearingAt} onChange={e => setS({ ...s, hearingAt: e.target.value })} /></Field><Field label="Employee's companion (colleague or union rep)"><input value={s.companion} onChange={e => setS({ ...s, companion: e.target.value })} /></Field></>}
          {s.to === 'Decided' && <><Field label="Outcome"><select value={s.outcome} onChange={e => setS({ ...s, outcome: e.target.value })}>{outcomes.map(o => <option key={o} value={o}>{human(o)}</option>)}</select></Field><label className="field inline"><input type="checkbox" checked={s.hearingHeld} onChange={e => setS({ ...s, hearingHeld: e.target.checked })} /> Hearing was held</label></>}
        </div>
        <Field label="Note (required)"><textarea rows={3} value={s.note} onChange={e => setS({ ...s, note: e.target.value })} /></Field>
        <button className="btn primary" disabled={!s.note || step.isPending} onClick={() => step.mutate()}>Record step</button>
      </div>}
    </Modal>
  )
}
