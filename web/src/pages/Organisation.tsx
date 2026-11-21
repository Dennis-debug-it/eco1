import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useBankBranches, useBanks, useBranches, useCostCentres, useDepartments, useLegalEntities, usePayingAccounts, usePayrollGroups } from '../lib/lookups'
import type { LegalEntity, Lookup, PayingAccount, PayrollPeriod } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, Tabs } from '../components/ui'
import { BankPicker } from '../components/BankPicker'

type Tab = 'entities' | 'branches' | 'departments' | 'cost-centres' | 'payroll-groups' | 'paying-accounts' | 'banks'

export default function OrganisationPage() {
  const { can, user } = useAuth()
  const [tab, setTab] = useState<Tab>('entities')
  const les = useLegalEntities()
  const [le, setLe] = useState('')
  const leId = le || les.data?.[0]?.id
  return (
    <>
      <PageHead title="Organisation" sub="Legal entities, branches, departments, cost centres and payroll groups" />
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'entities', label: 'Legal entities' }, { id: 'branches', label: 'Branches' }, { id: 'departments', label: 'Departments' }, { id: 'cost-centres', label: 'Cost centres' }, { id: 'payroll-groups', label: 'Payroll groups & periods' }, { id: 'paying-accounts', label: 'Paying accounts' }, { id: 'banks', label: 'Banks' }]} />
      {tab !== 'entities' && tab !== 'banks' && les.data && les.data.length > 1 && <div className="toolbar"><select value={leId} onChange={e => setLe(e.target.value)} aria-label="Legal entity">{les.data.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></div>}
      {tab === 'entities' && <LegalEntities canEdit={can(P.OrganisationManage)} />}
      {tab === 'branches' && leId && <LookupTable path="branches" title="Branches" legalEntityId={leId} extra={[['location', 'Location']]} canEdit={can(P.OrganisationManage)} hook={useBranches} />}
      {tab === 'departments' && leId && <LookupTable path="departments" title="Departments" legalEntityId={leId} extra={[]} canEdit={can(P.OrganisationManage)} hook={useDepartments} />}
      {tab === 'cost-centres' && leId && <LookupTable path="cost-centres" title="Cost centres" legalEntityId={leId} extra={[['glSegment', 'GL segment']]} canEdit={can(P.OrganisationManage)} hook={useCostCentres} />}
      {tab === 'payroll-groups' && leId && <PayrollGroups legalEntityId={leId} canEdit={can(P.OrganisationManage)} />}
      {tab === 'paying-accounts' && leId && <PayingAccounts legalEntityId={leId} currency={les.data?.find(x => x.id === leId)?.currencyCode ?? 'KES'} canEdit={can(P.OrganisationManage)} />}
      {tab === 'banks' && <BankDirectory canEdit={can(P.OrganisationManage)} isPlatformAdmin={!!user?.roles.includes('PlatformAdministrator')} />}
    </>
  )
}

function LegalEntities({ canEdit }: { canEdit: boolean }) {
  const q = useLegalEntities(); const qc = useQueryClient()
  const [edit, setEdit] = useState<Partial<LegalEntity> | null>(null)
  const m = useMutation({
    mutationFn: (e: Partial<LegalEntity>) => e.id ? api.put(`/api/payroll/organisation/legal-entities/${e.id}`, e) : api.post('/api/payroll/organisation/legal-entities', e),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['legal-entities'] }); setEdit(null) },
  })
  if (q.isLoading) return <Loading />
  return (
    <div className="card">
      <div className="page-head" style={{ marginBottom: 8 }}><h2>Legal entities</h2>{canEdit && <button className="btn primary" onClick={() => setEdit({ jurisdiction: 'KE', currencyCode: 'KES', financialYearStartMonth: 1, isActive: true, settings: { allowPreparerToApprove: false, requireReviewBeforeApproval: true, minimumApprovals: 1, salaryVarianceWarningPercent: 20, payslipFooter: 'This is a computer-generated payslip.', standardMonthlyHours: 195, standardMonthlyWorkingDays: 22, overtimeMultiplier: 1.5, restDayOvertimeMultiplier: 2, notifyEmployeesOnPayslip: true, allowBankDetailSelfVerification: false } })}>New legal entity</button>}</div>
      <table><thead><tr><th>Name</th><th>Registration</th><th>KRA PIN</th><th>NSSF employer</th><th>SHIF employer</th><th>Currency</th><th>Preparer may approve</th><th>Variance warning</th><th /></tr></thead>
        <tbody>{q.data!.map(e => <tr key={e.id}><td>{e.name}</td><td className="mono">{e.registrationNumber}</td><td className="mono">{e.taxPin}</td><td className="mono">{e.nssfEmployerNumber}</td><td className="mono">{e.shifEmployerNumber}</td><td>{e.currencyCode}</td><td>{e.settings.allowPreparerToApprove ? <Badge status="Warning">Yes</Badge> : 'No'}</td><td>{e.settings.salaryVarianceWarningPercent}%</td><td>{canEdit && <button className="btn sm" onClick={() => setEdit(e)}>Edit</button>}</td></tr>)}</tbody></table>
      {edit && <Modal title={edit.id ? 'Edit legal entity' : 'New legal entity'} onClose={() => setEdit(null)} width={720}>
        <ErrorBox error={m.error} />
        <div className="form-grid">
          {([['name', 'Name *'], ['registrationNumber', 'Registration number *'], ['taxPin', 'KRA PIN *'], ['nssfEmployerNumber', 'NSSF employer number'], ['shifEmployerNumber', 'SHIF employer number'], ['nitaRegistrationNumber', 'NITA registration'], ['address', 'Address'], ['email', 'Email'], ['phone', 'Phone'], ['currencyCode', 'Currency']] as [keyof LegalEntity, string][]).map(([k, l]) => <Field key={k} label={l}><input value={(edit[k] as string) ?? ''} onChange={ev => setEdit({ ...edit, [k]: ev.target.value })} /></Field>)}
        </div>
        <h3>Payroll controls</h3>
        <label className="field inline"><input type="checkbox" checked={!!edit.settings?.allowPreparerToApprove} onChange={ev => setEdit({ ...edit, settings: { ...edit.settings!, allowPreparerToApprove: ev.target.checked } })} /> Allow the preparer to approve their own payroll (not recommended)</label>
        <div className="form-grid">
          <Field label="Net pay variance warning (%)"><input type="number" value={edit.settings?.salaryVarianceWarningPercent ?? 20} onChange={ev => setEdit({ ...edit, settings: { ...edit.settings!, salaryVarianceWarningPercent: Number(ev.target.value) } })} /></Field>
          <Field label="Payslip footer"><input value={edit.settings?.payslipFooter ?? ''} onChange={ev => setEdit({ ...edit, settings: { ...edit.settings!, payslipFooter: ev.target.value } })} /></Field>
          <Field label="Standard hours / month (overtime rate)"><input type="number" value={edit.settings?.standardMonthlyHours ?? 195} onChange={ev => setEdit({ ...edit, settings: { ...edit.settings!, standardMonthlyHours: Number(ev.target.value) } })} /></Field>
          <Field label="Working days / month (absence rate)"><input type="number" value={edit.settings?.standardMonthlyWorkingDays ?? 22} onChange={ev => setEdit({ ...edit, settings: { ...edit.settings!, standardMonthlyWorkingDays: Number(ev.target.value) } })} /></Field>
          <Field label="Overtime multiplier"><input type="number" step="0.1" value={edit.settings?.overtimeMultiplier ?? 1.5} onChange={ev => setEdit({ ...edit, settings: { ...edit.settings!, overtimeMultiplier: Number(ev.target.value) } })} /></Field>
          <Field label="Rest-day / holiday multiplier"><input type="number" step="0.1" value={edit.settings?.restDayOvertimeMultiplier ?? 2} onChange={ev => setEdit({ ...edit, settings: { ...edit.settings!, restDayOvertimeMultiplier: Number(ev.target.value) } })} /></Field>
        </div>
        <label className="field inline"><input type="checkbox" checked={!!edit.settings?.allowBankDetailSelfVerification} onChange={ev => setEdit({ ...edit, settings: { ...edit.settings!, allowBankDetailSelfVerification: ev.target.checked } })} /> Let the person who enters an employee's bank details also verify them (not recommended — only for one-person HR teams)</label>
        <label className="field inline"><input type="checkbox" checked={edit.settings?.notifyEmployeesOnPayslip ?? true} onChange={ev => setEdit({ ...edit, settings: { ...edit.settings!, notifyEmployeesOnPayslip: ev.target.checked } })} /> Notify employees by email/WhatsApp when payslips are issued</label>
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setEdit(null)}>Cancel</button><button className="btn primary" disabled={m.isPending} onClick={() => m.mutate(edit)}>Save</button></div>
      </Modal>}
    </div>
  )
}

function LookupTable({ path, title, legalEntityId, extra, canEdit, hook }: { path: string; title: string; legalEntityId: string; extra: [string, string][]; canEdit: boolean; hook: (le?: string) => { data?: Lookup[]; isLoading: boolean } }) {
  const q = hook(legalEntityId); const qc = useQueryClient()
  const [edit, setEdit] = useState<Partial<Lookup> | null>(null)
  const key = path === 'cost-centres' ? 'cost-centres' : path
  const m = useMutation({
    mutationFn: (e: Partial<Lookup>) => e.id ? api.put(`/api/payroll/organisation/${path}/${e.id}`, e) : api.post(`/api/payroll/organisation/${path}`, { ...e, legalEntityId }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: [key] }); setEdit(null) },
  })
  const del = useMutation({ mutationFn: (id: string) => api.del(`/api/payroll/organisation/${path}/${id}`), onSuccess: () => qc.invalidateQueries({ queryKey: [key] }) })
  if (q.isLoading) return <Loading />
  return (
    <div className="card">
      <div className="page-head" style={{ marginBottom: 8 }}><h2>{title}</h2>{canEdit && <button className="btn primary" onClick={() => setEdit({ isActive: true })}>New</button>}</div>
      <ErrorBox error={del.error} />
      {!q.data?.length ? <Empty>None configured.</Empty> : <table><thead><tr><th>Code</th><th>Name</th>{extra.map(([k, l]) => <th key={k}>{l}</th>)}<th>Active</th><th /></tr></thead>
        <tbody>{q.data.map(r => <tr key={r.id}><td className="mono">{r.code}</td><td>{r.name}</td>{extra.map(([k]) => <td key={k}>{String(r[k] ?? '')}</td>)}<td>{r.isActive ? 'Yes' : 'No'}</td><td className="actions">{canEdit && <><button className="btn sm" onClick={() => setEdit(r)}>Edit</button><button className="btn sm danger" onClick={() => { if (confirm(`Delete ${r.name}?`)) del.mutate(r.id) }}>Delete</button></>}</td></tr>)}</tbody></table>}
      {edit && <Modal title={edit.id ? `Edit ${title.toLowerCase()}` : `New ${title.toLowerCase().replace(/s$/, '')}`} onClose={() => setEdit(null)}>
        <ErrorBox error={m.error} />
        <Field label="Code *"><input value={edit.code ?? ''} onChange={e => setEdit({ ...edit, code: e.target.value })} /></Field>
        <Field label="Name *"><input value={edit.name ?? ''} onChange={e => setEdit({ ...edit, name: e.target.value })} /></Field>
        {extra.map(([k, l]) => <Field key={k} label={l}><input value={String(edit[k] ?? '')} onChange={e => setEdit({ ...edit, [k]: e.target.value })} /></Field>)}
        <label className="field inline"><input type="checkbox" checked={!!edit.isActive} onChange={e => setEdit({ ...edit, isActive: e.target.checked })} /> Active</label>
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setEdit(null)}>Cancel</button><button className="btn primary" disabled={m.isPending || !edit.code || !edit.name} onClick={() => m.mutate(edit)}>Save</button></div>
      </Modal>}
    </div>
  )
}

function PayrollGroups({ legalEntityId, canEdit }: { legalEntityId: string; canEdit: boolean }) {
  const q = usePayrollGroups(legalEntityId); const qc = useQueryClient()
  const [sel, setSel] = useState('')
  const gId = sel || q.data?.[0]?.id
  const periods = useQuery({ queryKey: ['periods', gId], queryFn: () => api.get<PayrollPeriod[]>(`/api/payroll/organisation/payroll-groups/${gId}/periods`), enabled: !!gId })
  const [year, setYear] = useState(new Date().getFullYear() + 1)
  const gen = useMutation({ mutationFn: () => api.post(`/api/payroll/organisation/payroll-groups/${gId}/periods/generate`, { year, payDay: q.data?.find(g => g.id === gId)?.payDay ?? 28 }), onSuccess: () => qc.invalidateQueries({ queryKey: ['periods', gId] }) })
  const [edit, setEdit] = useState<Record<string, unknown> | null>(null)
  const m = useMutation({ mutationFn: (e: Record<string, unknown>) => e.id ? api.put(`/api/payroll/organisation/payroll-groups/${e.id}`, e) : api.post('/api/payroll/organisation/payroll-groups', { ...e, legalEntityId }), onSuccess: () => { qc.invalidateQueries({ queryKey: ['payroll-groups'] }); setEdit(null) } })
  if (q.isLoading) return <Loading />
  return (
    <div className="grid c2">
      <div className="card"><div className="page-head" style={{ marginBottom: 8 }}><h2>Payroll groups</h2>{canEdit && <button className="btn primary" onClick={() => setEdit({ frequency: 'Monthly', payDay: 28, isActive: true })}>New group</button>}</div>
        <table><thead><tr><th>Code</th><th>Name</th><th>Frequency</th><th className="num">Pay day</th><th /></tr></thead>
          <tbody>{q.data?.map(g => <tr key={g.id} className={`clickable`} onClick={() => setSel(g.id)} style={g.id === gId ? { fontWeight: 600 } : undefined}><td className="mono">{g.code}</td><td>{g.name}</td><td>{g.frequency}</td><td className="num">{g.payDay}</td><td>{canEdit && <button className="btn sm" onClick={e => { e.stopPropagation(); setEdit(g) }}>Edit</button>}</td></tr>)}</tbody></table></div>
      <div className="card"><div className="page-head" style={{ marginBottom: 8 }}><h2>Periods — {q.data?.find(g => g.id === gId)?.name}</h2>{canEdit && <div className="actions"><input type="number" value={year} onChange={e => setYear(Number(e.target.value))} style={{ width: 90 }} aria-label="Year" /><button className="btn" onClick={() => gen.mutate()} disabled={gen.isPending}>Generate periods</button></div>}</div>
        <ErrorBox error={gen.error} />
        {!periods.data?.length ? <Empty>No periods. Generate a year of monthly periods.</Empty> : <table><thead><tr><th>Period</th><th>From</th><th>To</th><th>Pay date</th><th>Status</th></tr></thead>
          <tbody>{periods.data.map(p => <tr key={p.id}><td>{p.name}</td><td>{fmt.date(p.periodStart)}</td><td>{fmt.date(p.periodEnd)}</td><td>{fmt.date(p.payDate)}</td><td><Badge status={p.status} /></td></tr>)}</tbody></table>}</div>
      {edit && <Modal title={edit.id ? 'Edit payroll group' : 'New payroll group'} onClose={() => setEdit(null)}>
        <ErrorBox error={m.error} />
        <Field label="Code *"><input value={String(edit.code ?? '')} onChange={e => setEdit({ ...edit, code: e.target.value })} /></Field>
        <Field label="Name *"><input value={String(edit.name ?? '')} onChange={e => setEdit({ ...edit, name: e.target.value })} /></Field>
        <Field label="Frequency"><select value={String(edit.frequency)} onChange={e => setEdit({ ...edit, frequency: e.target.value })}>{['Monthly', 'SemiMonthly', 'Weekly'].map(f => <option key={f}>{f}</option>)}</select></Field>
        <Field label="Pay day of month"><input type="number" min={1} max={31} value={Number(edit.payDay)} onChange={e => setEdit({ ...edit, payDay: Number(e.target.value) })} /></Field>
        <Field label="Description"><input value={String(edit.description ?? '')} onChange={e => setEdit({ ...edit, description: e.target.value })} /></Field>
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setEdit(null)}>Cancel</button><button className="btn primary" disabled={m.isPending} onClick={() => m.mutate(edit)}>Save</button></div>
      </Modal>}
    </div>
  )
}

function PayingAccounts({ legalEntityId, currency, canEdit }: { legalEntityId: string; currency: string; canEdit: boolean }) {
  const q = usePayingAccounts(legalEntityId); const qc = useQueryClient()
  const [add, setAdd] = useState<Record<string, unknown> | null>(null)
  const done = () => { qc.invalidateQueries({ queryKey: ['paying-accounts'] }); setAdd(null) }
  const create = useMutation({ mutationFn: (a: Record<string, unknown>) => api.post('/api/payroll/organisation/paying-accounts', { ...a, legalEntityId }), onSuccess: done })
  const update = useMutation({ mutationFn: (a: PayingAccount) => api.put(`/api/payroll/organisation/paying-accounts/${a.id}`, a), onSuccess: done })
  if (q.isLoading) return <Loading />
  return (
    <div className="card">
      <div className="page-head" style={{ marginBottom: 8 }}><h2>Paying accounts</h2>{canEdit && <button className="btn primary" onClick={() => setAdd({ currencyCode: currency, isDefault: !q.data?.length })}>Add account</button>}</div>
      <p className="muted small">The company accounts that salary payments are debited from. A bank's own bulk format (KCB, Equity, Co-op) must be debited from an account at that bank; the default is used otherwise.</p>
      <ErrorBox error={update.error} />
      {!q.data?.length ? <Empty>No paying account. Bank payment files can't be generated until one is added.</Empty> : (
        <table><thead><tr><th>Label</th><th>Bank</th><th>Branch</th><th>Account name</th><th>Account number</th><th>Currency</th><th>Default</th><th>Active</th><th /></tr></thead>
          <tbody>{q.data.map(a => <tr key={a.id}><td>{a.label}</td><td>{a.bankName} <span className="muted">{a.bankCode}</span></td><td>{a.branchName} <span className="muted">{a.branchCode}</span></td><td>{a.accountName}</td><td className="mono">{a.accountNumber}</td><td>{a.currencyCode}</td><td>{a.isDefault ? 'Yes' : ''}</td><td>{a.isActive ? 'Yes' : 'No'}</td>
            <td className="actions">{canEdit && <>{a.isActive && !a.isDefault && <button className="btn sm" onClick={() => update.mutate({ ...a, isDefault: true })}>Make default</button>}<button className="btn sm" onClick={() => update.mutate({ ...a, isActive: !a.isActive, isDefault: false })}>{a.isActive ? 'Deactivate' : 'Reactivate'}</button></>}</td></tr>)}</tbody></table>)}
      {add && <Modal title="Add paying account" onClose={() => setAdd(null)}>
        <ErrorBox error={create.error} />
        <div className="form-grid">
          <Field label="Label *"><input value={String(add.label ?? '')} placeholder="Salaries account (KCB)" onChange={e => setAdd({ ...add, label: e.target.value })} /></Field>
          <BankPicker bankCode={add.bankCode as string | undefined} branchCode={add.branchCode as string | undefined} onChange={(bankCode, branchCode) => setAdd({ ...add, bankCode, branchCode })} />
          <Field label="Account name *"><input value={String(add.accountName ?? '')} onChange={e => setAdd({ ...add, accountName: e.target.value })} /></Field>
          <Field label="Account number *"><input value={String(add.accountNumber ?? '')} inputMode="numeric" onChange={e => setAdd({ ...add, accountNumber: e.target.value })} /></Field>
          <Field label="Currency"><input value={String(add.currencyCode ?? '')} onChange={e => setAdd({ ...add, currencyCode: e.target.value })} /></Field>
        </div>
        <label className="field inline"><input type="checkbox" checked={!!add.isDefault} onChange={e => setAdd({ ...add, isDefault: e.target.checked })} /> Default paying account</label>
        <p className="muted small">The number can't be changed later: to switch accounts, add the new one and deactivate the old, so past payment batches keep showing the account they were debited from.</p>
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setAdd(null)}>Cancel</button><button className="btn primary" disabled={create.isPending} onClick={() => create.mutate(add)}>Save</button></div>
      </Modal>}
    </div>
  )
}

function BankDirectory({ canEdit, isPlatformAdmin }: { canEdit: boolean; isPlatformAdmin: boolean }) {
  const banks = useBanks(); const qc = useQueryClient()
  const [sel, setSel] = useState('')
  const branches = useBankBranches(sel || undefined)
  const [bank, setBank] = useState<{ code: string; name: string; swiftCode?: string; platform: boolean } | null>(null)
  const [csv, setCsv] = useState<string | null>(null)
  const [platform, setPlatform] = useState(false)
  const refresh = () => { qc.invalidateQueries({ queryKey: ['banks'] }); qc.invalidateQueries({ queryKey: ['bank-branches'] }) }
  const save = useMutation({ mutationFn: (b: NonNullable<typeof bank>) => api.post('/api/payroll/organisation/banks', b), onSuccess: () => { refresh(); setBank(null) } })
  const imp = useMutation({ mutationFn: () => api.post<{ added: number; updated: number; errors: string[] }>('/api/payroll/organisation/banks/import-branches', { csv, platform }), onSuccess: refresh })
  if (banks.isLoading) return <Loading />
  return (
    <div className="grid c2">
      <div className="card">
        <div className="page-head" style={{ marginBottom: 8 }}><h2>Banks</h2>{canEdit && <div className="actions"><button className="btn" onClick={() => { imp.reset(); setCsv('') }}>Import branches</button><button className="btn primary" onClick={() => setBank({ code: '', name: '', platform: false })}>Add bank</button></div>}</div>
        <p className="muted small">Bank and branch codes route payments. Employee and company accounts must use a listed bank; once a bank's branches are loaded, its branch codes are checked too. Check the shared list against the Kenya Bankers Association's current codes before going live.</p>
        <table><thead><tr><th>Code</th><th>Bank</th><th className="num">Branches</th><th>Source</th></tr></thead>
          <tbody>{banks.data?.map(b => <tr key={b.code} className="clickable" onClick={() => setSel(b.code)} style={b.code === sel ? { fontWeight: 600 } : undefined}><td className="mono">{b.code}</td><td>{b.name}</td><td className="num">{b.branchCount || '—'}</td><td>{b.isPlatform ? 'Shared' : 'Yours'}</td></tr>)}</tbody></table>
      </div>
      <div className="card"><h2>Branches{sel && ` — ${banks.data?.find(b => b.code === sel)?.name}`}</h2>
        {!sel ? <Empty>Select a bank to see its branches.</Empty> : !branches.data?.length ? <Empty>No branches loaded. Branch codes for this bank aren't checked until you import them.</Empty> : (
          <table><thead><tr><th>Code</th><th>Branch</th><th>Source</th></tr></thead>
            <tbody>{branches.data.map(b => <tr key={b.code}><td className="mono">{b.code}</td><td>{b.name}</td><td>{b.isPlatform ? 'Shared' : 'Yours'}</td></tr>)}</tbody></table>)}
      </div>
      {bank && <Modal title="Add or rename a bank" onClose={() => setBank(null)}>
        <ErrorBox error={save.error} />
        <Field label="Clearing code *"><input value={bank.code} inputMode="numeric" onChange={e => setBank({ ...bank, code: e.target.value })} /></Field>
        <Field label="Name *"><input value={bank.name} onChange={e => setBank({ ...bank, name: e.target.value })} /></Field>
        <Field label="SWIFT code"><input value={bank.swiftCode ?? ''} onChange={e => setBank({ ...bank, swiftCode: e.target.value })} /></Field>
        {isPlatformAdmin && <label className="field inline"><input type="checkbox" checked={bank.platform} onChange={e => setBank({ ...bank, platform: e.target.checked })} /> Shared with every tenant</label>}
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setBank(null)}>Cancel</button><button className="btn primary" disabled={save.isPending || !bank.code || !bank.name} onClick={() => save.mutate(bank)}>Save</button></div>
      </Modal>}
      {csv !== null && <Modal title="Import branches" onClose={() => setCsv(null)} width={640}>
        <ErrorBox error={imp.error} />
        <p className="muted small">Paste CSV with one branch per line: <span className="mono">bank code, branch code, branch name</span>. A header row is skipped. Existing branches are renamed; nothing is removed.</p>
        <textarea rows={10} className="mono" value={csv} placeholder={'bank_code,branch_code,branch_name\n68,001,Head Office'} onChange={e => setCsv(e.target.value)} style={{ width: '100%' }} />
        {isPlatformAdmin && <label className="field inline"><input type="checkbox" checked={platform} onChange={e => setPlatform(e.target.checked)} /> Shared with every tenant</label>}
        {imp.data && <div className="card" style={{ marginTop: 8 }}>
          <p>{imp.data.added} added, {imp.data.updated} updated{imp.data.errors.length ? `, ${imp.data.errors.length} skipped:` : '.'}</p>
          {imp.data.errors.length > 0 && <ul className="small">{imp.data.errors.slice(0, 20).map(e => <li key={e}>{e}</li>)}</ul>}
        </div>}
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setCsv(null)}>Close</button><button className="btn primary" disabled={imp.isPending || !csv.trim()} onClick={() => imp.mutate()}>Import</button></div>
      </Modal>}
    </div>
  )
}
