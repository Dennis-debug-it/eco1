import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../../lib/api'
import { OperatorRoles, useAuth } from '../../lib/auth'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, ReasonDialog } from '../../components/ui'
import { roleHint, roleLabel } from './shared'

type Operator = {
  id: string; email: string; displayName: string; roles: string[]; isActive: boolean; mfaEnabled: boolean; lastLoginAt?: string
  lockedUntil?: string; mustChangePassword: boolean; createdAt: string
}

export default function OperatorsPage() {
  const { user } = useAuth()
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['platform-operators'], queryFn: () => api.get<Operator[]>('/api/platform/operators') })
  const refresh = () => qc.invalidateQueries({ queryKey: ['platform-operators'] })
  const [invite, setInvite] = useState(false)
  const [roles, setRoles] = useState<Operator | null>(null)
  const [reason, setReason] = useState<{ op: Operator; kind: 'deactivate' | 'mfa' } | null>(null)
  const [password, setPassword] = useState<{ op: Operator; value: string } | null>(null)
  const act = useMutation({ mutationFn: ({ op, path }: { op: Operator; path: string }) => api.post(`/api/platform/operators/${op.id}/${path}`, {}), onSuccess: refresh })
  const reset = useMutation({
    mutationFn: (op: Operator) => api.post<{ temporaryPassword: string }>(`/api/platform/operators/${op.id}/password`, {}),
    onSuccess: (r, op) => { setPassword({ op, value: r.temporaryPassword }); refresh() },
  })
  return (
    <>
      <PageHead title="Operator accounts" sub="The people who run the platform. Each role sees only its part of the console, and no operator can open an organisation's employee or payroll records.">
        <button className="btn primary" onClick={() => setInvite(true)}>Invite operator</button>
      </PageHead>
      <div className="grid c3" style={{ marginBottom: 16 }}>
        {OperatorRoles.map(r => <div key={r} className="card small"><b>{roleLabel[r]}</b><div className="muted">{roleHint[r]}</div></div>)}
      </div>
      <div className="card">
        <ErrorBox error={q.error ?? act.error ?? reset.error} />
        {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No operators.</Empty> : <div className="table-wrap"><table>
          <thead><tr><th>Operator</th><th>Role</th><th>Sign-in</th><th>Last sign-in</th><th /></tr></thead>
          <tbody>{q.data.map(o => {
            const self = o.id === user?.id
            return <tr key={o.id}>
              <td>{o.displayName}{self && <span className="muted"> (you)</span>}<div className="small muted">{o.email}</div></td>
              <td>{o.roles.map(r => roleLabel[r] ?? r).join(', ')}</td>
              <td>
                {!o.isActive ? <Badge status="Cancelled">Deactivated</Badge> : o.mustChangePassword ? <Badge status="Warning">Password not yet chosen</Badge> : <Badge status="Active">Active</Badge>}
                {o.isActive && !o.mfaEnabled && <div className="small muted">No authenticator yet</div>}
                {o.lockedUntil && new Date(o.lockedUntil) > new Date() && <div className="small muted">Locked until {fmt.dateTime(o.lockedUntil)}</div>}
              </td>
              <td>{fmt.dateTime(o.lastLoginAt)}</td>
              <td style={{ whiteSpace: 'nowrap' }}>{!self && <>
                <button className="btn sm" onClick={() => setRoles(o)}>Role</button>{' '}
                {o.isActive
                  ? <button className="btn sm danger" onClick={() => setReason({ op: o, kind: 'deactivate' })}>Deactivate</button>
                  : <button className="btn sm" disabled={act.isPending} onClick={() => act.mutate({ op: o, path: 'reactivate' })}>Reactivate</button>}{' '}
                {o.isActive && <>
                  <button className="btn sm" disabled={reset.isPending} onClick={() => reset.mutate(o)}>Reset password</button>{' '}
                  {o.mfaEnabled && <button className="btn sm" onClick={() => setReason({ op: o, kind: 'mfa' })}>Reset authenticator</button>}{' '}
                  <button className="btn sm" disabled={act.isPending} onClick={() => act.mutate({ op: o, path: 'sessions/revoke' })}>Sign out everywhere</button>
                </>}
              </>}</td>
            </tr>
          })}</tbody>
        </table></div>}
        <p className="small muted">You can't change your own account here; use Settings &amp; security. Every change signs the operator out and is kept in the audit log.</p>
      </div>

      {invite && <InviteDialog onClose={() => { setInvite(false); refresh() }} />}
      {roles && <RolesDialog op={roles} onClose={() => { setRoles(null); refresh() }} />}
      {reason?.kind === 'deactivate' && <ReasonDialog title={`Deactivate ${reason.op.displayName}`} label="Reason (kept in the audit log)" required danger confirmLabel="Deactivate" onClose={() => setReason(null)}
        onConfirm={async r => { await api.post(`/api/platform/operators/${reason.op.id}/deactivate`, { reason: r }); refresh() }}>
        <p className="small">They are signed out at once and can't sign in until reactivated.</p>
      </ReasonDialog>}
      {reason?.kind === 'mfa' && <ReasonDialog title={`Reset ${reason.op.displayName}'s authenticator`} label="Reason (kept in the audit log)" required confirmLabel="Reset authenticator" onClose={() => setReason(null)}
        onConfirm={async r => { await api.post(`/api/platform/operators/${reason.op.id}/mfa/reset`, { reason: r }); refresh() }}>
        <p className="small">Only for a lost or replaced phone. Confirm who is asking before you do this. They set up a new authenticator at their next sign-in.</p>
      </ReasonDialog>}
      {password && <Modal title="New one-time password" onClose={() => setPassword(null)}>
        <dl className="kv"><dt>Operator</dt><dd>{password.op.email}</dd><dt>Sign-in code</dt><dd className="mono">platform</dd><dt>One-time password</dt><dd className="mono">{password.value}</dd></dl>
        <p className="small">Give it to them directly, not by email. It will not be shown again, and they choose their own password at sign-in.</p>
        <button className="btn primary" onClick={() => setPassword(null)}>Done</button>
      </Modal>}
    </>
  )
}

function RoleChoice({ value, onChange }: { value: string[]; onChange: (v: string[]) => void }) {
  return <div style={{ margin: '8px 0 16px' }}>{OperatorRoles.map(r => (
    <label key={r} className="field inline" style={{ display: 'block', marginBottom: 8 }}>
      <input type="checkbox" checked={value.includes(r)} onChange={e => onChange(e.target.checked ? [...value, r] : value.filter(x => x !== r))} /> <b>{roleLabel[r]}</b>
      <div className="small muted" style={{ marginLeft: 24 }}>{roleHint[r]}</div>
    </label>
  ))}</div>
}

function InviteDialog({ onClose }: { onClose: () => void }) {
  const [f, setF] = useState({ email: '', name: '', roles: ['PlatformOperations'] })
  const save = useMutation({ mutationFn: () => api.post<{ email: string; signInCode: string; temporaryPassword: string }>('/api/platform/operators', f) })
  if (save.data) return (
    <Modal title="Operator invited" onClose={onClose}>
      <dl className="kv"><dt>Sign-in code</dt><dd className="mono">{save.data.signInCode}</dd><dt>Email</dt><dd>{save.data.email}</dd><dt>One-time password</dt><dd className="mono">{save.data.temporaryPassword}</dd></dl>
      <p className="small">Give the password to them directly, not by email. It will not be shown again. At first sign-in they choose their own password and set up an authenticator app.</p>
      <button className="btn primary" onClick={onClose}>Done</button>
    </Modal>
  )
  return (
    <Modal title="Invite operator" onClose={onClose}>
      <form onSubmit={e => { e.preventDefault(); save.mutate() }}>
        <ErrorBox error={save.error} />
        <div className="form-grid">
          <Field label="Name"><input value={f.name} onChange={e => setF({ ...f, name: e.target.value })} required /></Field>
          <Field label="Email"><input type="email" value={f.email} onChange={e => setF({ ...f, email: e.target.value })} required /></Field>
        </div>
        <RoleChoice value={f.roles} onChange={roles => setF({ ...f, roles })} />
        <button className="btn primary" disabled={save.isPending || f.roles.length === 0}>Invite</button>
      </form>
    </Modal>
  )
}

function RolesDialog({ op, onClose }: { op: Operator; onClose: () => void }) {
  const [roles, setRoles] = useState(op.roles)
  const save = useMutation({ mutationFn: () => api.put(`/api/platform/operators/${op.id}/roles`, { roles }), onSuccess: onClose })
  return (
    <Modal title={`${op.displayName}'s role`} onClose={onClose}>
      <form onSubmit={e => { e.preventDefault(); save.mutate() }}>
        <ErrorBox error={save.error} />
        <RoleChoice value={roles} onChange={setRoles} />
        <p className="small muted">They are signed out so the new role applies at once.</p>
        <button className="btn primary" disabled={save.isPending || roles.length === 0}>Save role</button>
      </form>
    </Modal>
  )
}
