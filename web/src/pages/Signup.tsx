import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api, ApiError } from '../lib/api'
import { limitsLine, priceLine, type PlanView } from '../lib/billing'
import { ErrorBox, Field, Loading, PasswordInput } from '../components/ui'

type Options = { enabled: boolean; trialDays?: number; defaultPlan?: string; vatRate?: number; requireEmailVerification?: boolean; plans?: PlanView[] }
type Created = { slug: string; email: string; verificationRequired: boolean; trialEndsOn: string }

export default function SignupPage() {
  const [opts, setOpts] = useState<Options | null>(null)
  const [f, setF] = useState({ organisationName: '', slug: '', kraPin: '', adminName: '', adminEmail: '', phone: '', password: '', planCode: '', acceptTerms: false })
  const [slugEdited, setSlugEdited] = useState(false)
  const [slugState, setSlugState] = useState<{ slug: string; available: boolean; valid: boolean } | null>(null)
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<unknown>(null)
  const [done, setDone] = useState<Created | null>(null)

  useEffect(() => {
    api.get<Options>('/api/payroll/signup/options').then(o => { setOpts(o); setF(x => ({ ...x, planCode: x.planCode || o.defaultPlan || o.plans?.[0]?.code || '' })) }).catch(e => setErr(e))
  }, [])

  // Suggest a sign-in code from the name, and check availability as either changes.
  useEffect(() => {
    if (!f.organisationName && !f.slug) { setSlugState(null); return }
    const t = setTimeout(() => {
      const qs = slugEdited && f.slug ? `value=${encodeURIComponent(f.slug)}` : `name=${encodeURIComponent(f.organisationName)}`
      api.get<{ slug: string; available: boolean; valid: boolean }>(`/api/payroll/signup/slug?${qs}`).then(r => {
        setSlugState(r)
        if (!slugEdited) setF(x => ({ ...x, slug: r.slug }))
      }).catch(() => setSlugState(null))
    }, 350)
    return () => clearTimeout(t)
  }, [f.organisationName, f.slug, slugEdited])

  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value })

  const submit = async (e: FormEvent) => {
    e.preventDefault(); setBusy(true); setErr(null)
    try {
      const res = await api.post<Created>('/api/payroll/signup', { ...f, phone: f.phone || undefined, slug: f.slug || undefined })
      try { localStorage.setItem('ecosys.org', res.slug) } catch { /* private mode */ }
      setDone(res)
    } catch (ex) { setErr(ex) } finally { setBusy(false) }
  }

  if (!opts && !err) return <Loading what="Loading" />
  if (opts && !opts.enabled) return (
    <div className="login"><div className="card"><h2>Sign-up is not available</h2><p className="small">Accounts on this server are created by the operator. Contact your administrator.</p><Link className="btn" to="/">Back to sign in</Link></div></div>
  )
  if (done) return <CheckEmail created={done} />

  return (
    <div className="login">
      <form className="card" onSubmit={submit} style={{ width: 'min(760px, 100%)' }}>
        <div className="brand" style={{ padding: '0 0 6px' }}><div className="logo" aria-hidden>E</div><div>Ecosys Payroll<small>Start your {opts?.trialDays ?? 14}-day free trial. No card needed.</small></div></div>
        <p className="small muted" style={{ marginTop: 0 }}>Kenyan payroll with PAYE, NSSF, SHIF, Housing Levy and NITA, payslips, bank and M-Pesa files, and employee self-service.</p>
        <ErrorBox error={err} />

        <h3 style={{ marginBottom: 6 }}>Choose a plan</h3>
        <div className="grid" role="radiogroup" aria-label="Plan" style={{ gap: 10, marginBottom: 14, gridTemplateColumns: 'repeat(auto-fit, minmax(min(220px, 100%), 1fr))' }}>
          {opts?.plans?.map(p => (
            <label key={p.code} className="card" style={{ cursor: 'pointer', margin: 0, borderColor: f.planCode === p.code ? 'var(--primary)' : undefined, boxShadow: f.planCode === p.code ? '0 0 0 1px var(--primary)' : undefined }}>
              <div style={{ display: 'flex', gap: 8, alignItems: 'baseline' }}>
                <input type="radio" name="plan" value={p.code} checked={f.planCode === p.code} onChange={() => setF({ ...f, planCode: p.code })} />
                <b>{p.name}</b>
              </div>
              <div className="small" style={{ marginTop: 4 }}>{priceLine(p)}{opts.vatRate ? ' + VAT' : ''}</div>
              <div className="small muted">{limitsLine(p)}</div>
              {p.description && <div className="small muted" style={{ marginTop: 4 }}>{p.description}</div>}
            </label>
          ))}
        </div>

        <h3 style={{ marginBottom: 6 }}>Your organisation</h3>
        <div className="form-grid">
          <Field label="Organisation name"><input value={f.organisationName} onChange={set('organisationName')} autoComplete="organization" required /></Field>
          <Field label="Employer KRA PIN"><input value={f.kraPin} onChange={e => setF({ ...f, kraPin: e.target.value.toUpperCase() })} placeholder="P051234567A" required /></Field>
          <Field label="Sign-in code">
            <input value={f.slug} onChange={e => { setSlugEdited(true); setF({ ...f, slug: e.target.value.toLowerCase() }) }} pattern="[a-z0-9][a-z0-9\-]{1,38}[a-z0-9]" title="3–40 lowercase letters, digits or hyphens" aria-describedby="slug-hint" required />
            <div id="slug-hint" className="small" style={{ marginTop: 2 }}>
              {slugState && slugState.slug === f.slug ? (slugState.available ? <span style={{ color: 'var(--ok)' }}>Available. Your team signs in with this.</span> : <span style={{ color: 'var(--danger)' }}>{slugState.valid ? 'Already taken.' : 'Use 3–40 lowercase letters, digits or hyphens.'}</span>) : <span className="muted">Your team enters this when signing in.</span>}
            </div>
          </Field>
        </div>

        <h3 style={{ marginBottom: 6 }}>You, the administrator</h3>
        <div className="form-grid">
          <Field label="Full name"><input value={f.adminName} onChange={set('adminName')} autoComplete="name" required /></Field>
          <Field label="Work email"><input type="email" value={f.adminEmail} onChange={set('adminEmail')} autoComplete="email" required /></Field>
          <Field label="Mobile (optional)"><input type="tel" value={f.phone} onChange={set('phone')} autoComplete="tel" placeholder="07XX XXX XXX" /></Field>
          <Field label="Password"><PasswordInput value={f.password} onChange={set('password')} autoComplete="new-password" minLength={10} required /><div className="small muted" style={{ marginTop: 2 }}>At least 10 characters. A short phrase works well.</div></Field>
        </div>

        <label className="small" style={{ display: 'flex', gap: 8, alignItems: 'flex-start', margin: '8px 0 14px' }}>
          <input type="checkbox" checked={f.acceptTerms} onChange={e => setF({ ...f, acceptTerms: e.target.checked })} required style={{ marginTop: 2 }} />
          <span>I accept the terms of service and the data processing agreement, and I am authorised to act for this organisation. Employee personal data is processed under the Data Protection Act 2019.</span>
        </label>
        <button className="btn primary" type="submit" disabled={busy || (slugState?.slug === f.slug && !slugState.available)} style={{ width: '100%', justifyContent: 'center' }}>{busy ? 'Creating your account…' : 'Start free trial'}</button>
        <p className="small" style={{ marginTop: 12, textAlign: 'center' }}>Already have an account? <Link to="/">Sign in</Link></p>
      </form>
    </div>
  )
}

function CheckEmail({ created }: { created: Created }) {
  const [sent, setSent] = useState(false)
  const [err, setErr] = useState<unknown>(null)
  const resend = async () => { setErr(null); try { await api.post('/api/payroll/signup/resend', { organisation: created.slug, email: created.email }); setSent(true) } catch (e) { setErr(e) } }
  return (
    <div className="login"><div className="card">
      <h2>{created.verificationRequired ? 'Check your email' : 'Your account is ready'}</h2>
      {created.verificationRequired
        ? <p className="small">We sent a confirmation link to <b>{created.email}</b>. Open it to finish setting up, then sign in with the code <span className="mono">{created.slug}</span>.</p>
        : <p className="small">Sign in with the code <span className="mono">{created.slug}</span> and your email address.</p>}
      <p className="small muted">Your free trial runs until {new Date(created.trialEndsOn + 'T00:00:00').toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' })}. Administrators set up an authenticator app at first sign-in.</p>
      <ErrorBox error={err} />
      {sent && <div className="alert ok">If the address is waiting for confirmation, a new link is on its way.</div>}
      <div className="toolbar">
        <Link className="btn primary" to="/">Go to sign in</Link>
        {created.verificationRequired && <button className="btn" onClick={resend} disabled={sent}>Send the link again</button>}
      </div>
    </div></div>
  )
}

export function VerifyEmailPage() {
  const token = new URLSearchParams(window.location.search).get('token') ?? ''
  const [state, setState] = useState<{ ok?: { slug: string; email: string }; err?: unknown }>({})
  useEffect(() => {
    if (!token) { setState({ err: new ApiError(400, { title: 'Link not valid', detail: 'The confirmation link is incomplete. Copy the whole link from the email.' }) }); return }
    api.post<{ slug: string; email: string }>('/api/payroll/signup/verify', { token })
      .then(ok => { try { localStorage.setItem('ecosys.org', ok.slug) } catch { /* private mode */ } setState({ ok }) })
      .catch(err => setState({ err }))
  }, [token])
  return (
    <div className="login"><div className="card">
      {!state.ok && !state.err && <Loading what="Confirming your email" />}
      {state.ok && <><h2>Email confirmed</h2><p className="small">Thanks. Sign in to <span className="mono">{state.ok.slug}</span> as <b>{state.ok.email}</b> to set up your payroll.</p><Link className="btn primary" to="/">Sign in</Link></>}
      {state.err != null && <><h2>We couldn't confirm your email</h2><ErrorBox error={state.err} /><Link className="btn" to="/">Back to sign in</Link></>}
    </div></div>
  )
}
