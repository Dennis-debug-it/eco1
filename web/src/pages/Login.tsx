import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api, ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { ErrorBox, Field, PasswordInput } from '../components/ui'

export default function LoginPage() {
  const { login } = useAuth()
  const [demo, setDemo] = useState(false)
  const [signup, setSignup] = useState(false)
  const [demoOperator, setDemoOperator] = useState(false)
  const [localLogin, setLocalLogin] = useState(true)
  const [unverified, setUnverified] = useState(false)
  const [resent, setResent] = useState(false)
  const [tenant, setTenant] = useState(() => { try { return localStorage.getItem('ecosys.org') ?? '' } catch { return '' } })
  useEffect(() => {
    api.get<{ demo: boolean; demoOperator?: boolean; signup?: boolean; localLogin?: boolean }>('/api/payroll/auth/options').then(o => { setDemo(o.demo); setLocalLogin(o.localLogin !== false); setDemoOperator(!!o.demoOperator); setSignup(!!o.signup); if (o.demo) setTenant(t => t || 'demo') }).catch(() => {})
  }, [])
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [needCode, setNeedCode] = useState(false)
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<unknown>(null)

  const submit = async (e: FormEvent) => {
    e.preventDefault(); setBusy(true); setErr(null); setUnverified(false); setResent(false)
    try { await login(tenant.trim(), email.trim(), password, needCode ? code : undefined); try { localStorage.setItem('ecosys.org', tenant.trim()) } catch { /* private mode */ } }
    catch (ex) {
      if (ex instanceof ApiError && ex.problem.code === 'MFA_CODE_REQUIRED') { setNeedCode(true) }
      else if (ex instanceof ApiError && ex.problem.code === 'EMAIL_NOT_VERIFIED') { setUnverified(true) }
      else setErr(ex)
    } finally { setBusy(false) }
  }

  return (
    <div className="login">
      <form className="card" onSubmit={submit}>
        <div className="brand" style={{ padding: '0 0 14px' }}><div className="logo" aria-hidden>E</div><div>Ecosys Payroll<small>Sign in to your organisation</small></div></div>
        <ErrorBox error={err} />
        {unverified && <div className="alert warn">Confirm your email address first: open the link we sent to <b>{email}</b>.{' '}
          {resent ? 'A new link is on its way.' : <button type="button" className="btn sm" onClick={() => api.post('/api/payroll/signup/resend', { organisation: tenant.trim(), email: email.trim() }).then(() => setResent(true)).catch(setErr)}>Send a new link</button>}</div>}
        {!needCode ? <>
          <Field label="Organisation"><input value={tenant} onChange={e => setTenant(e.target.value)} autoComplete="organization" required /></Field>
          <Field label="Email"><input type="email" value={email} onChange={e => setEmail(e.target.value)} autoComplete="username" required autoFocus /></Field>
          <Field label="Password"><PasswordInput value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" required /></Field>
          {localLogin && <p className="small" style={{ marginTop: -6, textAlign: 'right' }}><Link to={`/forgot-password?org=${encodeURIComponent(tenant.trim())}&email=${encodeURIComponent(email.trim())}`}>Forgot password?</Link></p>}
        </> : <>
          <p className="small">Enter the 6-digit code from your authenticator app for <b>{email}</b>.</p>
          <Field label="Authentication code"><input inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" maxLength={6} value={code} onChange={e => setCode(e.target.value.replace(/\D/g, ''))} required autoFocus /></Field>
        </>}
        <button className="btn primary" type="submit" disabled={busy} style={{ width: '100%', justifyContent: 'center' }}>{busy ? 'Signing in…' : needCode ? 'Verify' : 'Sign in'}</button>
        {needCode && <button type="button" className="btn" style={{ width: '100%', justifyContent: 'center', marginTop: 8 }} onClick={() => { setNeedCode(false); setCode('') }}>Back</button>}
        {!needCode && signup && <p className="small" style={{ marginTop: 14, textAlign: 'center' }}>New to Ecosys Payroll? <Link to="/signup">Start a free trial</Link></p>}
        {!needCode && demo && <p className="muted small" style={{ marginTop: 14 }}>Demo accounts: admin@, payroll@, reviewer@, approver@, finance@, hr@, auditor@, brian.mwangi@ — all <span className="mono">@demo.ecosys</span>, password <span className="mono">Demo!2345</span>.</p>}
        {!needCode && demoOperator && <p className="muted small" style={{ marginTop: 8 }}>Operator console: organisation <span className="mono">platform</span>, <span className="mono">ops@platform.ecosys</span> (administrator), same password. Also <span className="mono">support@</span> (operations) and <span className="mono">finance@</span> (finance) <span className="mono">@platform.ecosys</span>.{' '}
          <button type="button" className="btn sm" onClick={() => { setTenant('platform'); setEmail('ops@platform.ecosys') }}>Fill in</button></p>}
      </form>
    </div>
  )
}
