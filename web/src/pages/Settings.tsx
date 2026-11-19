import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useLegalEntities } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, Tabs } from '../components/ui'
import MfaEnrol from '../components/MfaEnrol'
import ChangePassword from '../components/ChangePassword'

type User = { id: string; email: string; displayName: string; roles: string[]; employeeId?: string; legalEntityIds: string[]; isActive: boolean; lastLoginAt?: string; mfaEnabled: boolean; phone?: string; lockedUntil?: string; mustChangePassword: boolean; passwordChangedAt?: string }
type Notification = { id: string; channel: string; recipient: string; recipientName?: string; template: string; subject: string; status: string; attempts: number; sentAt?: string; lastError?: string; createdAt: string }
const human = (r: string) => r.replace(/([A-Z])/g, ' $1').trim()

export default function SettingsPage() {
  const { can } = useAuth()
  const admin = can(P.OrganisationManage)
  const [tab, setTab] = useState<'security' | 'users' | 'notifications'>(admin ? 'users' : 'security')
  return (
    <>
      <PageHead title="Settings & security" sub={admin ? 'Users, roles, two-step verification and outgoing notifications' : 'Your sign-in security'} />
      <Tabs value={tab} onChange={setTab} tabs={[...(admin ? [{ id: 'users' as const, label: 'Users & roles' }, { id: 'notifications' as const, label: 'Notifications' }] : []), { id: 'security' as const, label: 'My security' }]} />
      {tab === 'users' && admin && <Users />}
      {tab === 'notifications' && admin && <Notifications />}
      {tab === 'security' && <MySecurity />}
    </>
  )
}

function MySecurity() {
  const { logout } = useAuth()
  const revoke = useMutation({ mutationFn: () => api.post('/api/payroll/auth/sessions/revoke'), onSuccess: logout })
  return (
    <>
      <TwoStep />
      <ChangePassword />
      <div className="card">
        <h2>Sessions</h2>
        <p className="small">Signed in on a shared or lost device? Sign out everywhere, including this browser.</p>
        <ErrorBox error={revoke.error} />
        <button className="btn danger" disabled={revoke.isPending} onClick={() => { if (confirm('Sign out of Ecosys Payroll on every device?')) revoke.mutate() }}>Sign out everywhere</button>
      </div>
    </>
  )
}

function TwoStep() {
  const { user, logout } = useAuth()
  const [code, setCode] = useState('')
  const disable = useMutation({ mutationFn: () => api.post('/api/payroll/auth/mfa/disable', { code }), onSuccess: logout })
  if (!user) return null
  if (!user.mfaEnabled) return <MfaEnrol />
  return (
    <div className="card">
      <h2>Two-step verification <Badge status="Active">On</Badge></h2>
      <p className="small">Sign-ins require a code from your authenticator app.{user.mfaRequired && ' Your role requires this; turning it off will block payroll access until you enrol again.'}</p>
      <ErrorBox error={disable.error} />
      <div className="toolbar"><input inputMode="numeric" maxLength={6} placeholder="Current code" value={code} onChange={e => setCode(e.target.value.replace(/\D/g, ''))} aria-label="Current code" /><button className="btn danger" disabled={code.length !== 6 || disable.isPending} onClick={() => disable.mutate()}>Turn off</button></div>
    </div>
  )
}

function Users() {
  const qc = useQueryClient()
  const { user: me } = useAuth()
  const users = useQuery({ queryKey: ['users'], queryFn: () => api.get<User[]>('/api/payroll/auth/users') })
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => api.get<{ role: string; permissions: string[] }[]>('/api/payroll/auth/roles') })
  const les = useLegalEntities()
  const [show, setShow] = useState(false)
  const [f, setF] = useState({ email: '', displayName: '', password: '', phone: '', roles: [] as string[], employeeId: '', legalEntityIds: [] as string[] })
  const m = useMutation({ mutationFn: () => api.post('/api/payroll/auth/users', { ...f, employeeId: f.employeeId || null, phone: f.phone || null }), onSuccess: () => { qc.invalidateQueries({ queryKey: ['users'] }); setShow(false) } })
  const reset = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/auth/users/${id}/mfa/reset`), onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }) })
  const toggle = (r: string) => setF({ ...f, roles: f.roles.includes(r) ? f.roles.filter(x => x !== r) : [...f.roles, r] })
  const refresh = () => qc.invalidateQueries({ queryKey: ['users'] })
  const unlock = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/auth/users/${id}/unlock`), onSuccess: refresh })
  const resetPwd = useMutation({ mutationFn: (u: User) => api.post<{ temporaryPassword: string }>(`/api/payroll/auth/users/${u.id}/password`, {}).then(r => ({ ...r, user: u })), onSuccess: refresh })
  const [edit, setEdit] = useState<User | null>(null)
  const locked = (u: User) => !!u.lockedUntil && new Date(u.lockedUntil) > new Date()
  return (
    <>
      <div className="actions" style={{ marginBottom: 12 }}><button className="btn primary" onClick={() => setShow(true)}>New user</button><span className="muted small">Salary visibility is a separate permission — HR administrators do not see remuneration by default.</span></div>
      <div className="grid c2">
        <div className="card"><h2>Users</h2><ErrorBox error={reset.error ?? unlock.error ?? resetPwd.error} />
          {resetPwd.data && <div className="alert info">Temporary password for {resetPwd.data.user.displayName}: <b className="mono">{resetPwd.data.temporaryPassword}</b>. Give it to them in person or by phone. It works once; they choose their own at sign-in. It won't be shown again.</div>}{users.isLoading ? <Loading /> : !users.data?.length ? <Empty>No users.</Empty> : <div className="table-wrap"><table><thead><tr><th>Name</th><th>Email</th><th>Roles</th><th>2-step</th><th>Status</th><th>Last sign-in</th><th /></tr></thead>
          <tbody>{users.data.map(u => <tr key={u.id}><td>{u.displayName}</td><td className="mono small">{u.email}</td><td style={{ whiteSpace: 'normal' }}>{u.roles.map(r => <Badge key={r} status="neutral">{human(r)}</Badge>)}</td><td>{u.mfaEnabled ? <Badge status="Active">On</Badge> : <span className="muted small">Off</span>}</td><td>{!u.isActive ? <Badge status="Cancelled">Inactive</Badge> : locked(u) ? <Badge status="Error">Locked</Badge> : u.mustChangePassword ? <Badge status="Pending">Password to set</Badge> : <Badge status="Active">Active</Badge>}</td><td>{fmt.dateTime(u.lastLoginAt)}</td>
            <td className="actions">{u.id !== me?.id && <>
              <button className="btn sm" onClick={() => setEdit(u)}>Edit</button>
              {locked(u) && <button className="btn sm" onClick={() => unlock.mutate(u.id)}>Unlock</button>}
              {u.isActive && <button className="btn sm" onClick={() => { if (confirm(`Issue a temporary password for ${u.displayName}? Their current sessions end.`)) resetPwd.mutate(u) }}>Reset password</button>}
              {u.mfaEnabled && <button className="btn sm" onClick={() => { if (confirm(`Reset two-step verification for ${u.displayName}? They will need to enrol again.`)) reset.mutate(u.id) }}>Reset 2-step</button>}</>}</td></tr>)}</tbody></table></div>}</div>
        <div className="card"><h2>Roles and permissions</h2>{roles.isLoading ? <Loading /> : <table><thead><tr><th>Role</th><th>Permissions</th></tr></thead><tbody>{roles.data?.map(r => <tr key={r.role}><td><b>{human(r.role)}</b></td><td style={{ whiteSpace: 'normal' }} className="small mono">{r.permissions.join(' · ')}</td></tr>)}</tbody></table>}</div>
      </div>
      {show && <Modal title="New user" onClose={() => setShow(false)}>
        <ErrorBox error={m.error} />
        <div className="form-grid">
          <Field label="Email *"><input type="email" value={f.email} onChange={e => setF({ ...f, email: e.target.value })} /></Field>
          <Field label="Display name *"><input value={f.displayName} onChange={e => setF({ ...f, displayName: e.target.value })} /></Field>
          <Field label="Temporary password * (10+ chars; they change it at first sign-in)"><input type="password" value={f.password} onChange={e => setF({ ...f, password: e.target.value })} /></Field>
          <Field label="Mobile (WhatsApp alerts)"><input value={f.phone} onChange={e => setF({ ...f, phone: e.target.value })} placeholder="+2547…" /></Field>
          <Field label="Linked employee id (self-service)"><input value={f.employeeId} onChange={e => setF({ ...f, employeeId: e.target.value })} placeholder="optional" /></Field>
        </div>
        <h3>Roles</h3>{roles.data?.filter(r => r.role !== 'PlatformAdministrator').map(r => <label key={r.role} className="field inline"><input type="checkbox" checked={f.roles.includes(r.role)} onChange={() => toggle(r.role)} /> {human(r.role)}</label>)}
        {les.data && les.data.length > 1 && <><h3>Restrict to legal entities (none = all)</h3>{les.data.map(l => <label key={l.id} className="field inline"><input type="checkbox" checked={f.legalEntityIds.includes(l.id)} onChange={() => setF({ ...f, legalEntityIds: f.legalEntityIds.includes(l.id) ? f.legalEntityIds.filter(x => x !== l.id) : [...f.legalEntityIds, l.id] })} /> {l.name}</label>)}</>}
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setShow(false)}>Cancel</button><button className="btn primary" disabled={m.isPending || !f.email || !f.displayName || f.password.length < 10 || f.roles.length === 0} onClick={() => m.mutate()}>Create</button></div>
      </Modal>}
      {edit && <EditUser user={edit} roles={roles.data?.map(r => r.role).filter(r => r !== 'PlatformAdministrator') ?? []} onClose={() => { setEdit(null); refresh() }} />}
    </>
  )
}

function EditUser({ user, roles, onClose }: { user: User; roles: string[]; onClose: () => void }) {
  const les = useLegalEntities()
  const [f, setF] = useState({ displayName: user.displayName, roles: user.roles, isActive: user.isActive, phone: user.phone ?? '', employeeId: user.employeeId ?? '', legalEntityIds: user.legalEntityIds })
  const m = useMutation({ mutationFn: () => api.put(`/api/payroll/auth/users/${user.id}`, { ...f, phone: f.phone || null, employeeId: f.employeeId || null }), onSuccess: onClose })
  return (
    <Modal title={`Edit ${user.displayName}`} onClose={onClose}>
      <ErrorBox error={m.error} />
      <div className="form-grid">
        <Field label="Display name"><input value={f.displayName} onChange={e => setF({ ...f, displayName: e.target.value })} /></Field>
        <Field label="Mobile (WhatsApp alerts)"><input value={f.phone} onChange={e => setF({ ...f, phone: e.target.value })} placeholder="+2547…" /></Field>
        <Field label="Linked employee id"><input value={f.employeeId} onChange={e => setF({ ...f, employeeId: e.target.value })} /></Field>
      </div>
      <h3>Roles</h3>{roles.map(r => <label key={r} className="field inline"><input type="checkbox" checked={f.roles.includes(r)} onChange={() => setF({ ...f, roles: f.roles.includes(r) ? f.roles.filter(x => x !== r) : [...f.roles, r] })} /> {human(r)}</label>)}
      {les.data && les.data.length > 1 && <><h3>Restrict to legal entities (none = all)</h3>{les.data.map(l => <label key={l.id} className="field inline"><input type="checkbox" checked={f.legalEntityIds.includes(l.id)} onChange={() => setF({ ...f, legalEntityIds: f.legalEntityIds.includes(l.id) ? f.legalEntityIds.filter(x => x !== l.id) : [...f.legalEntityIds, l.id] })} /> {l.name}</label>)}</>}
      <label className="field inline" style={{ marginTop: 10 }}><input type="checkbox" checked={f.isActive} onChange={e => setF({ ...f, isActive: e.target.checked })} /> Active (untick when someone leaves; their sessions end immediately)</label>
      <p className="small muted">Changing roles, entities or status signs the user out so the change applies at once.</p>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={m.isPending || !f.displayName || f.roles.length === 0} onClick={() => m.mutate()}>Save</button></div>
    </Modal>
  )
}

function Notifications() {
  const [status, setStatus] = useState('')
  const q = useQuery({ queryKey: ['notifications', status], queryFn: () => api.get<{ items: Notification[]; total: number }>(`/api/payroll/reports/notifications?limit=200${status ? `&status=${status}` : ''}`), refetchInterval: 15_000 })
  return (
    <div className="card">
      <div className="toolbar"><h2 style={{ margin: 0 }}>Outgoing notifications</h2><span style={{ flex: 1 }} /><select value={status} onChange={e => setStatus(e.target.value)} aria-label="Status"><option value="">All</option>{['Pending', 'Sent', 'Failed'].map(s => <option key={s}>{s}</option>)}</select></div>
      <p className="muted small">Email and WhatsApp messages are queued with each payroll or leave action and delivered through Ecosys Notifications. Messages never include salary figures.</p>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : !q.data?.items.length ? <Empty>No notifications.</Empty> : <div className="table-wrap"><table><thead><tr><th>Queued</th><th>Channel</th><th>To</th><th>Subject</th><th>Status</th><th className="num">Attempts</th><th>Sent</th><th>Error</th></tr></thead>
        <tbody>{q.data.items.map(n => <tr key={n.id}><td>{fmt.dateTime(n.createdAt)}</td><td>{n.channel}</td><td>{n.recipientName} <span className="mono small muted">{n.recipient}</span></td><td style={{ whiteSpace: 'normal' }}>{n.subject}</td><td><Badge status={n.status === 'Sent' ? 'Confirmed' : n.status} >{n.status}</Badge></td><td className="num">{n.attempts}</td><td>{fmt.dateTime(n.sentAt)}</td><td className="small muted" style={{ whiteSpace: 'normal' }}>{n.lastError}</td></tr>)}</tbody></table></div>}
    </div>
  )
}
