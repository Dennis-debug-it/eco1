import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api, ApiError } from '../lib/api'
import { ErrorBox, Field, Loading, PasswordInput } from '../components/ui'

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

/**
 * Opened from the emailed link: choose a new password. Works whether or not someone is signed in on this browser.
 * Accounts with an authenticator app also give a current code, so the email link alone can't change the password.
 */
export function ResetPasswordPage() {
  const token = new URLSearchParams(window.location.search).get('token') ?? ''
  const [f, setF] = useState({ next: '', confirm: '', code: '' })
  // null while checking the link; then whether this account needs an authenticator code.
  const [mfa, setMfa] = useState<boolean | null>(null)
  const [done, setDone] = useState<{ slug: string; email: string; mfaEnabled: boolean } | null>(null)
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<unknown>(token ? null : new ApiError(400, { title: 'Link not valid', detail: 'The reset link is incomplete. Copy the whole link from the email, or request a new one.' }))
  useEffect(() => {
    if (!token) return
    api.post<{ mfaRequired: boolean }>('/api/payroll/auth/password/reset/inspect', { token }).then(r => setMfa(r.mfaRequired)).catch(setErr)
  }, [token])
  const mismatch = f.confirm.length > 0 && f.next !== f.confirm
  const submit = async (e: FormEvent) => {
    e.preventDefault(); setBusy(true); setErr(null)
    try {
      const r = await api.post<{ slug: string; email: string; mfaEnabled: boolean }>('/api/payroll/auth/password/reset', { token, newPassword: f.next, code: mfa ? f.code : undefined })
      try { localStorage.setItem('ecosys.org', r.slug) } catch { /* private mode */ }
      setDone(r)
    } catch (ex) {
      setErr(ex)
      // A wrong or reused code: clear it so the next one from the app can be typed straight in.
      if (ex instanceof ApiError && (ex.problem.code === 'MFA_CODE_INVALID' || ex.problem.code === 'MFA_CODE_REQUIRED')) { setMfa(true); setF(v => ({ ...v, code: '' })) }
    } finally { setBusy(false) }
  }
  // Full reload, so a session that was open on this browser (now signed out by the reset) is dropped.
  const signIn = () => { window.location.href = '/' }
  const invalid = err instanceof ApiError && err.problem.code === 'RESET_LINK_INVALID'
  const codeOk = !mfa || /^[0-9]{6}$/.test(f.code)
  return (
    <div className="login">
      <form className="card" onSubmit={submit} style={{ maxWidth: 520 }}>
        {done ? <>
          <h2>Password changed</h2>
          <p className="small">Sign in to <span className="mono">{done.slug}</span> as <b>{done.email}</b> with your new password.{done.mfaEnabled ? ' You will still need the code from your authenticator app.' : ''} Every other session was signed out.</p>
          <button className="btn primary" type="button" onClick={signIn}>Sign in</button>
        </> : <>
          <h2>Choose a new password</h2>
          <ErrorBox error={err} />
          {!token || invalid ? <Link className="btn" to="/forgot-password">Request a new link</Link> : mfa === null ? (err ? null : <Loading what="Checking your link" />) : <>
            <p className="small">Use at least 10 characters. A short phrase that is easy for you to remember works well; common words such as company or city names are refused.</p>
            <div className="form-grid">
              <Field label="New password"><PasswordInput autoComplete="new-password" value={f.next} onChange={e => setF({ ...f, next: e.target.value })} required autoFocus /></Field>
              <Field label="Confirm new password"><PasswordInput autoComplete="new-password" value={f.confirm} onChange={e => setF({ ...f, confirm: e.target.value })} required /></Field>
            </div>
            {mismatch && <p className="small" style={{ color: 'var(--danger)' }}>The new passwords don't match.</p>}
            {mfa && <>
              <Field label="Code from your authenticator app"><input inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" maxLength={6} value={f.code} onChange={e => setF({ ...f, code: e.target.value.replace(/\D/g, '') })} required aria-describedby="mfa-hint" /></Field>
              <p id="mfa-hint" className="small muted" style={{ marginTop: -6 }}>Your account uses two-step sign-in, so resetting the password needs a code as well. Lost your authenticator? Ask your administrator to reset it and your password.</p>
            </>}
            <div className="actions">
              <button className="btn primary" type="submit" disabled={busy || f.next.length < 10 || f.next !== f.confirm || !codeOk}>{busy ? 'Saving…' : 'Set new password'}</button>
              <Link className="btn" to="/">Cancel</Link>
            </div>
          </>}
        </>}
      </form>
    </div>
  )
}
