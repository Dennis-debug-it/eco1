import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { api, fmt, isoDate, nextMonthStart } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { byId, useBranches, useComponents, useDepartments, useLegalEntities, useStructures } from '../lib/lookups'
import type { BankAccount, EmployeeDetail, Payslip, StatutoryDetails } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, Tabs } from '../components/ui'
import { BankPicker } from '../components/BankPicker'
import { EmployeeForm } from './Employees'
import { ContractsTab } from '../components/Contracts'

type Tab = 'profile' | 'salary' | 'statutory' | 'bank' | 'payslips' | 'contracts' | 'history'

export default function EmployeeDetailPage() {
  const { id = '' } = useParams()
  const { can, user } = useAuth()
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['employee', id], queryFn: () => api.get<EmployeeDetail>(`/api/payroll/employees/${id}`) })
  const [tab, setTab] = useState<Tab>('profile')
  const [edit, setEdit] = useState(false)
  const [salaryModal, setSalaryModal] = useState(false)
  const [bankModal, setBankModal] = useState(false)
  const [statModal, setStatModal] = useState(false)
  const [loanModal, setLoanModal] = useState(false)
  const depts = useDepartments(); const branches = useBranches(); const entities = useLegalEntities()
  const refresh = () => qc.invalidateQueries({ queryKey: ['employee', id] })

  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const d = q.data!
  const e = d.employee
  const isSelf = user?.employeeId === id
  const tabs: { id: Tab; label: string }[] = [
    { id: 'profile', label: 'Profile' },
    ...(d.salaryAssignments ? [{ id: 'salary' as Tab, label: 'Salary' }] : []),
    { id: 'statutory', label: 'Statutory' }, { id: 'bank', label: 'Bank accounts' },
    ...(isSelf || can(P.SalaryRead) ? [{ id: 'payslips' as Tab, label: 'Payslips' }] : []),
    ...(can(P.SalaryRead) && (can(P.HrDocuments) || can(P.SalaryWrite)) ? [{ id: 'contracts' as Tab, label: 'Contracts & letters' }] : []),
    { id: 'history', label: 'Employment history' },
  ]
  const current = d.salaryAssignments?.find(a => !a.effectiveTo) ?? d.salaryAssignments?.[0]
  const makerMayVerify = !!entities.data?.find(x => x.id === e.legalEntityId)?.settings.allowBankDetailSelfVerification
  /** Why the current user can't verify this account (maker-checker), or null if they can. */
  const cannotVerify = (b: BankAccount) => isSelf ? 'You can’t verify your own bank details.'
    : b.createdBy === user?.id && !makerMayVerify ? 'You entered this account, so a colleague must verify it.' : null

  return (
    <>
      <PageHead title={`${e.firstName} ${e.lastName}`} sub={<><span className="mono">{e.employeeNumber}</span> · {e.jobTitle ?? '—'} · <Badge status={e.status} /></>}>
        <Link to="/employees" className="btn">All employees</Link>
        {can(P.EmployeesWrite) && <button className="btn" onClick={() => setEdit(true)}>Edit</button>}
      </PageHead>
      <Tabs tabs={tabs} value={tab} onChange={setTab} />

      {tab === 'profile' && (
        <div className="grid c2">
          <div className="card"><h2>Personal</h2>
            <dl className="kv"><dt>National ID</dt><dd className="mono">{e.nationalId ?? '—'}</dd><dt>Passport</dt><dd className="mono">{e.passportNumber ?? '—'}</dd><dt>Date of birth</dt><dd>{fmt.date(e.dateOfBirth)}</dd><dt>Gender</dt><dd>{e.gender}</dd>
              <dt>Email</dt><dd>{e.email ?? '—'}</dd><dt>Phone</dt><dd>{e.phone ?? '—'}</dd><dt>Address</dt><dd>{e.physicalAddress ?? '—'}</dd><dt>Emergency contact</dt><dd>{e.emergencyContactName ? `${e.emergencyContactName} (${e.emergencyContactPhone ?? '—'})` : '—'}</dd></dl></div>
          <div className="card"><h2>Employment</h2>
            <dl className="kv"><dt>Type</dt><dd>{e.employmentType}</dd><dt>Status</dt><dd><Badge status={e.status} /></dd><dt>Hire date</dt><dd>{fmt.date(e.hireDate)}</dd><dt>Contract end</dt><dd>{fmt.date(e.contractEndDate)}</dd><dt>Termination</dt><dd>{fmt.date(e.terminationDate)}</dd>
              <dt>Department</dt><dd>{byId(depts.data)[e.departmentId ?? '']?.name ?? '—'}</dd><dt>Branch</dt><dd>{byId(branches.data)[e.branchId ?? '']?.name ?? '—'}</dd><dt>Work location</dt><dd>{e.workLocation ?? '—'}</dd></dl></div>
        </div>
      )}

      {tab === 'salary' && d.salaryAssignments && (
        <>
          <div className="card">
            <div className="page-head" style={{ marginBottom: 8 }}><h2>Current remuneration</h2>{can(P.SalaryWrite) && <div className="actions"><button className="btn" onClick={() => setLoanModal(true)}>New loan / advance</button><button className="btn primary" onClick={() => setSalaryModal(true)}>New salary assignment</button></div>}</div>
            {current ? <dl className="kv"><dt>Basic salary</dt><dd className="mono">{fmt.money(current.basicSalary, current.currencyCode)}</dd><dt>Structure</dt><dd><StructureName id={current.salaryStructureId} /></dd><dt>Effective</dt><dd>{fmt.date(current.effectiveFrom)}{current.effectiveTo ? ` – ${fmt.date(current.effectiveTo)}` : ' onwards'}</dd><dt>Reason</dt><dd>{current.changeReason ?? '—'}</dd></dl> : <Empty>No salary assignment. Payroll cannot run for this employee.</Empty>}
            {current && current.lines.length > 0 && <div style={{ marginTop: 10 }}><h3>Employee-specific components</h3><AssignmentLines lines={current.lines} /></div>}
          </div>
          <div className="card"><h2>Salary history</h2>
            <table><thead><tr><th>Effective from</th><th>To</th><th className="num">Basic</th><th>Structure</th><th>Reason</th></tr></thead>
              <tbody>{d.salaryAssignments.map(a => <tr key={a.id}><td>{fmt.date(a.effectiveFrom)}</td><td>{a.effectiveTo ? fmt.date(a.effectiveTo) : <Badge status="Active">Current</Badge>}</td><td className="num">{fmt.num(a.basicSalary)}</td><td><StructureName id={a.salaryStructureId} /></td><td>{a.changeReason}</td></tr>)}</tbody></table></div>
          {d.loans && d.loans.length > 0 && <div className="card"><h2>Loans & advances</h2>
            <table><thead><tr><th>Reference</th><th className="num">Principal</th><th className="num">Balance</th><th className="num">Instalment</th><th>Start</th><th>Status</th></tr></thead>
              <tbody>{d.loans.map(l => <tr key={l.id}><td className="mono">{l.reference}</td><td className="num">{fmt.num(l.principal)}</td><td className="num">{fmt.num(l.balance)}</td><td className="num">{fmt.num(l.instalmentAmount)}</td><td>{fmt.date(l.startDate)}</td><td><Badge status={l.status} /></td></tr>)}</tbody></table></div>}
        </>
      )}

      {tab === 'statutory' && (
        <div className="card">
          <div className="page-head" style={{ marginBottom: 8 }}><h2>Statutory identifiers</h2>{can(P.EmployeesWrite) && <button className="btn primary" onClick={() => setStatModal(true)}>Edit</button>}</div>
          <dl className="kv"><dt>KRA PIN</dt><dd className="mono">{d.statutory?.kraPin ?? <span className="badge danger">Missing</span>}</dd><dt>NSSF number</dt><dd className="mono">{d.statutory?.nssfNumber ?? <span className="badge warn">Missing</span>}</dd><dt>SHIF number</dt><dd className="mono">{d.statutory?.shifNumber ?? <span className="badge warn">Missing</span>}</dd>
            <dt>Disability exemption</dt><dd>{d.statutory?.hasDisabilityExemption ? `Yes — ${fmt.money(d.statutory.disabilityExemptAmount)} / month, expires ${fmt.date(d.statutory.disabilityExemptionExpiry)}` : 'No'}</dd>
            <dt>NSSF before Ecosys</dt><dd>{fmt.money(d.statutory?.nssfOpeningBalance ?? 0)}</dd>
            <dt>Exempt from</dt><dd>{d.statutory?.statutoryExemptions?.length ? d.statutory.statutoryExemptions.join(', ') : 'None'}</dd></dl>
        </div>
      )}

      {tab === 'bank' && (
        <div className="card">
          <div className="page-head" style={{ marginBottom: 8 }}><h2>Bank accounts</h2>{(can(P.EmployeesWrite) || isSelf) && <button className="btn primary" onClick={() => setBankModal(true)}>Add account</button>}</div>
          {d.bankAccounts.length === 0 ? <Empty>No bank account. Payment cannot be generated.</Empty> : (
            <table><thead><tr><th>Method</th><th>Bank</th><th>Branch</th><th>Account name</th><th>Account number</th><th>Primary</th><th>Verified</th><th /></tr></thead>
              <tbody>{d.bankAccounts.map(b => <tr key={b.id}><td>{b.method}</td><td>{b.bankName} <span className="muted">{b.bankCode}</span></td><td>{b.branchName} <span className="muted">{b.branchCode}</span></td><td>{b.accountName}</td><td className="mono">{b.accountNumber}</td><td>{b.isPrimary ? (b.isVerified ? 'Yes' : 'Once verified') : ''}</td>
                <td>{b.isVerified ? <Badge status="Active">Verified</Badge> : <Badge status="Pending">Awaiting verification</Badge>}</td>
                <td>{!b.isVerified && can(P.EmployeesWrite) && (cannotVerify(b) ? <span className="muted small" title={cannotVerify(b)!}>Needs another verifier</span> : <VerifyButton employeeId={id} accountId={b.id} onDone={refresh} />)}</td></tr>)}</tbody></table>)}
          <p className="muted small">A new account isn’t paid until someone other than the person who entered it verifies it. Until then pay goes to the current verified account, and the employee is notified of every change.</p>
        </div>
      )}

      {tab === 'payslips' && <PayslipList employeeId={id} />}

      {tab === 'contracts' && <ContractsTab employeeId={id} employmentType={e.employmentType} hireDate={e.hireDate} contractEndDate={e.contractEndDate ?? undefined} />}
      {tab === 'history' && (
        <div className="card"><h2>Employment history</h2>
          <table><thead><tr><th>From</th><th>To</th><th>Job title</th><th>Type</th><th>Status</th><th>Reason</th></tr></thead>
            <tbody>{d.employmentHistory.map((h, i) => <tr key={i}><td>{fmt.date(h.effectiveFrom)}</td><td>{h.effectiveTo ? fmt.date(h.effectiveTo) : 'Current'}</td><td>{h.jobTitle}</td><td>{h.employmentType}</td><td><Badge status={h.status} /></td><td>{h.changeReason}</td></tr>)}</tbody></table></div>
      )}

      {edit && <EmployeeForm initial={e} onClose={() => { setEdit(false); refresh() }} />}
      {salaryModal && <SalaryModal employeeId={id} legalEntityId={e.legalEntityId} currentBasic={current?.basicSalary} onClose={() => { setSalaryModal(false); refresh() }} />}
      {bankModal && <BankModal employeeId={id} onClose={() => { setBankModal(false); refresh() }} />}
      {statModal && <StatutoryModal employeeId={id} initial={d.statutory} onClose={() => { setStatModal(false); refresh() }} />}
      {loanModal && <LoanModal employeeId={id} legalEntityId={e.legalEntityId} onClose={() => { setLoanModal(false); refresh() }} />}
    </>
  )
}

function StructureName({ id }: { id?: string }) { const s = useStructures(); return <>{id ? byId(s.data)[id]?.name ?? '…' : '—'}</> }

function AssignmentLines({ lines }: { lines: { salaryComponentId: string; method: string; amount?: number; rate?: number }[] }) {
  const comps = byId(useComponents().data)
  return <table><thead><tr><th>Component</th><th>Method</th><th className="num">Amount / rate</th></tr></thead><tbody>{lines.map((l, i) => <tr key={i}><td>{comps[l.salaryComponentId]?.name ?? l.salaryComponentId}</td><td>{l.method}</td><td className="num">{l.amount != null ? fmt.num(l.amount) : l.rate != null ? `${(l.rate * 100).toFixed(2)}%` : '—'}</td></tr>)}</tbody></table>
}

function VerifyButton({ employeeId, accountId, onDone }: { employeeId: string; accountId: string; onDone: () => void }) {
  const m = useMutation({ mutationFn: () => api.post(`/api/payroll/employees/${employeeId}/bank-accounts/${accountId}/verify`), onSuccess: onDone })
  return <>
    <button className="btn sm" onClick={() => { if (confirm('Have you confirmed this account with the employee on a number already on file? Pay will go to it from the next run.')) m.mutate() }} disabled={m.isPending}>Verify</button>
    {m.error && <ErrorBox error={m.error} />}
  </>
}

export function PayslipList({ employeeId }: { employeeId: string }) {
  const q = useQuery({ queryKey: ['payslips', employeeId], queryFn: () => api.get<Payslip[]>(`/api/payroll/employees/${employeeId}/payslips`) })
  const [busy, setBusy] = useState<string | null>(null)
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  return (
    <div className="card"><h2>Payslips</h2>
      {q.data!.length === 0 ? <Empty>No payslips issued yet.</Empty> : (
        <table><thead><tr><th>Payslip</th><th>Period</th><th className="num">Gross</th><th className="num">Net</th><th>Issued</th><th /></tr></thead>
          <tbody>{q.data!.map(p => <tr key={p.id}><td className="mono">{p.payslipNumber}</td><td>{fmt.date(p.periodStart)} – {fmt.date(p.periodEnd)}</td><td className="num">{fmt.num(p.grossPay)}</td><td className="num">{fmt.num(p.netPay)}</td><td>{fmt.dateTime(p.issuedAt)}</td>
            <td><button className="btn sm" disabled={busy === p.id} onClick={async () => { setBusy(p.id); try { await api.download(`/api/payroll/payslips/${p.id}/pdf`) } finally { setBusy(null) } }}>{busy === p.id ? 'Preparing…' : 'Download PDF'}</button></td></tr>)}</tbody></table>)}
    </div>
  )
}

function SalaryModal({ employeeId, legalEntityId, currentBasic, onClose }: { employeeId: string; legalEntityId: string; currentBasic?: number; onClose: () => void }) {
  const structures = useStructures(legalEntityId)
  const [basic, setBasic] = useState(currentBasic ?? 0)
  const [structureId, setStructureId] = useState('')
  const [from, setFrom] = useState(nextMonthStart())
  const [reason, setReason] = useState('')
  const m = useMutation({ mutationFn: () => api.post(`/api/payroll/employees/${employeeId}/salary`, { basicSalary: basic, salaryStructureId: structureId || null, effectiveFrom: from, changeReason: reason }), onSuccess: onClose })
  return (
    <Modal title="New salary assignment" onClose={onClose}>
      <ErrorBox error={m.error} />
      <p className="muted small">The current assignment is closed the day before the new effective date. Changes dated inside a finalised period are rejected — use an adjustment instead.</p>
      <Field label="Basic salary (monthly)"><input type="number" min={0} step="0.01" value={basic} onChange={e => setBasic(Number(e.target.value))} /></Field>
      <Field label="Salary structure"><select value={structureId} onChange={e => setStructureId(e.target.value)}><option value="">None (basic only)</option>{structures.data?.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></Field>
      <Field label="Effective from"><input type="date" value={from} onChange={e => setFrom(e.target.value)} /></Field>
      <Field label="Reason"><input value={reason} onChange={e => setReason(e.target.value)} placeholder="Annual review, promotion, …" /></Field>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={m.isPending} onClick={() => m.mutate()}>Save</button></div>
    </Modal>
  )
}

function BankModal({ employeeId, onClose }: { employeeId: string; onClose: () => void }) {
  const [f, setF] = useState<Partial<BankAccount>>({ method: 'BankTransfer', isPrimary: true, splitPercent: 100 })
  const set = (k: keyof BankAccount, v: unknown) => setF(x => ({ ...x, [k]: v }))
  const m = useMutation({ mutationFn: () => api.post(`/api/payroll/employees/${employeeId}/bank-accounts`, f), onSuccess: onClose })
  const isBank = f.method === 'BankTransfer'
  return (
    <Modal title="Add pay account" onClose={onClose}>
      <ErrorBox error={m.error} />
      <div className="form-grid">
        <Field label="Method"><select value={f.method} onChange={e => set('method', e.target.value)}>{[['BankTransfer', 'Bank transfer'], ['MobileMoney', 'M-PESA'], ['Cash', 'Cash'], ['Cheque', 'Cheque']].map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></Field>
        {isBank && <BankPicker bankCode={f.bankCode} branchCode={f.branchCode} onChange={(bank, branch) => setF(x => ({ ...x, bankCode: bank, branchCode: branch }))} />}
        <Field label="Account name *"><input value={f.accountName ?? ''} onChange={e => set('accountName', e.target.value)} /></Field>
        <Field label={f.method === 'MobileMoney' ? 'M-PESA number *' : 'Account number *'}><input value={f.accountNumber ?? ''} inputMode="numeric" placeholder={f.method === 'MobileMoney' ? '07XX XXX XXX' : undefined} onChange={e => set('accountNumber', e.target.value)} /></Field>
      </div>
      <label className="field inline"><input type="checkbox" checked={!!f.isPrimary} onChange={e => set('isPrimary', e.target.checked)} /> Primary account for salary</label>
      {!f.isPrimary && <Field label="Share of net pay to this account (%)"><input type="number" min={1} max={99} value={f.splitPercent ?? 100} onChange={e => set('splitPercent', Number(e.target.value))} /></Field>}
      <p className="muted small">The account is paid only after a second person verifies it. The employee is notified.</p>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={m.isPending} onClick={() => m.mutate()}>Save</button></div>
    </Modal>
  )
}

function StatutoryModal({ employeeId, initial, onClose }: { employeeId: string; initial?: StatutoryDetails; onClose: () => void }) {
  const [f, setF] = useState<StatutoryDetails>(initial ?? { hasDisabilityExemption: false, disabilityExemptAmount: 0, statutoryExemptions: [] })
  const set = (k: keyof StatutoryDetails, v: unknown) => setF(x => ({ ...x, [k]: v }))
  const m = useMutation({ mutationFn: () => api.put(`/api/payroll/employees/${employeeId}/statutory`, f), onSuccess: onClose })
  const toggle = (code: string) => set('statutoryExemptions', f.statutoryExemptions.includes(code) ? f.statutoryExemptions.filter(c => c !== code) : [...f.statutoryExemptions, code])
  return (
    <Modal title="Statutory details" onClose={onClose}>
      <ErrorBox error={m.error} />
      <div className="form-grid">
        <Field label="KRA PIN"><input value={f.kraPin ?? ''} onChange={e => set('kraPin', e.target.value)} placeholder="A000000000X" /></Field>
        <Field label="NSSF number"><input value={f.nssfNumber ?? ''} onChange={e => set('nssfNumber', e.target.value)} /></Field>
        <Field label="SHIF number"><input value={f.shifNumber ?? ''} onChange={e => set('shifNumber', e.target.value)} /></Field>
        <Field label="NSSF contributions before Ecosys (KES)"><input type="number" min={0} step="0.01" value={f.nssfOpeningBalance ?? 0} onChange={e => set('nssfOpeningBalance', Number(e.target.value))} /></Field>
      </div>
      <p className="small muted" style={{ marginTop: 0 }}>Employee plus employer NSSF contributions made before this employer's payroll moved here, from the NSSF statement. Payslips add this to the contributions recorded since, to show NSSF contributions to date.</p>
      <label className="field inline"><input type="checkbox" checked={f.hasDisabilityExemption} onChange={e => set('hasDisabilityExemption', e.target.checked)} /> Holds a KRA disability tax exemption certificate</label>
      {f.hasDisabilityExemption && <div className="form-grid">
        <Field label="Certificate number"><input value={f.disabilityExemptionCertificate ?? ''} onChange={e => set('disabilityExemptionCertificate', e.target.value)} /></Field>
        <Field label="Exempt amount per month"><input type="number" value={f.disabilityExemptAmount} onChange={e => set('disabilityExemptAmount', Number(e.target.value))} /></Field>
        <Field label="Expiry"><input type="date" value={f.disabilityExemptionExpiry ?? ''} onChange={e => set('disabilityExemptionExpiry', e.target.value || undefined)} /></Field>
      </div>}
      <h3>Exempt from statutory schemes</h3>
      {[['KE.NSSF', 'NSSF (e.g. expatriate on home scheme)'], ['KE.SHIF', 'SHIF'], ['KE.AHL', 'Affordable Housing Levy']].map(([c, l]) => <label key={c} className="field inline"><input type="checkbox" checked={f.statutoryExemptions.includes(c)} onChange={() => toggle(c)} /> {l}</label>)}
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={m.isPending} onClick={() => m.mutate()}>Save</button></div>
    </Modal>
  )
}

function LoanModal({ employeeId, legalEntityId, onClose }: { employeeId: string; legalEntityId: string; onClose: () => void }) {
  const comps = useComponents(legalEntityId)
  const deductions = comps.data?.filter(c => c.type === 'Deduction' && c.method === 'Variable') ?? []
  const [f, setF] = useState({ reference: '', principal: 0, instalmentAmount: 0, startDate: isoDate(), salaryComponentId: '' })
  const m = useMutation({ mutationFn: () => api.post(`/api/payroll/employees/${employeeId}/loans`, { ...f, salaryComponentId: f.salaryComponentId || deductions[0]?.id }), onSuccess: onClose })
  return (
    <Modal title="New loan or advance" onClose={onClose}>
      <ErrorBox error={m.error} />
      <Field label="Recovered through component"><select value={f.salaryComponentId || deductions[0]?.id || ''} onChange={e => setF({ ...f, salaryComponentId: e.target.value })}>{deductions.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
      <div className="form-grid">
        <Field label="Reference"><input value={f.reference} onChange={e => setF({ ...f, reference: e.target.value })} /></Field>
        <Field label="Principal"><input type="number" value={f.principal} onChange={e => setF({ ...f, principal: Number(e.target.value) })} /></Field>
        <Field label="Monthly instalment"><input type="number" value={f.instalmentAmount} onChange={e => setF({ ...f, instalmentAmount: Number(e.target.value) })} /></Field>
        <Field label="First deduction period"><input type="date" value={f.startDate} onChange={e => setF({ ...f, startDate: e.target.value })} /></Field>
      </div>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={m.isPending} onClick={() => m.mutate()}>Save</button></div>
    </Modal>
  )
}
