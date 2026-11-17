import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
import { api, fmt } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useLegalEntities, usePayrollGroups, usePeriods } from '../lib/lookups'
import type { Page, RunListItem } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead } from '../components/ui'

export default function PayrollRunsPage() {
  const { can } = useAuth()
  const nav = useNavigate()
  const [status, setStatus] = useState('')
  const [showNew, setShowNew] = useState(false)
  const q = useQuery({ queryKey: ['runs', status], queryFn: () => api.get<Page<RunListItem>>(`/api/payroll/runs?limit=100${status ? `&status=${status}` : ''}`) })
  return (
    <>
      <PageHead title="Payroll processing" sub="Prepare, calculate, review, approve, pay and finalise payroll runs">
        {can(P.PayrollPrepare) && <button className="btn primary" onClick={() => setShowNew(true)}>New payroll run</button>}
      </PageHead>
      <div className="card">
        <div className="toolbar">
          <select value={status} onChange={e => setStatus(e.target.value)} aria-label="Filter by status"><option value="">All statuses</option>{['Draft', 'Prepared', 'Calculated', 'UnderReview', 'Approved', 'PaymentProcessing', 'Paid', 'Finalised', 'Rejected', 'Reversed'].map(s => <option key={s} value={s}>{s}</option>)}</select>
        </div>
        <ErrorBox error={q.error} />
        {q.isLoading ? <Loading /> : q.data!.items.length === 0 ? <Empty>No payroll runs.</Empty> : (
          <div className="table-wrap"><table>
            <thead><tr><th>Reference</th><th>Period</th><th>Group</th><th>Type</th><th>Status</th><th className="num">Employees</th><th className="num">Gross</th><th className="num">Net</th><th className="num">Employer cost</th><th>Exceptions</th><th>Created</th></tr></thead>
            <tbody>{q.data!.items.map(r => <tr key={r.id} className="clickable" onClick={() => nav(`/payroll/${r.id}`)}>
              <td><Link to={`/payroll/${r.id}`} className="mono">{r.reference}</Link></td><td>{r.period}</td><td>{r.group}</td><td>{r.runType}</td><td><Badge status={r.status} /></td>
              <td className="num">{r.employeeCount}</td><td className="num">{fmt.num(r.totalGross)}</td><td className="num">{fmt.num(r.totalNet)}</td><td className="num">{fmt.num(r.totalEmployerCost)}</td>
              <td>{r.errorCount > 0 && <Badge status="Error">{r.errorCount} errors</Badge>} {r.warningCount > 0 && <Badge status="Warning">{r.warningCount} warnings</Badge>}</td><td>{fmt.dateTime(r.createdAt)}</td></tr>)}</tbody>
          </table></div>)}
      </div>
      {showNew && <NewRunModal onClose={() => setShowNew(false)} />}
    </>
  )
}

function NewRunModal({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient(); const nav = useNavigate()
  const les = useLegalEntities()
  const [le, setLe] = useState('')
  const leId = le || les.data?.[0]?.id
  const groups = usePayrollGroups(leId)
  const [groupId, setGroupId] = useState('')
  const gId = groupId || groups.data?.[0]?.id
  const periods = usePeriods(gId)
  const [periodId, setPeriodId] = useState('')
  const [runType, setRunType] = useState('Regular')
  const [notes, setNotes] = useState('')
  const [dup, setDup] = useState(false)
  const open = periods.data?.filter(p => p.status === 'Open') ?? []
  const pId = periodId || open[0]?.id
  const m = useMutation({
    mutationFn: () => api.post<{ id: string }>('/api/payroll/runs', { legalEntityId: leId, payrollGroupId: gId, payrollPeriodId: pId, runType, notes, authoriseDuplicate: dup }),
    onSuccess: r => { qc.invalidateQueries({ queryKey: ['runs'] }); onClose(); nav(`/payroll/${r.id}`) },
  })
  return (
    <Modal title="New payroll run" onClose={onClose}>
      <ErrorBox error={m.error} />
      <Field label="Legal entity"><select value={leId ?? ''} onChange={e => { setLe(e.target.value); setGroupId(''); setPeriodId('') }}>{les.data?.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></Field>
      <Field label="Payroll group"><select value={gId ?? ''} onChange={e => { setGroupId(e.target.value); setPeriodId('') }}>{groups.data?.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></Field>
      <Field label="Payroll period"><select value={pId ?? ''} onChange={e => setPeriodId(e.target.value)}>{open.map(p => <option key={p.id} value={p.id}>{p.name} (pay {fmt.date(p.payDate)})</option>)}</select></Field>
      <Field label="Run type"><select value={runType} onChange={e => setRunType(e.target.value)}>{['Regular', 'Supplementary', 'Correction'].map(t => <option key={t}>{t}</option>)}</select></Field>
      <Field label="Notes"><input value={notes} onChange={e => setNotes(e.target.value)} /></Field>
      <label className="field inline"><input type="checkbox" checked={dup} onChange={e => setDup(e.target.checked)} /> Authorise a second regular run for this period (requires approver permission)</label>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={m.isPending || !pId} onClick={() => m.mutate()}>Create</button></div>
    </Modal>
  )
}
