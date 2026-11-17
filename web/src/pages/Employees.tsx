import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
import { api, fmt } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { byId, useBranches, useCostCentres, useDepartments, useLegalEntities, usePayrollGroups } from '../lib/lookups'
import type { Employee, EmployeeListItem, Page } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead } from '../components/ui'

const employmentTypes = ['Permanent', 'Contract', 'Casual', 'Intern', 'Probation', 'Expatriate']
const statuses = ['Active', 'OnLeave', 'Suspended', 'Terminated', 'Resigned', 'Retired', 'Deceased']

export default function EmployeesPage() {
  const { can } = useAuth()
  const nav = useNavigate()
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState('')
  const [dept, setDept] = useState('')
  const [offset, setOffset] = useState(0)
  const [showNew, setShowNew] = useState(false)
  const limit = 50
  const depts = useDepartments()
  const params = new URLSearchParams({ offset: String(offset), limit: String(limit) })
  if (search) params.set('search', search)
  if (status) params.set('status', status)
  if (dept) params.set('departmentId', dept)
  const q = useQuery({ queryKey: ['employees', params.toString()], queryFn: () => api.get<Page<EmployeeListItem>>(`/api/payroll/employees?${params}`) })

  return (
    <>
      <PageHead title="Employees" sub={q.data ? `${fmt.int(q.data.total)} employee records` : undefined}>
        {can(P.EmployeesWrite) && <button className="btn primary" onClick={() => setShowNew(true)}>New employee</button>}
      </PageHead>
      <div className="card">
        <div className="toolbar">
          <input placeholder="Search name, number or title" value={search} onChange={e => { setSearch(e.target.value); setOffset(0) }} aria-label="Search employees" />
          <select value={status} onChange={e => { setStatus(e.target.value); setOffset(0) }} aria-label="Status"><option value="">All statuses</option>{statuses.map(s => <option key={s}>{s}</option>)}</select>
          <select value={dept} onChange={e => { setDept(e.target.value); setOffset(0) }} aria-label="Department"><option value="">All departments</option>{depts.data?.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}</select>
        </div>
        <ErrorBox error={q.error} />
        {q.isLoading ? <Loading /> : q.data!.items.length === 0 ? <Empty>No employees match.</Empty> : (
          <div className="table-wrap">
            <table>
              <thead><tr><th>No.</th><th>Name</th><th>Job title</th><th>Department</th><th>Branch</th><th>Type</th><th>Status</th><th>Hired</th>{can(P.SalaryRead) && <th className="num">Basic salary</th>}</tr></thead>
              <tbody>{q.data!.items.map(e => (
                <tr key={e.id} className="clickable" onClick={() => nav(`/employees/${e.id}`)}>
                  <td className="mono">{e.employeeNumber}</td><td><Link to={`/employees/${e.id}`}>{e.fullName}</Link></td><td>{e.jobTitle}</td><td>{e.department}</td><td>{e.branch}</td>
                  <td>{e.employmentType}</td><td><Badge status={e.status} /></td><td>{fmt.date(e.hireDate)}</td>{can(P.SalaryRead) && <td className="num">{fmt.num(e.basicSalary)}</td>}
                </tr>))}</tbody>
            </table>
          </div>
        )}
        {q.data && q.data.total > limit && (
          <div className="actions" style={{ marginTop: 12, justifyContent: 'flex-end' }}>
            <span className="muted small">{offset + 1}–{Math.min(offset + limit, q.data.total)} of {q.data.total}</span>
            <button className="btn sm" disabled={offset === 0} onClick={() => setOffset(offset - limit)}>Previous</button>
            <button className="btn sm" disabled={offset + limit >= q.data.total} onClick={() => setOffset(offset + limit)}>Next</button>
          </div>
        )}
      </div>
      {showNew && <EmployeeForm onClose={() => setShowNew(false)} />}
    </>
  )
}

export function EmployeeForm({ initial, onClose }: { initial?: Employee; onClose: () => void }) {
  const qc = useQueryClient()
  const nav = useNavigate()
  const les = useLegalEntities()
  const [form, setForm] = useState<Partial<Employee>>(initial ?? { gender: 'Unspecified', employmentType: 'Permanent', status: 'Active', hireDate: new Date().toISOString().slice(0, 10) })
  const le = form.legalEntityId || les.data?.[0]?.id
  const depts = useDepartments(le); const branches = useBranches(le); const ccs = useCostCentres(le); const groups = usePayrollGroups(le)
  const set = (k: keyof Employee, v: unknown) => setForm(f => ({ ...f, [k]: v === '' ? undefined : v }))
  const m = useMutation({
    mutationFn: () => initial ? api.put<Employee>(`/api/payroll/employees/${initial.id}`, { ...initial, ...form }) : api.post<Employee>('/api/payroll/employees', { ...form, legalEntityId: le }),
    onSuccess: (e) => { qc.invalidateQueries({ queryKey: ['employees'] }); qc.invalidateQueries({ queryKey: ['employee', e.id] }); onClose(); if (!initial) nav(`/employees/${e.id}`) },
  })
  const txt = (k: keyof Employee, label: string, type = 'text') => <Field label={label}><input type={type} value={(form[k] as string) ?? ''} onChange={e => set(k, e.target.value)} /></Field>
  const sel = (k: keyof Employee, label: string, opts: { id: string; name: string }[] | undefined, required = false) => (
    <Field label={label}><select value={(form[k] as string) ?? ''} onChange={e => set(k, e.target.value)} required={required}><option value="">—</option>{opts?.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}</select></Field>)

  return (
    <Modal title={initial ? `Edit ${initial.employeeNumber}` : 'New employee'} onClose={onClose} width={760}>
      <form onSubmit={e => { e.preventDefault(); m.mutate() }}>
        <ErrorBox error={m.error} />
        <h3>Identity</h3>
        <div className="form-grid">
          {!initial && <Field label="Legal entity"><select value={le ?? ''} onChange={e => set('legalEntityId', e.target.value)}>{les.data?.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></Field>}
          {txt('employeeNumber', 'Employee number *')}{txt('firstName', 'First name *')}{txt('middleName', 'Middle name')}{txt('lastName', 'Last name *')}
          {txt('nationalId', 'National ID')}{txt('passportNumber', 'Passport')}{txt('dateOfBirth', 'Date of birth', 'date')}
          <Field label="Gender"><select value={form.gender ?? 'Unspecified'} onChange={e => set('gender', e.target.value)}>{['Unspecified', 'Female', 'Male', 'Other'].map(g => <option key={g}>{g}</option>)}</select></Field>
          {txt('email', 'Email', 'email')}{txt('phone', 'Phone')}{txt('physicalAddress', 'Physical address')}{txt('emergencyContactName', 'Emergency contact')}{txt('emergencyContactPhone', 'Emergency phone')}
        </div>
        <h3>Employment</h3>
        <div className="form-grid">
          <Field label="Employment type"><select value={form.employmentType} onChange={e => set('employmentType', e.target.value)}>{employmentTypes.map(t => <option key={t}>{t}</option>)}</select></Field>
          <Field label="Status"><select value={form.status} onChange={e => set('status', e.target.value)}>{statuses.map(t => <option key={t}>{t}</option>)}</select></Field>
          {txt('hireDate', 'Hire date *', 'date')}{txt('contractEndDate', 'Contract end', 'date')}{txt('terminationDate', 'Termination date', 'date')}{txt('jobTitle', 'Job title')}
          {sel('departmentId', 'Department', depts.data)}{sel('branchId', 'Branch', branches.data)}{sel('costCentreId', 'Cost centre', ccs.data)}{sel('payrollGroupId', 'Payroll group *', groups.data, true)}{txt('workLocation', 'Work location')}
        </div>
        <div className="actions" style={{ justifyContent: 'flex-end' }}>
          <button type="button" className="btn" onClick={onClose}>Cancel</button>
          <button type="submit" className="btn primary" disabled={m.isPending}>{m.isPending ? 'Saving…' : 'Save'}</button>
        </div>
      </form>
    </Modal>
  )
}

export { byId }
