import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { api, ApiError, fmt, tokenStore } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useComponents, usePayingAccounts } from '../lib/lookups'
import type { Journal, Payment, RunEmployeeDetail, RunEmployeeRow, RunStatus, RunSummary } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, ReasonDialog, Tabs } from '../components/ui'

const flow: RunStatus[] = ['Draft', 'Prepared', 'Calculated', 'UnderReview', 'Approved', 'PaymentProcessing', 'Paid', 'Finalised']
type Tab = 'summary' | 'employees' | 'exceptions' | 'inputs' | 'attendance' | 'payments' | 'journal' | 'parallel' | 'history'

export default function PayrollRunDetailPage() {
  const { id = '' } = useParams()
  const { can, user } = useAuth()
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['run', id], queryFn: () => api.get<RunSummary>(`/api/payroll/runs/${id}`) })
  const emps = useQuery({ queryKey: ['run-employees', id], queryFn: () => api.get<RunEmployeeRow[]>(`/api/payroll/runs/${id}/employees`), enabled: can(P.SalaryRead) })
  const [tab, setTab] = useState<Tab>('summary')
  const [dialog, setDialog] = useState<'submit' | 'review' | 'approve' | 'reject' | 'reverse' | 'payfile' | 'confirm' | null>(null)
  const [detail, setDetail] = useState<string | null>(null)
  const [actionErr, setActionErr] = useState<unknown>(null)
  const refresh = () => { qc.invalidateQueries({ queryKey: ['run', id] }); qc.invalidateQueries({ queryKey: ['run-employees', id] }); qc.invalidateQueries({ queryKey: ['run-employee', id] }); qc.invalidateQueries({ queryKey: ['runs'] }); qc.invalidateQueries({ queryKey: ['dashboard'] }) }
  const act = useMutation({ mutationFn: (path: string) => api.post(`/api/payroll/runs/${id}/${path}`), onSuccess: refresh, onError: setActionErr, onMutate: () => setActionErr(null) })

  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const s = q.data!; const r = s.run
  const idx = flow.indexOf(r.status)
  const isPreparer = r.preparedBy === user?.id || r.submittedBy === user?.id
  const isReviewer = r.reviewedBy === user?.id
  const lastSubmit = s.approvals.filter(a => a.action === 'Submitted').map(a => a.createdAt).sort().at(-1) ?? ''
  const approvedByMe = r.status === 'UnderReview' && s.approvals.some(a => a.action === 'Approved' && a.actorId === user?.id && a.createdAt >= lastSubmit)
  const policy = s.policy ?? { allowPreparerToApprove: false, requireReview: true, minimumApprovals: 1 }
  const solo = policy.allowPreparerToApprove
  const approveBlock = isPreparer && !solo ? 'Preparers cannot approve their own payroll' : isReviewer && !solo ? 'You reviewed this run; someone else must approve it'
    : approvedByMe ? 'You have approved; waiting for another approver' : policy.requireReview && !r.reviewedBy ? 'A reviewer must mark this run reviewed first' : ''
  const editable = ['Draft', 'Prepared', 'Calculated', 'Rejected'].includes(r.status)
  const cur = r.currencyCode

  return (
    <>
      <PageHead title={r.reference} sub={<>{s.periodName} · {s.groupName} · {r.runType} · <Badge status={r.status} /></>}>
        <Link to="/payroll" className="btn">All runs</Link>
        {editable && can(P.PayrollPrepare) && <button className="btn" onClick={() => act.mutate('prepare')} disabled={act.isPending}>Refresh employees</button>}
        {editable && can(P.PayrollPrepare) && <button className="btn primary" onClick={() => act.mutate('calculate')} disabled={act.isPending}>{act.isPending && act.variables === 'calculate' ? 'Calculating…' : r.status === 'Calculated' ? 'Recalculate' : 'Calculate'}</button>}
        {r.status === 'Calculated' && can(P.PayrollPrepare) && <button className="btn primary" onClick={() => setDialog('submit')} disabled={r.errorCount > 0} title={r.errorCount > 0 ? 'Resolve errors first' : ''}>{policy.requireReview ? 'Submit for review' : 'Submit for approval'}</button>}
        {r.status === 'UnderReview' && (can(P.PayrollApprove) || can(P.PayrollReview)) && <button className="btn danger" onClick={() => setDialog('reject')}>Reject</button>}
        {r.status === 'UnderReview' && !r.reviewedBy && can(P.PayrollReview) && (!isPreparer || solo) && (policy.requireReview || !solo) && <button className="btn primary" onClick={() => setDialog('review')}>Mark reviewed</button>}
        {r.status === 'UnderReview' && can(P.PayrollApprove) && <button className="btn primary" onClick={() => setDialog('approve')} disabled={!!approveBlock} title={approveBlock}>Approve{r.approvalsRequired > 1 ? ` (${r.approvalsReceived}/${r.approvalsRequired})` : ''}</button>}
        {(r.status === 'Approved' || r.status === 'PaymentProcessing') && can(P.PaymentsManage) && <button className="btn primary" onClick={() => setDialog('payfile')}>Generate payment file</button>}
        {r.status === 'PaymentProcessing' && can(P.PaymentsManage) && <button className="btn" onClick={() => setDialog('confirm')}>Confirm bank payment</button>}
        {(r.status === 'Paid' || r.status === 'Approved') && can(P.PayrollFinalise) && <button className="btn primary" onClick={() => act.mutate('finalise')} disabled={act.isPending}>Finalise</button>}
        {(r.status === 'Approved' || r.status === 'Finalised') && can(P.PayrollReverse) && <button className="btn danger" onClick={() => setDialog('reverse')}>Reverse</button>}
      </PageHead>

      <div className="steps" aria-label="Payroll workflow">{flow.map((f, i) => <span key={f} className={r.status === 'Reversed' || r.status === 'Rejected' ? '' : i < idx ? 'done' : i === idx ? 'current' : ''}>{i + 1}. {f.replace(/([A-Z])/g, ' $1').trim()}</span>)}{(r.status === 'Reversed' || r.status === 'Rejected') && <span className="badge danger">{r.status}{r.reversalReason ? `: ${r.reversalReason}` : ''}</span>}</div>
      <ErrorBox error={actionErr} />
      {r.status === 'UnderReview' && isPreparer && !solo && <div className="alert info">You prepared or submitted this run, so a different approver must approve it (separation of duties).</div>}
      {r.status === 'UnderReview' && solo && <div className="alert warn">This company lets one person run payroll, so you can approve your own run. Check the register and exceptions first: no one else will. The approval is recorded as a self-approval.</div>}
      {r.status === 'UnderReview' && !solo && <div className="alert info">{r.reviewedBy ? <>Reviewed {fmt.dateTime(r.reviewedAt)}. </> : policy.requireReview ? <>Awaiting review by a payroll reviewer before it can be approved. </> : null}
        Approvals: <b>{r.approvalsReceived} of {r.approvalsRequired}</b> from different people.</div>}
      {r.errorCount > 0 && <div className="alert danger">{r.errorCount} validation error(s) must be resolved before submission. See the Exceptions tab.</div>}

      <div className="grid c4">
        <Card label="Employees" value={fmt.int(r.employeeCount)} />
        <Card label="Gross pay" value={fmt.money(r.totalGross, cur)} />
        <Card label="Net pay" value={fmt.money(r.totalNet, cur)} />
        <Card label="Total employer cost" value={fmt.money(r.totalEmployerCost, cur)} hint={`Employer contributions ${fmt.num(r.totalEmployerContributions)}`} />
      </div>

      <div style={{ marginTop: 16 }}>
        <Tabs value={tab} onChange={setTab} tabs={[{ id: 'summary', label: 'Summary' }, { id: 'employees', label: `Employees (${r.employeeCount})` }, { id: 'exceptions', label: `Exceptions (${r.errorCount + r.warningCount})` }, { id: 'inputs', label: 'Variable inputs' }, { id: 'attendance', label: 'Attendance' }, { id: 'payments', label: 'Payments' }, { id: 'journal', label: 'Journal' }, ...(can(P.SalaryRead) && !['Draft', 'Prepared'].includes(r.status) ? [{ id: 'parallel' as const, label: 'Parallel run' }] : []), { id: 'history', label: 'Approval history' }]} />
      </div>

      {tab === 'summary' && (
        <div className="grid c2">
          <div className="card"><h2>Statutory summary</h2>
            <table><thead><tr><th>Item</th><th className="num">Employee</th><th className="num">Employer</th><th className="num">Total</th></tr></thead><tbody>
              <Row n="PAYE" a={r.totalPaye} /><Row n="NSSF" a={r.totalNssfEmployee} b={r.totalNssfEmployer} /><Row n="SHIF" a={r.totalShif} /><Row n="Affordable Housing Levy" a={r.totalHousingLevyEmployee} b={r.totalHousingLevyEmployer} /><Row n="NITA" b={r.totalNita} />
              <Row n="Other deductions" a={r.totalOtherDeductions} /></tbody>
              <tfoot><tr><td>Total employee deductions</td><td className="num">{fmt.num(r.totalEmployeeDeductions)}</td><td className="num">{fmt.num(r.totalEmployerContributions)}</td><td className="num">{fmt.num(r.totalEmployeeDeductions + r.totalEmployerContributions)}</td></tr></tfoot></table></div>
          <div className="card"><h2>Run details</h2>
            <dl className="kv"><dt>Period</dt><dd>{fmt.date(s.periodStart)} – {fmt.date(s.periodEnd)}</dd><dt>Pay date</dt><dd>{fmt.date(s.payDate)}</dd><dt>Prepared by</dt><dd>{s.preparedByName ?? '—'}</dd><dt>Calculated</dt><dd>{fmt.dateTime(r.calculatedAt)}</dd><dt>Approved by</dt><dd>{s.approvedByName ?? '—'} {r.approvedAt && <span className="muted">({fmt.dateTime(r.approvedAt)})</span>}</dd><dt>Finalised</dt><dd>{fmt.dateTime(r.finalisedAt)}</dd><dt>Taxable pay</dt><dd className="mono">{fmt.num(r.totalTaxable)}</dd><dt>Notes</dt><dd>{r.notes ?? '—'}</dd></dl>
            {['Approved', 'PaymentProcessing', 'Paid', 'Finalised'].includes(r.status) && can(P.SalaryRead) && <NssfFiles runId={id!} />}
            {r.status === 'Finalised' && <div className="actions" style={{ marginTop: 12 }}>
              <button className="btn sm" onClick={() => api.download(`/api/payroll/reports/payroll-register?runId=${id}&format=xlsx`)}>Payroll register (Excel)</button>
              <button className="btn sm" onClick={() => api.download(`/api/payroll/reports/payroll-register?runId=${id}&format=pdf`)}>Register (PDF)</button>
              {['KE.PAYE', 'KE.NSSF', 'KE.SHIF', 'KE.AHL'].map(c => <button key={c} className="btn sm" onClick={() => api.download(`/api/payroll/reports/statutory?runId=${id}&ruleCode=${c}&format=xlsx`)}>{c.split('.')[1]} return</button>)}
            </div>}
          </div>
        </div>
      )}

      {tab === 'employees' && (
        <div className="card">
          {!can(P.SalaryRead) ? <Empty>Salary visibility is restricted for your role.</Empty> : emps.isLoading ? <Loading /> : !emps.data?.length ? <Empty>Run has not been prepared yet.</Empty> : (
            <div className="table-wrap"><table>
              <thead><tr><th>No.</th><th>Employee</th><th>Department</th><th className="num">Basic</th><th className="num">Gross</th><th className="num">Taxable</th><th className="num">PAYE</th><th className="num">NSSF</th><th className="num">SHIF</th><th className="num">AHL</th><th className="num">Other</th><th className="num">Net</th><th className="num">Prev. net</th><th className="num">Employer cost</th><th /></tr></thead>
              <tbody>{emps.data.map(e => <tr key={e.id} className="clickable" onClick={() => setDetail(e.employeeId)}>
                <td className="mono">{e.employeeNumber}</td><td>{e.employeeName}</td><td>{e.department}</td><td className="num">{fmt.num(e.basicPay)}</td><td className="num">{fmt.num(e.grossPay)}</td><td className="num">{fmt.num(e.taxablePay)}</td><td className="num">{fmt.num(e.paye)}</td><td className="num">{fmt.num(e.nssf)}</td><td className="num">{fmt.num(e.shif)}</td><td className="num">{fmt.num(e.housingLevy)}</td><td className="num">{fmt.num(e.totalOtherDeductions)}</td><td className="num"><b>{fmt.num(e.netPay)}</b></td><td className="num muted">{fmt.num(e.previousNetPay)}</td><td className="num">{fmt.num(e.totalEmployerCost)}</td>
                <td>{e.hasErrors ? <Badge status="Error">{e.issues.filter(i => i.severity === 'Error').length}</Badge> : e.issues.length > 0 ? <Badge status="Warning">{e.issues.length}</Badge> : null}</td></tr>)}</tbody>
              <tfoot><tr><td colSpan={3}>Totals</td><td className="num">{fmt.num(emps.data.reduce((a, e) => a + e.basicPay, 0))}</td><td className="num">{fmt.num(r.totalGross)}</td><td className="num">{fmt.num(r.totalTaxable)}</td><td className="num">{fmt.num(r.totalPaye)}</td><td className="num">{fmt.num(r.totalNssfEmployee)}</td><td className="num">{fmt.num(r.totalShif)}</td><td className="num">{fmt.num(r.totalHousingLevyEmployee)}</td><td className="num">{fmt.num(r.totalOtherDeductions)}</td><td className="num">{fmt.num(r.totalNet)}</td><td /><td className="num">{fmt.num(r.totalEmployerCost)}</td><td /></tr></tfoot>
            </table></div>)}
        </div>
      )}

      {tab === 'exceptions' && (
        <div className="card">
          {emps.data?.some(e => e.issues.length) ? (
            <table><thead><tr><th>Severity</th><th>Employee</th><th>Code</th><th>Message</th></tr></thead>
              <tbody>{emps.data.flatMap(e => e.issues.map((i, k) => <tr key={e.id + k}><td><Badge status={i.severity} /></td><td>{e.employeeNumber} {e.employeeName}</td><td className="mono">{i.code}</td><td style={{ whiteSpace: 'normal' }}>{i.message}</td></tr>))}</tbody></table>
          ) : <Empty>No validation issues.</Empty>}
        </div>
      )}

      {tab === 'inputs' && <VariableInputs runId={id} legalEntityId={r.legalEntityId} editable={editable && can(P.PayrollPrepare)} employees={emps.data ?? []} onChange={refresh} />}
      {tab === 'attendance' && <Attendance runId={id} editable={editable && can(P.PayrollPrepare)} employees={emps.data ?? []} onChange={refresh} />}
      {tab === 'payments' && <Payments runId={id} batches={s.batches} />}
      {tab === 'journal' && <JournalView runId={id} has={s.hasJournal} />}
      {tab === 'parallel' && <ParallelRun runId={id} />}
      {tab === 'history' && (
        <div className="card">{s.approvals.length === 0 ? <Empty>No workflow actions yet.</Empty> : (
          <table><thead><tr><th>When</th><th>Action</th><th>By</th><th>From</th><th>To</th><th>Comment</th></tr></thead>
            <tbody>{s.approvals.map(a => <tr key={a.id}><td>{fmt.dateTime(a.createdAt)}</td><td>{a.action}</td><td>{a.actorName}</td><td><Badge status={a.fromStatus} /></td><td><Badge status={a.toStatus} /></td><td style={{ whiteSpace: 'normal' }}>{a.comment}</td></tr>)}</tbody></table>)}</div>
      )}

      {detail && <EmployeeResultModal runId={id} employeeId={detail} currency={cur} onClose={() => setDetail(null)} />}
      {dialog === 'submit' && <ReasonDialog title={policy.requireReview ? 'Submit for review' : 'Submit for approval'} confirmLabel="Submit" onClose={() => setDialog(null)} onConfirm={t => api.post(`/api/payroll/runs/${id}/submit`, { text: t }).then(refresh)}><p>{solo ? 'The run is locked from further changes while you check it. Approve it next, or reject it to make corrections.' : `The run will be locked for ${policy.requireReview ? 'review' : 'approval'}. A ${policy.requireReview ? 'reviewer or ' : ''}approver can send it back.`}</p></ReasonDialog>}
      {dialog === 'review' && <ReasonDialog title="Mark payroll reviewed" label="Review notes (variances checked, exceptions explained)" confirmLabel="Mark reviewed" onClose={() => setDialog(null)} onConfirm={t => api.post(`/api/payroll/runs/${id}/review`, { text: t }).then(refresh)}><p>Confirm you have checked the exceptions, variances and statutory totals. An approver can then approve.</p></ReasonDialog>}
      {dialog === 'approve' && <ReasonDialog title="Approve payroll" confirmLabel="Approve" onClose={() => setDialog(null)} onConfirm={t => api.post(`/api/payroll/runs/${id}/approve`, { text: t }).then(refresh)}><p>Approving locks all calculations. Net payroll <b>{fmt.money(r.totalNet, cur)}</b> for {r.employeeCount} employees; total employer cost {fmt.money(r.totalEmployerCost, cur)}.</p></ReasonDialog>}
      {dialog === 'reject' && <ReasonDialog title="Reject payroll" label="Reason" required confirmLabel="Reject" danger onClose={() => setDialog(null)} onConfirm={t => api.post(`/api/payroll/runs/${id}/reject`, { text: t }).then(refresh)} />}
      {dialog === 'reverse' && <ReasonDialog title="Reverse payroll run" label="Reason" required confirmLabel="Reverse" danger onClose={() => setDialog(null)} onConfirm={t => api.post(`/api/payroll/runs/${id}/reverse`, { text: t }).then(refresh)}><p>Nothing is deleted: a contra journal is posted, the period is reopened and this run is marked Reversed. Process a corrected run afterwards.</p></ReasonDialog>}
      {dialog === 'payfile' && <PayFileDialog runId={id} legalEntityId={s.run.legalEntityId} payDate={s.payDate} onClose={() => { setDialog(null); refresh() }} />}
      {dialog === 'confirm' && <ConfirmDialog runId={id} batches={s.batches} onClose={() => { setDialog(null); refresh() }} />}
    </>
  )
}

const Card = ({ label, value, hint }: { label: string; value: string; hint?: string }) => <div className="card stat"><div className="label">{label}</div><div className="value">{value}</div>{hint && <div className="hint">{hint}</div>}</div>
const Row = ({ n, a = 0, b = 0 }: { n: string; a?: number; b?: number }) => <tr><td>{n}</td><td className="num">{a ? fmt.num(a) : '—'}</td><td className="num">{b ? fmt.num(b) : '—'}</td><td className="num">{fmt.num(a + b)}</td></tr>

function EmployeeResultModal({ runId, employeeId, currency, onClose }: { runId: string; employeeId: string; currency: string; onClose: () => void }) {
  const q = useQuery({ queryKey: ['run-employee', runId, employeeId], queryFn: () => api.get<RunEmployeeDetail>(`/api/payroll/runs/${runId}/employees/${employeeId}`) })
  const d = q.data
  const group = (t: string) => d?.lines.filter(l => l.type === t) ?? []
  return (
    <Modal title={d ? `${d.employeeNumber} · ${d.employeeName}` : 'Loading…'} onClose={onClose} width={820}>
      {q.isLoading && <Loading />}<ErrorBox error={q.error} />
      {d && <>
        {d.issues.length > 0 && <ul className="issue-list">{d.issues.map((i, k) => <li key={k}><Badge status={i.severity} /> {i.message}</li>)}</ul>}
        <div className="grid c2">
          <Section title="Earnings" rows={group('Earning')} total={d.grossPay} totalLabel="Gross pay" />
          <Section title="Deductions" rows={[...group('StatutoryDeduction'), ...group('Deduction')]} total={d.totalEmployeeDeductions} totalLabel="Total deductions" />
          <Section title="Tax computation" rows={[...group('Information'), ...group('TaxRelief')]} />
          <Section title="Employer contributions" rows={group('EmployerContribution')} total={d.totalEmployerCost} totalLabel="Total employer cost" />
        </div>
        <div className="card" style={{ marginTop: 12, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}><b>Net pay</b><b className="mono" style={{ fontSize: 18 }}>{fmt.money(d.netPay, currency)}</b></div>
        <p className="muted small">Statutory rule versions used: {d.statutoryRuleVersionIds.length}. Every figure above records the rule version that produced it, so this result can be reproduced after future rate changes.</p>
      </>}
    </Modal>
  )
}

function Section({ title, rows, total, totalLabel }: { title: string; rows: { code: string; name: string; amount: number; working?: string }[]; total?: number; totalLabel?: string }) {
  return (
    <div><h3>{title}</h3>
      {rows.length === 0 ? <p className="muted small">None</p> : <table><tbody>{rows.map((l, i) => <tr key={i}><td style={{ whiteSpace: 'normal' }}>{l.name}{l.working && <div className="muted small">{l.working}</div>}</td><td className="num">{fmt.num(l.amount)}</td></tr>)}</tbody>
        {total !== undefined && <tfoot><tr><td>{totalLabel}</td><td className="num">{fmt.num(total)}</td></tr></tfoot>}</table>}
    </div>
  )
}

function VariableInputs({ runId, legalEntityId, editable, employees, onChange }: { runId: string; legalEntityId: string; editable: boolean; employees: RunEmployeeRow[]; onChange: () => void }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['var-inputs', runId], queryFn: () => api.get<{ id: string; employeeId: string; salaryComponentId: string; amount: number; note?: string }[]>(`/api/payroll/runs/${runId}/variable-inputs`) })
  const comps = useComponents(legalEntityId)
  const variable = comps.data?.filter(c => c.method === 'Variable' && c.isActive) ?? []
  const [f, setF] = useState({ employeeId: '', salaryComponentId: '', amount: 0, note: '' })
  const add = useMutation({ mutationFn: () => api.post(`/api/payroll/runs/${runId}/variable-inputs`, [{ ...f, employeeId: f.employeeId || employees[0]?.employeeId, salaryComponentId: f.salaryComponentId || variable[0]?.id }]), onSuccess: () => { qc.invalidateQueries({ queryKey: ['var-inputs', runId] }); setF({ ...f, amount: 0, note: '' }); onChange() } })
  const del = useMutation({ mutationFn: (id: string) => api.del(`/api/payroll/runs/${runId}/variable-inputs/${id}`), onSuccess: () => { qc.invalidateQueries({ queryKey: ['var-inputs', runId] }); onChange() } })
  const empName = (id: string) => { const e = employees.find(x => x.employeeId === id); return e ? `${e.employeeNumber} ${e.employeeName}` : id }
  const compName = (id: string) => comps.data?.find(c => c.id === id)?.name ?? id
  return (
    <div className="card">
      <h2>Variable inputs (overtime, commissions, bonuses, one-off deductions)</h2>
      {editable && employees.length > 0 && (
        <div className="toolbar">
          <select value={f.employeeId || employees[0]?.employeeId} onChange={e => setF({ ...f, employeeId: e.target.value })} aria-label="Employee">{employees.map(e => <option key={e.employeeId} value={e.employeeId}>{e.employeeNumber} {e.employeeName}</option>)}</select>
          <select value={f.salaryComponentId || variable[0]?.id} onChange={e => setF({ ...f, salaryComponentId: e.target.value })} aria-label="Component">{variable.map(c => <option key={c.id} value={c.id}>{c.name} ({c.type})</option>)}</select>
          <input type="number" min={0} step="0.01" value={f.amount || ''} onChange={e => setF({ ...f, amount: Number(e.target.value) })} placeholder="Amount" aria-label="Amount" style={{ width: 130 }} />
          <input value={f.note} onChange={e => setF({ ...f, note: e.target.value })} placeholder="Note" aria-label="Note" />
          <button className="btn primary" disabled={!f.amount || add.isPending} onClick={() => add.mutate()}>Add</button>
        </div>)}
      {editable && employees.length === 0 && <div className="alert info">Prepare the run first to load its employees.</div>}
      <ErrorBox error={add.error} />
      {q.data?.length ? <table><thead><tr><th>Employee</th><th>Component</th><th className="num">Amount</th><th>Note</th><th /></tr></thead>
        <tbody>{q.data.map(v => <tr key={v.id}><td>{empName(v.employeeId)}</td><td>{compName(v.salaryComponentId)}</td><td className="num">{fmt.num(v.amount)}</td><td>{v.note}</td><td>{editable && <button className="btn sm danger" onClick={() => del.mutate(v.id)}>Remove</button>}</td></tr>)}</tbody></table> : <Empty>No variable inputs for this run.</Empty>}
      {editable && q.data?.length ? <p className="muted small">Adding or removing inputs after calculation marks the results stale — recalculate before submitting.</p> : null}
    </div>
  )
}

type AttendanceRow = { id: string; employeeId: string; employeeNumber: string; employeeName: string; daysWorked: number; absentDays: number; overtimeHours: number; restDayOvertimeHours: number; source: string; note?: string }

function Attendance({ runId, editable, employees, onChange }: { runId: string; editable: boolean; employees: RunEmployeeRow[]; onChange: () => void }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['attendance', runId], queryFn: () => api.get<AttendanceRow[]>(`/api/payroll/runs/${runId}/attendance`) })
  const [f, setF] = useState({ employeeNumber: '', daysWorked: 22, absentDays: 0, overtimeHours: 0, restDayOvertimeHours: 0, note: '' })
  const [err, setErr] = useState<unknown>(null)
  const [msg, setMsg] = useState('')
  const done = (n: number) => { setMsg(`${n} row(s) saved. Recalculate to apply.`); qc.invalidateQueries({ queryKey: ['attendance', runId] }); onChange() }
  // A daily-paid worker entered as 0 days drops out of the run but keeps an attendance row: keep them pickable to correct it.
  const people = [...employees.map(e => ({ employeeId: e.employeeId, employeeNumber: e.employeeNumber, employeeName: e.employeeName })),
    ...(q.data ?? []).filter(a => !employees.some(e => e.employeeId === a.employeeId)).map(a => ({ employeeId: a.employeeId, employeeNumber: a.employeeNumber, employeeName: a.employeeName }))]
  const save = useMutation({ mutationFn: () => api.put<{ saved: number }>(`/api/payroll/runs/${runId}/attendance`, [{ ...f, employeeNumber: f.employeeNumber || people[0]?.employeeNumber }]), onSuccess: r => done(r.saved), onError: setErr, onMutate: () => setErr(null) })
  const upload = async (file: File) => {
    setErr(null); setMsg('')
    try {
      const res = await fetch(`/api/payroll/runs/${runId}/attendance/import`, { method: 'POST', headers: { 'Content-Type': 'text/csv', Authorization: `Bearer ${tokenStore.get() ?? ''}` }, body: await file.text() })
      const body = await res.json()
      if (!res.ok) throw new ApiError(res.status, body)
      done(body.saved)
    } catch (e) { setErr(e) }
  }
  const num = (k: keyof typeof f, label: string, w = 80) => <input type="number" min={0} step="0.5" style={{ width: w }} value={f[k] as number} onChange={e => setF({ ...f, [k]: Number(e.target.value) })} aria-label={label} title={label} />
  return (
    <div className="card">
      <h2>Attendance</h2>
      <p className="small"><b>Daily-paid workers are paid from Days worked.</b> Each needs a row for every run; enter 0 for anyone who didn't work, and they're left out of the run.</p>
      <p className="muted small">Absent days are deducted at the daily rate of basic pay; overtime is paid at the hourly rate × the legal entity's multipliers. Import a CSV from your time or biometric system with columns <span className="mono">EmployeeNumber, DaysWorked, AbsentDays, OvertimeHours, RestDayOvertimeHours, Note</span>.</p>
      <ErrorBox error={err} />{msg && <div className="alert ok">{msg}</div>}
      {editable && <>
        <div className="toolbar"><label className="btn">Import CSV<input type="file" accept=".csv,text/csv" className="sr-only" onChange={e => { const file = e.target.files?.[0]; if (file) upload(file); e.target.value = '' }} /></label></div>
        {people.length > 0 && <div className="toolbar">
          <select value={f.employeeNumber || people[0]?.employeeNumber} onChange={e => setF({ ...f, employeeNumber: e.target.value })} aria-label="Employee">{people.map(e => <option key={e.employeeId} value={e.employeeNumber}>{e.employeeNumber} {e.employeeName}</option>)}</select>
          <span className="small muted">Days</span>{num('daysWorked', 'Days worked', 64)}<span className="small muted">Absent</span>{num('absentDays', 'Absent days', 64)}<span className="small muted">OT h</span>{num('overtimeHours', 'Overtime hours', 64)}<span className="small muted">Rest-day OT h</span>{num('restDayOvertimeHours', 'Rest day overtime hours', 64)}
          <input placeholder="Note" value={f.note} onChange={e => setF({ ...f, note: e.target.value })} aria-label="Note" />
          <button className="btn primary" onClick={() => save.mutate()} disabled={save.isPending}>Save</button>
        </div>}
      </>}
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No attendance captured for this run.</Empty> : <table><thead><tr><th>Employee</th><th className="num">Days worked</th><th className="num">Absent</th><th className="num">OT hours</th><th className="num">Rest-day OT</th><th>Source</th><th>Note</th></tr></thead>
        <tbody>{q.data.map(a => <tr key={a.id}><td>{a.employeeNumber} {a.employeeName}</td><td className="num">{a.daysWorked}</td><td className="num">{a.absentDays || ''}</td><td className="num">{a.overtimeHours || ''}</td><td className="num">{a.restDayOvertimeHours || ''}</td><td>{a.source}</td><td>{a.note}</td></tr>)}</tbody></table>}
    </div>
  )
}

function Payments({ runId, batches }: { runId: string; batches: RunSummary['batches'] }) {
  const { can } = useAuth()
  const q = useQuery({ queryKey: ['payments', runId], queryFn: () => api.get<Payment[]>(`/api/payroll/runs/${runId}/payments`), enabled: can(P.PaymentsManage) })
  if (!can(P.PaymentsManage)) return <div className="card"><Empty>Payment details are restricted to finance and payroll administrators.</Empty></div>
  return (
    <>
      <div className="card"><h2>Payment batches</h2>{batches.length === 0 ? <Empty>No payment file generated yet.</Empty> : (
        <table><thead><tr><th>Reference</th><th>Format</th><th className="num">Payments</th><th className="num">Total</th><th>Value date</th><th>Status</th><th>SHA-256</th><th>Generated</th></tr></thead>
          <tbody>{batches.map(b => <tr key={b.id}><td className="mono">{b.reference}</td><td>{b.format}</td><td className="num">{b.paymentCount}</td><td className="num">{fmt.num(b.totalAmount)}</td><td>{fmt.date(b.valueDate)}</td><td><Badge status={b.status} /></td><td className="mono small" title={b.contentHash}>{b.contentHash.slice(0, 12)}…</td><td>{fmt.dateTime(b.createdAt)}</td></tr>)}</tbody></table>)}</div>
      <div className="card"><h2>Payment instructions</h2>{q.isLoading ? <Loading /> : !q.data?.length ? <Empty>None.</Empty> : (
        <table><thead><tr><th>Reference</th><th>Payee</th><th>Bank</th><th>Account</th><th className="num">Amount</th><th>Status</th><th>Bank ref / failure</th></tr></thead>
          <tbody>{q.data.map(p => <tr key={p.id}><td className="mono">{p.reference}</td><td>{p.accountName}</td><td>{p.bankName}</td><td className="mono">{p.accountNumber}</td><td className="num">{fmt.num(p.amount)}</td><td><Badge status={p.status} /></td><td>{p.bankReference ?? p.failureReason}</td></tr>)}</tbody></table>)}</div>
    </>
  )
}

function JournalView({ runId, has }: { runId: string; has: boolean }) {
  const { can } = useAuth()
  const q = useQuery({ queryKey: ['journal', runId], queryFn: () => api.get<Journal[]>(`/api/payroll/runs/${runId}/journal`), enabled: has && can(P.JournalsRead) })
  if (!can(P.JournalsRead)) return <div className="card"><Empty>Journals are visible to finance roles.</Empty></div>
  if (!has) return <div className="card"><Empty>The accounting journal is generated when the run is finalised.</Empty></div>
  if (q.isLoading) return <Loading />
  return <>{q.data?.map(j => (
    <div className="card" key={j.id}><h2>{j.reference} <Badge status={j.status} /> <span className="muted small">posted {fmt.date(j.postingDate)}{j.reversalOfJournalId && ' · reversal'}</span></h2>
      <table><thead><tr><th>#</th><th>Account</th><th>Description</th><th>Cost centre</th><th className="num">Debit</th><th className="num">Credit</th></tr></thead>
        <tbody>{j.lines.map(l => <tr key={l.sequence}><td className="muted">{l.sequence}</td><td className="mono">{l.accountCode}</td><td>{l.description}</td><td>{l.costCentre}</td><td className="num">{l.debit ? fmt.num(l.debit) : ''}</td><td className="num">{l.credit ? fmt.num(l.credit) : ''}</td></tr>)}</tbody>
        <tfoot><tr><td colSpan={4}>Totals {j.totalDebit === j.totalCredit ? <Badge status="Posted">Balanced</Badge> : <Badge status="Error">Unbalanced</Badge>}</td><td className="num">{fmt.num(j.totalDebit)}</td><td className="num">{fmt.num(j.totalCredit)}</td></tr></tfoot></table></div>))}</>
}

/** Bank whose own bulk format must be debited from an account at that bank. */
const formatBank: Record<string, string> = { KcbBulk: '01', EquityBulk: '68', CoopBulk: '11' }

function PayFileDialog({ runId, legalEntityId, payDate, onClose }: { runId: string; legalEntityId: string; payDate: string; onClose: () => void }) {
  const [format, setFormat] = useState('GenericCsv'); const [valueDate, setValueDate] = useState(payDate); const [err, setErr] = useState<unknown>(null); const [busy, setBusy] = useState(false)
  const [debit, setDebit] = useState('')
  const accounts = (usePayingAccounts(legalEntityId).data ?? []).filter(a => a.isActive && (!formatBank[format] || a.bankCode === formatBank[format]))
  const go = async () => { setBusy(true); setErr(null); try { await api.download(`/api/payroll/runs/${runId}/payment-file`, { format, valueDate, debitAccountId: debit || undefined }); onClose() } catch (e) { setErr(e) } finally { setBusy(false) } }
  return (
    <Modal title="Generate bank payment file" onClose={onClose}>
      <ErrorBox error={err} />
      <p className="small muted">The file total is reconciled to the approved net payroll before it is released. Regenerating cancels earlier un-sent instructions.</p>
      <Field label="Bank format"><select value={format} onChange={e => { setFormat(e.target.value); setDebit('') }}>{[['GenericCsv', 'Generic CSV'], ['KcbBulk', 'KCB bulk upload'], ['EquityBulk', 'Equity bulk upload'], ['CoopBulk', 'Co-operative Bank bulk'], ['MpesaB2C', 'M-PESA B2C']].map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></Field>
      <Field label="Debit account">
        <select value={debit} onChange={e => setDebit(e.target.value)}>
          <option value="">{format === 'MpesaB2C' ? 'M-PESA short code (no bank account)' : 'Default for this format'}</option>
          {accounts.map(a => <option key={a.id} value={a.id}>{a.label} — {a.bankName} {a.accountNumber}</option>)}
        </select>
        {format !== 'MpesaB2C' && accounts.length === 0 && <span className="muted small">No {formatBank[format] ? 'account at this bank' : 'paying account'} is set up. Add one under Organisation → Paying accounts.</span>}
      </Field>
      <Field label="Value date"><input type="date" value={valueDate} onChange={e => setValueDate(e.target.value)} /></Field>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy} onClick={go}>{busy ? 'Generating…' : 'Generate & download'}</button></div>
    </Modal>
  )
}

function ConfirmDialog({ runId, batches, onClose }: { runId: string; batches: RunSummary['batches']; onClose: () => void }) {
  const open = batches.filter(b => b.status === 'FileGenerated')
  const [batchId, setBatchId] = useState(open[0]?.id ?? ''); const [ref, setRef] = useState('')
  const m = useMutation({ mutationFn: () => api.post(`/api/payroll/runs/${runId}/payments/confirm`, { batchId, bankReference: ref }), onSuccess: onClose })
  return (
    <Modal title="Confirm bank payment" onClose={onClose}>
      <ErrorBox error={m.error} />
      <Field label="Batch"><select value={batchId} onChange={e => setBatchId(e.target.value)}>{open.map(b => <option key={b.id} value={b.id}>{b.reference} — {fmt.num(b.totalAmount)}</option>)}</select></Field>
      <Field label="Bank confirmation reference"><input value={ref} onChange={e => setRef(e.target.value)} /></Field>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={!batchId || m.isPending} onClick={() => m.mutate()}>Mark as paid</button></div>
    </Modal>
  )
}

type Diff = { field: string; ecosys: number; legacy: number; difference: number }
type Recon = { tolerance: number; employees: number; matched: number; different: number; onlyInEcosys: number; onlyInLegacy: number; fullyReconciled: boolean; columns: string[]; warnings: string[]
  totals: { field: string; ecosys: number; legacy: number; difference: number }[]; rows: { employeeNumber: string; name: string; status: string; differences: Diff[] }[] }
const fieldLabel: Record<string, string> = { Gross: 'Gross', Taxable: 'Taxable pay', PAYE: 'PAYE', NSSF: 'NSSF', SHIF: 'SHIF', HousingLevy: 'Housing levy', OtherDeductions: 'Other deductions', Net: 'Net pay' }

/** Parallel run (PRD §18): upload the outgoing system's register for the same period and compare. */
function ParallelRun({ runId }: { runId: string }) {
  const [file, setFile] = useState<File | null>(null)
  const [tolerance, setTolerance] = useState('0')
  const [result, setResult] = useState<Recon | null>(null)
  const [err, setErr] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)
  const [show, setShow] = useState<'issues' | 'all'>('issues')
  const send = async (format?: string) => {
    if (!file) return
    setErr(null); setBusy(true)
    try {
      const res = await fetch(`/api/payroll/runs/${runId}/reconcile?tolerance=${tolerance}${format ? `&format=${format}` : ''}`, { method: 'POST', headers: { 'Content-Type': 'text/csv', Authorization: `Bearer ${tokenStore.get() ?? ''}` }, body: await file.text() })
      if (!res.ok) throw new ApiError(res.status, await res.json().catch(() => ({})))
      if (!format) { setResult(await res.json()); return }
      const blob = await res.blob(); const a = document.createElement('a'); a.href = URL.createObjectURL(blob)
      a.download = /filename=([^;]+)/.exec(res.headers.get('Content-Disposition') ?? '')?.[1]?.replace(/"/g, '') ?? `parallel-run.${format}`; a.click()
    } catch (x) { setErr(x) } finally { setBusy(false) }
  }
  const rows = result?.rows.filter(r => show === 'all' || r.status !== 'Matched') ?? []
  return (
    <div className="card">
      <h2>Parallel run</h2>
      <p className="small">Before go-live, run the same month in your current payroll system and here, then compare every employee. Export the outgoing system's payroll register as CSV with an employee number column and any of: Gross, Taxable, PAYE, NSSF, SHIF, Housing Levy, Other deductions, Net. Only the columns you include are compared. <a href="#" onClick={e => { e.preventDefault(); api.download('/api/payroll/runs/reconcile-template.csv') }}>Download a template</a>.</p>
      <ErrorBox error={err} />
      <div className="toolbar">
        <input type="file" accept=".csv,text/csv" onChange={e => { setFile(e.target.files?.[0] ?? null); setResult(null) }} aria-label="Outgoing system register (CSV)" />
        <label className="small">Tolerance <select value={tolerance} onChange={e => setTolerance(e.target.value)} aria-label="Tolerance"><option value="0">exact (to the cent)</option><option value="0.5">KES 0.50</option><option value="1">KES 1 (whole-shilling rounding)</option></select></label>
        <button className="btn primary" disabled={!file || busy} onClick={() => send()}>{busy ? 'Comparing…' : 'Compare'}</button>
        {result && <><button className="btn" disabled={busy} onClick={() => send('xlsx')}>Excel</button><button className="btn" disabled={busy} onClick={() => send('pdf')}>PDF</button></>}
      </div>
      {result && <>
        {result.fullyReconciled ? <div className="alert ok">All {result.employees} employees match on {result.columns.map(c => fieldLabel[c]).join(', ')}{result.tolerance > 0 ? ` within KES ${result.tolerance}` : ''}. Keep the Excel or PDF for the sign-off pack.</div>
          : <div className="alert warn">{result.matched} of {result.employees} employees match. {result.different > 0 && `${result.different} differ. `}{result.onlyInEcosys > 0 && `${result.onlyInEcosys} only in Ecosys. `}{result.onlyInLegacy > 0 && `${result.onlyInLegacy} only in the outgoing system. `}Open an employee in the Employees tab to see how each Ecosys figure was worked out.</div>}
        {result.warnings.map(w => <div key={w} className="alert info">{w}</div>)}
        <h3>Totals</h3>
        <table><thead><tr><th>Figure</th><th className="num">Ecosys</th><th className="num">Outgoing system</th><th className="num">Difference</th></tr></thead>
          <tbody>{result.totals.map(t => <tr key={t.field}><td>{fieldLabel[t.field]}</td><td className="num">{fmt.num(t.ecosys)}</td><td className="num">{fmt.num(t.legacy)}</td><td className="num" style={{ color: t.difference ? 'var(--danger)' : undefined }}>{fmt.num(t.difference)}</td></tr>)}</tbody></table>
        <div className="toolbar" style={{ marginTop: 14 }}><h3 style={{ margin: 0 }}>Employees</h3><span style={{ flex: 1 }} /><select value={show} onChange={e => setShow(e.target.value as 'issues' | 'all')} aria-label="Show"><option value="issues">Differences only</option><option value="all">Everyone</option></select></div>
        {!rows.length ? <Empty>No differences.</Empty> : <div className="table-wrap"><table><thead><tr><th>No.</th><th>Name</th><th>Result</th><th>Figure</th><th className="num">Ecosys</th><th className="num">Outgoing</th><th className="num">Difference</th></tr></thead>
          <tbody>{rows.flatMap(r => (r.differences.length ? r.differences : [null]).map((d, i) => <tr key={r.employeeNumber + i}>
            <td className="mono">{i === 0 ? r.employeeNumber : ''}</td><td>{i === 0 ? r.name : ''}</td>
            <td>{i === 0 && <Badge status={r.status === 'Matched' ? 'Active' : r.status === 'Different' ? 'Error' : 'Warning'}>{r.status === 'OnlyInEcosys' ? 'Only in Ecosys' : r.status === 'OnlyInLegacy' ? 'Only in outgoing' : r.status}</Badge>}</td>
            <td>{d ? fieldLabel[d.field] : ''}</td><td className="num">{d ? fmt.num(d.ecosys) : ''}</td><td className="num">{d ? fmt.num(d.legacy) : ''}</td><td className="num" style={{ color: 'var(--danger)' }}>{d ? fmt.num(d.difference) : ''}</td></tr>))}</tbody></table></div>}
      </>}
    </div>
  )
}

type NssfCheck = { runReference: string; period: string; members: number; pensionable: number; member: number; employer: number; ready: boolean; problems: { employeeNumber: string; employeeName: string; message: string }[]; warnings: string[] }

/** NSSF upload files, checked first: problems are listed instead of a file that the portal would reject. */
function NssfFiles({ runId }: { runId: string }) {
  const [check, setCheck] = useState<NssfCheck | null>(null)
  const [err, setErr] = useState<unknown>(null)
  const get = async (layout: 'portal' | 'returns') => {
    setErr(null)
    try {
      const c = await api.get<NssfCheck>(`/api/payroll/reports/nssf-file/check?runId=${runId}`)
      if (!c.ready || c.warnings.length > 0) setCheck(c)
      if (c.ready) await api.download(`/api/payroll/reports/nssf-file?runId=${runId}&layout=${layout}`)
    } catch (e) { setErr(e) }
  }
  return (
    <div style={{ marginTop: 12 }}>
      <div className="small muted" style={{ marginBottom: 6 }}>NSSF upload files</div>
      <div className="actions">
        <button className="btn sm" onClick={() => get('portal')} title="The employer portal's payroll template: one row per member with pay; the portal works out contributions">NSSF portal file</button>
        <button className="btn sm" onClick={() => get('returns')} title="The returns file: header totals, then Tier I and Tier II rows per member">NSSF returns file</button>
      </div>
      <ErrorBox error={err} />
      {check && <Modal title={check.ready ? 'NSSF file downloaded' : 'Fix these before filing'} onClose={() => setCheck(null)} width={720}>
        <p className="small">{check.period} · {check.members} members · member {fmt.money(check.member)} · employer {fmt.money(check.employer)}</p>
        {!check.ready && <div className="table-wrap"><table><thead><tr><th>Employee</th><th>Problem</th></tr></thead>
          <tbody>{check.problems.map((p, i) => <tr key={i}><td>{p.employeeNumber ? `${p.employeeNumber} ${p.employeeName}` : '—'}</td><td>{p.message}</td></tr>)}</tbody></table></div>}
        {check.warnings.map((w, i) => <div key={i} className="alert warn">{w}</div>)}
        <div className="actions"><button className="btn" onClick={() => setCheck(null)}>Close</button></div>
      </Modal>}
    </div>
  )
}
