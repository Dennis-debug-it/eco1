import { useEffect, useState, type FormEvent } from 'react'
import { api, ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { ErrorBox, Field } from '../components/ui'

export default function LoginPage() {
  const { login } = useAuth()
  const [demo, setDemo] = useState(false)
  const [tenant, setTenant] = useState(() => { try { return localStorage.getItem('ecosys.org') ?? '' } catch { return '' } })
  useEffect(() => {
    api.get<{ demo: boolean }>('/api/payroll/auth/options').then(o => { setDemo(o.demo); if (o.demo) setTenant(t => t || 'demo') }).catch(() => {})
  }, [])
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [needCode, setNeedCode] = useState(false)
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<unknown>(null)

  const submit = async (e: FormEvent) => {
    e.preventDefault(); setBusy(true); setErr(null)
    try { await login(tenant.trim(), email.trim(), password, needCode ? code : undefined); try { localStorage.setItem('ecosys.org', tenant.trim()) } catch { /* private mode */ } }
    catch (ex) {
      if (ex instanceof ApiError && ex.problem.code === 'MFA_CODE_REQUIRED') { setNeedCode(true) }
      else setErr(ex)
    } finally { setBusy(false) }
  }

  return (
    <div className="login">
      <form className="card" onSubmit={submit}>
        <div className="brand" style={{ padding: '0 0 14px' }}><div className="logo" aria-hidden>E</div><div>Ecosys Payroll<small>Sign in to your organisation</small></div></div>
        <ErrorBox error={err} />
        {!needCode ? <>
          <Field label="Organisation"><input value={tenant} onChange={e => setTenant(e.target.value)} autoComplete="organization" required /></Field>
          <Field label="Email"><input type="email" value={email} onChange={e => setEmail(e.target.value)} autoComplete="username" required autoFocus /></Field>
          <Field label="Password"><input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" required /></Field>
        </> : <>
          <p className="small">Enter the 6-digit code from your authenticator app for <b>{email}</b>.</p>
          <Field label="Authentication code"><input inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" maxLength={6} value={code} onChange={e => setCode(e.target.value.replace(/\D/g, ''))} required autoFocus /></Field>
        </>}
        <button className="btn primary" type="submit" disabled={busy} style={{ width: '100%', justifyContent: 'center' }}>{busy ? 'Signing in…' : needCode ? 'Verify' : 'Sign in'}</button>
        {needCode && <button type="button" className="btn" style={{ width: '100%', justifyContent: 'center', marginTop: 8 }} onClick={() => { setNeedCode(false); setCode('') }}>Back</button>}
        {!needCode && demo && <p className="muted small" style={{ marginTop: 14 }}>Demo accounts: admin@, payroll@, reviewer@, approver@, finance@, hr@, auditor@, brian.mwangi@ — all <span className="mono">@demo.ecosys</span>, password <span className="mono">Demo!2345</span>.</p>}
      </form>
    </div>
  )
}
