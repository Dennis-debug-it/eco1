import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { Badge, Empty, ErrorBox, Field, FileLink, Loading, ReasonDialog, UploadButton } from './ui'

export type DependantRow = {
  id: string; employeeId: string; fullName: string; relationship: string; dateOfBirth?: string; idNumber?: string; phone?: string; isBeneficiary: boolean; beneficiaryPercent: number
  status: 'Submitted' | 'Verified' | 'Approved' | 'Rejected'; submittedAt: string; submittedBy?: string; verifiedAt?: string; verifiedBy?: string; approvedAt?: string; approvedBy?: string
  rejectedAt?: string; rejectionReason?: string; documents: { id: string; fileName: string }[]
}

const relationships = ['Spouse', 'Child', 'Parent', 'Sibling', 'Guardian', 'Other']

/** Where a record is in the sequence: submitted → HR verifies documents → second person approves. */
export function DependantStatus({ d }: { d: DependantRow }) {
  if (d.status === 'Approved') return <Badge status="Active">Approved</Badge>
  if (d.status === 'Rejected') return <Badge status="Error">Not approved</Badge>
  if (d.status === 'Verified') return <Badge status="Open">Awaiting approval</Badge>
  return <Badge status="Pending">Awaiting verification</Badge>
}

export function DependantDocuments({ d, canAdd, onChanged }: { d: DependantRow; canAdd: boolean; onChanged: () => void }) {
  return (
    <span className="small">
      {d.documents.map(f => <span key={f.id} style={{ marginRight: 8 }}><FileLink id={f.id} label={f.fileName} /></span>)}
      {canAdd && <UploadButton label="Add document" url={`/api/payroll/benefits/dependants/${d.id}/documents`} onDone={onChanged} accept=".pdf,.png,.jpg,.jpeg" />}
    </span>
  )
}

/** An employee's dependants and beneficiaries, with the form to submit a new one. Used in self-service. */
export function MyDependants({ employeeId }: { employeeId: string }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['dependants', employeeId], queryFn: () => api.get<DependantRow[]>(`/api/payroll/benefits/dependants/${employeeId}`) })
  const refresh = () => qc.invalidateQueries({ queryKey: ['dependants'] })
  const withdraw = useMutation({ mutationFn: (id: string) => api.del(`/api/payroll/benefits/dependants/${id}`), onSuccess: refresh })
  const [adding, setAdding] = useState(false)
  const approvedShare = (q.data ?? []).filter(d => d.isBeneficiary && d.status === 'Approved').reduce((s, d) => s + d.beneficiaryPercent, 0)
  return (
    <div className="card">
      <h2>Dependants & beneficiaries</h2>
      <p className="small muted" style={{ marginTop: 0 }}>Changes take effect once HR has checked the supporting documents and a second person has approved them.{approvedShare > 0 && ` Approved beneficiary shares: ${approvedShare}%.`}</p>
      <ErrorBox error={withdraw.error} />
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>None recorded.</Empty> : (
        <div className="table-wrap"><table><tbody>{q.data.map(d => (
          <tr key={d.id}>
            <td style={{ whiteSpace: 'normal' }}>{d.fullName} <DependantStatus d={d} /><div className="small muted">{d.relationship}{d.dateOfBirth && ` · born ${fmt.date(d.dateOfBirth)}`}{d.isBeneficiary && ` · beneficiary ${d.beneficiaryPercent}%`}</div>
              {d.status === 'Rejected' && d.rejectionReason && <div className="small" style={{ color: 'var(--danger)' }}>{d.rejectionReason}</div>}
              <DependantDocuments d={d} canAdd={d.status === 'Submitted' || d.status === 'Verified'} onChanged={refresh} /></td>
            <td style={{ textAlign: 'right' }}>{d.status !== 'Approved' && <button className="btn sm" disabled={withdraw.isPending} onClick={() => { if (confirm(`Withdraw ${d.fullName}?`)) withdraw.mutate(d.id) }}>{d.status === 'Rejected' ? 'Remove' : 'Withdraw'}</button>}</td>
          </tr>))}</tbody></table></div>
      )}
      {q.data?.some(d => d.status === 'Approved') && <p className="small muted">To change or remove an approved record, contact HR.</p>}
      {adding ? <DependantForm employeeId={employeeId} onDone={() => { setAdding(false); refresh() }} onCancel={() => setAdding(false)} /> : <button className="btn" onClick={() => setAdding(true)}>Add dependant or beneficiary</button>}
    </div>
  )
}

export function DependantForm({ employeeId, onDone, onCancel }: { employeeId: string; onDone: () => void; onCancel: () => void }) {
  const [d, setD] = useState({ fullName: '', relationship: 'Spouse', dateOfBirth: '', idNumber: '', phone: '', isBeneficiary: false, beneficiaryPercent: 0 })
  const [files, setFiles] = useState<File[]>([])
  const submit = useMutation({
    mutationFn: () => {
      const form = new FormData()
      form.append('meta', JSON.stringify({ ...d, employeeId, dateOfBirth: d.dateOfBirth || null, idNumber: d.idNumber || null, phone: d.phone || null }))
      files.forEach(f => form.append('files', f))
      return api.upload('/api/payroll/benefits/dependants', form)
    },
    onSuccess: onDone,
  })
  return (
    <form onSubmit={e => { e.preventDefault(); submit.mutate() }} style={{ marginTop: 10 }}>
      <ErrorBox error={submit.error} />
      <div className="form-grid">
        <Field label="Full name"><input value={d.fullName} onChange={e => setD({ ...d, fullName: e.target.value })} required /></Field>
        <Field label="Relationship"><select value={d.relationship} onChange={e => setD({ ...d, relationship: e.target.value })}>{relationships.map(r => <option key={r}>{r}</option>)}</select></Field>
        <Field label="Date of birth"><input type="date" value={d.dateOfBirth} onChange={e => setD({ ...d, dateOfBirth: e.target.value })} /></Field>
        <Field label="ID or birth certificate number"><input value={d.idNumber} onChange={e => setD({ ...d, idNumber: e.target.value })} /></Field>
        <Field label="Phone (optional)"><input type="tel" value={d.phone} onChange={e => setD({ ...d, phone: e.target.value })} placeholder="07XX XXX XXX" /></Field>
      </div>
      <label className="field inline"><input type="checkbox" checked={d.isBeneficiary} onChange={e => setD({ ...d, isBeneficiary: e.target.checked })} /> Nominate as a beneficiary</label>
      {d.isBeneficiary && <Field label="Share (%)"><input type="number" min={1} max={100} value={d.beneficiaryPercent} onChange={e => setD({ ...d, beneficiaryPercent: Number(e.target.value) })} required /></Field>}
      <Field label="Supporting documents (ID copy, birth or marriage certificate)">
        <input type="file" multiple accept=".pdf,.png,.jpg,.jpeg" onChange={e => setFiles(Array.from(e.target.files ?? []))} required />
        <div className="small muted">PDF or photo, up to 5 MB each.{files.length > 0 && ` ${files.length} selected.`}</div>
      </Field>
      <div className="toolbar"><button className="btn primary" disabled={submit.isPending || files.length === 0}>{submit.isPending ? 'Submitting…' : 'Submit for approval'}</button><button type="button" className="btn" onClick={onCancel}>Cancel</button></div>
    </form>
  )
}

type QueueRow = { dependant: DependantRow; employee?: string; employeeNumber?: string }

/** HR: records awaiting verification or approval, and the history. */
export function DependantQueue() {
  const qc = useQueryClient()
  const [status, setStatus] = useState('pending')
  const [reject, setReject] = useState<DependantRow | null>(null)
  const q = useQuery({ queryKey: ['dependant-queue', status], queryFn: () => api.get<QueueRow[]>(`/api/payroll/benefits/dependants?status=${status}`) })
  const refresh = () => { qc.invalidateQueries({ queryKey: ['dependant-queue'] }); qc.invalidateQueries({ queryKey: ['dependants'] }) }
  const act = useMutation({ mutationFn: ({ id, step }: { id: string; step: 'verify' | 'approve' }) => api.post(`/api/payroll/benefits/dependants/${id}/${step}`), onSuccess: refresh })
  return (
    <div className="card">
      <div className="toolbar">
        <h2 style={{ margin: 0 }}>Dependants & beneficiaries</h2><span style={{ flex: 1 }} />
        <select value={status} onChange={e => setStatus(e.target.value)} aria-label="Show"><option value="pending">Awaiting action</option><option value="approved">Approved</option><option value="rejected">Not approved</option><option value="all">All</option></select>
      </div>
      <p className="small muted">1. HR checks the supporting documents match the details and marks them verified. 2. A different person approves. Nobody can verify or approve their own family, and the person who verified cannot approve.</p>
      <ErrorBox error={act.error} />
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>{status === 'pending' ? 'Nothing waiting.' : 'None.'}</Empty> : (
        <div className="table-wrap"><table>
          <thead><tr><th>Employee</th><th>Person and documents</th><th>Status</th><th /></tr></thead>
          <tbody>{q.data.map(({ dependant: d, employee, employeeNumber }) => (
            <tr key={d.id}>
              <td>{employee}<div className="mono small muted">{employeeNumber}</div></td>
              <td style={{ whiteSpace: 'normal' }}>{d.fullName}<div className="small muted">{d.relationship}{d.dateOfBirth && ` · born ${fmt.date(d.dateOfBirth)}`}{d.idNumber && ` · ID ${d.idNumber}`}{d.isBeneficiary && ` · beneficiary ${d.beneficiaryPercent}%`}</div>
                <div className="small muted">Submitted {fmt.dateTime(d.submittedAt)}{d.submittedBy && ` by ${d.submittedBy}`}{d.verifiedBy && ` · verified by ${d.verifiedBy}`}{d.approvedBy && ` · approved by ${d.approvedBy}`}</div>
                {d.rejectionReason && <div className="small" style={{ color: 'var(--danger)' }}>{d.rejectionReason}</div>}
                <DependantDocuments d={d} canAdd={d.status === 'Submitted' || d.status === 'Verified'} onChanged={refresh} /></td>
              <td><DependantStatus d={d} /></td>
              <td style={{ whiteSpace: 'nowrap' }}>
                {d.status === 'Submitted' && <button className="btn sm primary" disabled={act.isPending} onClick={() => act.mutate({ id: d.id, step: 'verify' })}>Documents verified</button>}
                {d.status === 'Verified' && <button className="btn sm primary" disabled={act.isPending} onClick={() => act.mutate({ id: d.id, step: 'approve' })}>Approve</button>}{' '}
                {(d.status === 'Submitted' || d.status === 'Verified') && <button className="btn sm" onClick={() => setReject(d)}>Reject</button>}
              </td>
            </tr>))}</tbody>
        </table></div>
      )}
      {reject && <ReasonDialog title={`Reject ${reject.fullName}`} label="Reason (sent to the employee)" required danger confirmLabel="Reject" onClose={() => setReject(null)}
        onConfirm={async reason => { await api.post(`/api/payroll/benefits/dependants/${reject.id}/reject`, { reason }); refresh() }} />}
    </div>
  )
}
