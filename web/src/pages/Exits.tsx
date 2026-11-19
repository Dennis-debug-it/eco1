import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt, isoDate } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useEmployeeOptions } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, ReasonDialog } from '../components/ui'

type Row = { id: string; employeeId: string; employeeNumber: string; employee: string; jobTitle?: string; type: string; noticeDate: string; lastWorkingDay: string; status: string; checklistDone: number; checklistTotal: number }
type Detail = {
  separation: { id: string; type: string; noticeDate: string; lastWorkingDay: string; noticeDaysServed: number; payNoticeInLieu: boolean; reason: string; status: string; exitInterviewNotes?: string; eligibleForRehire: boolean;
    checklist: { key: string; title: string; owner: string; done: boolean; doneBy?: string; doneAt?: string; note?: string }[];
    dues?: { monthlyBasic: number; dailyRateWorking: number; dailyRateCalendar: number; completedYearsOfService: number; lines: { code: string; description: string; amount: number; working: string }[]; notes: string[]; payableInPeriodEnding: string } }
  duesHidden: boolean; employee: { id: string; employeeNumber: string; name: string; jobTitle?: string; hireDate: string; noticePeriodDays: number; status: string; terminationDate?: string }
  payItems: { id: string; description: string; amount: number; status: string; dueDate: string }[]; canApprove: boolean
}
const flow = ['Clearance', 'DuesCalculated', 'Approved', 'Completed']
const human = (s: string) => s.replace(/([A-Z])/g, ' $1').trim()

export default function ExitsPage() {
  const { can } = useAuth()
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['separations'], queryFn: () => api.get<Row[]>('/api/payroll/separations') })
  const [open, setOpen] = useState<string | null>(null)
  const [start, setStart] = useState(false)
  return (
    <>
      <PageHead title="Exits & final dues" sub="HR runs the exit and checklist; payroll calculates final dues; a different approver signs off. Dues are paid by the employee's final regular payroll.">
        {can(P.HrSeparations) && <button className="btn primary" onClick={() => setStart(true)}>Start an exit</button>}
      </PageHead>
      <div className="card">
        <ErrorBox error={q.error} />
        {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No exits in progress.</Empty> : <table><thead><tr><th>Employee</th><th>Type</th><th>Notice given</th><th>Last day</th><th>Checklist</th><th>Status</th></tr></thead>
          <tbody>{q.data.map(r => <tr key={r.id} className="clickable" onClick={() => setOpen(r.id)}><td>{r.employeeNumber} {r.employee}<div className="muted small">{r.jobTitle}</div></td><td>{human(r.type)}</td><td>{fmt.date(r.noticeDate)}</td><td>{fmt.date(r.lastWorkingDay)}</td><td>{r.checklistDone}/{r.checklistTotal}</td><td><Badge status={r.status === 'Completed' ? 'Active' : r.status === 'Cancelled' ? 'Cancelled' : r.status === 'Approved' ? 'Approved' : 'Pending'}>{human(r.status)}</Badge></td></tr>)}</tbody></table>}
      </div>
      {start && <StartExit onClose={id => { setStart(false); qc.invalidateQueries({ queryKey: ['separations'] }); if (id) setOpen(id) }} />}
      {open && <ExitDialog id={open} onClose={() => { setOpen(null); qc.invalidateQueries({ queryKey: ['separations'] }) }} />}
    </>
  )
}

function StartExit({ onClose }: { onClose: (id?: string) => void }) {
  const emps = useEmployeeOptions()
  const today = isoDate()
  const [f, setF] = useState({ employeeId: '', type: 'Resignation', noticeDate: today, lastWorkingDay: '', noticeDaysServed: 30, settleNoticeInLieu: false, reason: '' })
  const m = useMutation({ mutationFn: () => api.post<{ id: string }>('/api/payroll/separations', { ...f, employeeId: f.employeeId || emps.data?.[0]?.id }), onSuccess: r => onClose(r.id) })
  return (
    <Modal title="Start an exit" onClose={() => onClose()}>
      <ErrorBox error={m.error} />
      <Field label="Employee"><select value={f.employeeId || emps.data?.[0]?.id} onChange={e => setF({ ...f, employeeId: e.target.value })}>{emps.data?.map(e => <option key={e.id} value={e.id}>{e.employeeNumber} {e.fullName}</option>)}</select></Field>
      <div className="form-grid">
        <Field label="Type"><select value={f.type} onChange={e => setF({ ...f, type: e.target.value })}>{['Resignation', 'Termination', 'Redundancy', 'Retirement', 'EndOfContract', 'SummaryDismissal', 'Death'].map(t => <option key={t} value={t}>{human(t)}</option>)}</select></Field>
        <Field label="Notice date"><input type="date" value={f.noticeDate} onChange={e => setF({ ...f, noticeDate: e.target.value })} /></Field>
        <Field label="Last working day"><input type="date" value={f.lastWorkingDay} onChange={e => setF({ ...f, lastWorkingDay: e.target.value })} /></Field>
        <Field label="Notice days served"><input type="number" value={f.noticeDaysServed} onChange={e => setF({ ...f, noticeDaysServed: Number(e.target.value) })} /></Field>
      </div>
      <label className="field inline"><input type="checkbox" checked={f.settleNoticeInLieu} onChange={e => setF({ ...f, settleNoticeInLieu: e.target.checked })} /> Settle any notice shortfall in lieu (employer pays it when ending the contract; recovered from a resigning employee)</label>
      <Field label="Reason"><textarea rows={2} value={f.reason} onChange={e => setF({ ...f, reason: e.target.value })} /></Field>
      {f.type === 'Redundancy' && <div className="alert warn">Redundancy requires one month's written notice to the employee/union and the Labour Officer, fair selection criteria, and severance of at least 15 days' pay per completed year (Employment Act s.40).</div>}
      {f.type === 'SummaryDismissal' && <div className="alert warn">Summary dismissal must follow a fair hearing (s.41) for gross misconduct (s.44). Record the disciplinary case first.</div>}
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => onClose()}>Cancel</button><button className="btn primary" disabled={!f.lastWorkingDay || !f.reason || m.isPending} onClick={() => m.mutate()}>Start exit</button></div>
    </Modal>
  )
}

function ExitDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['separation', id], queryFn: () => api.get<Detail>(`/api/payroll/separations/${id}`) })
  const refresh = () => { qc.invalidateQueries({ queryKey: ['separation', id] }); qc.invalidateQueries({ queryKey: ['separations'] }) }
  const act = useMutation({ mutationFn: (a: string) => api.post(`/api/payroll/separations/${id}/${a}`), onSuccess: refresh })
  const tick = useMutation({ mutationFn: (x: { key: string; done: boolean }) => api.post(`/api/payroll/separations/${id}/checklist`, x), onSuccess: refresh })
  const [interview, setInterview] = useState<{ notes: string; rehire: boolean } | null>(null)
  const saveInterview = useMutation({ mutationFn: () => api.post(`/api/payroll/separations/${id}/exit-interview`, { notes: interview!.notes, eligibleForRehire: interview!.rehire }), onSuccess: () => { setInterview(null); refresh() } })
  const [cancel, setCancel] = useState(false)
  const d = q.data
  if (!d) return <Modal title="Exit" onClose={onClose}>{q.isLoading ? <Loading /> : <ErrorBox error={q.error} />}</Modal>
  const s = d.separation
  const total = (s.dues?.lines ?? []).reduce((a, l) => a + l.amount, 0)
  return (
    <Modal title={`${d.employee.name} — ${human(s.type)}`} onClose={onClose} width={900}>
      <div className="steps">{flow.map((f, i) => <span key={f} className={s.status === f ? 'current' : flow.indexOf(s.status) > i ? 'done' : ''}>{human(f)}</span>)}{s.status === 'Cancelled' && <span className="badge danger">Cancelled</span>}</div>
      <dl className="kv"><dt>Employee</dt><dd>{d.employee.employeeNumber} · {d.employee.jobTitle} · hired {fmt.date(d.employee.hireDate)}</dd><dt>Notice</dt><dd>{fmt.date(s.noticeDate)} → last day {fmt.date(s.lastWorkingDay)} · {s.noticeDaysServed} of {d.employee.noticePeriodDays} days served{s.payNoticeInLieu && ' · shortfall settled in lieu'}</dd><dt>Reason</dt><dd>{s.reason}</dd></dl>
      <ErrorBox error={act.error ?? tick.error ?? saveInterview.error} />
      <div className="grid c2" style={{ marginTop: 12 }}>
        <div><h3>Offboarding checklist</h3>
          <table><tbody>{s.checklist.map(i => <tr key={i.key}><td style={{ width: 28, paddingRight: 0 }}><input type="checkbox" checked={i.done} disabled={!can(P.HrSeparations) || s.status === 'Completed' || s.status === 'Cancelled'} onChange={e => tick.mutate({ key: i.key, done: e.target.checked })} aria-label={i.title} /></td><td style={{ whiteSpace: 'normal' }}>{i.title}<div className="muted small">{i.owner}{i.done && ` · ${i.doneBy} ${fmt.dateTime(i.doneAt)}`}</div></td></tr>)}</tbody></table>
          <div style={{ marginTop: 8 }}>{s.exitInterviewNotes ? <p className="small"><b>Exit interview:</b> {s.exitInterviewNotes} {s.eligibleForRehire ? '· eligible for rehire' : '· not eligible for rehire'}</p> : can(P.HrSeparations) && <button className="btn sm" onClick={() => setInterview({ notes: '', rehire: true })}>Record exit interview</button>}</div>
        </div>
        <div><h3>Final dues</h3>
          {d.duesHidden ? <p className="small muted">Final dues are visible to payroll and finance roles only.</p> : !s.dues ? <p className="small muted">Not calculated yet.</p> : <>
            <table><tbody>{s.dues.lines.map((l, i) => <tr key={i}><td style={{ whiteSpace: 'normal' }}>{l.description}<div className="muted small">{l.working}</div></td><td className="num" style={{ color: l.amount < 0 ? 'var(--danger)' : undefined }}>{fmt.num(l.amount)}</td></tr>)}</tbody>
              <tfoot><tr><td>Net (before statutory deductions)</td><td className="num">{fmt.num(total)}</td></tr></tfoot></table>
            <ul className="issue-list small muted">{s.dues.notes.map((n, i) => <li key={i}>{n}</li>)}</ul></>}
          {d.payItems.length > 0 && <><h3>Handed to payroll</h3><table><tbody>{d.payItems.map(p => <tr key={p.id}><td>{p.description}</td><td className="num">{fmt.num(p.amount)}</td><td><Badge status={p.status === 'Processed' ? 'Active' : p.status === 'Cancelled' ? 'Cancelled' : 'Pending'}>{p.status}</Badge></td></tr>)}</tbody></table></>}
        </div>
      </div>
      <div className="actions" style={{ justifyContent: 'flex-end', marginTop: 14 }}>
        {(s.status === 'Clearance' || s.status === 'DuesCalculated') && can(P.SalaryRead) && <button className="btn" disabled={act.isPending} onClick={() => act.mutate('calculate')}>{s.dues ? 'Recalculate dues' : 'Calculate dues'}</button>}
        {s.status === 'DuesCalculated' && d.canApprove && <button className="btn primary" disabled={act.isPending} onClick={() => act.mutate('approve')}>Approve final dues</button>}
        {s.status === 'DuesCalculated' && !d.canApprove && can(P.SalaryRead) && <span className="small muted">Awaiting approval by a payroll approver other than the initiator.</span>}
        {s.status === 'Approved' && can(P.HrSeparations) && <button className="btn primary" disabled={act.isPending} onClick={() => act.mutate('complete')}>Complete exit</button>}
        {s.dues && !d.duesHidden && <button className="btn" onClick={() => api.download(`/api/payroll/separations/${id}/statement.pdf`)}>Dues statement (PDF)</button>}
        {(s.status === 'Approved' || s.status === 'Completed') && can(P.HrSeparations) && <button className="btn" onClick={() => api.download(`/api/payroll/employees/${d.employee.id}/certificate-of-service.pdf`)}>Certificate of service</button>}
        {s.status !== 'Completed' && s.status !== 'Cancelled' && can(P.HrSeparations) && <button className="btn danger" onClick={() => setCancel(true)}>Cancel exit</button>}
      </div>
      {interview && <Modal title="Exit interview" onClose={() => setInterview(null)}><Field label="Notes"><textarea rows={5} value={interview.notes} onChange={e => setInterview({ ...interview, notes: e.target.value })} /></Field><label className="field inline"><input type="checkbox" checked={interview.rehire} onChange={e => setInterview({ ...interview, rehire: e.target.checked })} /> Eligible for rehire</label><div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn primary" onClick={() => saveInterview.mutate()}>Save</button></div></Modal>}
      {cancel && <ReasonDialog title="Cancel exit" label="Reason" required danger confirmLabel="Cancel exit" onClose={() => setCancel(false)} onConfirm={t => api.post(`/api/payroll/separations/${id}/cancel`, { text: t }).then(refresh)} />}
    </Modal>
  )
}
