import { useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api, ApiError } from '../lib/api'
import { ErrorBox, Field } from '../components/ui'

/** "Forgot password": asks for an emailed reset link. The answer is the same whether or not the account exists. */
export function ForgotPasswordPage() {
  const params = new URLSearchParams(window.location.search)
  const [f, setF] = useState({ organisation: params.get('org') ?? '', email: params.get('email') ?? '' })
  const [sent, setSent] = useState(false)
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<unknown>(null)
  const submit = async (e: FormEvent) => {
    e.preventDefault(); setBusy(true); setErr(null)
    try { await api.post('/api/payroll/auth/password/forgot', { organisation: f.organisation.trim() || undefined, email: f.email.trim() }); setSent(true) }
    catch (ex) { setErr(ex) } finally { setBusy(false) }
  }
  return (
    <div className="login">
      <form className="card" onSubmit={submit}>
        <h2>Reset your password</h2>
        {sent ? <>
          <div className="alert ok" role="status">If <b>{f.email.trim()}</b> has an account{f.organisation.trim() ? <> in <span className="mono">{f.organisation.trim().toLowerCase()}</span></> : null}, we've emailed a link to choose a new password. It works for one hour.</div>
          <p className="small muted">No email after a few minutes? Check your spam folder, or ask your organisation's administrator to reset your password from Settings &amp; security.</p>
          <Link className="btn primary" to="/">Back to sign in</Link>
        </> : <>
          <p className="small">Enter the email address you sign in with. We'll email you a link to choose a new password.</p>
          <ErrorBox error={err} />
          <Field label="Email"><input type="email" value={f.email} onChange={e => setF({ ...f, email: e.target.value })} autoComplete="username" required autoFocus /></Field>
          <Field label="Organisation (optional)"><input value={f.organisation} onChange={e => setF({ ...f, organisation: e.target.value })} autoComplete="organization" aria-describedby="org-hint" /></Field>
          <p id="org-hint" className="small muted" style={{ marginTop: -6 }}>Forgotten it? Leave it blank: the email names each organisation you can sign in to.</p>
          <button className="btn primary" type="submit" disabled={busy || !f.email.includes('@')} style={{ width: '100%', justifyContent: 'center' }}>{busy ? 'Sending…' : 'Email me a reset link'}</button>
          <p className="small" style={{ marginTop: 14, textAlign: 'center' }}><Link to="/">Back to sign in</Link></p>
        </>}
      </form>
    </div>
  )
}

/** Opened from the emailed link: choose a new password. Works whether or not someone is signed in on this browser. */
export function ResetPasswordPage() {
  const token = new URLSearchParams(window.location.search).get('token') ?? ''
  const [f, setF] = useState({ next: '', confirm: '' })
  const [done, setDone] = useState<{ slug: string; email: string; mfaEnabled: boolean } | null>(null)
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<unknown>(token ? null : new ApiError(400, { title: 'Link not valid', detail: 'The reset link is incomplete. Copy the whole link from the email, or request a new one.' }))
  const mismatch = f.confirm.length > 0 && f.next !== f.confirm
  const submit = async (e: FormEvent) => {
    e.preventDefault(); setBusy(true); setErr(null)
    try {
      const r = await api.post<{ slug: string; email: string; mfaEnabled: boolean }>('/api/payroll/auth/password/reset', { token, newPassword: f.next })
      try { localStorage.setItem('ecosys.org', r.slug) } catch { /* private mode */ }
      setDone(r)
    } catch (ex) { setErr(ex) } finally { setBusy(false) }
  }
  // Full reload, so a session that was open on this browser (now signed out by the reset) is dropped.
  const signIn = () => { window.location.href = '/' }
  const invalid = err instanceof ApiError && err.problem.code === 'RESET_LINK_INVALID'
  return (
    <div className="login">
      <form className="card" onSubmit={submit} style={{ maxWidth: 520 }}>
        {done ? <>
          <h2>Password changed</h2>
          <p className="small">Sign in to <span className="mono">{done.slug}</span> as <b>{done.email}</b> with your new password.{done.mfaEnabled ? ' You will still need the code from your authenticator app.' : ''} Every other session was signed out.</p>
          <button className="btn primary" type="button" onClick={signIn}>Sign in</button>
        </> : <>
          <h2>Choose a new password</h2>
          <p className="small">Use at least 10 characters. A short phrase that is easy for you to remember works well; common words such as company or city names are refused.</p>
          <ErrorBox error={err} />
          {!token || invalid ? <Link className="btn" to="/forgot-password">Request a new link</Link> : <>
            <div className="form-grid">
              <Field label="New password"><input type="password" autoComplete="new-password" value={f.next} onChange={e => setF({ ...f, next: e.target.value })} required autoFocus /></Field>
              <Field label="Confirm new password"><input type="password" autoComplete="new-password" value={f.confirm} onChange={e => setF({ ...f, confirm: e.target.value })} required /></Field>
            </div>
            {mismatch && <p className="small" style={{ color: 'var(--danger)' }}>The new passwords don't match.</p>}
            <div className="actions">
              <button className="btn primary" type="submit" disabled={busy || f.next.length < 10 || f.next !== f.confirm}>{busy ? 'Saving…' : 'Set new password'}</button>
              <Link className="btn" to="/">Cancel</Link>
            </div>
          </>}
        </>}
      </form>
    </div>
  )
}
