import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { useAuth } from '../lib/auth'
import type { EmployeeDetail, LeaveBalance, LeaveRequest, LeaveType, Payslip } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, Loading, PageHead, Stat, Tabs } from '../components/ui'
import { PayslipList } from './EmployeeDetail'

export default function SelfServicePage() {
  const { user } = useAuth()
  const id = user?.employeeId
  const [tab, setTab] = useState<'overview' | 'payslips' | 'leave' | 'profile'>('overview')
  if (!id) return <div className="card"><Empty>Your account is not linked to an employee record. Ask HR to link it.</Empty></div>
  return (
    <>
      <PageHead title="My workspace" sub="Your payslips, leave and personal details" />
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'overview', label: 'Overview' }, { id: 'payslips', label: 'Payslips' }, { id: 'leave', label: 'Leave' }, { id: 'profile', label: 'My details' }]} />
      {tab === 'overview' && <Overview employeeId={id} />}
      {tab === 'payslips' && <><PayslipList employeeId={id} /><P9 employeeId={id} /></>}
      {tab === 'leave' && <MyLeave employeeId={id} />}
      {tab === 'profile' && <Profile employeeId={id} />}
    </>
  )
}

function Overview({ employeeId }: { employeeId: string }) {
  const slips = useQuery({ queryKey: ['payslips', employeeId], queryFn: () => api.get<Payslip[]>(`/api/payroll/employees/${employeeId}/payslips`) })
  const bal = useQuery({ queryKey: ['leave-balances', employeeId], queryFn: () => api.get<LeaveBalance[]>(`/api/payroll/leave/balances/${employeeId}`) })
  const latest = slips.data?.[0]
  const annual = bal.data?.find(b => b.code === 'ANNUAL') ?? bal.data?.[0]
  return (
    <>
      <div className="grid c4">
        <Stat label="Latest net pay" value={latest ? fmt.money(latest.netPay, latest.currencyCode) : '—'} hint={latest ? `${fmt.date(latest.periodStart)} – ${fmt.date(latest.periodEnd)}` : 'No payslip yet'} />
        <Stat label="Latest gross pay" value={latest ? fmt.money(latest.grossPay, latest.currencyCode) : '—'} />
        <Stat label="Deductions" value={latest ? fmt.money(latest.grossPay - latest.netPay, latest.currencyCode) : '—'} hint="Statutory and other deductions" />
        <Stat label={annual ? `${annual.leaveType} available` : 'Leave available'} value={annual ? `${annual.available} days` : '—'} hint={annual ? `${annual.taken} taken of ${annual.carriedOver + annual.accrued} accrued` : undefined} />
      </div>
      <div className="grid c2" style={{ marginTop: 16 }}>
        <div className="card"><h2>Recent payslips</h2>{slips.isLoading ? <Loading /> : !slips.data?.length ? <Empty>No payslips yet.</Empty> : <table><tbody>{slips.data.slice(0, 6).map(p => <tr key={p.id}><td>{fmt.date(p.periodEnd)}</td><td className="mono">{p.payslipNumber}</td><td className="num">{fmt.num(p.netPay)}</td><td><button className="btn sm" onClick={() => api.download(`/api/payroll/payslips/${p.id}/pdf`)}>PDF</button></td></tr>)}</tbody></table>}</div>
        <div className="card"><h2>Leave balances {bal.data?.[0] && <span className="muted small">{bal.data[0].year}</span>}</h2>{bal.isLoading ? <Loading /> : !bal.data?.length ? <Empty>No balances.</Empty> : <table><thead><tr><th>Type</th><th className="num">Entitlement</th><th className="num">Accrued</th><th className="num">Taken</th><th className="num">Available</th></tr></thead><tbody>{bal.data.map(b => <tr key={b.id}><td>{b.leaveType}</td><td className="num">{b.entitlement}</td><td className="num">{b.accrued + b.carriedOver}</td><td className="num">{b.taken}</td><td className="num"><b>{b.available}</b></td></tr>)}</tbody></table>}</div>
      </div>
    </>
  )
}

function MyLeave({ employeeId }: { employeeId: string }) {
  const qc = useQueryClient()
  const types = useQuery({ queryKey: ['leave-types'], queryFn: () => api.get<LeaveType[]>('/api/payroll/leave/types') })
  const reqs = useQuery({ queryKey: ['my-leave'], queryFn: () => api.get<LeaveRequest[]>('/api/payroll/leave/requests') })
  const [f, setF] = useState({ leaveTypeId: '', startDate: '', endDate: '', days: 1, reason: '' })
  const typeId = f.leaveTypeId || types.data?.[0]?.id
  const m = useMutation({ mutationFn: () => api.post('/api/payroll/leave/requests', { ...f, leaveTypeId: typeId }), onSuccess: () => { qc.invalidateQueries({ queryKey: ['my-leave'] }); qc.invalidateQueries({ queryKey: ['leave-balances', employeeId] }); setF({ ...f, reason: '', startDate: '', endDate: '' }) } })
  const cancel = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/leave/requests/${id}/cancel`), onSuccess: () => { qc.invalidateQueries({ queryKey: ['my-leave'] }); qc.invalidateQueries({ queryKey: ['leave-balances', employeeId] }) } })
  const workingDays = (a: string, b: string) => { if (!a || !b) return 0; let n = 0; for (let d = new Date(a); d <= new Date(b); d.setDate(d.getDate() + 1)) if (d.getDay() !== 0 && d.getDay() !== 6) n++; return n }
  return (
    <div className="grid c2">
      <div className="card"><h2>Apply for leave</h2><ErrorBox error={m.error} />
        <Field label="Leave type"><select value={typeId ?? ''} onChange={e => setF({ ...f, leaveTypeId: e.target.value })}>{types.data?.map(t => <option key={t.id} value={t.id}>{t.name}{t.isPaid ? '' : ' (unpaid)'}</option>)}</select></Field>
        <div className="form-grid">
          <Field label="First day"><input type="date" value={f.startDate} onChange={e => setF({ ...f, startDate: e.target.value, days: workingDays(e.target.value, f.endDate) || 1 })} /></Field>
          <Field label="Last day"><input type="date" value={f.endDate} onChange={e => setF({ ...f, endDate: e.target.value, days: workingDays(f.startDate, e.target.value) || 1 })} /></Field>
          <Field label="Working days"><input type="number" min={0.5} step={0.5} value={f.days} onChange={e => setF({ ...f, days: Number(e.target.value) })} /></Field>
        </div>
        <Field label="Reason"><input value={f.reason} onChange={e => setF({ ...f, reason: e.target.value })} /></Field>
        <button className="btn primary" disabled={!f.startDate || !f.endDate || m.isPending} onClick={() => m.mutate()}>Submit request</button></div>
      <div className="card"><h2>My requests</h2>{reqs.isLoading ? <Loading /> : !reqs.data?.length ? <Empty>No leave requests.</Empty> : <table><thead><tr><th>Type</th><th>Dates</th><th className="num">Days</th><th>Status</th><th /></tr></thead>
        <tbody>{reqs.data.map(r => <tr key={r.id}><td>{r.leaveType}</td><td>{fmt.date(r.startDate)} – {fmt.date(r.endDate)}</td><td className="num">{r.days}</td><td><Badge status={r.status} />{r.approverComment && <div className="muted small">{r.approverComment}</div>}</td><td>{(r.status === 'Pending' || r.status === 'Approved') && <button className="btn sm" onClick={() => cancel.mutate(r.id)}>Cancel</button>}</td></tr>)}</tbody></table>}</div>
    </div>
  )
}

function Profile({ employeeId }: { employeeId: string }) {
  const q = useQuery({ queryKey: ['employee', employeeId], queryFn: () => api.get<EmployeeDetail>(`/api/payroll/employees/${employeeId}`) })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const e = q.data!.employee
  return (
    <div className="grid c2">
      <div className="card"><h2>Personal details</h2><dl className="kv"><dt>Employee number</dt><dd className="mono">{e.employeeNumber}</dd><dt>Name</dt><dd>{e.firstName} {e.middleName} {e.lastName}</dd><dt>Job title</dt><dd>{e.jobTitle}</dd><dt>Email</dt><dd>{e.email}</dd><dt>Phone</dt><dd>{e.phone}</dd><dt>Hire date</dt><dd>{fmt.date(e.hireDate)}</dd><dt>Employment</dt><dd>{e.employmentType} · <Badge status={e.status} /></dd></dl><p className="muted small">To change personal details, contact HR — changes are audited.</p></div>
      <div className="card"><h2>Bank accounts</h2>{!q.data!.bankAccounts.length ? <Empty>No bank account on file.</Empty> : <table><tbody>{q.data!.bankAccounts.map(b => <tr key={b.id}><td>{b.bankName}</td><td className="mono">{b.accountNumber}</td><td>{b.isPrimary && <Badge status="Active">Primary</Badge>}</td><td>{b.isVerified ? <Badge status="Active">Verified</Badge> : <Badge status="Pending">Awaiting verification</Badge>}</td></tr>)}</tbody></table>}
        <h2 style={{ marginTop: 14 }}>Statutory numbers</h2><dl className="kv"><dt>KRA PIN</dt><dd className="mono">{q.data!.statutory?.kraPin ?? '—'}</dd><dt>NSSF</dt><dd className="mono">{q.data!.statutory?.nssfNumber ?? '—'}</dd><dt>SHIF</dt><dd className="mono">{q.data!.statutory?.shifNumber ?? '—'}</dd></dl></div>
    </div>
  )
}

function P9({ employeeId }: { employeeId: string }) {
  const q = useQuery({ queryKey: ['employee', employeeId], queryFn: () => api.get<EmployeeDetail>(`/api/payroll/employees/${employeeId}`) })
  const [year, setYear] = useState(new Date().getFullYear())
  const [err, setErr] = useState<unknown>(null)
  const le = q.data?.employee.legalEntityId
  return (
    <div className="card"><h2>Tax deduction card (P9)</h2>
      <p className="muted small">Your annual summary of pay, tax and statutory deductions for filing your KRA return.</p>
      <ErrorBox error={err} />
      <div className="toolbar"><input type="number" value={year} onChange={e => setYear(Number(e.target.value))} style={{ width: 90 }} aria-label="Year" />
        <button className="btn" disabled={!le} onClick={async () => { setErr(null); try { await api.download(`/api/payroll/reports/annual?legalEntityId=${le}&year=${year}&employeeId=${employeeId}&format=pdf`) } catch (e) { setErr(e) } }}>Download PDF</button></div>
    </div>
  )
}
