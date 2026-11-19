import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, ReasonDialog, Tabs } from '../components/ui'

type Due = { employeeId: string; employeeNumber: string; name: string; terminationDate: string; dueSince: string }
type ApplicantDue = { applicationId: string; name: string; requisition: string; appliedAt: string; consent: boolean }
type Retention = { employeeRetentionYears: number; applicantMonths: number; applicantConsentMonths: number; employees: Due[]; applicants: ApplicantDue[] }
type Breach = { id: string; reference: string; detectedAt: string; occurredAt?: string; description: string; dataCategories: string[]; subjectsAffected?: number; risk: string; cause?: string; containment?: string; reportedToCommissionerAt?: string; commissionerReference?: string; notReportedReason?: string; subjectsNotifiedAt?: string; subjectsNotNotifiedReason?: string; status: string; closedAt?: string }
type BreachRow = { breach: Breach; commissionerDeadline: string; commissionerOverdue: boolean; hoursLeft?: number }

export default function DataProtectionPage() {
  const [tab, setTab] = useState<'breaches' | 'retention' | 'access'>('breaches')
  return (
    <>
      <PageHead title="Data protection" sub="Kenya Data Protection Act 2019: breach register (s.43), retention (s.39) and access requests (s.26)." />
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'breaches', label: 'Breach register' }, { id: 'retention', label: 'Retention' }, { id: 'access', label: 'Access requests' }]} />
      {tab === 'breaches' && <Breaches />}
      {tab === 'retention' && <RetentionTab />}
      {tab === 'access' && <Access />}
    </>
  )
}

const toLocalInput = (v?: string) => (v ? new Date(new Date(v).getTime() + 3 * 3600e3).toISOString().slice(0, 16) : '')
const fromLocalInput = (v: string) => (v ? `${v}:00+03:00` : null)

function Breaches() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['breaches'], queryFn: () => api.get<BreachRow[]>('/api/payroll/data-protection/breaches'), refetchInterval: 60_000 })
  const [edit, setEdit] = useState<Breach | 'new' | null>(null)
  return (
    <div className="card">
      <div className="toolbar"><h2 style={{ margin: 0 }}>Personal data breaches</h2><span style={{ flex: 1 }} /><button className="btn primary" onClick={() => setEdit('new')}>Record a breach</button></div>
      <p className="small muted">Notify the Office of the Data Protection Commissioner within 72 hours of becoming aware of a breach that puts people at risk, and tell the people affected without delay. Record the decision either way.</p>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No breaches recorded.</Empty> : <div className="table-wrap"><table><thead><tr><th>Ref</th><th>Detected</th><th>What happened</th><th>Risk</th><th>Commissioner</th><th>People told</th><th>Status</th><th /></tr></thead>
        <tbody>{q.data.map(({ breach: b, commissionerDeadline, commissionerOverdue, hoursLeft }) => <tr key={b.id}>
          <td className="mono">{b.reference}</td><td>{fmt.dateTime(b.detectedAt)}</td><td style={{ whiteSpace: 'normal' }}>{b.description}<div className="muted small">{b.dataCategories.join(', ')}{b.subjectsAffected ? ` · ${b.subjectsAffected} people` : ''}</div></td>
          <td><Badge status={b.risk === 'High' ? 'Error' : b.risk === 'Medium' ? 'Warning' : 'neutral'}>{b.risk}</Badge></td>
          <td>{b.reportedToCommissionerAt ? <>{fmt.dateTime(b.reportedToCommissionerAt)}<div className="mono small muted">{b.commissionerReference}</div></> : b.notReportedReason ? <span className="small">Not required</span>
            : commissionerOverdue ? <Badge status="Error">Overdue since {fmt.dateTime(commissionerDeadline)}</Badge> : <Badge status="Warning">{hoursLeft}h left</Badge>}</td>
          <td>{b.subjectsNotifiedAt ? fmt.dateTime(b.subjectsNotifiedAt) : b.subjectsNotNotifiedReason ? <span className="small">Not required</span> : <span className="muted small">Pending</span>}</td>
          <td><Badge status={b.status === 'Closed' ? 'Active' : 'Pending'}>{b.status}</Badge></td>
          <td>{b.status !== 'Closed' && <button className="btn sm" onClick={() => setEdit(b)}>Update</button>}</td></tr>)}</tbody></table></div>}
      {edit && <BreachDialog breach={edit === 'new' ? null : edit} onClose={() => { setEdit(null); qc.invalidateQueries({ queryKey: ['breaches'] }) }} />}
    </div>
  )
}

function BreachDialog({ breach, onClose }: { breach: Breach | null; onClose: () => void }) {
  const [f, setF] = useState({
    detectedAt: toLocalInput(breach?.detectedAt ?? new Date().toISOString()), occurredAt: toLocalInput(breach?.occurredAt), description: breach?.description ?? '', categories: breach?.dataCategories.join(', ') ?? '',
    subjectsAffected: breach?.subjectsAffected?.toString() ?? '', risk: breach?.risk ?? 'Medium', cause: breach?.cause ?? '', containment: breach?.containment ?? '',
    reportedToCommissionerAt: toLocalInput(breach?.reportedToCommissionerAt), commissionerReference: breach?.commissionerReference ?? '', notReportedReason: breach?.notReportedReason ?? '',
    subjectsNotifiedAt: toLocalInput(breach?.subjectsNotifiedAt), subjectsNotNotifiedReason: breach?.subjectsNotNotifiedReason ?? '', status: breach?.status ?? 'Open',
  })
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value })
  const body = () => ({
    detectedAt: fromLocalInput(f.detectedAt), occurredAt: fromLocalInput(f.occurredAt), description: f.description, dataCategories: f.categories.split(',').map(s => s.trim()).filter(Boolean),
    subjectsAffected: f.subjectsAffected ? Number(f.subjectsAffected) : null, risk: f.risk, cause: f.cause || null, containment: f.containment || null,
    reportedToCommissionerAt: fromLocalInput(f.reportedToCommissionerAt), commissionerReference: f.commissionerReference || null, notReportedReason: f.notReportedReason || null,
    subjectsNotifiedAt: fromLocalInput(f.subjectsNotifiedAt), subjectsNotNotifiedReason: f.subjectsNotNotifiedReason || null, status: f.status,
  })
  const m = useMutation({ mutationFn: () => (breach ? api.put(`/api/payroll/data-protection/breaches/${breach.id}`, body()) : api.post('/api/payroll/data-protection/breaches', body())), onSuccess: onClose })
  return (
    <Modal title={breach ? `Update ${breach.reference}` : 'Record a personal data breach'} onClose={onClose} width={760}>
      <ErrorBox error={m.error} />
      <div className="form-grid">
        <Field label="Detected (EAT)"><input type="datetime-local" value={f.detectedAt} onChange={set('detectedAt')} /></Field>
        <Field label="Happened (if known)"><input type="datetime-local" value={f.occurredAt} onChange={set('occurredAt')} /></Field>
        <Field label="Risk to people"><select value={f.risk} onChange={set('risk')}>{['Low', 'Medium', 'High'].map(r => <option key={r}>{r}</option>)}</select></Field>
        <Field label="People affected (approx.)"><input type="number" min={0} value={f.subjectsAffected} onChange={set('subjectsAffected')} /></Field>
      </div>
      <Field label="What happened"><textarea rows={3} value={f.description} onChange={set('description')} /></Field>
      <Field label="Data involved (comma-separated)"><input value={f.categories} onChange={set('categories')} placeholder="Salaries, bank details, KRA PINs" /></Field>
      <div className="form-grid"><Field label="Cause"><input value={f.cause} onChange={set('cause')} /></Field><Field label="Containment"><input value={f.containment} onChange={set('containment')} /></Field></div>
      <h3>Data Commissioner</h3>
      <div className="form-grid">
        <Field label="Notified (EAT)"><input type="datetime-local" value={f.reportedToCommissionerAt} onChange={set('reportedToCommissionerAt')} /></Field>
        <Field label="ODPC reference"><input value={f.commissionerReference} onChange={set('commissionerReference')} /></Field>
      </div>
      <Field label="Or: why notification isn't required"><input value={f.notReportedReason} onChange={set('notReportedReason')} placeholder="e.g. data was encrypted and the key was not exposed" /></Field>
      <h3>People affected</h3>
      <div className="form-grid">
        <Field label="Told on (EAT)"><input type="datetime-local" value={f.subjectsNotifiedAt} onChange={set('subjectsNotifiedAt')} /></Field>
        <Field label="Or: why they weren't told"><input value={f.subjectsNotNotifiedReason} onChange={set('subjectsNotNotifiedReason')} /></Field>
      </div>
      <Field label="Status"><select value={f.status} onChange={set('status')}>{['Open', 'Contained', 'Closed'].map(s => <option key={s}>{s}</option>)}</select></Field>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={!f.description || !f.detectedAt || m.isPending} onClick={() => m.mutate()}>Save</button></div>
    </Modal>
  )
}

function RetentionTab() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['retention'], queryFn: () => api.get<Retention>('/api/payroll/data-protection/retention') })
  const [target, setTarget] = useState<Due | null>(null)
  const purge = useMutation({ mutationFn: () => api.post<{ removed: number }>('/api/payroll/data-protection/applicants/purge'), onSuccess: () => qc.invalidateQueries({ queryKey: ['retention'] }) })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const r = q.data!
  return (
    <div className="grid c2">
      <div className="card">
        <h2>Former employees past retention</h2>
        <p className="small muted">Records are kept {r.employeeRetentionYears} years after employment ends (Employment Act s.74 and Tax Procedures Act s.23 require five). Anonymising removes names, IDs, contacts, statutory numbers, bank details, documents and dependants; payroll amounts stay so the accounts still reconcile. It can't be undone.</p>
        {!r.employees.length ? <Empty>Nobody is past the retention period.</Empty> : <table><thead><tr><th>Employee</th><th>Left</th><th>Due since</th><th /></tr></thead>
          <tbody>{r.employees.map(e => <tr key={e.employeeId}><td>{e.employeeNumber} {e.name}</td><td>{fmt.date(e.terminationDate)}</td><td>{fmt.date(e.dueSince)}</td><td><button className="btn sm danger" onClick={() => setTarget(e)}>Anonymise</button></td></tr>)}</tbody></table>}
      </div>
      <div className="card">
        <h2>Job applicants</h2>
        <p className="small muted">Unsuccessful applications are removed after {r.applicantMonths} months, or {r.applicantConsentMonths} months if the applicant agreed to be considered for future roles.</p>
        <ErrorBox error={purge.error} />
        {purge.data && <div className="alert ok">Removed {purge.data.removed} application(s) and their CVs.</div>}
        {!r.applicants.length ? <Empty>No applications are due for removal.</Empty> : <>
          <table><thead><tr><th>Applicant</th><th>Role</th><th>Applied</th><th>Consent</th></tr></thead><tbody>{r.applicants.map(a => <tr key={a.applicationId}><td>{a.name}</td><td>{a.requisition}</td><td>{fmt.date(a.appliedAt)}</td><td>{a.consent ? 'Yes' : 'No'}</td></tr>)}</tbody></table>
          <button className="btn danger" style={{ marginTop: 10 }} disabled={purge.isPending} onClick={() => { if (confirm(`Delete ${r.applicants.length} application(s) and their CVs? This can't be undone.`)) purge.mutate() }}>Delete these applications</button></>}
      </div>
      {target && <ReasonDialog title={`Anonymise ${target.employeeNumber}`} label="Reason (recorded in the audit log)" required danger confirmLabel="Anonymise" onClose={() => setTarget(null)}
        onConfirm={t => api.post(`/api/payroll/data-protection/employees/${target.employeeId}/anonymise`, { text: t }).then(() => qc.invalidateQueries({ queryKey: ['retention'] }))}>
        <p>{target.name} left on {fmt.date(target.terminationDate)}. Their personal data will be removed permanently.</p></ReasonDialog>}
    </div>
  )
}

function Access() {
  const [number, setNumber] = useState('')
  const [err, setErr] = useState<unknown>(null)
  const run = async () => {
    setErr(null)
    try {
      const found = await api.get<{ items: { id: string; employeeNumber: string }[] }>(`/api/payroll/employees?search=${encodeURIComponent(number)}&limit=5`)
      const e = found.items.find(x => x.employeeNumber.toLowerCase() === number.trim().toLowerCase()) ?? found.items[0]
      if (!e) throw new Error(`No employee matches "${number}".`)
      await api.download(`/api/payroll/data-protection/employees/${e.id}/export`)
    } catch (x) { setErr(x) }
  }
  return (
    <div className="card" style={{ maxWidth: 720 }}>
      <h2>Subject access request</h2>
      <p className="small">An employee (or former employee) can ask for a copy of their personal data. Respond within the time the Data Protection (General) Regulations allow. Employees can also download their own copy from My workspace → My details.</p>
      <ErrorBox error={err} />
      <div className="toolbar"><input value={number} onChange={e => setNumber(e.target.value)} placeholder="Employee number, e.g. SL012" aria-label="Employee number" /><button className="btn primary" disabled={!number.trim()} onClick={run}>Download their data</button></div>
      <p className="small muted">The download is recorded in the audit log. Send it to the person securely; it contains their bank and statutory numbers.</p>
    </div>
  )
}
