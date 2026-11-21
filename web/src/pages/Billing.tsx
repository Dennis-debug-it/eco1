import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { featureLabel, limitsLine, priceLine, type PlanView } from '../lib/billing'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, ReasonDialog } from '../components/ui'

type Line = { description: string; quantity: number; unitPrice: number; amount: number }
type Invoice = { id: string; number: string; periodStart: string; periodEnd: string; issuedOn: string; dueOn: string; currency: string; billableEmployees: number; subtotal: number; vat: number; total: number; amountPaid: number; balance: number; status: string; paidAt?: string; overdue: boolean }
type Billing = {
  subscription: { id: string; status: string; trialEndsOn?: string; activatedAt?: string; currentPeriodStart?: string; currentPeriodEnd?: string; cancelAtPeriodEnd: boolean; cancelledAt?: string; suspendedAt?: string; suspensionReason?: string; billingName?: string; billingEmail?: string; billingTaxPin?: string; billingAddress?: string; readOnly: boolean }
  plan: PlanView
  estimate?: { periodStart: string; periodEnd: string; invoiceOn: string; headcount: number; lines: Line[]; subtotal: number; vatRate: number; vat: number; total: number; currency: string } | null
  usage: { employees: number; legalEntities: number }
  invoices: Invoice[]
  payments: { id: string; invoiceId: string; amount: number; method: string; reference: string; receivedAt: string }[]
  plans: PlanView[]
  paymentInstructions: string; vatRate: number; supportEmail?: string
}

export default function BillingPage() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['billing'], queryFn: () => api.get<Billing>('/api/payroll/billing') })
  const refresh = () => { qc.invalidateQueries({ queryKey: ['billing'] }); qc.invalidateQueries({ queryKey: ['billing-status'] }) }
  const [details, setDetails] = useState(false)
  const [cancel, setCancel] = useState(false)
  const changePlan = useMutation({ mutationFn: (planCode: string) => api.post('/api/payroll/billing/plan', { planCode }), onSuccess: refresh })
  const resume = useMutation({ mutationFn: () => api.post('/api/payroll/billing/resume'), onSuccess: refresh })

  if (q.isLoading) return <Loading />
  if (q.error) return <><PageHead title="Billing" /><ErrorBox error={q.error} /></>
  const b = q.data!
  const s = b.subscription
  const trialLapsed = s.status === 'Suspended' && s.suspensionReason === 'Trial ended'
  const needsActivation = (s.status === 'Trialing' && !s.activatedAt) || s.status === 'Cancelled' || trialLapsed
  const unpaid = b.invoices.filter(i => i.status === 'Open')

  return (
    <>
      <PageHead title="Billing" sub="Your plan, usage and invoices">
        {!s.readOnly && !s.cancelAtPeriodEnd && <button className="btn" onClick={() => setCancel(true)}>Cancel subscription</button>}
      </PageHead>

      {s.status === 'Trialing' && <div className={`alert ${s.activatedAt ? 'ok' : 'info'}`}>
        Free trial until {fmt.date(s.trialEndsOn)}. {s.activatedAt ? 'Billing details are in place; your subscription starts when the trial ends.' : 'Add billing details before then to carry on without interruption.'}</div>}
      {s.status === 'PastDue' && <div className="alert warn">An invoice is overdue. Pay it soon to keep full access; the account becomes read-only after the grace period.</div>}
      {s.status === 'Suspended' && <div className="alert danger">The account is read-only: {s.suspensionReason ?? 'suspended'}. You can still view and export everything. {unpaid.length ? `Pay ${unpaid.map(i => i.number).join(', ')} to reopen it.` : trialLapsed ? 'Add billing details below to reopen it.' : `Contact ${b.supportEmail ?? 'support'} to reopen it.`}</div>}
      {s.status === 'Cancelled' && <div className="alert danger">The subscription was cancelled{s.cancelledAt ? ` on ${fmt.date(s.cancelledAt)}` : ''}. Your records are read-only and can still be exported. Reactivate below to carry on.</div>}
      {s.cancelAtPeriodEnd && <div className="alert warn">The subscription ends on {fmt.date(s.currentPeriodEnd)}. <button className="btn sm" onClick={() => resume.mutate()} disabled={resume.isPending}>Keep my subscription</button></div>}
      <ErrorBox error={resume.error} />

      <div className="grid c4">
        <div className="card stat"><div className="label">Plan</div><div className="value">{b.plan.name}</div><div className="hint"><Badge status={s.status} /></div></div>
        <div className="card stat"><div className="label">Employees</div><div className="value">{fmt.int(b.usage.employees)}{b.plan.maxEmployees ? <span className="small muted"> / {b.plan.maxEmployees}</span> : null}</div><div className="hint">{b.plan.includedEmployees} included in the base fee</div></div>
        <div className="card stat"><div className="label">Companies</div><div className="value">{b.usage.legalEntities}{b.plan.maxLegalEntities ? <span className="small muted"> / {b.plan.maxLegalEntities}</span> : null}</div></div>
        <div className="card stat"><div className="label">{s.status === 'Trialing' ? 'Trial ends' : 'Current period'}</div><div className="value" style={{ fontSize: 18 }}>{s.status === 'Trialing' ? fmt.date(s.trialEndsOn) : s.currentPeriodEnd ? `to ${fmt.date(s.currentPeriodEnd)}` : '—'}</div></div>
      </div>

      {needsActivation && <ActivateForm b={b} onDone={refresh} />}

      <div className="grid c2" style={{ marginTop: 16 }}>
        {!needsActivation && <div className="card">
          <div className="toolbar"><h2 style={{ margin: 0 }}>Billing details</h2><span style={{ flex: 1 }} /><button className="btn sm" onClick={() => setDetails(true)}>Edit</button></div>
          <dl className="kv">
            <dt>Invoice to</dt><dd>{s.billingName ?? '—'}</dd>
            <dt>Email</dt><dd>{s.billingEmail ?? '—'}</dd>
            <dt>KRA PIN</dt><dd className="mono">{s.billingTaxPin ?? '—'}</dd>
            <dt>Address</dt><dd>{s.billingAddress ?? '—'}</dd>
          </dl>
        </div>}
        {b.estimate && <div className="card">
          <h2>Next invoice (estimate)</h2>
          <p className="small muted">For {fmt.date(b.estimate.periodStart)} – {fmt.date(b.estimate.periodEnd)}, issued {fmt.date(b.estimate.invoiceOn)}. Based on the {b.estimate.headcount} employees on the books today; the invoice counts them on the day it is issued.</p>
          <table><tbody>
            {b.estimate.lines.map((l, i) => <tr key={i}><td style={{ whiteSpace: 'normal' }}>{l.description}</td><td className="num">{fmt.num(l.amount)}</td></tr>)}
            <tr><td className="muted">VAT {(b.estimate.vatRate * 100).toFixed(0)}%</td><td className="num">{fmt.num(b.estimate.vat)}</td></tr>
            <tr><td><b>Total</b></td><td className="num"><b>{fmt.money(b.estimate.total, b.estimate.currency)}</b></td></tr>
          </tbody></table>
        </div>}
      </div>

      <div className="card">
        <h2>Plans</h2>
        <ErrorBox error={changePlan.error} />
        <div className="grid c4">
          {b.plans.map(p => (
            <div key={p.code} className="card" style={{ margin: 0, borderColor: p.id === b.plan.id ? 'var(--primary)' : undefined }}>
              <b>{p.name}</b> {p.id === b.plan.id && <Badge status="Active">Current</Badge>}
              <div className="small" style={{ marginTop: 4 }}>{priceLine(p)} + VAT</div>
              <div className="small muted">{limitsLine(p)}</div>
              {p.features.length > 0 && <div className="small muted">Includes {p.features.map(f => featureLabel[f] ?? f).join(', ')}</div>}
              {p.description && <p className="small muted" style={{ margin: '6px 0' }}>{p.description}</p>}
              {p.id !== b.plan.id && !s.readOnly && <button className="btn sm" disabled={changePlan.isPending} onClick={() => { if (confirm(`Switch to the ${p.name} plan? The next invoice uses its prices.`)) changePlan.mutate(p.code) }}>Switch to {p.name}</button>}
            </div>
          ))}
        </div>
      </div>

      <div className="card">
        <h2>Invoices</h2>
        {b.invoices.length === 0 ? <Empty>No invoices yet. {s.status === 'Trialing' ? 'The first one comes a month after your trial ends.' : 'Invoices are issued at the end of each monthly period.'}</Empty> : (
          <div className="table-wrap"><table>
            <thead><tr><th>Number</th><th>Period</th><th>Issued</th><th>Due</th><th className="num">Employees</th><th className="num">Total</th><th className="num">Balance</th><th>Status</th><th /></tr></thead>
            <tbody>{b.invoices.map(i => <tr key={i.id}>
              <td className="mono">{i.number}</td><td>{fmt.date(i.periodStart)} – {fmt.date(i.periodEnd)}</td><td>{fmt.date(i.issuedOn)}</td><td>{fmt.date(i.dueOn)}</td>
              <td className="num">{i.billableEmployees}</td><td className="num">{fmt.money(i.total, i.currency)}</td><td className="num">{i.balance ? fmt.money(i.balance, i.currency) : '—'}</td>
              <td>{i.overdue ? <Badge status="Error">Overdue</Badge> : <Badge status={i.status} />}</td>
              <td><button className="btn sm" onClick={() => api.download(`/api/payroll/billing/invoices/${i.id}/pdf`)}>PDF</button></td>
            </tr>)}</tbody>
          </table></div>
        )}
        <h3>How to pay</h3>
        <p className="small">{b.paymentInstructions} Use the invoice number as the reference.{b.supportEmail && <> Questions: <a href={`mailto:${b.supportEmail}`}>{b.supportEmail}</a>.</>}</p>
      </div>

      {details && <Modal title="Billing details" onClose={() => setDetails(false)}><ActivateForm b={b} onDone={() => { setDetails(false); refresh() }} embedded /></Modal>}
      {cancel && <ReasonDialog title="Cancel subscription" label="Why are you leaving? (optional)" danger confirmLabel="Cancel subscription" onClose={() => setCancel(false)}
        onConfirm={async (reason: string) => { await api.post('/api/payroll/billing/cancel', { reason }); setCancel(false); refresh() }}>
        <p className="small">{s.status === 'Trialing' ? 'Your trial ends now and the account becomes read-only.' : `You keep full access until ${fmt.date(s.currentPeriodEnd)}, and receive a final invoice for this period. After that the account is read-only.`} Nothing is deleted; you can export your records or reactivate at any time.</p>
      </ReasonDialog>}
    </>
  )
}

function ActivateForm({ b, onDone, embedded }: { b: Billing; onDone: () => void; embedded?: boolean }) {
  const s = b.subscription
  const [f, setF] = useState({ billingName: s.billingName ?? '', billingEmail: s.billingEmail ?? '', billingTaxPin: s.billingTaxPin ?? '', billingAddress: s.billingAddress ?? '', planCode: b.plan.isPublic ? b.plan.code : b.plans[0]?.code ?? '' })
  const save = useMutation({ mutationFn: () => api.post('/api/payroll/billing/activate', { ...f, billingTaxPin: f.billingTaxPin || undefined, billingAddress: f.billingAddress || undefined, planCode: embedded ? undefined : f.planCode }), onSuccess: onDone })
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value })
  const body = (
    <form onSubmit={e => { e.preventDefault(); save.mutate() }}>
      <ErrorBox error={save.error} />
      <div className="form-grid">
        <Field label="Invoice to (company name)"><input value={f.billingName} onChange={set('billingName')} required /></Field>
        <Field label="Invoice email"><input type="email" value={f.billingEmail} onChange={set('billingEmail')} required /></Field>
        <Field label="KRA PIN (for VAT invoices)"><input value={f.billingTaxPin} onChange={e => setF({ ...f, billingTaxPin: e.target.value.toUpperCase() })} placeholder="P051234567A" /></Field>
        <Field label="Postal address"><input value={f.billingAddress} onChange={set('billingAddress')} /></Field>
        {!embedded && <Field label="Plan"><select value={f.planCode} onChange={set('planCode')}>{b.plans.filter(p => p.isPublic).map(p => <option key={p.code} value={p.code}>{p.name}: {priceLine(p)}</option>)}</select></Field>}
      </div>
      <button className="btn primary" disabled={save.isPending}>{embedded ? 'Save' : s.status === 'Trialing' ? 'Confirm billing details' : 'Reactivate subscription'}</button>
    </form>
  )
  if (embedded) return body
  return (
    <div className="card" style={{ marginTop: 16 }}>
      <h2>{s.status === 'Trialing' ? 'Continue after your trial' : 'Reactivate your subscription'}</h2>
      <p className="small muted">{s.status === 'Trialing' ? `Nothing is charged during the trial. Billing starts on ${fmt.date(s.trialEndsOn)} and you are invoiced monthly in arrears.` : 'Your subscription restarts today and you are invoiced monthly in arrears.'} Prices exclude VAT at {(b.vatRate * 100).toFixed(0)}%.</p>
      {body}
    </div>
  )
}
