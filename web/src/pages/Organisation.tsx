import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useBranches, useCostCentres, useDepartments, useLegalEntities, usePayrollGroups } from '../lib/lookups'
import type { LegalEntity, Lookup, PayrollPeriod } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, Tabs } from '../components/ui'

type Tab = 'entities' | 'branches' | 'departments' | 'cost-centres' | 'payroll-groups'

export default function OrganisationPage() {
  const { can } = useAuth()
  const [tab, setTab] = useState<Tab>('entities')
  const les = useLegalEntities()
  const [le, setLe] = useState('')
  const leId = le || les.data?.[0]?.id
  return (
    <>
      <PageHead title="Organisation" sub="Legal entities, branches, departments, cost centres and payroll groups" />
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'entities', label: 'Legal entities' }, { id: 'branches', label: 'Branches' }, { id: 'departments', label: 'Departments' }, { id: 'cost-centres', label: 'Cost centres' }, { id: 'payroll-groups', label: 'Payroll groups & periods' }]} />
      {tab !== 'entities' && les.data && les.data.length > 1 && <div className="toolbar"><select value={leId} onChange={e => setLe(e.target.value)} aria-label="Legal entity">{les.data.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></div>}
      {tab === 'entities' && <LegalEntities canEdit={can(P.OrganisationManage)} />}
      {tab === 'branches' && leId && <LookupTable path="branches" title="Branches" legalEntityId={leId} extra={[['location', 'Location']]} canEdit={can(P.OrganisationManage)} hook={useBranches} />}
      {tab === 'departments' && leId && <LookupTable path="departments" title="Departments" legalEntityId={leId} extra={[]} canEdit={can(P.OrganisationManage)} hook={useDepartments} />}
      {tab === 'cost-centres' && leId && <LookupTable path="cost-centres" title="Cost centres" legalEntityId={leId} extra={[['glSegment', 'GL segment']]} canEdit={can(P.OrganisationManage)} hook={useCostCentres} />}
      {tab === 'payroll-groups' && leId && <PayrollGroups legalEntityId={leId} canEdit={can(P.OrganisationManage)} />}
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
      <div className="page-head" style={{ marginBottom: 8 }}><h2>Legal entities</h2>{canEdit && <button className="btn primary" onClick={() => setEdit({ jurisdiction: 'KE', currencyCode: 'KES', financialYearStartMonth: 1, isActive: true, settings: { allowPreparerToApprove: false, requireReviewBeforeApproval: true, minimumApprovals: 1, salaryVarianceWarningPercent: 20, payslipFooter: 'This is a computer-generated payslip.' } })}>New legal entity</button>}</div>
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
        </div>
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
