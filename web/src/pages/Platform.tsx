import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt, isoDate } from '../lib/api'
import { featureLabel, priceLine, type PlanView } from '../lib/billing'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, ReasonDialog, Stat, Tabs } from '../components/ui'

type Overview = { saas: boolean; tenants: number; managed: number; byStatus: Record<string, number>; mrrExVat: number; currency: string; openBalance: number; overdueBalance: number; trialsEndingThisWeek: number; signupsLast30Days: number; employeesUnderManagement: number }
type TenantRow = { id: string; name: string; slug: string; isActive: boolean; createdAt: string; status: string; plan?: string; planCode?: string; trialEndsOn?: string; employees: number; balance: number; lastLoginAt?: string; suspensionReason?: string; cancelAtPeriodEnd: boolean }
type InvoiceRow = { id: string; tenantId: string; tenant: string; slug: string; number: string; planName: string; periodStart: string; periodEnd: string; issuedOn: string; dueOn: string; currency: string; total: number; amountPaid: number; balance: number; status: string; overdue: boolean }
type Detail = {
  tenant: { id: string; name: string; slug: string; isActive: boolean; createdAt: string }
  subscription?: { status: string; trialEndsOn?: string; activatedAt?: string; currentPeriodStart?: string; currentPeriodEnd?: string; cancelAtPeriodEnd: boolean; suspensionReason?: string; billingName?: string; billingEmail?: string; billingTaxPin?: string } | null
  plan?: PlanView | null
  usage: { employees: number; legalEntities: number; users: number; payrollRuns: number }
  administrators: { email: string; displayName: string; lastLoginAt?: string; mfaEnabled: boolean; emailVerified: boolean }[]
  invoices: { id: string; number: string; periodStart: string; periodEnd: string; total: number; amountPaid: number; status: string; dueOn: string; currency: string }[]
  history: { action: string; actorName?: string; occurredAt: string; details?: string }[]
}

export default function PlatformPage() {
  const [tab, setTab] = useState<'customers' | 'invoices' | 'plans'>('customers')
  const qc = useQueryClient()
  const run = useMutation({ mutationFn: () => api.post<Record<string, number>>('/api/platform/billing/run'), onSuccess: () => qc.invalidateQueries() })
  return (
    <>
      <PageHead title="Operator console" sub="Customers, subscriptions, invoices and the price list. No payroll or employee records are shown here.">
        <button className="btn" onClick={() => run.mutate()} disabled={run.isPending}>{run.isPending ? 'Running…' : 'Run billing now'}</button>
      </PageHead>
      <ErrorBox error={run.error} />
      {run.data && <div className="alert ok">Billing run: {Object.entries(run.data).map(([k, v]) => `${k.replace(/([A-Z])/g, ' $1').toLowerCase()} ${v}`).join(', ')}.</div>}
      <OverviewStats />
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'customers', label: 'Customers' }, { id: 'invoices', label: 'Invoices' }, { id: 'plans', label: 'Plans & prices' }]} />
      {tab === 'customers' && <Customers />}
      {tab === 'invoices' && <Invoices />}
      {tab === 'plans' && <Plans />}
    </>
  )
}

function OverviewStats() {
  const q = useQuery({ queryKey: ['platform-overview'], queryFn: () => api.get<Overview>('/api/platform/overview') })
  if (!q.data) return <ErrorBox error={q.error} />
  const o = q.data
  return (
    <>
      {!o.saas && <div className="alert info">SaaS mode is off on this server (Saas:Enabled=false): no sign-ups, billing or plan limits. You can still see customers here.</div>}
      <div className="grid c4" style={{ marginBottom: 16 }}>
        <Stat label="Monthly recurring revenue" value={fmt.money(o.mrrExVat, o.currency)} hint="Excl. VAT, paying customers at today's headcount" />
        <Stat label="Customers" value={fmt.int(o.tenants)} hint={Object.entries(o.byStatus).map(([k, v]) => `${v} ${k === 'Trialing' ? 'on trial' : k === 'PastDue' ? 'past due' : k.toLowerCase()}`).concat(o.managed ? [`${o.managed} managed`] : []).join(' · ')} />
        <Stat label="Outstanding" value={fmt.money(o.openBalance, o.currency)} hint={o.overdueBalance ? `${fmt.money(o.overdueBalance, o.currency)} overdue` : 'Nothing overdue'} />
        <Stat label="Trials ending this week" value={o.trialsEndingThisWeek} hint={`${o.signupsLast30Days} sign-ups in 30 days · ${fmt.int(o.employeesUnderManagement)} employees on the platform`} />
      </div>
    </>
  )
}

function Customers() {
  const [status, setStatus] = useState('')
  const [search, setSearch] = useState('')
  const [open, setOpen] = useState<string | null>(null)
  const [create, setCreate] = useState(false)
  const q = useQuery({ queryKey: ['platform-tenants', status, search], queryFn: () => api.get<TenantRow[]>(`/api/platform/tenants?status=${status}&q=${encodeURIComponent(search)}`) })
  return (
    <div className="card">
      <div className="toolbar">
        <input placeholder="Search name or code" value={search} onChange={e => setSearch(e.target.value)} aria-label="Search customers" />
        <select value={status} onChange={e => setStatus(e.target.value)} aria-label="Status">
          <option value="">All statuses</option>{['Trialing', 'Active', 'PastDue', 'Suspended', 'Cancelled', 'Managed'].map(s => <option key={s} value={s}>{s === 'PastDue' ? 'Past due' : s}</option>)}
        </select>
        <span style={{ flex: 1 }} /><button className="btn primary" onClick={() => setCreate(true)}>New customer</button>
      </div>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No customers match.</Empty> : (
        <div className="table-wrap"><table>
          <thead><tr><th>Organisation</th><th>Status</th><th>Plan</th><th className="num">Employees</th><th className="num">Balance</th><th>Trial ends</th><th>Last sign-in</th><th /></tr></thead>
          <tbody>{q.data.map(t => <tr key={t.id}>
            <td>{t.name}<div className="mono small muted">{t.slug}{!t.isActive && ' · sign-in blocked'}</div></td>
            <td><Badge status={t.status} />{t.cancelAtPeriodEnd && <div className="small muted">cancelling</div>}{t.suspensionReason && <div className="small muted">{t.suspensionReason}</div>}</td>
            <td>{t.plan ?? '—'}</td><td className="num">{fmt.int(t.employees)}</td><td className="num">{t.balance ? fmt.money(t.balance) : '—'}</td>
            <td>{t.status === 'Trialing' ? fmt.date(t.trialEndsOn) : '—'}</td><td>{fmt.dateTime(t.lastLoginAt)}</td>
            <td><button className="btn sm" onClick={() => setOpen(t.id)}>Manage</button></td>
          </tr>)}</tbody>
        </table></div>
      )}
      {open && <TenantDialog id={open} onClose={() => setOpen(null)} />}
      {create && <NewCustomer onClose={() => setCreate(false)} />}
    </div>
  )
}

function TenantDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['platform-tenant', id], queryFn: () => api.get<Detail>(`/api/platform/tenants/${id}`) })
  const plans = useQuery({ queryKey: ['platform-plans'], queryFn: () => api.get<{ plan: PlanView; subscribers: number }[]>('/api/platform/plans') })
  const [plan, setPlan] = useState('')
  const [until, setUntil] = useState(isoDate(14))
  const [dialog, setDialog] = useState<'suspend' | 'block' | null>(null)
  const refresh = () => { qc.invalidateQueries({ queryKey: ['platform-tenant', id] }); qc.invalidateQueries({ queryKey: ['platform-tenants'] }); qc.invalidateQueries({ queryKey: ['platform-overview'] }) }
  const act = useMutation({ mutationFn: ({ path, body }: { path: string; body?: unknown }) => api.post(`/api/platform/tenants/${id}/${path}`, body ?? {}), onSuccess: refresh })
  if (!q.data) return <Modal title="Customer" onClose={onClose}>{q.error ? <ErrorBox error={q.error} /> : <Loading />}</Modal>
  const d = q.data, s = d.subscription
  return (
    <Modal title={d.tenant.name} onClose={onClose} width={860}>
      <ErrorBox error={act.error} />
      <div className="grid c2">
        <dl className="kv">
          <dt>Sign-in code</dt><dd className="mono">{d.tenant.slug}</dd>
          <dt>Created</dt><dd>{fmt.date(d.tenant.createdAt)}</dd>
          <dt>Status</dt><dd>{s ? <Badge status={s.status} /> : <Badge status="Managed" />} {s?.suspensionReason && <span className="small muted">{s.suspensionReason}</span>}</dd>
          <dt>Plan</dt><dd>{d.plan ? `${d.plan.name} (${priceLine(d.plan)})` : 'Operator-managed: not billed here'}</dd>
          {s?.trialEndsOn && <><dt>Trial ends</dt><dd>{fmt.date(s.trialEndsOn)}{s.activatedAt ? ' · billing details given' : ''}</dd></>}
          {s?.currentPeriodEnd && <><dt>Period</dt><dd>{fmt.date(s.currentPeriodStart)} – {fmt.date(s.currentPeriodEnd)}{s.cancelAtPeriodEnd && ' · cancels at end'}</dd></>}
          {s?.billingEmail && <><dt>Billing</dt><dd>{s.billingName} · {s.billingEmail}{s.billingTaxPin && <> · <span className="mono">{s.billingTaxPin}</span></>}</dd></>}
        </dl>
        <dl className="kv">
          <dt>Employees</dt><dd>{d.usage.employees}{d.plan?.maxEmployees ? ` / ${d.plan.maxEmployees}` : ''}</dd>
          <dt>Companies</dt><dd>{d.usage.legalEntities}{d.plan?.maxLegalEntities ? ` / ${d.plan.maxLegalEntities}` : ''}</dd>
          <dt>Active users</dt><dd>{d.usage.users}</dd>
          <dt>Payroll runs</dt><dd>{d.usage.payrollRuns}</dd>
          <dt>Administrators</dt><dd>{d.administrators.map(a => <div key={a.email}>{a.displayName} · {a.email}{!a.emailVerified && ' (email unconfirmed)'}{!a.mfaEnabled && ' (no 2-step)'}</div>)}</dd>
        </dl>
      </div>

      <h3>Actions</h3>
      <div className="toolbar" style={{ flexWrap: 'wrap' }}>
        <select value={plan} onChange={e => setPlan(e.target.value)} aria-label="Plan"><option value="">Choose plan…</option>{plans.data?.filter(p => p.plan.isActive).map(p => <option key={p.plan.code} value={p.plan.code}>{p.plan.name}{p.plan.isPublic ? '' : ' (private)'}</option>)}</select>
        <button className="btn" disabled={!plan || act.isPending} onClick={() => act.mutate({ path: s ? 'plan' : 'subscription', body: { planCode: plan } })}>{s ? 'Change plan' : 'Start subscription'}</button>
        {s && (s.status === 'Trialing' || s.suspensionReason === 'Trial ended') && <>
          <input type="date" value={until} onChange={e => setUntil(e.target.value)} aria-label="Extend trial until" />
          <button className="btn" disabled={act.isPending} onClick={() => act.mutate({ path: 'extend-trial', body: { until } })}>Extend trial</button>
        </>}
        {s && !['Suspended', 'Cancelled'].includes(s.status) && <button className="btn" onClick={() => setDialog('suspend')}>Make read-only</button>}
        {s && ['Suspended', 'Cancelled', 'PastDue'].includes(s.status) && <button className="btn" disabled={act.isPending} onClick={() => act.mutate({ path: 'reactivate' })}>Reactivate</button>}
        {d.tenant.isActive
          ? <button className="btn danger" onClick={() => setDialog('block')}>Block sign-in</button>
          : <button className="btn" disabled={act.isPending} onClick={() => act.mutate({ path: 'access', body: { enabled: true } })}>Allow sign-in</button>}
      </div>

      <h3>Invoices</h3>
      {d.invoices.length === 0 ? <Empty>No invoices.</Empty> : <div className="table-wrap"><table><thead><tr><th>Number</th><th>Period</th><th>Due</th><th className="num">Total</th><th className="num">Paid</th><th>Status</th><th /></tr></thead>
        <tbody>{d.invoices.map(i => <tr key={i.id}><td className="mono">{i.number}</td><td>{fmt.date(i.periodStart)} – {fmt.date(i.periodEnd)}</td><td>{fmt.date(i.dueOn)}</td><td className="num">{fmt.money(i.total, i.currency)}</td><td className="num">{fmt.money(i.amountPaid, i.currency)}</td><td><Badge status={i.status} /></td>
          <td><button className="btn sm" onClick={() => api.download(`/api/platform/invoices/${i.id}/pdf`)}>PDF</button></td></tr>)}</tbody></table></div>}

      <h3>Account history</h3>
      {d.history.length === 0 ? <Empty>Nothing yet.</Empty> : <div className="table-wrap"><table><tbody>{d.history.map((h, i) => <tr key={i}><td>{fmt.dateTime(h.occurredAt)}</td><td className="mono small">{h.action}</td><td>{h.actorName}</td><td className="small muted" style={{ whiteSpace: 'normal' }}>{h.details}</td></tr>)}</tbody></table></div>}

      {dialog === 'suspend' && <ReasonDialog title="Make the account read-only" label="Reason (sent to the customer)" required danger confirmLabel="Make read-only" onClose={() => setDialog(null)}
        onConfirm={async reason => { await api.post(`/api/platform/tenants/${id}/suspend`, { reason }); refresh() }}>
        <p className="small">They can still sign in, view and export their records, and settle billing. Use this for non-payment handled outside the normal cycle.</p>
      </ReasonDialog>}
      {dialog === 'block' && <ReasonDialog title="Block all sign-ins" label="Reason (kept in the audit log)" required danger confirmLabel="Block sign-in" onClose={() => setDialog(null)}
        onConfirm={async reason => { await api.post(`/api/platform/tenants/${id}/access`, { enabled: false, reason }); refresh() }}>
        <p className="small">Nobody in this organisation, including employees on self-service, can sign in until you allow it again. For abuse or a legal hold; for non-payment, use read-only instead.</p>
      </ReasonDialog>}
    </Modal>
  )
}

function NewCustomer({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient()
  const plans = useQuery({ queryKey: ['platform-plans'], queryFn: () => api.get<{ plan: PlanView; subscribers: number }[]>('/api/platform/plans') })
  const [f, setF] = useState({ organisationName: '', slug: '', legalEntityName: '', kraPin: '', adminName: '', adminEmail: '', planCode: '', trial: true })
  const save = useMutation({ mutationFn: () => api.post<{ slug: string; adminEmail: string; temporaryPassword: string }>('/api/platform/tenants', { ...f, planCode: f.planCode || undefined }), onSuccess: () => qc.invalidateQueries({ queryKey: ['platform-tenants'] }) })
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value })
  if (save.data) return (
    <Modal title="Customer created" onClose={onClose}>
      <dl className="kv"><dt>Sign-in code</dt><dd className="mono">{save.data.slug}</dd><dt>Administrator</dt><dd>{save.data.adminEmail}</dd><dt>One-time password</dt><dd className="mono">{save.data.temporaryPassword}</dd></dl>
      <p className="small">Give the password to the administrator directly, not by email. It will not be shown again. They choose their own password and set up an authenticator app at first sign-in.</p>
      <button className="btn primary" onClick={onClose}>Done</button>
    </Modal>
  )
  return (
    <Modal title="New customer" onClose={onClose}>
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
        <button className="btn primary" disabled={save.isPending}>Create customer</button>
      </form>
    </Modal>
  )
}

function Invoices() {
  const qc = useQueryClient()
  const [status, setStatus] = useState('Open')
  const [pay, setPay] = useState<InvoiceRow | null>(null)
  const [voiding, setVoiding] = useState<InvoiceRow | null>(null)
  const q = useQuery({ queryKey: ['platform-invoices', status], queryFn: () => api.get<InvoiceRow[]>(`/api/platform/invoices?status=${status}`) })
  const refresh = () => { qc.invalidateQueries({ queryKey: ['platform-invoices'] }); qc.invalidateQueries({ queryKey: ['platform-overview'] }); qc.invalidateQueries({ queryKey: ['platform-tenants'] }) }
  return (
    <div className="card">
      <div className="toolbar"><select value={status} onChange={e => setStatus(e.target.value)} aria-label="Status"><option value="Open">Unpaid</option><option value="Paid">Paid</option><option value="Void">Void</option><option value="">All</option></select></div>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No invoices.</Empty> : <div className="table-wrap"><table>
        <thead><tr><th>Number</th><th>Customer</th><th>Period</th><th>Due</th><th className="num">Total</th><th className="num">Balance</th><th>Status</th><th /></tr></thead>
        <tbody>{q.data.map(i => <tr key={i.id}>
          <td className="mono">{i.number}</td><td>{i.tenant}<div className="mono small muted">{i.slug}</div></td><td>{fmt.date(i.periodStart)} – {fmt.date(i.periodEnd)}</td><td>{fmt.date(i.dueOn)}</td>
          <td className="num">{fmt.money(i.total, i.currency)}</td><td className="num">{i.balance ? fmt.money(i.balance, i.currency) : '—'}</td>
          <td>{i.overdue ? <Badge status="Error">Overdue</Badge> : <Badge status={i.status} />}</td>
          <td style={{ whiteSpace: 'nowrap' }}>
            {i.status === 'Open' && <button className="btn sm primary" onClick={() => setPay(i)}>Record payment</button>}{' '}
            {i.status === 'Open' && i.amountPaid === 0 && <button className="btn sm" onClick={() => setVoiding(i)}>Void</button>}{' '}
            <button className="btn sm" onClick={() => api.download(`/api/platform/invoices/${i.id}/pdf`)}>PDF</button>
          </td>
        </tr>)}</tbody>
      </table></div>}
      {pay && <PaymentDialog inv={pay} onClose={() => { setPay(null); refresh() }} />}
      {voiding && <ReasonDialog title={`Void ${voiding.number}`} label="Reason" required danger confirmLabel="Void invoice" onClose={() => setVoiding(null)}
        onConfirm={async reason => { await api.post(`/api/platform/invoices/${voiding.id}/void`, { reason }); refresh() }} />}
    </div>
  )
}

function PaymentDialog({ inv, onClose }: { inv: InvoiceRow; onClose: () => void }) {
  const [f, setF] = useState({ amount: String(inv.balance), method: 'MPesa', reference: '', receivedAt: isoDate(), notes: '' })
  const save = useMutation({ mutationFn: () => api.post(`/api/platform/invoices/${inv.id}/payments`, { amount: Number(f.amount), method: f.method, reference: f.reference, receivedAt: `${f.receivedAt}T12:00:00+03:00`, notes: f.notes || undefined }), onSuccess: onClose })
  return (
    <Modal title={`Payment for ${inv.number}`} onClose={onClose}>
      <p className="small">{inv.tenant}: {fmt.money(inv.balance, inv.currency)} outstanding of {fmt.money(inv.total, inv.currency)}.</p>
      <form onSubmit={e => { e.preventDefault(); save.mutate() }}>
        <ErrorBox error={save.error} />
        <div className="form-grid">
          <Field label="Amount"><input type="number" step="0.01" min="0.01" max={inv.balance} value={f.amount} onChange={e => setF({ ...f, amount: e.target.value })} required /></Field>
          <Field label="Method"><select value={f.method} onChange={e => setF({ ...f, method: e.target.value })}>{[['MPesa', 'M-Pesa'], ['BankTransfer', 'Bank transfer'], ['Card', 'Card'], ['Cheque', 'Cheque'], ['Other', 'Other']].map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></Field>
          <Field label="Reference"><input value={f.reference} onChange={e => setF({ ...f, reference: e.target.value.toUpperCase() })} placeholder="M-Pesa code or bank reference" required /></Field>
          <Field label="Received on"><input type="date" value={f.receivedAt} onChange={e => setF({ ...f, receivedAt: e.target.value })} required /></Field>
        </div>
        <Field label="Notes"><input value={f.notes} onChange={e => setF({ ...f, notes: e.target.value })} /></Field>
        <button className="btn primary" disabled={save.isPending}>Record payment</button>
      </form>
    </Modal>
  )
}

const blankPlan = { code: '', name: '', description: '', monthlyBaseFee: 0, includedEmployees: 0, pricePerAdditionalEmployee: 0, maxEmployees: null as number | null, maxLegalEntities: null as number | null, features: [] as string[], isPublic: true, isActive: true, sortOrder: 10, currency: 'KES' }

function Plans() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['platform-plans'], queryFn: () => api.get<{ plan: PlanView; subscribers: number }[]>('/api/platform/plans') })
  const [edit, setEdit] = useState<PlanView | 'new' | null>(null)
  return (
    <div className="card">
      <div className="toolbar"><p className="small muted" style={{ margin: 0 }}>Prices exclude VAT. A change applies to each subscriber's next invoice; issued invoices keep their prices.</p><span style={{ flex: 1 }} /><button className="btn primary" onClick={() => setEdit('new')}>New plan</button></div>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : <div className="table-wrap"><table>
        <thead><tr><th>Plan</th><th>Price</th><th>Limits</th><th>Features</th><th>Visibility</th><th className="num">Subscribers</th><th /></tr></thead>
        <tbody>{q.data?.map(({ plan: p, subscribers }) => <tr key={p.id}>
          <td><b>{p.name}</b><div className="mono small muted">{p.code}</div></td><td className="small" style={{ whiteSpace: 'normal' }}>{priceLine(p)}</td>
          <td className="small">{p.maxEmployees ?? '∞'} employees · {p.maxLegalEntities ?? '∞'} companies</td>
          <td className="small">{p.features.map(f => featureLabel[f] ?? f).join(', ') || '—'}</td>
          <td>{!p.isActive ? <Badge status="Cancelled">Retired</Badge> : p.isPublic ? <Badge status="Active">Public</Badge> : <Badge status="neutral">Private</Badge>}</td>
          <td className="num">{subscribers}</td><td><button className="btn sm" onClick={() => setEdit(p)}>Edit</button></td>
        </tr>)}</tbody>
      </table></div>}
      {edit && <PlanDialog plan={edit === 'new' ? null : edit} onClose={() => { setEdit(null); qc.invalidateQueries({ queryKey: ['platform-plans'] }) }} />}
    </div>
  )
}

function PlanDialog({ plan, onClose }: { plan: PlanView | null; onClose: () => void }) {
  const [f, setF] = useState(plan ? { ...blankPlan, ...plan, description: plan.description ?? '' } : blankPlan)
  const save = useMutation({ mutationFn: () => plan ? api.put(`/api/platform/plans/${plan.id}`, f) : api.post('/api/platform/plans', f), onSuccess: onClose })
  const num = (k: 'monthlyBaseFee' | 'includedEmployees' | 'pricePerAdditionalEmployee' | 'sortOrder') => (e: { target: { value: string } }) => setF({ ...f, [k]: Number(e.target.value) })
  const opt = (k: 'maxEmployees' | 'maxLegalEntities') => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value === '' ? null : Number(e.target.value) })
  return (
    <Modal title={plan ? `Edit ${plan.name}` : 'New plan'} onClose={onClose}>
      <form onSubmit={e => { e.preventDefault(); save.mutate() }}>
        <ErrorBox error={save.error} />
        {plan && <div className="alert warn">Existing subscribers move to the new prices on their next invoice.</div>}
        <div className="form-grid">
          <Field label="Code"><input value={f.code} onChange={e => setF({ ...f, code: e.target.value.toLowerCase() })} disabled={!!plan} required /></Field>
          <Field label="Name"><input value={f.name} onChange={e => setF({ ...f, name: e.target.value })} required /></Field>
          <Field label={`Monthly base fee (${f.currency})`}><input type="number" min="0" step="0.01" value={f.monthlyBaseFee} onChange={num('monthlyBaseFee')} /></Field>
          <Field label="Employees included"><input type="number" min="0" value={f.includedEmployees} onChange={num('includedEmployees')} /></Field>
          <Field label={`Price per extra employee (${f.currency})`}><input type="number" min="0" step="0.01" value={f.pricePerAdditionalEmployee} onChange={num('pricePerAdditionalEmployee')} /></Field>
          <Field label="Employee limit (empty = none)"><input type="number" min="1" value={f.maxEmployees ?? ''} onChange={opt('maxEmployees')} /></Field>
          <Field label="Company limit (empty = none)"><input type="number" min="1" value={f.maxLegalEntities ?? ''} onChange={opt('maxLegalEntities')} /></Field>
          <Field label="Sort order"><input type="number" value={f.sortOrder} onChange={num('sortOrder')} /></Field>
        </div>
        <Field label="Description"><input value={f.description} onChange={e => setF({ ...f, description: e.target.value })} /></Field>
        <div className="toolbar">
          <label className="small"><input type="checkbox" checked={f.features.includes('hr_suite')} onChange={e => setF({ ...f, features: e.target.checked ? [...f.features, 'hr_suite'] : f.features.filter(x => x !== 'hr_suite') })} /> HR suite</label>
          <label className="small"><input type="checkbox" checked={f.isPublic} onChange={e => setF({ ...f, isPublic: e.target.checked })} /> Offered on sign-up</label>
          <label className="small"><input type="checkbox" checked={f.isActive} onChange={e => setF({ ...f, isActive: e.target.checked })} /> Available</label>
        </div>
        <button className="btn primary" disabled={save.isPending}>Save plan</button>
      </form>
    </Modal>
  )
}
