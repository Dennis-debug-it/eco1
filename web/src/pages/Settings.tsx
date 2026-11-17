import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { useLegalEntities } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead } from '../components/ui'

type User = { id: string; email: string; displayName: string; roles: string[]; employeeId?: string; legalEntityIds: string[]; isActive: boolean; lastLoginAt?: string }

export default function SettingsPage() {
  const qc = useQueryClient()
  const users = useQuery({ queryKey: ['users'], queryFn: () => api.get<User[]>('/api/payroll/auth/users') })
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => api.get<{ role: string; permissions: string[] }[]>('/api/payroll/auth/roles') })
  const les = useLegalEntities()
  const [show, setShow] = useState(false)
  const [f, setF] = useState({ email: '', displayName: '', password: '', roles: [] as string[], employeeId: '', legalEntityIds: [] as string[] })
  const m = useMutation({ mutationFn: () => api.post('/api/payroll/auth/users', { ...f, employeeId: f.employeeId || null }), onSuccess: () => { qc.invalidateQueries({ queryKey: ['users'] }); setShow(false) } })
  const toggle = (r: string) => setF({ ...f, roles: f.roles.includes(r) ? f.roles.filter(x => x !== r) : [...f.roles, r] })
  return (
    <>
      <PageHead title="Settings & users" sub="Role assignments. Salary visibility is a separate permission — HR administrators do not see remuneration by default."><button className="btn primary" onClick={() => setShow(true)}>New user</button></PageHead>
      <div className="grid c2">
        <div className="card"><h2>Users</h2>{users.isLoading ? <Loading /> : !users.data?.length ? <Empty>No users.</Empty> : <table><thead><tr><th>Name</th><th>Email</th><th>Roles</th><th>Entities</th><th>Last sign-in</th><th>Active</th></tr></thead>
          <tbody>{users.data.map(u => <tr key={u.id}><td>{u.displayName}</td><td className="mono small">{u.email}</td><td>{u.roles.map(r => <Badge key={r} status="neutral">{r.replace(/([A-Z])/g, ' $1').trim()}</Badge>)}</td><td className="small">{u.legalEntityIds.length ? `${u.legalEntityIds.length} restricted` : 'All'}</td><td>{fmt.dateTime(u.lastLoginAt)}</td><td>{u.isActive ? 'Yes' : 'No'}</td></tr>)}</tbody></table>}</div>
        <div className="card"><h2>Roles and permissions</h2>{roles.isLoading ? <Loading /> : <table><thead><tr><th>Role</th><th>Permissions</th></tr></thead><tbody>{roles.data?.map(r => <tr key={r.role}><td><b>{r.role.replace(/([A-Z])/g, ' $1').trim()}</b></td><td style={{ whiteSpace: 'normal' }} className="small mono">{r.permissions.join(' · ')}</td></tr>)}</tbody></table>}</div>
      </div>
      {show && <Modal title="New user" onClose={() => setShow(false)}>
        <ErrorBox error={m.error} />
        <div className="form-grid">
          <Field label="Email *"><input type="email" value={f.email} onChange={e => setF({ ...f, email: e.target.value })} /></Field>
          <Field label="Display name *"><input value={f.displayName} onChange={e => setF({ ...f, displayName: e.target.value })} /></Field>
          <Field label="Initial password * (10+ chars)"><input type="password" value={f.password} onChange={e => setF({ ...f, password: e.target.value })} /></Field>
          <Field label="Linked employee id (for self-service)"><input value={f.employeeId} onChange={e => setF({ ...f, employeeId: e.target.value })} placeholder="optional" /></Field>
        </div>
        <h3>Roles</h3>{roles.data?.filter(r => r.role !== 'PlatformAdministrator').map(r => <label key={r.role} className="field inline"><input type="checkbox" checked={f.roles.includes(r.role)} onChange={() => toggle(r.role)} /> {r.role.replace(/([A-Z])/g, ' $1').trim()}</label>)}
        {les.data && les.data.length > 1 && <><h3>Restrict to legal entities (none = all)</h3>{les.data.map(l => <label key={l.id} className="field inline"><input type="checkbox" checked={f.legalEntityIds.includes(l.id)} onChange={() => setF({ ...f, legalEntityIds: f.legalEntityIds.includes(l.id) ? f.legalEntityIds.filter(x => x !== l.id) : [...f.legalEntityIds, l.id] })} /> {l.name}</label>)}</>}
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setShow(false)}>Cancel</button><button className="btn primary" disabled={m.isPending || !f.email || !f.displayName || f.password.length < 10 || f.roles.length === 0} onClick={() => m.mutate()}>Create</button></div>
      </Modal>}
    </>
  )
}
