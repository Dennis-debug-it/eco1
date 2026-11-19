import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { api } from '../lib/api'
import { useAuth } from '../lib/auth'
import type { Me } from '../lib/types'
import { ErrorBox, Field } from './ui'

/** Change your own password. As a gate it is shown after an administrator issued or reset the password. */
export default function ChangePassword({ gate }: { gate?: boolean }) {
  const { adopt, logout } = useAuth()
  const [f, setF] = useState({ current: '', next: '', confirm: '' })
  const [done, setDone] = useState(false)
  const m = useMutation({
    mutationFn: () => api.post<{ token: string; user: Me }>('/api/payroll/auth/password', { currentPassword: f.current, newPassword: f.next }),
    onSuccess: r => { adopt(r.token, r.user); setF({ current: '', next: '', confirm: '' }); setDone(true) },
  })
  const mismatch = f.confirm.length > 0 && f.next !== f.confirm
  const body = (
    <>
      <h2>{gate ? 'Choose your own password' : 'Change password'}</h2>
      <p className="small">{gate ? 'Your password was set by an administrator. Choose a new one before you continue.' : 'Changing your password signs you out on every other device.'} Use at least 10 characters. A short phrase that is easy for you to remember works well; common words such as company or city names are refused.</p>
      {done && !gate && <div className="alert ok">Password changed.</div>}
      <ErrorBox error={m.error} />
      <form onSubmit={e => { e.preventDefault(); m.mutate() }}>
        <div className="form-grid">
          <Field label={gate ? 'Temporary password' : 'Current password'}><input type="password" autoComplete="current-password" value={f.current} onChange={e => setF({ ...f, current: e.target.value })} /></Field>
          <Field label="New password"><input type="password" autoComplete="new-password" value={f.next} onChange={e => setF({ ...f, next: e.target.value })} /></Field>
          <Field label="Confirm new password"><input type="password" autoComplete="new-password" value={f.confirm} onChange={e => setF({ ...f, confirm: e.target.value })} /></Field>
        </div>
        {mismatch && <p className="small" style={{ color: 'var(--danger)' }}>The new passwords don't match.</p>}
        <div className="actions">
          <button className="btn primary" type="submit" disabled={!f.current || f.next.length < 10 || f.next !== f.confirm || m.isPending}>Change password</button>
          {gate && <button className="btn" type="button" onClick={logout}>Sign out</button>}
        </div>
      </form>
    </>
  )
  return gate ? <div className="login"><div className="card" style={{ maxWidth: 520 }}>{body}</div></div> : <div className="card">{body}</div>
}
