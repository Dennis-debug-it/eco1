import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt, isoDate } from '../../lib/api'
import { PP, useAuth } from '../../lib/auth'
import { featureLabel, priceLine, type PlanView } from '../../lib/billing'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, ReasonDialog, Tabs } from '../../components/ui'
import { usePlans, type InvoiceRow } from './shared'

export default function SubscriptionsPage() {
  const { can } = useAuth()
  const [tab, setTab] = useState<'invoices' | 'plans'>('invoices')
  const qc = useQueryClient()
  const run = useMutation({ mutationFn: () => api.post<Record<string, number>>('/api/platform/billing/run'), onSuccess: () => qc.invalidateQueries() })
  return (
    <>
      <PageHead title="Plans & subscriptions" sub="Invoices, payments and the price list. Billing runs by itself every few hours.">
        {can(PP.BillingManage) && <button className="btn" onClick={() => run.mutate()} disabled={run.isPending}>{run.isPending ? 'Running…' : 'Run billing now'}</button>}
      </PageHead>
      <ErrorBox error={run.error} />
      {run.data && <div className="alert ok">Billing run: {Object.entries(run.data).map(([k, v]) => `${k.replace(/([A-Z])/g, ' $1').toLowerCase()} ${v}`).join(', ')}.</div>}
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'invoices', label: 'Invoices' }, { id: 'plans', label: 'Plans & prices' }]} />
      {tab === 'invoices' && <Invoices />}
      {tab === 'plans' && <Plans />}
    </>
  )
}

function Invoices() {
  const { can } = useAuth()
  const manage = can(PP.BillingManage)
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
            {manage && i.status === 'Open' && <button className="btn sm primary" onClick={() => setPay(i)}>Record payment</button>}{' '}
            {manage && i.status === 'Open' && i.amountPaid === 0 && <button className="btn sm" onClick={() => setVoiding(i)}>Void</button>}{' '}
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
  const { can } = useAuth()
  const edits = can(PP.PlansManage)
  const qc = useQueryClient()
  const q = usePlans()
  const [edit, setEdit] = useState<PlanView | 'new' | null>(null)
  return (
    <div className="card">
      <div className="toolbar"><p className="small muted" style={{ margin: 0 }}>Prices exclude VAT. A change applies to each subscriber's next invoice; issued invoices keep their prices.</p><span style={{ flex: 1 }} />{edits && <button className="btn primary" onClick={() => setEdit('new')}>New plan</button>}</div>
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : <div className="table-wrap"><table>
        <thead><tr><th>Plan</th><th>Price</th><th>Limits</th><th>Features</th><th>Visibility</th><th className="num">Subscribers</th><th /></tr></thead>
        <tbody>{q.data?.map(({ plan: p, subscribers }) => <tr key={p.id}>
          <td><b>{p.name}</b><div className="mono small muted">{p.code}</div></td><td className="small" style={{ whiteSpace: 'normal' }}>{priceLine(p)}</td>
          <td className="small">{p.maxEmployees ?? '∞'} employees · {p.maxLegalEntities ?? '∞'} companies</td>
          <td className="small">{p.features.map(f => featureLabel[f] ?? f).join(', ') || '—'}</td>
          <td>{!p.isActive ? <Badge status="Cancelled">Retired</Badge> : p.isPublic ? <Badge status="Active">Public</Badge> : <Badge status="neutral">Private</Badge>}</td>
          <td className="num">{subscribers}</td><td>{edits && <button className="btn sm" onClick={() => setEdit(p)}>Edit</button>}</td>
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
          {Object.entries(featureLabel).map(([code, label]) => (
            <label key={code} className="small" style={{ marginRight: 12 }}><input type="checkbox" checked={f.features.includes(code)} onChange={e => setF({ ...f, features: e.target.checked ? [...f.features, code] : f.features.filter(x => x !== code) })} /> {label}</label>
          ))}
          <label className="small"><input type="checkbox" checked={f.isPublic} onChange={e => setF({ ...f, isPublic: e.target.checked })} /> Offered on sign-up</label>
          <label className="small"><input type="checkbox" checked={f.isActive} onChange={e => setF({ ...f, isActive: e.target.checked })} /> Available</label>
        </div>
        <button className="btn primary" disabled={save.isPending}>Save plan</button>
      </form>
    </Modal>
  )
}
