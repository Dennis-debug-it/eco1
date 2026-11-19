import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useDepartments, useEmployeeOptions, useLegalEntities } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, FileLink, Loading, Modal, PageHead, Tabs } from '../components/ui'

type Tab = 'documents' | 'positions' | 'benefits' | 'assets'

export default function PeopleRecordsPage() {
  const { can } = useAuth()
  const tabs = [
    ...(can(P.HrDocuments) ? [{ id: 'documents' as Tab, label: 'Documents' }] : []),
    { id: 'positions' as Tab, label: 'Positions & headcount' },
    ...(can(P.HrBenefits) ? [{ id: 'benefits' as Tab, label: 'Medical cover' }] : []),
    ...(can(P.HrAssets) ? [{ id: 'assets' as Tab, label: 'Assets' }] : []),
  ]
  const [tab, setTab] = useState<Tab>(tabs[0].id)
  return (
    <>
      <PageHead title="People records" sub="Contracts and documents with expiry tracking, budgeted positions, medical cover and company property." />
      <Tabs value={tab} onChange={setTab} tabs={tabs} />
      {tab === 'documents' && <Documents />}
      {tab === 'positions' && <Positions />}
      {tab === 'benefits' && <Benefits />}
      {tab === 'assets' && <Assets />}
    </>
  )
}

type Doc = { id: string; employeeId: string; employee: string; employeeNumber: string; category: string; title: string; storedFileId: string; issuedOn?: string; expiresOn?: string; version: number; visibleToEmployee: boolean }

function Documents() {
  const qc = useQueryClient()
  const emps = useEmployeeOptions()
  const [expiring, setExpiring] = useState(false)
  const q = useQuery({ queryKey: ['documents', expiring], queryFn: () => api.get<Doc[]>(`/api/payroll/documents${expiring ? '?expiringWithinDays=60' : ''}`) })
  const [f, setF] = useState({ employeeId: '', category: 'Contract', title: '', issuedOn: '', expiresOn: '', visibleToEmployee: true })
  const [file, setFile] = useState<File | null>(null)
  const upload = useMutation({
    mutationFn: () => { const form = new FormData(); form.append('file', file!); form.append('meta', JSON.stringify({ ...f, employeeId: f.employeeId || emps.data?.[0]?.id, issuedOn: f.issuedOn || null, expiresOn: f.expiresOn || null })); return api.upload('/api/payroll/documents', form) },
    onSuccess: () => { setFile(null); setF({ ...f, title: '' }); qc.invalidateQueries({ queryKey: ['documents'] }) },
  })
  const del = useMutation({ mutationFn: (id: string) => api.del(`/api/payroll/documents/${id}`), onSuccess: () => qc.invalidateQueries({ queryKey: ['documents'] }) })
  const soon = new Date(Date.now() + 60 * 864e5).toISOString().slice(0, 10)
  return (
    <div className="card">
      <div className="toolbar">
        <select value={f.employeeId || emps.data?.[0]?.id} onChange={e => setF({ ...f, employeeId: e.target.value })} aria-label="Employee">{emps.data?.map(e => <option key={e.id} value={e.id}>{e.employeeNumber} {e.fullName}</option>)}</select>
        <select value={f.category} onChange={e => setF({ ...f, category: e.target.value })} aria-label="Category">{['Contract', 'Identification', 'KraPinCertificate', 'AcademicCertificate', 'ProfessionalCertificate', 'Medical', 'Disciplinary', 'Other'].map(c => <option key={c}>{c}</option>)}</select>
        <input placeholder="Title" value={f.title} onChange={e => setF({ ...f, title: e.target.value })} aria-label="Title" />
        <label className="small muted">Expires</label><input type="date" value={f.expiresOn} onChange={e => setF({ ...f, expiresOn: e.target.value })} aria-label="Expires" />
        <label className="small"><input type="checkbox" checked={f.visibleToEmployee} onChange={e => setF({ ...f, visibleToEmployee: e.target.checked })} /> Visible to employee</label>
        <input type="file" onChange={e => setFile(e.target.files?.[0] ?? null)} aria-label="File" />
        <button className="btn primary" disabled={!file || !f.title || upload.isPending} onClick={() => upload.mutate()}>Upload</button>
      </div>
      <label className="field inline"><input type="checkbox" checked={expiring} onChange={e => setExpiring(e.target.checked)} /> Expiring within 60 days</label>
      <ErrorBox error={q.error ?? upload.error ?? del.error} />
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No documents.</Empty> : <div className="table-wrap"><table><thead><tr><th>Employee</th><th>Category</th><th>Title</th><th>Version</th><th>Expires</th><th>Employee can see</th><th /></tr></thead>
        <tbody>{q.data.map(d => <tr key={d.id}><td>{d.employeeNumber} {d.employee}</td><td>{d.category}</td><td>{d.title}</td><td className="num">v{d.version}</td><td>{d.expiresOn ? (d.expiresOn <= soon ? <Badge status="Warning">{fmt.date(d.expiresOn)}</Badge> : fmt.date(d.expiresOn)) : '—'}</td><td>{d.visibleToEmployee ? 'Yes' : 'No'}</td><td className="actions"><FileLink id={d.storedFileId} /><button className="btn sm danger" onClick={() => del.mutate(d.id)}>Remove</button></td></tr>)}</tbody></table></div>}
      <p className="small muted">Disciplinary documents are never shown to the employee in self-service. Removed documents are kept for the audit trail.</p>
    </div>
  )
}

type Position = { id: string; code: string; title: string; grade?: string; department?: string; departmentId?: string; budgetedHeadcount: number; filled: number; openRequisitions: number; vacancies: number; overBudget: boolean; salaryMin?: number; salaryMax?: number; isActive: boolean }

function Positions() {
  const { can } = useAuth()
  const qc = useQueryClient()
  const les = useLegalEntities(); const depts = useDepartments()
  const q = useQuery({ queryKey: ['positions'], queryFn: () => api.get<Position[]>('/api/payroll/positions') })
  const chart = useQuery({ queryKey: ['org-chart'], queryFn: () => api.get<{ id: string; name: string; jobTitle?: string; reportingManagerId?: string }[]>('/api/payroll/positions/org-chart') })
  const [edit, setEdit] = useState<Partial<Position> | null>(null)
  const save = useMutation({ mutationFn: (p: Partial<Position>) => p.id ? api.put(`/api/payroll/positions/${p.id}`, p) : api.post('/api/payroll/positions', { ...p, legalEntityId: les.data?.[0]?.id }), onSuccess: () => { qc.invalidateQueries({ queryKey: ['positions'] }); setEdit(null) } })
  const totals = (q.data ?? []).reduce((a, p) => ({ budget: a.budget + p.budgetedHeadcount, filled: a.filled + p.filled, vacant: a.vacant + p.vacancies }), { budget: 0, filled: 0, vacant: 0 })
  const roots = (chart.data ?? []).filter(e => !e.reportingManagerId || !chart.data!.some(x => x.id === e.reportingManagerId))
  const Node = ({ id, depth }: { id: string; depth: number }): React.ReactElement => {
    const e = chart.data!.find(x => x.id === id)!
    const kids = chart.data!.filter(x => x.reportingManagerId === id)
    return <div style={{ marginLeft: depth ? 18 : 0, borderLeft: depth ? '1px solid var(--border)' : undefined, paddingLeft: depth ? 10 : 0 }}><div style={{ padding: '3px 0' }}><b>{e.name}</b> <span className="muted small">{e.jobTitle}</span>{kids.length > 0 && <span className="muted small"> · {kids.length} report{kids.length > 1 ? 's' : ''}</span>}</div>{kids.map(k => <Node key={k.id} id={k.id} depth={depth + 1} />)}</div>
  }
  return (
    <div className="grid c2">
      <div className="card" style={{ gridColumn: '1 / -1' }}>
        <div className="toolbar"><h2 style={{ margin: 0 }}>Positions</h2><span className="muted small">Budget {totals.budget} · filled {totals.filled} · vacant {totals.vacant}</span><span style={{ flex: 1 }} />{can(P.OrganisationManage) && <button className="btn primary" onClick={() => setEdit({ budgetedHeadcount: 1, isActive: true })}>New position</button>}</div>
        {q.isLoading ? <Loading /> : <div className="table-wrap"><table><thead><tr><th>Code</th><th>Title</th><th>Department</th><th>Grade</th><th className="num">Budget</th><th className="num">Filled</th><th className="num">Vacant</th><th className="num">Open reqs</th>{can(P.SalaryRead) && <th>Salary band</th>}<th /></tr></thead>
          <tbody>{q.data?.map(p => <tr key={p.id}><td className="mono">{p.code}</td><td>{p.title}</td><td>{p.department}</td><td>{p.grade}</td><td className="num">{p.budgetedHeadcount}</td><td className="num">{p.overBudget ? <Badge status="Error">{p.filled}</Badge> : p.filled}</td><td className="num">{p.vacancies}</td><td className="num">{p.openRequisitions || ''}</td>{can(P.SalaryRead) && <td>{p.salaryMin || p.salaryMax ? `${fmt.int(p.salaryMin)} – ${fmt.int(p.salaryMax)}` : '—'}</td>}<td>{can(P.OrganisationManage) && <button className="btn sm" onClick={() => setEdit(p)}>Edit</button>}</td></tr>)}</tbody></table></div>}
      </div>
      <div className="card" style={{ gridColumn: '1 / -1' }}><h2>Organisation chart</h2>{chart.isLoading ? <Loading /> : roots.map(r => <Node key={r.id} id={r.id} depth={0} />)}</div>
      {edit && <Modal title={edit.id ? `Edit ${edit.code}` : 'New position'} onClose={() => setEdit(null)}>
        <ErrorBox error={save.error} />
        <div className="form-grid">
          <Field label="Code *"><input value={edit.code ?? ''} disabled={!!edit.id} onChange={e => setEdit({ ...edit, code: e.target.value })} /></Field>
          <Field label="Title *"><input value={edit.title ?? ''} onChange={e => setEdit({ ...edit, title: e.target.value })} /></Field>
          <Field label="Department"><select value={edit.departmentId ?? ''} onChange={e => setEdit({ ...edit, departmentId: e.target.value || undefined })}><option value="">—</option>{depts.data?.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}</select></Field>
          <Field label="Grade"><input value={edit.grade ?? ''} onChange={e => setEdit({ ...edit, grade: e.target.value })} /></Field>
          <Field label="Budgeted headcount"><input type="number" min={0} value={edit.budgetedHeadcount ?? 1} onChange={e => setEdit({ ...edit, budgetedHeadcount: Number(e.target.value) })} /></Field>
          <Field label="Salary min"><input type="number" value={edit.salaryMin ?? ''} onChange={e => setEdit({ ...edit, salaryMin: e.target.value ? Number(e.target.value) : undefined })} /></Field>
          <Field label="Salary max"><input type="number" value={edit.salaryMax ?? ''} onChange={e => setEdit({ ...edit, salaryMax: e.target.value ? Number(e.target.value) : undefined })} /></Field>
        </div>
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setEdit(null)}>Cancel</button><button className="btn primary" disabled={!edit.code || !edit.title || save.isPending} onClick={() => save.mutate(edit)}>Save</button></div>
      </Modal>}
    </div>
  )
}

function Benefits() {
  const qc = useQueryClient()
  const les = useLegalEntities(); const emps = useEmployeeOptions()
  const schemes = useQuery({ queryKey: ['schemes'], queryFn: () => api.get<{ id: string; name: string; provider: string; policyNumber?: string; premiumByCover: Record<string, number>; employeeSharePercent: number }[]>('/api/payroll/benefits/schemes') })
  const enrol = useQuery({ queryKey: ['enrolments-all'], queryFn: () => api.get<{ id: string; employee: string; employeeNumber: string; scheme: string; cover: string; startDate: string; endDate?: string; membershipNumber?: string; premiumByCover: Record<string, number>; employeeSharePercent: number }[]>('/api/payroll/benefits/enrolments') })
  const [f, setF] = useState({ employeeId: '', medicalSchemeId: '', cover: 'M', startDate: new Date().toISOString().slice(0, 8) + '01', membershipNumber: '' })
  const add = useMutation({ mutationFn: () => api.post('/api/payroll/benefits/enrolments', { ...f, employeeId: f.employeeId || emps.data?.[0]?.id, medicalSchemeId: f.medicalSchemeId || schemes.data?.[0]?.id, coveredDependantIds: [] }), onSuccess: () => qc.invalidateQueries({ queryKey: ['enrolments-all'] }) })
  const end = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/benefits/enrolments/${id}/end?endDate=${new Date().toISOString().slice(0, 10)}`), onSuccess: () => qc.invalidateQueries({ queryKey: ['enrolments-all'] }) })
  const [scheme, setScheme] = useState({ name: '', provider: '', policyNumber: '', m: 3500, m1: 6000, m2: 8000, m3: 9500, share: 25 })
  const addScheme = useMutation({ mutationFn: () => api.post('/api/payroll/benefits/schemes', { legalEntityId: les.data?.[0]?.id, name: scheme.name, provider: scheme.provider, policyNumber: scheme.policyNumber, premiumByCover: { M: scheme.m, 'M+1': scheme.m1, 'M+2': scheme.m2, 'M+3': scheme.m3 }, employeeSharePercent: scheme.share }), onSuccess: () => qc.invalidateQueries({ queryKey: ['schemes'] }) })
  const current = schemes.data?.find(s => s.id === (f.medicalSchemeId || schemes.data?.[0]?.id))
  return (
    <div className="grid c2">
      <div className="card"><h2>Schemes</h2>
        {!schemes.data?.length ? <Empty>No schemes.</Empty> : <table><tbody>{schemes.data.map(s => <tr key={s.id}><td><b>{s.name}</b><div className="muted small">{s.provider} · {s.policyNumber}</div></td><td className="small">{Object.entries(s.premiumByCover).map(([k, v]) => `${k} ${fmt.int(v)}`).join(' · ')}</td><td>Employee pays {s.employeeSharePercent}%</td></tr>)}</tbody></table>}
        <h3 style={{ marginTop: 12 }}>Add scheme</h3><ErrorBox error={addScheme.error} />
        <div className="form-grid"><Field label="Name"><input value={scheme.name} onChange={e => setScheme({ ...scheme, name: e.target.value })} /></Field><Field label="Insurer"><input value={scheme.provider} onChange={e => setScheme({ ...scheme, provider: e.target.value })} /></Field><Field label="Policy no."><input value={scheme.policyNumber} onChange={e => setScheme({ ...scheme, policyNumber: e.target.value })} /></Field><Field label="Employee share %"><input type="number" value={scheme.share} onChange={e => setScheme({ ...scheme, share: Number(e.target.value) })} /></Field>
          {(['m', 'm1', 'm2', 'm3'] as const).map((k, i) => <Field key={k} label={`Monthly premium ${['M', 'M+1', 'M+2', 'M+3'][i]}`}><input type="number" value={scheme[k]} onChange={e => setScheme({ ...scheme, [k]: Number(e.target.value) })} /></Field>)}</div>
        <button className="btn" disabled={!scheme.name || !scheme.provider} onClick={() => addScheme.mutate()}>Add scheme</button>
      </div>
      <div className="card"><h2>Enrolments</h2>
        <div className="toolbar"><select value={f.employeeId || emps.data?.[0]?.id} onChange={e => setF({ ...f, employeeId: e.target.value })} aria-label="Employee">{emps.data?.map(e => <option key={e.id} value={e.id}>{e.employeeNumber} {e.fullName}</option>)}</select>
          <select value={f.medicalSchemeId || schemes.data?.[0]?.id} onChange={e => setF({ ...f, medicalSchemeId: e.target.value })} aria-label="Scheme">{schemes.data?.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select>
          <select value={f.cover} onChange={e => setF({ ...f, cover: e.target.value })} aria-label="Cover">{Object.keys(current?.premiumByCover ?? { M: 0 }).map(c => <option key={c}>{c}</option>)}</select>
          <input type="date" value={f.startDate} onChange={e => setF({ ...f, startDate: e.target.value })} aria-label="Start" /><button className="btn primary" disabled={add.isPending} onClick={() => add.mutate()}>Enrol</button></div>
        <ErrorBox error={add.error ?? end.error} />
        {!enrol.data?.length ? <Empty>No enrolments.</Empty> : <table><thead><tr><th>Employee</th><th>Scheme</th><th>Cover</th><th className="num">Employee share / month</th><th>From</th><th /></tr></thead>
          <tbody>{enrol.data.map(e => <tr key={e.id}><td>{e.employeeNumber} {e.employee}</td><td>{e.scheme}</td><td>{e.cover}</td><td className="num">{fmt.num((e.premiumByCover[e.cover] ?? 0) * e.employeeSharePercent / 100)}</td><td>{fmt.date(e.startDate)}{e.endDate && ` – ${fmt.date(e.endDate)}`}</td><td>{!e.endDate && <button className="btn sm" onClick={() => end.mutate(e.id)}>End today</button>}</td></tr>)}</tbody></table>}
        <p className="small muted">The employee share is deducted by payroll each period and qualifies for insurance relief (15%, capped).</p>
      </div>
    </div>
  )
}

function Assets() {
  const qc = useQueryClient()
  const les = useLegalEntities(); const emps = useEmployeeOptions()
  const q = useQuery({ queryKey: ['assets'], queryFn: () => api.get<{ asset: { id: string; tag: string; name: string; category: string; serialNumber?: string; value: number; status: string }; assignment?: { holder: string; employeeNumber: string; issuedOn: string } }[]>('/api/payroll/assets') })
  const refresh = () => qc.invalidateQueries({ queryKey: ['assets'] })
  const [f, setF] = useState({ tag: '', name: '', category: 'IT', serialNumber: '', value: 0 })
  const add = useMutation({ mutationFn: () => api.post('/api/payroll/assets', { ...f, legalEntityId: les.data?.[0]?.id }), onSuccess: () => { setF({ tag: '', name: '', category: 'IT', serialNumber: '', value: 0 }); refresh() } })
  const [issue, setIssue] = useState<{ id: string; employeeId: string } | null>(null)
  const doIssue = useMutation({ mutationFn: () => api.post(`/api/payroll/assets/${issue!.id}/issue`, { employeeId: issue!.employeeId || emps.data?.[0]?.id, issuedOn: new Date().toISOString().slice(0, 10), condition: 'Good' }), onSuccess: () => { setIssue(null); refresh() } })
  const ret = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/assets/${id}/return`, { condition: 'Returned in good order' }), onSuccess: refresh })
  return (
    <div className="card">
      <div className="toolbar"><input placeholder="Tag" value={f.tag} onChange={e => setF({ ...f, tag: e.target.value })} style={{ width: 100 }} aria-label="Tag" /><input placeholder="Name" value={f.name} onChange={e => setF({ ...f, name: e.target.value })} aria-label="Name" /><input placeholder="Category" value={f.category} onChange={e => setF({ ...f, category: e.target.value })} style={{ width: 100 }} aria-label="Category" /><input placeholder="Serial" value={f.serialNumber} onChange={e => setF({ ...f, serialNumber: e.target.value })} style={{ width: 110 }} aria-label="Serial" /><input type="number" placeholder="Value" value={f.value || ''} onChange={e => setF({ ...f, value: Number(e.target.value) })} style={{ width: 100 }} aria-label="Value" /><button className="btn primary" disabled={!f.tag || !f.name || add.isPending} onClick={() => add.mutate()}>Add asset</button></div>
      <ErrorBox error={q.error ?? add.error ?? doIssue.error ?? ret.error} />
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No assets.</Empty> : <table><thead><tr><th>Tag</th><th>Asset</th><th>Serial</th><th className="num">Value</th><th>Status</th><th>Held by</th><th /></tr></thead>
        <tbody>{q.data.map(({ asset: a, assignment }) => <tr key={a.id}><td className="mono">{a.tag}</td><td>{a.name} <span className="muted small">{a.category}</span></td><td className="mono small">{a.serialNumber}</td><td className="num">{fmt.num(a.value)}</td><td><Badge status={a.status === 'InStock' ? 'Active' : a.status === 'Issued' ? 'Open' : 'Warning'}>{a.status}</Badge></td><td>{assignment ? `${assignment.employeeNumber} ${assignment.holder} since ${fmt.date(assignment.issuedOn)}` : '—'}</td>
          <td>{a.status === 'InStock' && <button className="btn sm" onClick={() => setIssue({ id: a.id, employeeId: '' })}>Issue</button>}{a.status === 'Issued' && <button className="btn sm" onClick={() => ret.mutate(a.id)}>Return</button>}</td></tr>)}</tbody></table>}
      {issue && <Modal title="Issue asset" onClose={() => setIssue(null)}><Field label="To employee"><select value={issue.employeeId || emps.data?.[0]?.id} onChange={e => setIssue({ ...issue, employeeId: e.target.value })}>{emps.data?.map(e => <option key={e.id} value={e.id}>{e.employeeNumber} {e.fullName}</option>)}</select></Field><p className="small muted">Unreturned property is listed on the employee's exit checklist and can be recovered from final dues.</p><div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn primary" onClick={() => doIssue.mutate()}>Issue</button></div></Modal>}
    </div>
  )
}
