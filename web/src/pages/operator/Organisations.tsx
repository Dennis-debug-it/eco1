import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery } from '@tanstack/react-query'
import { api, fmt } from '../../lib/api'
import { PP, useAuth } from '../../lib/auth'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead } from '../../components/ui'
import { usePlans, useRefreshTenants, type TenantRow } from './shared'

export default function OrganisationsPage() {
  const { can } = useAuth()
  const [status, setStatus] = useState('')
  const [search, setSearch] = useState('')
  const [create, setCreate] = useState(false)
  const q = useQuery({ queryKey: ['platform-tenants', status, search], queryFn: () => api.get<TenantRow[]>(`/api/platform/tenants?status=${status}&q=${encodeURIComponent(search)}`) })
  const money = can(PP.BillingRead)
  return (
    <>
      <PageHead title="Organisations" sub="Every customer organisation on the platform.">
        {can(PP.TenantsManage) && <button className="btn primary" onClick={() => setCreate(true)}>New organisation</button>}
      </PageHead>
      <div className="card">
        <div className="toolbar">
          <input placeholder="Search name or code" value={search} onChange={e => setSearch(e.target.value)} aria-label="Search organisations" />
          <select value={status} onChange={e => setStatus(e.target.value)} aria-label="Status">
            <option value="">All statuses</option>{['Trialing', 'Active', 'PastDue', 'Suspended', 'Cancelled', 'Managed'].map(s => <option key={s} value={s}>{s === 'PastDue' ? 'Past due' : s}</option>)}
          </select>
        </div>
        <ErrorBox error={q.error} />
        {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No organisations match.</Empty> : (
          <div className="table-wrap"><table>
            <thead><tr><th>Organisation</th><th>Status</th><th>Plan</th><th className="num">Employees</th>{money && <th className="num">Balance</th>}<th>Trial ends</th><th>Last sign-in</th></tr></thead>
            <tbody>{q.data.map(t => <tr key={t.id}>
              <td><Link to={`/operator/organisations/${t.id}`}>{t.name}</Link><div className="mono small muted">{t.slug}{!t.isActive && ' · sign-in blocked'}</div></td>
              <td><Badge status={t.status} />{t.cancelAtPeriodEnd && <div className="small muted">cancelling</div>}{t.suspensionReason && <div className="small muted">{t.suspensionReason}</div>}</td>
              <td>{t.plan ?? '—'}</td><td className="num">{fmt.int(t.employees)}</td>
              {money && <td className="num">{t.balance ? fmt.money(t.balance) : '—'}</td>}
              <td>{t.status === 'Trialing' ? fmt.date(t.trialEndsOn) : '—'}</td><td>{fmt.dateTime(t.lastLoginAt)}</td>
            </tr>)}</tbody>
          </table></div>
        )}
      </div>
      {create && <NewOrganisation onClose={() => setCreate(false)} />}
    </>
  )
}

function NewOrganisation({ onClose }: { onClose: () => void }) {
  const refresh = useRefreshTenants()
  const plans = usePlans()
  const [f, setF] = useState({ organisationName: '', slug: '', legalEntityName: '', kraPin: '', adminName: '', adminEmail: '', planCode: '', trial: true })
  const save = useMutation({ mutationFn: () => api.post<{ slug: string; adminEmail: string; temporaryPassword: string }>('/api/platform/tenants', { ...f, planCode: f.planCode || undefined }), onSuccess: () => refresh() })
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value })
  if (save.data) return (
    <Modal title="Organisation created" onClose={onClose}>
      <dl className="kv"><dt>Sign-in code</dt><dd className="mono">{save.data.slug}</dd><dt>Administrator</dt><dd>{save.data.adminEmail}</dd><dt>One-time password</dt><dd className="mono">{save.data.temporaryPassword}</dd></dl>
      <p className="small">Give the password to the administrator directly, not by email. It will not be shown again. They choose their own password and set up an authenticator app at first sign-in.</p>
      <button className="btn primary" onClick={onClose}>Done</button>
    </Modal>
  )
  return (
    <Modal title="New organisation" onClose={onClose}>
      <form onSubmit={e => { e.preventDefault(); save.mutate() }}>
        <ErrorBox error={save.error} />
        <div className="form-grid">
          <Field label="Organisation name"><input value={f.organisationName} onChange={set('organisationName')} required /></Field>
          <Field label="Sign-in code"><input value={f.slug} onChange={e => setF({ ...f, slug: e.target.value.toLowerCase() })} required /></Field>
          <Field label="Company (legal name)"><input value={f.legalEntityName} onChange={set('legalEntityName')} placeholder="Same as organisation" /></Field>
          <Field label="Employer KRA PIN"><input value={f.kraPin} onChange={e => setF({ ...f, kraPin: e.target.value.toUpperCase() })} required /></Field>
          <Field label="Administrator name"><input value={f.adminName} onChange={set('adminName')} required /></Field>
          <Field label="Administrator email"><input type="email" value={f.adminEmail} onChange={set('adminEmail')} required /></Field>
          <Field label="Plan"><select value={f.planCode} onChange={set('planCode')}><option value="">None (operator-managed, not billed)</option>{plans.data?.filter(p => p.plan.isActive).map(p => <option key={p.plan.code} value={p.plan.code}>{p.plan.name}</option>)}</select></Field>
          {f.planCode && <Field label="Start"><select value={f.trial ? 'trial' : 'now'} onChange={e => setF({ ...f, trial: e.target.value === 'trial' })}><option value="trial">With a free trial</option><option value="now">Billing from today</option></select></Field>}
        </div>
        <button className="btn primary" disabled={save.isPending}>Create organisation</button>
      </form>
    </Modal>
  )
}
