import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import type { LeaveRequest, LeaveType } from '../lib/types'
import { Badge, Empty, ErrorBox, Loading, PageHead, ReasonDialog, Tabs } from '../components/ui'

export default function LeavePage() {
  const qc = useQueryClient()
  const [tab, setTab] = useState<'requests' | 'types'>('requests')
  const [status, setStatus] = useState('Pending')
  const q = useQuery({ queryKey: ['leave-requests', status], queryFn: () => api.get<LeaveRequest[]>(`/api/payroll/leave/requests${status ? `?status=${status}` : ''}`) })
  const types = useQuery({ queryKey: ['leave-types'], queryFn: () => api.get<LeaveType[]>('/api/payroll/leave/types') })
  const [decide, setDecide] = useState<{ r: LeaveRequest; action: 'approve' | 'reject' } | null>(null)
  const refresh = () => qc.invalidateQueries({ queryKey: ['leave-requests'] })
  const approve = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/leave/requests/${id}/approve`, {}), onSuccess: refresh })
  return (
    <>
      <PageHead title="Leave management" sub="Approve leave; approved unpaid leave is deducted automatically in the matching payroll period" />
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'requests', label: 'Requests' }, { id: 'types', label: 'Leave types' }]} />
      {tab === 'requests' && <div className="card">
        <div className="toolbar"><select value={status} onChange={e => setStatus(e.target.value)} aria-label="Status"><option value="">All</option>{['Pending', 'Approved', 'Rejected', 'Cancelled'].map(s => <option key={s}>{s}</option>)}</select></div>
        <ErrorBox error={q.error ?? approve.error} />
        {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No {status.toLowerCase()} leave requests.</Empty> : (
          <table><thead><tr><th>Employee</th><th>Type</th><th>From</th><th>To</th><th className="num">Days</th><th>Reason</th><th>Status</th><th>Decision</th><th /></tr></thead>
            <tbody>{q.data.map(r => <tr key={r.id}><td>{r.employeeNumber} {r.employee}</td><td>{r.leaveType} {!r.isPaid && <Badge status="Warning">Unpaid</Badge>}</td><td>{fmt.date(r.startDate)}</td><td>{fmt.date(r.endDate)}</td><td className="num">{r.days}</td><td style={{ whiteSpace: 'normal' }}>{r.reason}</td><td><Badge status={r.status} /></td><td className="small muted">{r.approverComment}</td>
              <td className="actions">{r.status === 'Pending' && <><button className="btn sm primary" onClick={() => approve.mutate(r.id)}>Approve</button><button className="btn sm danger" onClick={() => setDecide({ r, action: 'reject' })}>Reject</button></>}</td></tr>)}</tbody></table>)}
      </div>}
      {tab === 'types' && <div className="card"><h2>Leave types</h2>
        {types.isLoading ? <Loading /> : <table><thead><tr><th>Code</th><th>Name</th><th className="num">Annual entitlement</th><th>Paid</th><th>Accrues monthly</th><th className="num">Max carry-over</th><th>Attachment</th></tr></thead>
          <tbody>{types.data?.map(t => <tr key={t.id}><td className="mono">{t.code}</td><td>{t.name}</td><td className="num">{t.annualEntitlementDays}</td><td>{t.isPaid ? 'Yes' : <Badge status="Warning">Unpaid</Badge>}</td><td>{t.accruesMonthly ? 'Yes' : 'No'}</td><td className="num">{t.maxCarryOverDays}</td><td>{t.requiresAttachment ? 'Required' : '—'}</td></tr>)}</tbody></table>}</div>}
      {decide && <ReasonDialog title={`Reject leave for ${decide.r.employee}`} label="Reason" required confirmLabel="Reject" danger onClose={() => setDecide(null)} onConfirm={t => api.post(`/api/payroll/leave/requests/${decide.r.id}/reject`, { comment: t }).then(refresh)} />}
    </>
  )
}
