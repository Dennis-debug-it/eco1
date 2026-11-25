import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useMutation, useQuery } from '@tanstack/react-query'
import { api, fmt, isoDate } from '../../lib/api'
import { PP, useAuth } from '../../lib/auth'
import { priceLine, type PlanView } from '../../lib/billing'
import { Badge, Empty, ErrorBox, Loading, PageHead, ReasonDialog } from '../../components/ui'
import { usePlans, useRefreshTenants, type TenantDetail } from './shared'

export default function OrganisationProfilePage() {
  const { id = '' } = useParams()
  const { can } = useAuth()
  const q = useQuery({ queryKey: ['platform-tenant', id], queryFn: () => api.get<TenantDetail>(`/api/platform/tenants/${id}`) })
  const billing = can(PP.BillingManage)
  const plans = usePlans(billing)
  const refresh = useRefreshTenants()
  const [plan, setPlan] = useState('')
  const [until, setUntil] = useState(isoDate(14))
  const [dialog, setDialog] = useState<'suspend' | 'block' | null>(null)
  const act = useMutation({ mutationFn: ({ path, body }: { path: string; body?: unknown }) => api.post(`/api/platform/tenants/${id}/${path}`, body ?? {}), onSuccess: () => refresh(id) })

  const back = <Link to="/operator/organisations">← All organisations</Link>
  if (!q.data) return <><PageHead title="Organisation" sub={back} />{q.error ? <ErrorBox error={q.error} /> : <Loading />}</>
  const d = q.data, s = d.subscription
  // Read-only during its free trial: reopening goes back to the trial, or needs a new end date once it has passed.
  const readOnlyInTrial = !!s?.inTrialStage && ['Suspended', 'Cancelled'].includes(s.status)
  const trialOver = readOnlyInTrial && !!s?.trialEndsOn && s.trialEndsOn < isoDate(0)
  const reopensTrial = readOnlyInTrial && !trialOver
  const priced = d.plan && d.plan.monthlyBaseFee != null ? (d.plan as PlanView) : null
  return (
    <>
      <PageHead title={d.tenant.name} sub={<>{back} · <span className="mono">{d.tenant.slug}</span></>} />
      <ErrorBox error={act.error} />
      <div className="grid c2">
        <div className="card">
          <h3>Account</h3>
          <dl className="kv">
            <dt>Sign-in code</dt><dd className="mono">{d.tenant.slug}</dd>
            <dt>Created</dt><dd>{fmt.date(d.tenant.createdAt)}</dd>
            <dt>Status</dt><dd>{s ? <Badge status={s.status} /> : <Badge status="Managed" />} {s?.suspensionReason && <span className="small muted">{s.suspensionReason}</span>}{!d.tenant.isActive && <> <Badge status="Error">Sign-in blocked</Badge></>}</dd>
            {d.migration && (d.migration.inMigration || d.migration.goLiveSignedAt) && <><dt>Migration</dt><dd>{d.migration.inMigration
              ? <><Badge status="Warning">Migrating</Badge> <span className="small muted">since {fmt.date(d.migration.migrationStartedAt!)}; nothing paid or filed until go-live</span></>
              : <span className="small">Live since {fmt.date(d.migration.goLiveSignedAt!)} ({d.migration.goLiveSignedBy}), paying from {fmt.date(d.migration.goLiveFrom!)}</span>}</dd></>}
            <dt>Plan</dt><dd>{d.plan ? (priced ? `${d.plan.name} (${priceLine(priced)})` : d.plan.name) : 'Operator-managed: not billed here'}</dd>
            {s?.trialEndsOn && <><dt>Trial ends</dt><dd>{fmt.date(s.trialEndsOn)}{s.activatedAt ? ' · billing details given' : ''}</dd></>}
            {s?.currentPeriodEnd && <><dt>Period</dt><dd>{fmt.date(s.currentPeriodStart)} – {fmt.date(s.currentPeriodEnd)}{s.cancelAtPeriodEnd && ' · cancels at end'}</dd></>}
            {s?.billingEmail && <><dt>Billing contact</dt><dd>{s.billingName} · {s.billingEmail}{s.billingTaxPin && <> · <span className="mono">{s.billingTaxPin}</span></>}</dd></>}
          </dl>
        </div>
        <div className="card">
          <h3>Usage</h3>
          <dl className="kv">
            <dt>Employees</dt><dd>{d.usage.employees}{d.plan?.maxEmployees ? ` / ${d.plan.maxEmployees}` : ''}</dd>
            <dt>Companies</dt><dd>{d.usage.legalEntities}{d.plan?.maxLegalEntities ? ` / ${d.plan.maxLegalEntities}` : ''}</dd>
            <dt>Active users</dt><dd>{d.usage.users}</dd>
            <dt>Payroll runs</dt><dd>{d.usage.payrollRuns}</dd>
            <dt>Administrators</dt><dd>{d.administrators.length === 0 ? <span className="muted">None active</span> : d.administrators.map(a => <div key={a.email}>{a.displayName} · {a.email}{!a.emailVerified && ' (email unconfirmed)'}{!a.mfaEnabled && ' (no 2-step)'}</div>)}</dd>
          </dl>
        </div>
      </div>

      {(billing || can(PP.TenantsManage)) && <div className="card">
        <h3>Actions</h3>
        <div className="toolbar" style={{ flexWrap: 'wrap' }}>
          {billing && <>
            <select value={plan} onChange={e => setPlan(e.target.value)} aria-label="Plan"><option value="">Choose plan…</option>{plans.data?.filter(p => p.plan.isActive).map(p => <option key={p.plan.code} value={p.plan.code}>{p.plan.name}{p.plan.isPublic ? '' : ' (private)'}</option>)}</select>
            <button className="btn" disabled={!plan || act.isPending} onClick={() => act.mutate({ path: s ? 'plan' : 'subscription', body: { planCode: plan } })}>{s ? 'Change plan' : 'Start subscription'}</button>
            {s && s.inTrialStage && ['Trialing', 'Suspended', 'Cancelled'].includes(s.status) && <>
              <input type="date" value={until} onChange={e => setUntil(e.target.value)} aria-label="Extend trial until" />
              <button className="btn" disabled={act.isPending} onClick={() => act.mutate({ path: 'extend-trial', body: { until } })}>Extend trial</button>
            </>}
            {s && !['Suspended', 'Cancelled'].includes(s.status) && <button className="btn" onClick={() => setDialog('suspend')}>Make read-only</button>}
            {s && ['Suspended', 'Cancelled', 'PastDue'].includes(s.status) && !trialOver && <button className="btn" disabled={act.isPending} onClick={() => act.mutate({ path: 'reactivate' })}>{reopensTrial ? 'Reopen trial' : 'Reactivate'}</button>}
          </>}
          {can(PP.TenantsManage) && (d.tenant.isActive
            ? <button className="btn danger" onClick={() => setDialog('block')}>Block sign-in</button>
            : <button className="btn" disabled={act.isPending} onClick={() => act.mutate({ path: 'access', body: { enabled: true } })}>Allow sign-in</button>)}
        </div>
        {billing && reopensTrial && <p className="small muted">Reopening returns the account to its free trial, ending {fmt.date(s!.trialEndsOn!)}. Billing starts only if the customer adds billing details before then.</p>}
        {billing && trialOver && <p className="small muted">The free trial ended on {fmt.date(s!.trialEndsOn!)}. To reopen the account, extend the trial to a new date; the customer can also start paying from their Billing page.</p>}
      </div>}

      {can(PP.BillingRead) && <div className="card">
        <h3>Invoices</h3>
        {d.invoices.length === 0 ? <Empty>No invoices.</Empty> : <div className="table-wrap"><table><thead><tr><th>Number</th><th>Period</th><th>Due</th><th className="num">Total</th><th className="num">Paid</th><th>Status</th><th /></tr></thead>
          <tbody>{d.invoices.map(i => <tr key={i.id}><td className="mono">{i.number}</td><td>{fmt.date(i.periodStart)} – {fmt.date(i.periodEnd)}</td><td>{fmt.date(i.dueOn)}</td><td className="num">{fmt.money(i.total, i.currency)}</td><td className="num">{fmt.money(i.amountPaid, i.currency)}</td><td><Badge status={i.status} /></td>
            <td><button className="btn sm" onClick={() => api.download(`/api/platform/invoices/${i.id}/pdf`)}>PDF</button></td></tr>)}</tbody></table></div>}
      </div>}

      <div className="card">
        <h3>Account history</h3>
        {d.history.length === 0 ? <Empty>Nothing yet.</Empty> : <div className="table-wrap"><table><tbody>{d.history.map((h, i) => <tr key={i}><td>{fmt.dateTime(h.occurredAt)}</td><td className="mono small">{h.action}</td><td>{h.actorName}</td><td className="small muted" style={{ whiteSpace: 'normal' }}>{h.details}</td></tr>)}</tbody></table></div>}
      </div>

      {dialog === 'suspend' && <ReasonDialog title="Make the account read-only" label="Reason (sent to the customer)" required danger confirmLabel="Make read-only" onClose={() => setDialog(null)}
        onConfirm={async reason => { await api.post(`/api/platform/tenants/${id}/suspend`, { reason }); refresh(id) }}>
        <p className="small">They can still sign in, view and export their records, and settle billing. Use this for non-payment handled outside the normal cycle.</p>
      </ReasonDialog>}
      {dialog === 'block' && <ReasonDialog title="Block all sign-ins" label="Reason (kept in the audit log)" required danger confirmLabel="Block sign-in" onClose={() => setDialog(null)}
        onConfirm={async reason => { await api.post(`/api/platform/tenants/${id}/access`, { enabled: false, reason }); refresh(id) }}>
        <p className="small">Nobody in this organisation, including employees on self-service, can sign in until you allow it again. For abuse or a legal hold; for non-payment, use read-only instead.</p>
      </ReasonDialog>}
    </>
  )
}
