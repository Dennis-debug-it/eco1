import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { api } from '../lib/api'
import { useAuth } from '../lib/auth'
import type { Me } from '../lib/types'
import { ErrorBox, Field } from './ui'
import { QrCode } from './QrCode'

/** Authenticator-app enrolment. Used as a gate for privileged roles and from Settings. */
export default function MfaEnrol({ gate }: { gate?: boolean }) {
  const { adopt, logout, user } = useAuth()
  const [setup, setSetup] = useState<{ secret: string; provisioningUri: string } | null>(null)
  const [code, setCode] = useState('')
  const start = useMutation({ mutationFn: () => api.post<{ secret: string; provisioningUri: string }>('/api/payroll/auth/mfa/setup'), onSuccess: setSetup })
  const enable = useMutation({ mutationFn: () => api.post<{ token: string; user: Me }>('/api/payroll/auth/mfa/enable', { code }), onSuccess: r => adopt(r.token, r.user) })
  const grouped = setup?.secret.match(/.{1,4}/g)?.join(' ')
  const body = (
    <>
      <h2>Set up two-step verification</h2>
      {gate && <p className="small">Your role ({user?.roles.join(', ')}) can view or approve payroll, so an authenticator app is required before you continue.</p>}
      <ErrorBox error={start.error ?? enable.error} />
      {!setup ? <button className="btn primary" onClick={() => start.mutate()} disabled={start.isPending}>Begin</button> : <>
        <ol className="small" style={{ paddingLeft: 18 }}>
          <li>Open Google Authenticator, Microsoft Authenticator, Authy or 1Password on your phone.</li>
          <li>Add an account and scan this QR code:
            <div style={{ margin: '10px 0' }}><QrCode value={setup.provisioningUri} label={`QR code to add Ecosys Payroll (${user?.email ?? 'your account'}) to an authenticator app`} /></div>
            <details>
              <summary style={{ cursor: 'pointer' }}>Can't scan it?</summary>
              Choose "enter a setup key" in the app and type this key (time-based):
              <div className="mono" style={{ fontSize: 15, margin: '6px 0', letterSpacing: '.05em', wordBreak: 'break-all' }}>{grouped}</div>
              On the phone that has the app, you can instead open <a href={setup.provisioningUri}>this link</a>.
            </details>
          </li>
          <li>Enter the 6-digit code the app shows.</li>
        </ol>
        <Field label="Code"><input inputMode="numeric" maxLength={6} value={code} onChange={e => setCode(e.target.value.replace(/\D/g, ''))} autoFocus /></Field>
        <button className="btn primary" disabled={code.length !== 6 || enable.isPending} onClick={() => enable.mutate()}>Verify and enable</button>
      </>}
      {gate && <button className="btn" style={{ marginLeft: 8 }} onClick={logout}>Sign out</button>}
    </>
  )
  return gate ? <div className="login"><div className="card" style={{ width: 'min(480px, 100%)' }}>{body}</div></div> : <div className="card">{body}</div>
}
