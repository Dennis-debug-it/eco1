import { useState, type FormEvent } from 'react'
import { useAuth } from '../lib/auth'
import { ErrorBox, Field } from '../components/ui'

export default function LoginPage() {
  const { login } = useAuth()
  const [tenant, setTenant] = useState('demo')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<unknown>(null)

  const submit = async (e: FormEvent) => {
    e.preventDefault(); setBusy(true); setErr(null)
    try { await login(tenant.trim(), email.trim(), password) } catch (ex) { setErr(ex) } finally { setBusy(false) }
  }

  return (
    <div className="login">
      <form className="card" onSubmit={submit}>
        <div className="brand" style={{ padding: '0 0 14px' }}><div className="logo" aria-hidden>E</div><div>Ecosys Payroll<small>Sign in to your organisation</small></div></div>
        <ErrorBox error={err} />
        <Field label="Organisation"><input value={tenant} onChange={e => setTenant(e.target.value)} autoComplete="organization" required /></Field>
        <Field label="Email"><input type="email" value={email} onChange={e => setEmail(e.target.value)} autoComplete="username" required autoFocus /></Field>
        <Field label="Password"><input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" required /></Field>
        <button className="btn primary" type="submit" disabled={busy} style={{ width: '100%', justifyContent: 'center' }}>{busy ? 'Signing in…' : 'Sign in'}</button>
        <p className="muted small" style={{ marginTop: 14 }}>Demo accounts: admin@, payroll@, reviewer@, approver@, finance@, hr@, auditor@, brian.mwangi@ — all <span className="mono">@demo.ecosys</span>, password <span className="mono">Demo!2345</span>.</p>
      </form>
    </div>
  )
}
