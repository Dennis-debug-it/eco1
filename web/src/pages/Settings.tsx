import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useLegalEntities } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, Tabs } from '../components/ui'
import MfaEnrol from '../components/MfaEnrol'

type User = { id: string; email: string; displayName: string; roles: string[]; employeeId?: string; legalEntityIds: string[]; isActive: boolean; lastLoginAt?: string; mfaEnabled: boolean; phone?: string }
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
  return (
    <>
      <div className="actions" style={{ marginBottom: 12 }}><button className="btn primary" onClick={() => setShow(true)}>New user</button><span className="muted small">Salary visibility is a separate permission — HR administrators do not see remuneration by default.</span></div>
      <div className="grid c2">
        <div className="card"><h2>Users</h2><ErrorBox error={reset.error} />{users.isLoading ? <Loading /> : !users.data?.length ? <Empty>No users.</Empty> : <div className="table-wrap"><table><thead><tr><th>Name</th><th>Email</th><th>Roles</th><th>2-step</th><th>Last sign-in</th><th /></tr></thead>
          <tbody>{users.data.map(u => <tr key={u.id}><td>{u.displayName}</td><td className="mono small">{u.email}</td><td style={{ whiteSpace: 'normal' }}>{u.roles.map(r => <Badge key={r} status="neutral">{human(r)}</Badge>)}</td><td>{u.mfaEnabled ? <Badge status="Active">On</Badge> : <span className="muted small">Off</span>}</td><td>{fmt.dateTime(u.lastLoginAt)}</td>
            <td>{u.mfaEnabled && u.id !== me?.id && <button className="btn sm" onClick={() => { if (confirm(`Reset two-step verification for ${u.displayName}? They will need to enrol again.`)) reset.mutate(u.id) }}>Reset 2-step</button>}</td></tr>)}</tbody></table></div>}</div>
        <div className="card"><h2>Roles and permissions</h2>{roles.isLoading ? <Loading /> : <table><thead><tr><th>Role</th><th>Permissions</th></tr></thead><tbody>{roles.data?.map(r => <tr key={r.role}><td><b>{human(r.role)}</b></td><td style={{ whiteSpace: 'normal' }} className="small mono">{r.permissions.join(' · ')}</td></tr>)}</tbody></table>}</div>
      </div>
      {show && <Modal title="New user" onClose={() => setShow(false)}>
        <ErrorBox error={m.error} />
        <div className="form-grid">
          <Field label="Email *"><input type="email" value={f.email} onChange={e => setF({ ...f, email: e.target.value })} /></Field>
          <Field label="Display name *"><input value={f.displayName} onChange={e => setF({ ...f, displayName: e.target.value })} /></Field>
          <Field label="Initial password * (10+ chars)"><input type="password" value={f.password} onChange={e => setF({ ...f, password: e.target.value })} /></Field>
          <Field label="Mobile (WhatsApp alerts)"><input value={f.phone} onChange={e => setF({ ...f, phone: e.target.value })} placeholder="+2547…" /></Field>
          <Field label="Linked employee id (self-service)"><input value={f.employeeId} onChange={e => setF({ ...f, employeeId: e.target.value })} placeholder="optional" /></Field>
        </div>
        <h3>Roles</h3>{roles.data?.filter(r => r.role !== 'PlatformAdministrator').map(r => <label key={r.role} className="field inline"><input type="checkbox" checked={f.roles.includes(r.role)} onChange={() => toggle(r.role)} /> {human(r.role)}</label>)}
        {les.data && les.data.length > 1 && <><h3>Restrict to legal entities (none = all)</h3>{les.data.map(l => <label key={l.id} className="field inline"><input type="checkbox" checked={f.legalEntityIds.includes(l.id)} onChange={() => setF({ ...f, legalEntityIds: f.legalEntityIds.includes(l.id) ? f.legalEntityIds.filter(x => x !== l.id) : [...f.legalEntityIds, l.id] })} /> {l.name}</label>)}</>}
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setShow(false)}>Cancel</button><button className="btn primary" disabled={m.isPending || !f.email || !f.displayName || f.password.length < 10 || f.roles.length === 0} onClick={() => m.mutate()}>Create</button></div>
      </Modal>}
    </>
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
